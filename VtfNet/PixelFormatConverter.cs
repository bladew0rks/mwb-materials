using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpBcn;

namespace VtfNet;

public static class PixelFormatConverter
{
    private const float LuminanceR = 0.299f;
    private const float LuminanceG = 0.587f;
    private const float LuminanceB = 0.114f;

    private enum Transform
    {
        None,
        Luminance,
        BlueScreen,
    }

    private sealed record Layout(int BytesPerPixel, int RBits, int GBits, int BBits, int ABits, int RIndex, int GIndex, int BIndex, int AIndex, Transform Transform = Transform.None)
    {
        public (int Shift, int Bits) Channel(int channel)
        {
            int[] bits = [RBits, GBits, BBits, ABits];
            int[] order = [RIndex, GIndex, BIndex, AIndex];

            if (order[channel] < 0)
            {
                return (0, 0);
            }

            int shift = 0;

            for (int other = 0; other < 4; other++)
            {
                if (other != channel && order[other] >= 0 && order[other] < order[channel])
                {
                    shift += bits[other];
                }
            }

            return (shift, bits[channel]);
        }
    }

    private static readonly Dictionary<VtfImageFormat, Layout> Layouts = new()
    {
        [VtfImageFormat.RGBA8888] = new(4, 8, 8, 8, 8, 0, 1, 2, 3),
        [VtfImageFormat.ABGR8888] = new(4, 8, 8, 8, 8, 3, 2, 1, 0),
        [VtfImageFormat.RGB888] = new(3, 8, 8, 8, 0, 0, 1, 2, -1),
        [VtfImageFormat.BGR888] = new(3, 8, 8, 8, 0, 2, 1, 0, -1),
        [VtfImageFormat.RGB565] = new(2, 5, 6, 5, 0, 0, 1, 2, -1),
        [VtfImageFormat.I8] = new(1, 8, 0, 0, 0, 0, -1, -1, -1, Transform.Luminance),
        [VtfImageFormat.IA88] = new(2, 8, 0, 0, 8, 0, -1, -1, 1, Transform.Luminance),
        [VtfImageFormat.A8] = new(1, 0, 0, 0, 8, -1, -1, -1, 0),
        [VtfImageFormat.RGB888Bluescreen] = new(3, 8, 8, 8, 0, 0, 1, 2, -1, Transform.BlueScreen),
        [VtfImageFormat.BGR888Bluescreen] = new(3, 8, 8, 8, 0, 2, 1, 0, -1, Transform.BlueScreen),
        [VtfImageFormat.ARGB8888] = new(4, 8, 8, 8, 8, 3, 0, 1, 2),
        [VtfImageFormat.BGRA8888] = new(4, 8, 8, 8, 8, 2, 1, 0, 3),
        [VtfImageFormat.BGRX8888] = new(4, 8, 8, 8, 0, 2, 1, 0, -1),
        [VtfImageFormat.BGR565] = new(2, 5, 6, 5, 0, 2, 1, 0, -1),
        [VtfImageFormat.BGRX5551] = new(2, 5, 5, 5, 0, 2, 1, 0, -1),
        [VtfImageFormat.BGRA4444] = new(2, 4, 4, 4, 4, 2, 1, 0, 3),
        [VtfImageFormat.BGRA5551] = new(2, 5, 5, 5, 1, 2, 1, 0, 3),
        [VtfImageFormat.UV88] = new(2, 8, 8, 0, 0, 0, 1, -1, -1),
        [VtfImageFormat.UVWQ8888] = new(4, 8, 8, 8, 8, 0, 1, 2, 3),
        [VtfImageFormat.UVLX8888] = new(4, 8, 8, 8, 8, 0, 1, 2, 3),
        [VtfImageFormat.HdrBGRA8888] = new(4, 8, 8, 8, 8, 2, 1, 0, 3),
    };

    public static int GetImageSize(int width, int height, VtfImageFormat format)
    {
        VtfImageFormatInfo info = VtfImageFormatInfo.Get(format);

        if (info.IsCompressed)
        {
            return BcEncoder.GetCompressedSize(width, height, info.BlockSize);
        }

        return width * height * info.BytesPerPixel;
    }

