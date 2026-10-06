using Pfim;
using StbImageSharp;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace mwb_materials.MwbMats
{
    sealed class SourceStats
    {
        public int Width;
        public int Height;
        public int GrayMin = 255;
        public int GrayMax;
        public long GraySum;
        public int AlphaMin = 255;
        public int AlphaMax;
        public long AlphaSum;

        public int Count => Width * Height;

        public void Merge(SourceStats other)
        {
            GrayMin = Math.Min(GrayMin, other.GrayMin);
            GrayMax = Math.Max(GrayMax, other.GrayMax);
            GraySum += other.GraySum;
            AlphaMin = Math.Min(AlphaMin, other.AlphaMin);
            AlphaMax = Math.Max(AlphaMax, other.AlphaMax);
            AlphaSum += other.AlphaSum;
        }
    }

    class ImageLoader
    {
        private static readonly string[] StbExtensions = new string[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".psd" };

        private enum Layout
        {
            Gray,
            GrayAlpha,
            Rgb,
            Bgr,
            Rgba,
            Bgra
        }

        private sealed class RawImage
        {
            public byte[] Data;
            public int Width;
            public int Height;
            public int Stride;
            public Layout Layout;

            public int Channels => Layout switch
            {
                Layout.Gray => 1,
                Layout.GrayAlpha => 2,
                Layout.Rgb or Layout.Bgr => 3,
                _ => 4
            };

            public bool? AlphaOverride;

            public bool HasAlpha => AlphaOverride ?? Layout is Layout.GrayAlpha or Layout.Rgba or Layout.Bgra;
        }

        public static PixelBuffer Load(string path)
        {
            return LoadRgba(path, out _, out _);
        }

        public static PixelBuffer LoadRgba(string path, out bool sourceHasAlpha, out SourceStats stats)
        {
            RawImage raw = Decode(path);
            sourceHasAlpha = raw.HasAlpha;
            PixelBuffer result = new PixelBuffer(raw.Width, raw.Height);
            stats = Convert(raw, result.Bytes, null);
            return result;
        }

        public static GrayBuffer LoadGray(string path, out SourceStats stats)
        {
            RawImage raw = Decode(path);
            GrayBuffer result = new GrayBuffer(raw.Width, raw.Height);
            stats = Convert(raw, null, result.Bytes);
            return result;
        }

        public static void SplitRgbm(PixelBuffer source, bool sourceHasAlpha, out PixelBuffer albedo, out GrayBuffer metalness)
        {
            metalness = sourceHasAlpha
                ? new GrayBuffer(source.Width, source.Height, PixelKernels.ExtractChannel(source.Bytes, 3))
                : new GrayBuffer(source.Width, source.Height);

            PixelKernels.FillChannel(source.Bytes, 3, 255);
            albedo = source;
        }

        private static RawImage Decode(string path)
        {
            if (IsPfimSupportedSource(path))
            {
                return DecodeWithPfim(path);
            }

            byte[] file = File.ReadAllBytes(path);
            PngDecoder.Result png = PngDecoder.TryDecode(file);

            if (png != null)
            {
                return new RawImage()
                {
                    Data = png.Data,
                    Width = png.Width,
                    Height = png.Height,
                    Stride = png.Width * png.Channels,
                    Layout = png.Channels switch { 1 => Layout.Gray, 2 => Layout.GrayAlpha, 3 => Layout.Rgb, _ => Layout.Rgba }
                };
            }

            ImageResult image = ImageResult.FromMemory(file, ColorComponents.Default);
            Layout layout = image.Comp switch
            {
                ColorComponents.Grey => Layout.Gray,
                ColorComponents.GreyAlpha => Layout.GrayAlpha,
                ColorComponents.RedGreenBlue => Layout.Rgb,
                _ => Layout.Rgba
            };

            RawImage raw = new RawImage() { Data = image.Data, Width = image.Width, Height = image.Height, Layout = layout };
            raw.Stride = raw.Width * raw.Channels;
            return raw;
        }

        private static RawImage DecodeWithPfim(string path)
        {
            using (IImage image = Pfimage.FromFile(path))
            {
                RawImage raw = new RawImage() { Data = image.Data, Width = image.Width, Height = image.Height, Stride = image.Stride };

                switch (image.Format)
                {
                    case Pfim.ImageFormat.Rgba32:
                        raw.Layout = Layout.Bgra;
                        return raw;
                    case Pfim.ImageFormat.Rgb24:
                        raw.Layout = Layout.Bgr;
                        return raw;
                    case Pfim.ImageFormat.Rgb8:
                        raw.Layout = Layout.Gray;
                        return raw;
                    case Pfim.ImageFormat.R5g6b5:
                        return Expand16Bit(raw, 11, 5, 5, 6, 0, 5, -1);
                    case Pfim.ImageFormat.R5g5b5:
                        return Expand16Bit(raw, 10, 5, 5, 5, 0, 5, -1);
                    case Pfim.ImageFormat.R5g5b5a1:
                        return Expand16Bit(raw, 10, 5, 5, 5, 0, 5, 15);
                    default:
                        throw new NotSupportedException(
                            "Unsupported source texture pixel format " + image.Format + " in " + Path.GetFileName(path) +
                            ". HDR/float formats (BC6H, R16F, R32F) and Rgba16 are not supported for material source textures.");
                }
            }
        }

        private static RawImage Expand16Bit(RawImage raw, int rShift, int rBits, int gShift, int gBits, int bShift, int bBits, int alphaBit)
        {
            byte[] rgba = new byte[raw.Width * raw.Height * 4];

            for (int y = 0; y < raw.Height; y++)
            {
                int src = y * raw.Stride;
                int dest = y * raw.Width * 4;

                for (int x = 0; x < raw.Width; x++, src += 2, dest += 4)
                {
                    int packed = raw.Data[src] | (raw.Data[src + 1] << 8);
                    rgba[dest] = ExpandBits(packed >> rShift, rBits);
                    rgba[dest + 1] = ExpandBits(packed >> gShift, gBits);
                    rgba[dest + 2] = ExpandBits(packed >> bShift, bBits);
                    rgba[dest + 3] = alphaBit < 0 || ((packed >> alphaBit) & 1) != 0 ? (byte)255 : (byte)0;
                }
            }

            return new RawImage() { Data = rgba, Width = raw.Width, Height = raw.Height, Stride = raw.Width * 4, Layout = Layout.Rgba, AlphaOverride = alphaBit >= 0 };
        }

        private static byte ExpandBits(int value, int bits)
        {
            int max = (1 << bits) - 1;
            return (byte)(((value & max) * 255 + max / 2) / max);
        }

        private static SourceStats Convert(RawImage raw, byte[] rgba, byte[] gray)
        {
            int width = raw.Width;
            int rowsPerChunk = Math.Max(1, ParallelPixels.ChunkPixels / Math.Max(1, width));
            int chunks = (raw.Height + rowsPerChunk - 1) / rowsPerChunk;
            SourceStats[] partials = new SourceStats[chunks];

            Parallel.For(0, chunks, chunk =>
            {
                SourceStats stats = new SourceStats();
                int grayMin = 255, grayMax = 0, alphaMin = 255, alphaMax = 0;
                long graySum = 0, alphaSum = 0;
                int yEnd = Math.Min(raw.Height, (chunk + 1) * rowsPerChunk);
                byte[] data = raw.Data;
                int channels = raw.Channels;
                bool bgr = raw.Layout is Layout.Bgr or Layout.Bgra;

                for (int y = chunk * rowsPerChunk; y < yEnd; y++)
                {
                    int src = y * raw.Stride;
                    int pixel = y * width;

                    for (int x = 0; x < width; x++, src += channels, pixel++)
                    {
                        int r, g, b, a;

                        switch (raw.Layout)
                        {
                            case Layout.Gray:
                                r = g = b = data[src];
                                a = 255;
                                break;
                            case Layout.GrayAlpha:
                                r = g = b = data[src];
                                a = data[src + 1];
                                break;
                            case Layout.Rgb:
                            case Layout.Bgr:
                                r = data[src + (bgr ? 2 : 0)];
                                g = data[src + 1];
                                b = data[src + (bgr ? 0 : 2)];
                                a = 255;
                                break;
                            default:
                                r = data[src + (bgr ? 2 : 0)];
                                g = data[src + 1];
                                b = data[src + (bgr ? 0 : 2)];
                                a = data[src + 3];
                                break;
                        }

                        int value = (r + g + b) / 3;
                        grayMin = Math.Min(grayMin, value);
                        grayMax = Math.Max(grayMax, value);
                        graySum += value;
                        alphaMin = Math.Min(alphaMin, a);
                        alphaMax = Math.Max(alphaMax, a);
                        alphaSum += a;

                        if (gray != null)
                        {
                            gray[pixel] = (byte)value;
                        }

                        if (rgba != null)
                        {
                            int o = pixel * 4;
                            rgba[o] = (byte)r;
                            rgba[o + 1] = (byte)g;
                            rgba[o + 2] = (byte)b;
                            rgba[o + 3] = (byte)a;
                        }
                    }
                }

                stats.GrayMin = grayMin;
                stats.GrayMax = grayMax;
                stats.GraySum = graySum;
                stats.AlphaMin = alphaMin;
                stats.AlphaMax = alphaMax;
                stats.AlphaSum = alphaSum;
                partials[chunk] = stats;
            });

            SourceStats total = new SourceStats() { Width = raw.Width, Height = raw.Height };

            foreach (SourceStats partial in partials)
            {
                total.Merge(partial);
            }

            return total;
        }

        public static bool IsDds(string path)
        {
            return Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTga(string path)
        {
            return Path.GetExtension(path).Equals(".tga", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPfimSupportedSource(string path)
        {
            return IsDds(path) || IsTga(path);
        }

        public static bool HasSupportedExtension(string path)
        {
            string extension = Path.GetExtension(path);
            return IsPfimSupportedSource(path) || StbExtensions.Any(candidate => candidate.Equals(extension, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsSupportedImage(string path)
        {
            if (IsPfimSupportedSource(path))
            {
                return true;
            }

            if (!HasSupportedExtension(path))
            {
                return false;
            }

            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    return ImageInfo.FromStream(stream) != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
