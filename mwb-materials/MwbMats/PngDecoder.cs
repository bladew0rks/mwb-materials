using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;

namespace mwb_materials.MwbMats
{
    static class PngDecoder
    {
        private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public sealed class Result
        {
            public byte[] Data;
            public int Width;
            public int Height;

            public int Channels;
        }

        public static Result TryDecode(byte[] file)
        {
            if (file.Length < 33 || !file.AsSpan(0, 8).SequenceEqual(Signature))
            {
                return null;
            }

            int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
            byte[] palette = null;
            byte[] transparency = null;
            MemoryStream idat = new MemoryStream();
            int pos = 8;

            while (pos + 12 <= file.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
                uint type = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos + 4));
                int data = pos + 8;

                if (length < 0 || data + length > file.Length)
                {
                    return null;
                }

                switch (type)
                {
                    case 0x49484452:
                        width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(data));
                        height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(data + 4));
                        bitDepth = file[data + 8];
                        colorType = file[data + 9];
                        interlace = file[data + 12];
                        break;
                    case 0x504C5445:
                        palette = file.AsSpan(data, length).ToArray();
                        break;
                    case 0x74524E53:
                        transparency = file.AsSpan(data, length).ToArray();
                        break;
                    case 0x49444154:
                        idat.Write(file, data, length);
                        break;
                    case 0x43674249:
                        return null;
                }

                if (type == 0x49454E44)
                {
                    break;
                }

                pos = data + length + 4;
            }

            bool supported = width > 0 && height > 0 && interlace == 0 &&
                ((colorType is 0 or 2 or 4 or 6 && (bitDepth == 8 || bitDepth == 16) && (transparency == null || colorType is 4 or 6)) ||
                 (colorType == 3 && bitDepth == 8 && palette != null));

            if (!supported || (long)width * height > int.MaxValue / 8)
            {
                return null;
            }

            int samples = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
            int sampleBytes = bitDepth / 8;
            int bpp = samples * sampleBytes;
            int rowBytes = width * bpp;

            int channels = colorType == 3 ? (transparency != null ? 4 : 3) : samples;
            byte[] output = GC.AllocateUninitializedArray<byte>(width * height * channels);
            idat.Position = 0;

            bool ok = (long)rowBytes * height >= PipelineThresholdBytes
                ? DecodePipelined(idat, width, height, rowBytes, bpp, channels, output, colorType, sampleBytes, palette, transparency)
                : DecodeSequential(idat, width, height, rowBytes, bpp, channels, output, colorType, sampleBytes, palette, transparency);

            return ok ? new Result() { Data = output, Width = width, Height = height, Channels = channels } : null;
        }

        private const long PipelineThresholdBytes = 1 << 20;
        private const int RowsPerSlot = 32;
        private const int Slots = 4;

        private static bool DecodeSequential(Stream idat, int width, int height, int rowBytes, int bpp, int channels, byte[] output,
            int colorType, int sampleBytes, byte[] palette, byte[] transparency)
        {
            byte[] previous = new byte[rowBytes];
            byte[] current = new byte[rowBytes];

            try
            {
                using (ZLibStream zlib = new ZLibStream(idat, CompressionMode.Decompress))
                {
                    for (int y = 0; y < height; y++)
                    {
                        int filter = zlib.ReadByte();

                        if (filter < 0 || filter > 4)
                        {
                            return false;
                        }

                        zlib.ReadExactly(current);
                        Unfilter(filter, current, previous, bpp);
                        WriteRow(current, output.AsSpan(y * width * channels, width * channels), colorType, sampleBytes, palette, transparency);
                        (previous, current) = (current, previous);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                return false;
            }

            return true;
        }

        private static bool DecodePipelined(Stream idat, int width, int height, int rowBytes, int bpp, int channels, byte[] output,
            int colorType, int sampleBytes, byte[] palette, byte[] transparency)
        {
            int stride = rowBytes + 1;
            byte[][] slots = new byte[Slots][];
            int[] slotRows = new int[Slots];

            for (int i = 0; i < Slots; i++)
            {
                slots[i] = new byte[stride * RowsPerSlot];
            }

            using SemaphoreSlim free = new SemaphoreSlim(Slots);
            using SemaphoreSlim filled = new SemaphoreSlim(0);
            bool abort = false;

            Thread producer = new Thread(() =>
            {
                int slot = 0;

                try
                {
                    using (ZLibStream zlib = new ZLibStream(idat, CompressionMode.Decompress))
                    {
                        for (int y = 0; y < height; slot = (slot + 1) % Slots)
                        {
                            free.Wait();

                            if (Volatile.Read(ref abort))
                            {
                                return;
                            }

                            int rows = Math.Min(RowsPerSlot, height - y);
                            zlib.ReadExactly(slots[slot].AsSpan(0, rows * stride));
                            slotRows[slot] = rows;
                            filled.Release();
                            y += rows;
                        }
                    }
                }
                catch (Exception)
                {
                    if (!Volatile.Read(ref abort))
                    {
                        slotRows[slot] = -1;
                        filled.Release();
                    }
                }
            })
            {
                IsBackground = true,
                Name = "PNG inflate"
            };

            producer.Start();
            byte[] previous = new byte[rowBytes];
            bool ok = true;

            try
            {
                for (int y = 0, slot = 0; y < height && ok; slot = (slot + 1) % Slots)
                {
                    filled.Wait();
                    int rows = slotRows[slot];

                    if (rows < 0)
                    {
                        ok = false;
                        break;
                    }

                    byte[] batch = slots[slot];

                    for (int r = 0; r < rows; r++, y++)
                    {
                        int filter = batch[r * stride];

                        if (filter > 4)
                        {
                            ok = false;
                            break;
                        }

                        Span<byte> row = batch.AsSpan(r * stride + 1, rowBytes);
                        Unfilter(filter, row, previous, bpp);
                        WriteRow(row, output.AsSpan(y * width * channels, width * channels), colorType, sampleBytes, palette, transparency);
                        row.CopyTo(previous);
                    }

                    free.Release();
                }
            }
            finally
            {
                Volatile.Write(ref abort, true);
                free.Release(Slots);
                producer.Join();
            }

            return ok;
        }

        private static void Unfilter(int filter, Span<byte> row, ReadOnlySpan<byte> prior, int bpp)
        {
            switch (filter)
            {
                case 0:
                    return;
                case 2:
                    UnfilterUp(row, prior);
                    return;
            }

            if (Vector128.IsHardwareAccelerated)
            {
                switch (bpp)
                {
                    case 3:
                        UnfilterPixels<Pixel3>(filter, row, prior);
                        return;
                    case 4:
                        UnfilterPixels<Pixel4>(filter, row, prior);
                        return;
                    case 6:
                        UnfilterPixels<Pixel6>(filter, row, prior);
                        return;
                    case 8:
                        UnfilterPixels<Pixel8>(filter, row, prior);
                        return;
                }
            }

            UnfilterScalar(filter, row, prior, bpp);
        }

        private static void UnfilterUp(Span<byte> row, ReadOnlySpan<byte> prior)
        {
            int i = 0;

            if (Vector256.IsHardwareAccelerated)
            {
                ref byte r = ref MemoryMarshal.GetReference(row);
                ref byte p = ref MemoryMarshal.GetReference(prior);

                for (; i <= row.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
                {
                    Vector256<byte> sum = Vector256.LoadUnsafe(ref r, (nuint)i) + Vector256.LoadUnsafe(ref p, (nuint)i);
                    sum.StoreUnsafe(ref r, (nuint)i);
                }
            }

            for (; i < row.Length; i++)
            {
                row[i] += prior[i];
            }
        }

        private static void UnfilterScalar(int filter, Span<byte> row, ReadOnlySpan<byte> prior, int bpp)
        {
            switch (filter)
            {
                case 1:
                    for (int i = bpp; i < row.Length; i++)
                    {
                        row[i] += row[i - bpp];
                    }

                    break;
                case 3:
                    for (int i = 0; i < bpp; i++)
                    {
                        row[i] += (byte)(prior[i] >> 1);
                    }

                    for (int i = bpp; i < row.Length; i++)
                    {
                        row[i] += (byte)((row[i - bpp] + prior[i]) >> 1);
                    }

                    break;
                case 4:
                    for (int i = 0; i < bpp; i++)
                    {
                        row[i] += prior[i];
                    }

                    for (int i = bpp; i < row.Length; i++)
                    {
                        row[i] += (byte)Paeth(row[i - bpp], prior[i], prior[i - bpp]);
                    }

                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Paeth(int a, int b, int c)
        {
            int pa = Math.Abs(b - c);
            int pb = Math.Abs(a - c);
            int pc = Math.Abs(a + b - c - c);
            int useB = -((pb < pa ? 1 : 0) & (pb <= pc ? 1 : 0));
            int useC = -((pc < pa ? 1 : 0) & (pc < pb ? 1 : 0));
            return (a & ~(useB | useC)) | (b & useB) | (c & useC);
        }

        private interface IPixelWidth
        {
            static abstract int Bytes { get; }
            static abstract ulong Read(ref byte source);
            static abstract void Write(ref byte dest, ulong value);
        }

        private struct Pixel3 : IPixelWidth
        {
            public static int Bytes => 3;
            public static ulong Read(ref byte s) => Unsafe.ReadUnaligned<ushort>(ref s) | ((ulong)Unsafe.Add(ref s, 2) << 16);

            public static void Write(ref byte d, ulong v)
            {
                Unsafe.WriteUnaligned(ref d, (ushort)v);
                Unsafe.Add(ref d, 2) = (byte)(v >> 16);
            }
        }

        private struct Pixel4 : IPixelWidth
        {
            public static int Bytes => 4;
            public static ulong Read(ref byte s) => Unsafe.ReadUnaligned<uint>(ref s);
            public static void Write(ref byte d, ulong v) => Unsafe.WriteUnaligned(ref d, (uint)v);
        }

        private struct Pixel6 : IPixelWidth
        {
            public static int Bytes => 6;
            public static ulong Read(ref byte s) => Unsafe.ReadUnaligned<uint>(ref s) | ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, 4)) << 32);

            public static void Write(ref byte d, ulong v)
            {
                Unsafe.WriteUnaligned(ref d, (uint)v);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 4), (ushort)(v >> 32));
            }
        }

        private struct Pixel8 : IPixelWidth
        {
            public static int Bytes => 8;
            public static ulong Read(ref byte s) => Unsafe.ReadUnaligned<ulong>(ref s);
            public static void Write(ref byte d, ulong v) => Unsafe.WriteUnaligned(ref d, v);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void UnfilterPixels<T>(int filter, Span<byte> row, ReadOnlySpan<byte> prior) where T : struct, IPixelWidth
        {
            ref byte r = ref MemoryMarshal.GetReference(row);
            ref byte p = ref MemoryMarshal.GetReference(prior);
            int pixels = row.Length / T.Bytes;
            Vector128<short> byteMask = Vector128.Create((short)0xFF);

            Vector128<short> a = Vector128<short>.Zero;
            Vector128<short> c = Vector128<short>.Zero;

            switch (filter)
            {
                case 1:
                    for (int x = 0, o = 0; x < pixels; x++, o += T.Bytes)
                    {
                        a = (Load<T>(ref Unsafe.Add(ref r, o)) + a) & byteMask;
                        Store<T>(ref Unsafe.Add(ref r, o), a);
                    }

                    break;
                case 3:
                    for (int x = 0, o = 0; x < pixels; x++, o += T.Bytes)
                    {
                        Vector128<short> b = Load<T>(ref Unsafe.Add(ref p, o));
                        a = (Load<T>(ref Unsafe.Add(ref r, o)) + ((a + b) >>> 1)) & byteMask;
                        Store<T>(ref Unsafe.Add(ref r, o), a);
                    }

                    break;
                case 4:
                    for (int x = 0, o = 0; x < pixels; x++, o += T.Bytes)
                    {
                        Vector128<short> b = Load<T>(ref Unsafe.Add(ref p, o));
                        Vector128<short> pa = b - c;
                        Vector128<short> pb = a - c;
                        Vector128<short> pc = Vector128.Abs(pa + pb);
                        pa = Vector128.Abs(pa);
                        pb = Vector128.Abs(pb);
                        Vector128<short> smallest = Vector128.Min(pc, Vector128.Min(pa, pb));
                        Vector128<short> predicted = Vector128.ConditionalSelect(Vector128.Equals(smallest, pa), a,
                            Vector128.ConditionalSelect(Vector128.Equals(smallest, pb), b, c));

                        a = (Load<T>(ref Unsafe.Add(ref r, o)) + predicted) & byteMask;
                        Store<T>(ref Unsafe.Add(ref r, o), a);
                        c = b;
                    }

                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<short> Load<T>(ref byte source) where T : struct, IPixelWidth
        {
            return Vector128.WidenLower(Vector128.CreateScalar(T.Read(ref source)).AsByte()).AsInt16();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Store<T>(ref byte dest, Vector128<short> value) where T : struct, IPixelWidth
        {
            T.Write(ref dest, Vector128.Narrow(value.AsUInt16(), value.AsUInt16()).AsUInt64().ToScalar());
        }

        private static void WriteRow(ReadOnlySpan<byte> row, Span<byte> dest, int colorType, int sampleBytes, byte[] palette, byte[] transparency)
        {
            if (colorType == 3)
            {
                int channels = transparency != null ? 4 : 3;

                for (int x = 0; x < row.Length; x++)
                {
                    int index = row[x];
                    int o = x * channels;
                    int p = index * 3;

                    dest[o] = p + 2 < palette.Length ? palette[p] : (byte)0;
                    dest[o + 1] = p + 2 < palette.Length ? palette[p + 1] : (byte)0;
                    dest[o + 2] = p + 2 < palette.Length ? palette[p + 2] : (byte)0;

                    if (channels == 4)
                    {
                        dest[o + 3] = index < transparency.Length ? transparency[index] : (byte)255;
                    }
                }

                return;
            }

            if (sampleBytes == 1)
            {
                row.CopyTo(dest);
                return;
            }

            for (int i = 0; i < dest.Length; i++)
            {
                dest[i] = row[i * 2];
            }
        }
    }
}