    public static byte[] ToRgba8888(ReadOnlySpan<byte> data, int width, int height, VtfImageFormat format)
    {
        switch (format)
        {
            case VtfImageFormat.RGBA8888:
                return data[..(width * height * 4)].ToArray();
            case VtfImageFormat.DXT1:
                return BcDecoder.DecodeBc1(data, width, height, oneBitAlpha: true);
            case VtfImageFormat.DXT1OneBitAlpha:
                return BcDecoder.DecodeBc1(data, width, height, oneBitAlpha: true);
            case VtfImageFormat.DXT3:
                return BcDecoder.DecodeBc2(data, width, height);
            case VtfImageFormat.DXT5:
                return BcDecoder.DecodeBc3(data, width, height);
            case VtfImageFormat.ATI1N:
                return BcDecoder.DecodeBc4(data, width, height);
            case VtfImageFormat.ATI2N:
                return BcDecoder.DecodeBc5(data, width, height);
            case VtfImageFormat.BC7:
                return BcDecoder.DecodeBc7(data, width, height);
            case VtfImageFormat.BC6HSigned:
            case VtfImageFormat.BC6HUnsigned:
                Half[] hdr = BcDecoder.DecodeBc6h(data, width, height, format == VtfImageFormat.BC6HSigned);
                return DecodeHighPrecision(MemoryMarshal.AsBytes(hdr.AsSpan()), width, height, VtfImageFormat.RGBA16161616F);
            case VtfImageFormat.RGBA16161616F:
            case VtfImageFormat.RGBA16161616:
            case VtfImageFormat.R32F:
            case VtfImageFormat.RGB323232F:
            case VtfImageFormat.RGBA32323232F:
                return DecodeHighPrecision(data, width, height, format);
        }

        if (!Layouts.TryGetValue(format, out Layout? layout))
        {
            throw new NotSupportedException("Decoding " + VtfImageFormatInfo.Get(format).Name + " is not supported.");
        }

        byte[] output = new byte[width * height * 4];
        int pixels = width * height;

        if (data.Length < pixels * layout.BytesPerPixel)
        {
            throw new ArgumentException("Image buffer is too small for the image dimensions.", nameof(data));
        }

        var (rShift, rBits) = layout.Channel(0);
        var (gShift, gBits) = layout.Channel(1);
        var (bShift, bBits) = layout.Channel(2);
        var (aShift, aBits) = layout.Channel(3);

        for (int i = 0; i < pixels; i++)
        {
            ulong packed = ReadPacked(data, i * layout.BytesPerPixel, layout.BytesPerPixel);

            int r = ExpandChannel(packed, rShift, rBits, 0);
            int g = ExpandChannel(packed, gShift, gBits, 0);
            int b = ExpandChannel(packed, bShift, bBits, 0);
            int a = ExpandChannel(packed, aShift, aBits, 255);

            if (format == VtfImageFormat.HdrBGRA8888)
            {
                int scale = a * 16;
                r = Math.Clamp(r * scale / 512, 0, 255);
                g = Math.Clamp(g * scale / 512, 0, 255);
                b = Math.Clamp(b * scale / 512, 0, 255);
                a = 255;
            }
            else if (layout.Transform == Transform.Luminance)
            {
                g = r;
                b = r;
            }
            else if (layout.Transform == Transform.BlueScreen)
            {
                if (r == 0 && g == 0 && b == 255)
                {
                    b = 0;
                    a = 0;
                }
            }

            output[i * 4] = (byte)r;
            output[i * 4 + 1] = (byte)g;
            output[i * 4 + 2] = (byte)b;
            output[i * 4 + 3] = (byte)a;
        }

        return output;
    }

    public static byte[] FromRgba8888(ReadOnlySpan<byte> rgba, int width, int height, VtfImageFormat format, int alphaThreshold = 128)
    {
        byte[] output = new byte[GetImageSize(width, height, format)];
        FromRgba8888(rgba, width, height, format, output, 0, alphaThreshold);
        return output;
    }

