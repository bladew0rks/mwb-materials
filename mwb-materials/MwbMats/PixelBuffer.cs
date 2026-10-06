using System;
using System.Threading.Tasks;
using VtfNet.Processing;

namespace mwb_materials.MwbMats
{
    class PixelBuffer
    {
        public PixelBuffer(int width, int height)
            : this(width, height, new byte[checked(width * height * 4)])
        {
        }

        public PixelBuffer(int width, int height, byte[] bytes)
        {
            if (bytes.Length != width * height * 4)
            {
                throw new ArgumentException("Pixel buffer size does not match its dimensions.", nameof(bytes));
            }

            Width = width;
            Height = height;
            Bytes = bytes;
        }

        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Bytes { get; private set; }
        public int PixelCount => Width * Height;

        public void Resize(int width, int height)
        {
            Bytes = ImageResampler.Resize(Bytes, Width, Height, width, height, ResampleFilter.CatmullRom, EdgeMode.Mirror);
            Width = width;
            Height = height;
        }
    }

    class GrayBuffer
    {
        public GrayBuffer(int width, int height)
            : this(width, height, new byte[checked(width * height)])
        {
        }

        public GrayBuffer(int width, int height, byte[] bytes)
        {
            if (bytes.Length != width * height)
            {
                throw new ArgumentException("Gray buffer size does not match its dimensions.", nameof(bytes));
            }

            Width = width;
            Height = height;
            Bytes = bytes;
        }

        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Bytes { get; private set; }
        public int PixelCount => Width * Height;

        public void Resize(int width, int height)
        {
            Bytes = ImageResampler.Resize(Bytes, Width, Height, 1, width, height, ResampleFilter.CatmullRom, EdgeMode.Mirror);
            Width = width;
            Height = height;
        }
    }

    static class ParallelPixels
    {
        public const int ChunkPixels = 1 << 16;

        public static void For(int pixelCount, Action<int, int> body)
        {
            int chunks = (pixelCount + ChunkPixels - 1) / ChunkPixels;

            if (chunks <= 1)
            {
                body(0, pixelCount);
                return;
            }

            Parallel.For(0, chunks, chunk =>
            {
                int start = chunk * ChunkPixels;
                body(start, Math.Min(pixelCount, start + ChunkPixels));
            });
        }

        public static T[] Map<T>(int pixelCount, Func<int, int, T> body)
        {
            int chunks = Math.Max(1, (pixelCount + ChunkPixels - 1) / ChunkPixels);
            T[] results = new T[chunks];

            Parallel.For(0, chunks, chunk =>
            {
                int start = chunk * ChunkPixels;
                results[chunk] = body(start, Math.Min(pixelCount, start + ChunkPixels));
            });

            return results;
        }
    }
}
