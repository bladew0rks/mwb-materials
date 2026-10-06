using System.Runtime.CompilerServices;

namespace VtfNet.Processing;

public enum ResampleFilter
{
    Point,
    Box,
    Triangle,
    CatmullRom,
    Mitchell,
    Lanczos3,
}

public enum EdgeMode
{
    Clamp,
    Mirror,
    Wrap,
}

public static class ImageResampler
{
    private const int ParallelThresholdOps = 1 << 16;

    public static byte[] Resize(ReadOnlySpan<byte> rgba, int srcWidth, int srcHeight, int dstWidth, int dstHeight,
        ResampleFilter filter, EdgeMode edge = EdgeMode.Clamp)
    {
        return Resize(rgba[..Math.Min(rgba.Length, srcWidth * srcHeight * 4)].ToArray(), srcWidth, srcHeight, dstWidth, dstHeight, filter, edge);
    }

    public static byte[] Resize(byte[] rgba, int srcWidth, int srcHeight, int dstWidth, int dstHeight,
        ResampleFilter filter, EdgeMode edge = EdgeMode.Clamp)
    {
        return Resize(rgba, srcWidth, srcHeight, 4, dstWidth, dstHeight, filter, edge);
    }

    public static byte[] Resize(byte[] source, int srcWidth, int srcHeight, int channels, int dstWidth, int dstHeight,
        ResampleFilter filter, EdgeMode edge = EdgeMode.Clamp)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "Image dimensions must be positive.");
        }

        if (channels < 1 || channels > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (source.Length < srcWidth * srcHeight * channels)
        {
            throw new ArgumentException("Source buffer is smaller than width * height * channels.", nameof(source));
        }

        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            return source.AsSpan(0, srcWidth * srcHeight * channels).ToArray();
        }

        if (filter == ResampleFilter.Box && dstWidth * 2 == srcWidth && dstHeight * 2 == srcHeight)
        {
            return HalveBox(source, srcWidth, srcHeight, channels);
        }

        return ResizeCore<byte, byte>(source, srcWidth, srcHeight, channels, dstWidth, dstHeight, filter, edge);
    }

    public static byte[] ResizeAlphaWeighted(byte[] rgba, int srcWidth, int srcHeight, int dstWidth, int dstHeight,
        ResampleFilter filter, EdgeMode edge = EdgeMode.Clamp)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "Image dimensions must be positive.");
        }

        if (rgba.Length < srcWidth * srcHeight * 4)
        {
            throw new ArgumentException("Source buffer is smaller than width * height * 4.", nameof(rgba));
        }

        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            return rgba.AsSpan(0, srcWidth * srcHeight * 4).ToArray();
        }

        if (filter == ResampleFilter.Box && dstWidth * 2 == srcWidth && dstHeight * 2 == srcHeight)
        {
            return HalveBoxAlphaWeighted(rgba, srcWidth, srcHeight);
        }

        int sourcePixels = srcWidth * srcHeight;
        float[] premultiplied = new float[sourcePixels * 4];

        For(srcHeight, sourcePixels, y =>
        {
            for (int i = y * srcWidth * 4, end = i + srcWidth * 4; i < end; i += 4)
            {
                float alpha = rgba[i + 3];
                premultiplied[i] = rgba[i] * alpha;
                premultiplied[i + 1] = rgba[i + 1] * alpha;
                premultiplied[i + 2] = rgba[i + 2] * alpha;
                premultiplied[i + 3] = alpha;
            }
        });

        float[] weighted = ResizeCore<float, float>(premultiplied, srcWidth, srcHeight, 4, dstWidth, dstHeight, filter, edge);
        byte[] output = ResizeCore<byte, byte>(rgba, srcWidth, srcHeight, 4, dstWidth, dstHeight, filter, edge);

        For(dstHeight, (long)dstWidth * dstHeight, y =>
        {
            for (int i = y * dstWidth * 4, end = i + dstWidth * 4; i < end; i += 4)
            {
                float alpha = weighted[i + 3];

                if (alpha > 0.5f)
                {
                    output[i] = (byte)Math.Clamp((int)(weighted[i] / alpha + 0.5f), 0, 255);
                    output[i + 1] = (byte)Math.Clamp((int)(weighted[i + 1] / alpha + 0.5f), 0, 255);
                    output[i + 2] = (byte)Math.Clamp((int)(weighted[i + 2] / alpha + 0.5f), 0, 255);
                }
            }
        });

        return output;
    }

    private static TOut[] ResizeCore<TIn, TOut>(TIn[] source, int srcWidth, int srcHeight, int channels, int dstWidth, int dstHeight,
        ResampleFilter filter, EdgeMode edge)
    {
        Contributions horizontal = Contributions.Build(srcWidth, dstWidth, filter, edge);
        Contributions vertical = Contributions.Build(srcHeight, dstHeight, filter, edge);
        TOut[] output = new TOut[dstWidth * dstHeight * channels];

        if ((long)dstWidth * srcHeight <= (long)srcWidth * dstHeight)
        {
            float[] temp = new float[dstWidth * srcHeight * channels];

            For(srcHeight, (long)srcHeight * horizontal.Taps, y => HorizontalPass(source, y * srcWidth, temp, y * dstWidth, horizontal, channels));
            For(dstHeight, (long)dstWidth * vertical.Taps, y => VerticalPass(temp, dstWidth, vertical, y, output, channels));
        }
        else
        {
            float[] temp = new float[srcWidth * dstHeight * channels];

            For(dstHeight, (long)srcWidth * vertical.Taps, y => VerticalPass(source, srcWidth, vertical, y, temp, channels));
            For(dstHeight, (long)dstHeight * horizontal.Taps, y => HorizontalPass(temp, y * srcWidth, output, y * dstWidth, horizontal, channels));
        }

        return output;
    }

    private static void For(int count, long work, Action<int> body)
    {
        if (work >= ParallelThresholdOps && count > 1)
        {
            Parallel.For(0, count, body);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                body(i);
            }
        }
    }

    private static byte[] HalveBox(byte[] source, int srcWidth, int srcHeight, int channels)
    {
        int dstWidth = srcWidth / 2;
        int dstHeight = srcHeight / 2;
        byte[] output = new byte[dstWidth * dstHeight * channels];
        int srcStride = srcWidth * channels;

        For(dstHeight, (long)dstWidth * 4, y =>
        {
            int row0 = y * 2 * srcStride;
            int row1 = row0 + srcStride;
            int o = y * dstWidth * channels;

            for (int x = 0; x < dstWidth; x++)
            {
                int i = x * 2 * channels;

                for (int c = 0; c < channels; c++, o++)
                {
                    int sum = source[row0 + i + c] + source[row0 + i + channels + c] + source[row1 + i + c] + source[row1 + i + channels + c];
                    output[o] = (byte)((sum + 2) >> 2);
                }
            }
        });

        return output;
    }

    private static byte[] HalveBoxAlphaWeighted(byte[] source, int srcWidth, int srcHeight)
    {
        int dstWidth = srcWidth / 2;
        int dstHeight = srcHeight / 2;
        byte[] output = new byte[dstWidth * dstHeight * 4];
        int srcStride = srcWidth * 4;

        For(dstHeight, (long)dstWidth * 4, y =>
        {
            int row0 = y * 2 * srcStride;
            int row1 = row0 + srcStride;
            int o = y * dstWidth * 4;

            for (int x = 0; x < dstWidth; x++, o += 4)
            {
                int p0 = row0 + x * 8, p1 = p0 + 4, p2 = row1 + x * 8, p3 = p2 + 4;
                int a0 = source[p0 + 3], a1 = source[p1 + 3], a2 = source[p2 + 3], a3 = source[p3 + 3];
                int alpha = a0 + a1 + a2 + a3;

                for (int c = 0; c < 3; c++)
                {
                    output[o + c] = alpha == 0
                        ? (byte)((source[p0 + c] + source[p1 + c] + source[p2 + c] + source[p3 + c] + 2) >> 2)
                        : (byte)((source[p0 + c] * a0 + source[p1 + c] * a1 + source[p2 + c] * a2 + source[p3 + c] * a3 + alpha / 2) / alpha);
                }

                output[o + 3] = (byte)((alpha + 2) >> 2);
            }
        });

        return output;
    }

    private static void HorizontalPass<TIn, TOut>(TIn[] source, int sourcePixel, TOut[] dest, int destPixel, Contributions contributions, int channels)
    {
        Span<float> acc = stackalloc float[4];

        for (int x = 0; x < contributions.Count; x++)
        {
            acc.Clear();
            int end = contributions.Starts[x + 1];

            for (int k = contributions.Starts[x]; k < end; k++)
            {
                int offset = (sourcePixel + contributions.Indices[k]) * channels;
                float w = contributions.Weights[k];

                for (int c = 0; c < channels; c++)
                {
                    acc[c] += Read(source, offset + c) * w;
                }
            }

            int o = (destPixel + x) * channels;

            for (int c = 0; c < channels; c++)
            {
                Write(dest, o + c, acc[c]);
            }
        }
    }

    private static void VerticalPass<TIn, TOut>(TIn[] source, int width, Contributions vertical, int y, TOut[] dest, int channels)
    {
        Span<float> acc = stackalloc float[4];
        int start = vertical.Starts[y];
        int end = vertical.Starts[y + 1];
        int rowOffset = y * width * channels;

        for (int x = 0; x < width; x++)
        {
            acc.Clear();

            for (int k = start; k < end; k++)
            {
                int offset = (vertical.Indices[k] * width + x) * channels;
                float w = vertical.Weights[k];

                for (int c = 0; c < channels; c++)
                {
                    acc[c] += Read(source, offset + c) * w;
                }
            }

            for (int c = 0; c < channels; c++)
            {
                Write(dest, rowOffset + x * channels + c, acc[c]);
            }
        }
    }

    private static float Read<T>(T[] buffer, int index)
    {
        if (typeof(T) == typeof(byte))
        {
            return Unsafe.As<byte[]>(buffer)[index];
        }

        return Unsafe.As<float[]>(buffer)[index];
    }

    private static void Write<T>(T[] buffer, int index, float value)
    {
        if (typeof(T) == typeof(byte))
        {
            Unsafe.As<byte[]>(buffer)[index] = (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
        }
        else
        {
            Unsafe.As<float[]>(buffer)[index] = value;
        }
    }

    private static float Support(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Point => 0.5f,
        ResampleFilter.Box => 0.5f,
        ResampleFilter.Triangle => 1f,
        ResampleFilter.CatmullRom => 2f,
        ResampleFilter.Mitchell => 2f,
        ResampleFilter.Lanczos3 => 3f,
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };

    private static float Evaluate(ResampleFilter filter, float x)
    {
        switch (filter)
        {
            case ResampleFilter.Point:
            case ResampleFilter.Box:
                return x >= -0.5f && x < 0.5f ? 1f : 0f;
            case ResampleFilter.Triangle:
                return MathF.Max(0f, 1f - MathF.Abs(x));
            case ResampleFilter.CatmullRom:
                return Cubic(x, 0f, 0.5f);
            case ResampleFilter.Mitchell:
                return Cubic(x, 1f / 3f, 1f / 3f);
            case ResampleFilter.Lanczos3:
                x = MathF.Abs(x);

                if (x < 1e-6f)
                {
                    return 1f;
                }

                if (x >= 3f)
                {
                    return 0f;
                }

                float px = MathF.PI * x;
                return 3f * MathF.Sin(px) * MathF.Sin(px / 3f) / (px * px);
            default:
                throw new ArgumentOutOfRangeException(nameof(filter));
        }
    }

    private static float Cubic(float x, float b, float c)
    {
        x = MathF.Abs(x);

        if (x < 1f)
        {
            return ((12f - 9f * b - 6f * c) * x * x * x + (-18f + 12f * b + 6f * c) * x * x + (6f - 2f * b)) / 6f;
        }

        if (x < 2f)
        {
            return ((-b - 6f * c) * x * x * x + (6f * b + 30f * c) * x * x + (-12f * b - 48f * c) * x + (8f * b + 24f * c)) / 6f;
        }

        return 0f;
    }

    private static int ResolveEdge(int index, int size, EdgeMode edge)
    {
        if (index >= 0 && index < size)
        {
            return index;
        }

        switch (edge)
        {
            case EdgeMode.Wrap:
                return ((index % size) + size) % size;
            case EdgeMode.Mirror:
                int period = size * 2;
                int m = ((index % period) + period) % period;
                return m < size ? m : period - 1 - m;
            default:
                return Math.Clamp(index, 0, size - 1);
        }
    }

    private sealed class Contributions
    {
        public required int Count { get; init; }
        public required int[] Starts { get; init; }
        public required int[] Indices { get; init; }
        public required float[] Weights { get; init; }

        public int Taps => Indices.Length;

        public static Contributions Build(int srcSize, int dstSize, ResampleFilter filter, EdgeMode edge)
        {
            float scale = (float)dstSize / srcSize;
            float filterScale = filter == ResampleFilter.Point ? 1f : MathF.Max(1f, 1f / scale);
            float support = Support(filter) * filterScale;

            int[] starts = new int[dstSize + 1];
            List<int> indices = new List<int>(dstSize * (int)MathF.Ceiling(support * 2 + 1));
            List<float> weights = new List<float>(indices.Capacity);

            for (int i = 0; i < dstSize; i++)
            {
                starts[i] = indices.Count;
                float center = (i + 0.5f) / scale;
                int left = (int)MathF.Floor(center - support);
                int right = (int)MathF.Ceiling(center + support);
                float total = 0f;
                int first = indices.Count;

                for (int j = left; j <= right; j++)
                {
                    float w = Evaluate(filter, (j + 0.5f - center) / filterScale);

                    if (w == 0f)
                    {
                        continue;
                    }

                    indices.Add(ResolveEdge(j, srcSize, edge));
                    weights.Add(w);
                    total += w;
                }

                if (indices.Count == first || MathF.Abs(total) < 1e-8f)
                {
                    indices.RemoveRange(first, indices.Count - first);
                    weights.RemoveRange(first, weights.Count - first);
                    indices.Add(Math.Clamp((int)center, 0, srcSize - 1));
                    weights.Add(1f);
                }
                else
                {
                    for (int k = first; k < weights.Count; k++)
                    {
                        weights[k] /= total;
                    }
                }
            }

            starts[dstSize] = indices.Count;

            return new Contributions
            {
                Count = dstSize,
                Starts = starts,
                Indices = indices.ToArray(),
                Weights = weights.ToArray(),
            };
        }
    }
}