    public static void FromRgba8888(ReadOnlySpan<byte> rgba, int width, int height, VtfImageFormat format, byte[] output, int outputOffset, int alphaThreshold = 128)
    {
        switch (format)
        {
            case VtfImageFormat.RGBA8888:
                rgba[..(width * height * 4)].CopyTo(output.AsSpan(outputOffset));
                return;
            case VtfImageFormat.DXT1:
            case VtfImageFormat.DXT3:
            case VtfImageFormat.DXT5:
            case VtfImageFormat.ATI1N:
            case VtfImageFormat.ATI2N:
            case VtfImageFormat.DXT1OneBitAlpha:
            case VtfImageFormat.BC7:
                BcEncoder.Encode(format switch
                {
                    VtfImageFormat.DXT1 => BcFormat.Bc1,
                    VtfImageFormat.DXT1OneBitAlpha => BcFormat.Bc1Alpha,
                    VtfImageFormat.DXT3 => BcFormat.Bc2,
                    VtfImageFormat.DXT5 => BcFormat.Bc3,
                    VtfImageFormat.ATI1N => BcFormat.Bc4,
                    VtfImageFormat.BC7 => BcFormat.Bc7,
                    _ => BcFormat.Bc5,
                }, rgba, width, height, output.AsSpan(outputOffset), alphaThreshold);
                return;
            case VtfImageFormat.BC6HSigned:
            case VtfImageFormat.BC6HUnsigned:
                Half[] halves = new Half[width * height * 4];

                for (int i = 0; i < halves.Length; i++)
                {
                    halves[i] = (i & 3) == 3 ? Half.One : (Half)(rgba[i] / 255f);
                }

                BcEncoder.Encode(format == VtfImageFormat.BC6HSigned ? BcFormat.Bc6hSigned : BcFormat.Bc6hUnsigned,
                    MemoryMarshal.AsBytes(halves.AsSpan()), width, height, output.AsSpan(outputOffset));
                return;
        }

        if (!Layouts.TryGetValue(format, out Layout? layout) || format == VtfImageFormat.HdrBGRA8888)
        {
            throw new NotSupportedException("Encoding " + VtfImageFormatInfo.Get(format).Name + " is not supported.");
        }

        int pixels = width * height;
        Span<byte> dest = output.AsSpan(outputOffset, pixels * layout.BytesPerPixel);

        var (rShift, rBits) = layout.Channel(0);
        var (gShift, gBits) = layout.Channel(1);
        var (bShift, bBits) = layout.Channel(2);
        var (aShift, aBits) = layout.Channel(3);

        for (int i = 0; i < pixels; i++)
        {
            int r = rgba[i * 4];
            int g = rgba[i * 4 + 1];
            int b = rgba[i * 4 + 2];
            int a = rgba[i * 4 + 3];

            if (layout.Transform == Transform.Luminance)
            {
                r = Math.Clamp((int)(LuminanceR * r + LuminanceG * g + LuminanceB * b + 0.5f), 0, 255);
            }
            else if (layout.Transform == Transform.BlueScreen && a == 0)
            {
                r = 0;
                g = 0;
                b = 255;
            }

            ulong packed = PackChannel(r, rShift, rBits)
                | PackChannel(g, gShift, gBits)
                | PackChannel(b, bShift, bBits)
                | PackChannel(a, aShift, aBits);

            WritePacked(dest, i * layout.BytesPerPixel, layout.BytesPerPixel, packed);
        }
    }

    private static byte[] DecodeHighPrecision(ReadOnlySpan<byte> data, int width, int height, VtfImageFormat format)
    {
        int pixels = width * height;
        int channels = format switch
        {
            VtfImageFormat.R32F => 1,
            VtfImageFormat.RGB323232F => 3,
            _ => 4,
        };
        int componentSize = format is VtfImageFormat.RGBA16161616F or VtfImageFormat.RGBA16161616 ? 2 : 4;

        if (data.Length < pixels * channels * componentSize)
        {
            throw new ArgumentException("Image buffer is too small for the image dimensions.", nameof(data));
        }

        byte[] output = new byte[pixels * 4];

        for (int i = 0; i < pixels; i++)
        {
            Span<float> rgba = [0f, 0f, 0f, 1f];

            for (int c = 0; c < channels; c++)
            {
                ReadOnlySpan<byte> component = data.Slice((i * channels + c) * componentSize, componentSize);
                rgba[c] = format switch
                {
                    VtfImageFormat.RGBA16161616F => (float)BinaryPrimitives.ReadHalfLittleEndian(component),
                    VtfImageFormat.RGBA16161616 => BinaryPrimitives.ReadUInt16LittleEndian(component) / 65535f,
                    _ => BinaryPrimitives.ReadSingleLittleEndian(component),
                };
            }

            if (channels == 1)
            {
                rgba[1] = rgba[0];
                rgba[2] = rgba[0];
            }

            for (int c = 0; c < 4; c++)
            {
                float value = float.IsNaN(rgba[c]) ? 0f : rgba[c];
                output[i * 4 + c] = (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);
            }
        }

        return output;
    }

    private static ulong ReadPacked(ReadOnlySpan<byte> data, int offset, int bytes)
    {
        ulong value = 0;

        for (int i = 0; i < bytes; i++)
        {
            value |= (ulong)data[offset + i] << (i * 8);
        }

        return value;
    }

    private static void WritePacked(Span<byte> data, int offset, int bytes, ulong value)
    {
        for (int i = 0; i < bytes; i++)
        {
            data[offset + i] = (byte)(value >> (i * 8));
        }
    }

    private static int ExpandChannel(ulong packed, int shift, int bits, int fallback)
    {
        if (bits == 0)
        {
            return fallback;
        }

        int max = (1 << bits) - 1;
        int value = (int)((packed >> shift) & (ulong)max);
        return bits == 8 ? value : (value * 255 + max / 2) / max;
    }

    private static ulong PackChannel(int value, int shift, int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        int max = (1 << bits) - 1;
        int quantized = bits == 8 ? value : (value * max + 127) / 255;
        return (ulong)quantized << shift;
    }
}
