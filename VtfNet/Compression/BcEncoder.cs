using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace VtfNet.Compression;

public static class BcEncoder
{
    private const int ParallelThresholdBlocks = 1024;

    private static readonly byte[] SingleColor5 = BuildSingleColorTable(5);
    private static readonly byte[] SingleColor6 = BuildSingleColorTable(6);

    public static int GetCompressedSize(int width, int height, int blockSize)
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);
        return blocksX * blocksY * blockSize;
    }

    public static byte[] EncodeBc1(ReadOnlySpan<byte> rgba, int width, int height, int alphaThreshold = 0, bool parallel = true)
    {
        byte[] output = new byte[GetCompressedSize(width, height, 8)];
        EncodeBlocks<Bc1Block>(rgba, width, height, output, 0, alphaThreshold, parallel, BcEncoderSimd.IsSupported);
        return output;
    }

    public static byte[] EncodeBc2(ReadOnlySpan<byte> rgba, int width, int height, bool parallel = true)
    {
        byte[] output = new byte[GetCompressedSize(width, height, 16)];
        EncodeBlocks<Bc2Block>(rgba, width, height, output, 0, 0, parallel, BcEncoderSimd.IsSupported);
        return output;
    }

    public static byte[] EncodeBc3(ReadOnlySpan<byte> rgba, int width, int height, bool parallel = true)
    {
        byte[] output = new byte[GetCompressedSize(width, height, 16)];
        EncodeBlocks<Bc3Block>(rgba, width, height, output, 0, 0, parallel, BcEncoderSimd.IsSupported);
        return output;
    }

    public static byte[] EncodeBc4(ReadOnlySpan<byte> rgba, int width, int height, bool parallel = true)
    {
        byte[] output = new byte[GetCompressedSize(width, height, 8)];
        EncodeBlocks<Bc4Block>(rgba, width, height, output, 0, 0, parallel, BcEncoderSimd.IsSupported);
        return output;
    }

    public static byte[] EncodeBc5(ReadOnlySpan<byte> rgba, int width, int height, bool parallel = true)
    {
        byte[] output = new byte[GetCompressedSize(width, height, 16)];
        EncodeBlocks<Bc5Block>(rgba, width, height, output, 0, 0, parallel, BcEncoderSimd.IsSupported);
        return output;
    }

    public static void Encode(VtfImageFormat format, ReadOnlySpan<byte> rgba, int width, int height, byte[] output, int outputOffset, int alphaThreshold = 0, bool parallel = true)
    {
        bool simd = BcEncoderSimd.IsSupported;

        switch (format)
        {
            case VtfImageFormat.DXT1:
                EncodeBlocks<Bc1Block>(rgba, width, height, output, outputOffset, 0, parallel, simd);
                break;
            case VtfImageFormat.DXT1OneBitAlpha:
                EncodeBlocks<Bc1Block>(rgba, width, height, output, outputOffset, Math.Max(1, alphaThreshold), parallel, simd);
                break;
            case VtfImageFormat.DXT3:
                EncodeBlocks<Bc2Block>(rgba, width, height, output, outputOffset, 0, parallel, simd);
                break;
            case VtfImageFormat.DXT5:
                EncodeBlocks<Bc3Block>(rgba, width, height, output, outputOffset, 0, parallel, simd);
                break;
            case VtfImageFormat.ATI1N:
                EncodeBlocks<Bc4Block>(rgba, width, height, output, outputOffset, 0, parallel, simd);
                break;
            case VtfImageFormat.ATI2N:
                EncodeBlocks<Bc5Block>(rgba, width, height, output, outputOffset, 0, parallel, simd);
                break;
            default:
                throw new ArgumentException("Not a block-compressed format: " + format, nameof(format));
        }
    }

    private interface IBlockEncoder
    {
        static abstract int BlockSize { get; }
        static abstract void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold);

        static abstract void EncodeBatch(ReadOnlySpan<byte> blocks, ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g,
            ReadOnlySpan<Vector256<int>> b, ReadOnlySpan<Vector256<int>> a, int count, Span<byte> dest, int alphaThreshold);
    }

    private struct Bc1Block : IBlockEncoder
    {
        public static int BlockSize => 8;
        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold) => CompressColorBlock(block, dest, false, alphaThreshold);

        public static void EncodeBatch(ReadOnlySpan<byte> blocks, ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g,
            ReadOnlySpan<Vector256<int>> b, ReadOnlySpan<Vector256<int>> a, int count, Span<byte> dest, int alphaThreshold)
        {
            if (alphaThreshold > 0)
            {
                for (int lane = 0; lane < count; lane++)
                {
                    Encode(blocks.Slice(lane * 64, 64), dest.Slice(lane * 8, 8), alphaThreshold);
                }

                return;
            }

            BcEncoderSimd.WriteColorBlocks(blocks, r, g, b, count, dest, 8);
        }
    }

    private struct Bc2Block : IBlockEncoder
    {
        public static int BlockSize => 16;

        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold)
        {
            CompressExplicitAlphaBlock(block, dest[..8]);
            CompressColorBlock(block, dest[8..], true, 0);
        }

        public static void EncodeBatch(ReadOnlySpan<byte> blocks, ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g,
            ReadOnlySpan<Vector256<int>> b, ReadOnlySpan<Vector256<int>> a, int count, Span<byte> dest, int alphaThreshold)
        {
            for (int lane = 0; lane < count; lane++)
            {
                CompressExplicitAlphaBlock(blocks.Slice(lane * 64, 64), dest.Slice(lane * 16, 8));
            }

            BcEncoderSimd.WriteColorBlocks(blocks, r, g, b, count, dest[8..], 16);
        }
    }

    private struct Bc3Block : IBlockEncoder
    {
        public static int BlockSize => 16;

        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold)
        {
            CompressSingleChannelBlock(block, 3, dest[..8]);
            CompressColorBlock(block, dest[8..], true, 0);
        }

        public static void EncodeBatch(ReadOnlySpan<byte> blocks, ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g,
            ReadOnlySpan<Vector256<int>> b, ReadOnlySpan<Vector256<int>> a, int count, Span<byte> dest, int alphaThreshold)
        {
            BcEncoderSimd.EncodeSingleChannel(a, count, dest, 16);
            BcEncoderSimd.WriteColorBlocks(blocks, r, g, b, count, dest[8..], 16);
        }
    }

    private struct Bc4Block : IBlockEncoder
    {
        public static int BlockSize => 8;
        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold) => CompressSingleChannelBlock(block, 0, dest);

        public static void EncodeBatch(ReadOnlySpan<byte> blocks, ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g,
            ReadOnlySpan<Vector256<int>> b, ReadOnlySpan<Vector256<int>> a, int count, Span<byte> dest, int alphaThreshold)
        {
            BcEncoderSimd.EncodeSingleChannel(r, count, dest, 8);
        }
    }

    private struct Bc5Block : IBlockEncoder
    {
        public static int BlockSize => 16;

        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold)
        {
            CompressSingleChannelBlock(block, 0, dest[..8]);
            CompressSingleChannelBlock(block, 1, dest[8..]);
        }

        public static void EncodeBatch(ReadOnlySpan<byte> blocks, ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g,
            ReadOnlySpan<Vector256<int>> b, ReadOnlySpan<Vector256<int>> a, int count, Span<byte> dest, int alphaThreshold)
        {
            BcEncoderSimd.EncodeSingleChannel(r, count, dest, 16);
            BcEncoderSimd.EncodeSingleChannel(g, count, dest[8..], 16);
        }
    }

    private static unsafe void EncodeBlocks<T>(ReadOnlySpan<byte> rgba, int width, int height, byte[] output, int outputOffset, int alphaThreshold, bool parallel, bool simd)
        where T : struct, IBlockEncoder
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("Source buffer is smaller than width * height * 4.", nameof(rgba));
        }

        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);
        int blockSize = T.BlockSize;

        if (outputOffset < 0 || outputOffset + blocksX * blocksY * blockSize > output.Length)
        {
            throw new ArgumentException("Output buffer is too small.", nameof(output));
        }

        fixed (byte* sourcePtr = rgba)
        {
            nint source = (nint)sourcePtr;
            int sourceLength = rgba.Length;

            void EncodeRow(int by)
            {
                ReadOnlySpan<byte> src = new ReadOnlySpan<byte>((void*)source, sourceLength);
                Span<byte> row = output.AsSpan(outputOffset + by * blocksX * blockSize, blocksX * blockSize);

                if (simd)
                {
                    EncodeRowSimd<T>(src, width, height, by, blocksX, row, alphaThreshold);
                    return;
                }

                Span<byte> block = stackalloc byte[64];

                for (int bx = 0; bx < blocksX; bx++)
                {
                    GatherBlock(src, width, height, bx * 4, by * 4, block);
                    T.Encode(block, row.Slice(bx * blockSize, blockSize), alphaThreshold);
                }
            }

            if (parallel && blocksX * blocksY >= ParallelThresholdBlocks)
            {
                Parallel.For(0, blocksY, EncodeRow);
            }
            else
            {
                for (int by = 0; by < blocksY; by++)
                {
                    EncodeRow(by);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeRowSimd<T>(ReadOnlySpan<byte> src, int width, int height, int by, int blocksX, Span<byte> row, int alphaThreshold)
        where T : struct, IBlockEncoder
    {
        const int Lanes = BcEncoderSimd.Lanes;
        Span<byte> blocks = stackalloc byte[64 * Lanes];
        Span<Vector256<int>> r = stackalloc Vector256<int>[16];
        Span<Vector256<int>> g = stackalloc Vector256<int>[16];
        Span<Vector256<int>> b = stackalloc Vector256<int>[16];
        Span<Vector256<int>> a = stackalloc Vector256<int>[16];

        for (int bx = 0; bx < blocksX; bx += Lanes)
        {
            int count = Math.Min(Lanes, blocksX - bx);

            for (int lane = 0; lane < Lanes; lane++)
            {
                GatherBlock(src, width, height, (bx + Math.Min(lane, count - 1)) * 4, by * 4, blocks.Slice(lane * 64, 64));
            }

            BcEncoderSimd.Load(blocks, r, g, b, a);
            T.EncodeBatch(blocks, r, g, b, a, count, row[(bx * T.BlockSize)..], alphaThreshold);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GatherBlock(ReadOnlySpan<byte> src, int width, int height, int x0, int y0, Span<byte> block)
    {
        for (int y = 0; y < 4; y++)
        {
            int sy = Math.Min(y0 + y, height - 1);

            for (int x = 0; x < 4; x++)
            {
                int sx = Math.Min(x0 + x, width - 1);
                src.Slice((sy * width + sx) * 4, 4).CopyTo(block.Slice((y * 4 + x) * 4, 4));
            }
        }
    }

    #region Color (BC1)

    internal static void CompressColorBlock(ReadOnlySpan<byte> block, Span<byte> dest, bool forceFourColor, int alphaThreshold)
    {
        Span<int> colors = stackalloc int[48];
        Span<bool> transparent = stackalloc bool[16];
        bool anyTransparent = false;
        bool allTransparent = true;

        for (int i = 0; i < 16; i++)
        {
            colors[i * 3] = block[i * 4];
            colors[i * 3 + 1] = block[i * 4 + 1];
            colors[i * 3 + 2] = block[i * 4 + 2];
            transparent[i] = !forceFourColor && alphaThreshold > 0 && block[i * 4 + 3] < alphaThreshold;
            anyTransparent |= transparent[i];
            allTransparent &= transparent[i];
        }

        if (allTransparent)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], 0xFFFFFFFF);
            return;
        }

        if (anyTransparent)
        {
            CompressThreeColorBlock(colors, transparent, dest);
            return;
        }

        if (IsSolidColor(colors))
        {
            WriteSolidColorBlock(colors[0], colors[1], colors[2], dest);
            return;
        }

        Span<int> endpoints = stackalloc int[6];
        Span<int> bestEndpoints = stackalloc int[6];
        Span<byte> indices = stackalloc byte[16];
        Span<byte> bestIndices = stackalloc byte[16];
        Span<int> refined = stackalloc int[6];
        Span<byte> refinedIndices = stackalloc byte[16];
        long bestError = long.MaxValue;

        for (int candidate = 0; candidate < 3; candidate++)
        {
            if (candidate == 1)
            {
                FindBoundingBoxEndpoints(colors, endpoints);
            }
            else
            {
                FindPrincipalEndpoints(colors, endpoints);

                if (candidate == 2)
                {
                    ExtrapolateEndpoints(endpoints);
                }
            }

            QuantizeEndpoints(endpoints);
            long error = MatchFourColor(colors, endpoints, indices);

            for (int iteration = 0; iteration < 3; iteration++)
            {
                if (!RefineFourColor(colors, indices, refined))
                {
                    break;
                }

                QuantizeEndpoints(refined);
                long refinedError = MatchFourColor(colors, refined, refinedIndices);

                if (refinedError >= error)
                {
                    break;
                }

                refined.CopyTo(endpoints);
                refinedIndices.CopyTo(indices);
                error = refinedError;
            }

            if (error < bestError)
            {
                bestError = error;
                endpoints.CopyTo(bestEndpoints);
                indices.CopyTo(bestIndices);
            }
        }

        SearchColorEndpoints(colors, bestEndpoints, bestIndices, ref bestError);
        WriteFourColorBlock(bestEndpoints, bestIndices, dest);
    }

    internal const int EndpointSearchRounds = 4;

    private static void SearchColorEndpoints(ReadOnlySpan<int> colors, Span<int> endpoints, Span<byte> indices, ref long error)
    {
        Span<int> trial = stackalloc int[6];
        Span<byte> trialIndices = stackalloc byte[16];

        for (int round = 0; round < EndpointSearchRounds && error > 0; round++)
        {
            bool improved = false;

            for (int e = 0; e < 6; e++)
            {
                int bits = e % 3 == 1 ? 6 : 5;
                int max = (1 << bits) - 1;

                for (int delta = -1; delta <= 1; delta += 2)
                {
                    int level = Quantize(endpoints[e], bits) + delta;

                    if (level < 0 || level > max)
                    {
                        continue;
                    }

                    endpoints.CopyTo(trial);
                    trial[e] = bits == 6 ? Expand6(level) : Expand5(level);
                    long trialError = MatchFourColor(colors, trial, trialIndices);

                    if (trialError < error)
                    {
                        error = trialError;
                        trial.CopyTo(endpoints);
                        trialIndices.CopyTo(indices);
                        improved = true;
                    }
                }
            }

            if (!improved)
            {
                break;
            }
        }
    }

    internal static bool TryWriteSolidColorBlock(ReadOnlySpan<byte> block, Span<byte> dest)
    {
        for (int i = 1; i < 16; i++)
        {
            if (block[i * 4] != block[0] || block[i * 4 + 1] != block[1] || block[i * 4 + 2] != block[2])
            {
                return false;
            }
        }

        WriteSolidColorBlock(block[0], block[1], block[2], dest);
        return true;
    }

    private static bool IsSolidColor(ReadOnlySpan<int> colors)
    {
        for (int i = 1; i < 16; i++)
        {
            if (colors[i * 3] != colors[0] || colors[i * 3 + 1] != colors[1] || colors[i * 3 + 2] != colors[2])
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteSolidColorBlock(int r, int g, int b, Span<byte> dest)
    {
        int c0 = (SingleColor5[r * 2] << 11) | (SingleColor6[g * 2] << 5) | SingleColor5[b * 2];
        int c1 = (SingleColor5[r * 2 + 1] << 11) | (SingleColor6[g * 2 + 1] << 5) | SingleColor5[b * 2 + 1];
        uint mask = 0xAAAAAAAA;

        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
            mask = 0xFFFFFFFF;
        }
        else if (c0 == c1)
        {
            mask = 0;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], (ushort)c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], mask);
    }

    private static void FindPrincipalEndpoints(ReadOnlySpan<int> colors, Span<int> endpoints)
    {
        float meanR = 0, meanG = 0, meanB = 0;

        for (int i = 0; i < 16; i++)
        {
            meanR += colors[i * 3];
            meanG += colors[i * 3 + 1];
            meanB += colors[i * 3 + 2];
        }

        meanR /= 16f;
        meanG /= 16f;
        meanB /= 16f;

        float rr = 0, rg = 0, rb = 0, gg = 0, gb = 0, bb = 0;

        for (int i = 0; i < 16; i++)
        {
            float r = colors[i * 3] - meanR;
            float g = colors[i * 3 + 1] - meanG;
            float b = colors[i * 3 + 2] - meanB;
            rr += r * r;
            rg += r * g;
            rb += r * b;
            gg += g * g;
            gb += g * b;
            bb += b * b;
        }

        float vr = rr, vg = gg, vb = bb;

        if (vr + vg + vb <= 0f)
        {
            vr = vg = vb = 1f;
        }

        for (int iteration = 0; iteration < 8; iteration++)
        {
            float nr = vr * rr + vg * rg + vb * rb;
            float ng = vr * rg + vg * gg + vb * gb;
            float nb = vr * rb + vg * gb + vb * bb;
            float length = MathF.Max(MathF.Max(MathF.Abs(nr), MathF.Abs(ng)), MathF.Abs(nb));

            if (length < 1e-6f)
            {
                break;
            }

            vr = nr / length;
            vg = ng / length;
            vb = nb / length;
        }

        int minIndex = 0, maxIndex = 0;
        float minDot = float.MaxValue, maxDot = float.MinValue;

        for (int i = 0; i < 16; i++)
        {
            float dot = colors[i * 3] * vr + colors[i * 3 + 1] * vg + colors[i * 3 + 2] * vb;

            if (dot < minDot)
            {
                minDot = dot;
                minIndex = i;
            }

            if (dot > maxDot)
            {
                maxDot = dot;
                maxIndex = i;
            }
        }

        endpoints[0] = colors[maxIndex * 3];
        endpoints[1] = colors[maxIndex * 3 + 1];
        endpoints[2] = colors[maxIndex * 3 + 2];
        endpoints[3] = colors[minIndex * 3];
        endpoints[4] = colors[minIndex * 3 + 1];
        endpoints[5] = colors[minIndex * 3 + 2];
    }

    private static void ExtrapolateEndpoints(Span<int> endpoints)
    {
        for (int c = 0; c < 3; c++)
        {
            int high = endpoints[c], low = endpoints[3 + c];
            endpoints[c] = Math.Clamp(2 * high - low, 0, 255);
            endpoints[3 + c] = Math.Clamp(2 * low - high, 0, 255);
        }
    }

    private static void FindBoundingBoxEndpoints(ReadOnlySpan<int> colors, Span<int> endpoints)
    {
        for (int c = 0; c < 3; c++)
        {
            int min = 255, max = 0;

            for (int i = 0; i < 16; i++)
            {
                min = Math.Min(min, colors[i * 3 + c]);
                max = Math.Max(max, colors[i * 3 + c]);
            }

            int inset = (max - min) >> 4;
            endpoints[c] = max - inset;
            endpoints[3 + c] = min + inset;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quantize(int value, int bits)
    {
        int max = (1 << bits) - 1;
        return Math.Clamp((value * max + 127) / 255, 0, max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Expand5(int value) => (value << 3) | (value >> 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Expand6(int value) => (value << 2) | (value >> 4);

    private static void QuantizeEndpoints(Span<int> endpoints)
    {
        for (int e = 0; e < 2; e++)
        {
            endpoints[e * 3] = Expand5(Quantize(Math.Clamp(endpoints[e * 3], 0, 255), 5));
            endpoints[e * 3 + 1] = Expand6(Quantize(Math.Clamp(endpoints[e * 3 + 1], 0, 255), 6));
            endpoints[e * 3 + 2] = Expand5(Quantize(Math.Clamp(endpoints[e * 3 + 2], 0, 255), 5));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pack565(int r, int g, int b)
    {
        return (Quantize(r, 5) << 11) | (Quantize(g, 6) << 5) | Quantize(b, 5);
    }

    private static long MatchFourColor(ReadOnlySpan<int> colors, ReadOnlySpan<int> endpoints, Span<byte> indices)
    {
        Span<int> palette = stackalloc int[12];

        for (int c = 0; c < 3; c++)
        {
            int c0 = endpoints[c];
            int c1 = endpoints[3 + c];
            palette[c] = c0;
            palette[3 + c] = c1;
            palette[6 + c] = (2 * c0 + c1) / 3;
            palette[9 + c] = (c0 + 2 * c1) / 3;
        }

        long total = 0;

        for (int i = 0; i < 16; i++)
        {
            int r = colors[i * 3], g = colors[i * 3 + 1], b = colors[i * 3 + 2];
            int best = 0;
            int bestError = int.MaxValue;

            for (int p = 0; p < 4; p++)
            {
                int dr = r - palette[p * 3];
                int dg = g - palette[p * 3 + 1];
                int db = b - palette[p * 3 + 2];
                int error = dr * dr + dg * dg + db * db;

                if (error < bestError)
                {
                    bestError = error;
                    best = p;
                }
            }

            indices[i] = (byte)best;
            total += bestError;
        }

        return total;
    }

    private static bool RefineFourColor(ReadOnlySpan<int> colors, ReadOnlySpan<byte> indices, Span<int> endpoints)
    {
        ReadOnlySpan<float> weights = [1f, 0f, 2f / 3f, 1f / 3f];
        float aa = 0, bb = 0, ab = 0;
        float ar = 0, ag = 0, ab2 = 0, br = 0, bg = 0, bb2 = 0;

        for (int i = 0; i < 16; i++)
        {
            float a = weights[indices[i]];
            float b = 1f - a;
            aa += a * a;
            bb += b * b;
            ab += a * b;
            ar += a * colors[i * 3];
            ag += a * colors[i * 3 + 1];
            ab2 += a * colors[i * 3 + 2];
            br += b * colors[i * 3];
            bg += b * colors[i * 3 + 1];
            bb2 += b * colors[i * 3 + 2];
        }

        float det = aa * bb - ab * ab;

        if (MathF.Abs(det) < 1e-6f)
        {
            return false;
        }

        float inv = 1f / det;
        endpoints[0] = (int)MathF.Round((ar * bb - br * ab) * inv);
        endpoints[1] = (int)MathF.Round((ag * bb - bg * ab) * inv);
        endpoints[2] = (int)MathF.Round((ab2 * bb - bb2 * ab) * inv);
        endpoints[3] = (int)MathF.Round((br * aa - ar * ab) * inv);
        endpoints[4] = (int)MathF.Round((bg * aa - ag * ab) * inv);
        endpoints[5] = (int)MathF.Round((bb2 * aa - ab2 * ab) * inv);
        return true;
    }

    private static void WriteFourColorBlock(ReadOnlySpan<int> endpoints, ReadOnlySpan<byte> indices, Span<byte> dest)
    {
        int c0 = Pack565(endpoints[0], endpoints[1], endpoints[2]);
        int c1 = Pack565(endpoints[3], endpoints[4], endpoints[5]);
        uint mask = 0;

        for (int i = 0; i < 16; i++)
        {
            mask |= (uint)indices[i] << (i * 2);
        }

        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
            mask ^= 0x55555555;
        }
        else if (c0 == c1)
        {
            mask = 0;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], (ushort)c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], mask);
    }

    private static void CompressThreeColorBlock(ReadOnlySpan<int> colors, ReadOnlySpan<bool> transparent, Span<byte> dest)
    {
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;

        for (int i = 0; i < 16; i++)
        {
            if (transparent[i])
            {
                continue;
            }

            minR = Math.Min(minR, colors[i * 3]);
            minG = Math.Min(minG, colors[i * 3 + 1]);
            minB = Math.Min(minB, colors[i * 3 + 2]);
            maxR = Math.Max(maxR, colors[i * 3]);
            maxG = Math.Max(maxG, colors[i * 3 + 1]);
            maxB = Math.Max(maxB, colors[i * 3 + 2]);
        }

        int c0 = Pack565(minR, minG, minB);
        int c1 = Pack565(maxR, maxG, maxB);

        if (c0 > c1)
        {
            (c0, c1) = (c1, c0);
        }

        Span<int> palette = stackalloc int[9];
        int r0 = Expand5(c0 >> 11), g0 = Expand6((c0 >> 5) & 63), b0 = Expand5(c0 & 31);
        int r1 = Expand5(c1 >> 11), g1 = Expand6((c1 >> 5) & 63), b1 = Expand5(c1 & 31);
        palette[0] = r0; palette[1] = g0; palette[2] = b0;
        palette[3] = r1; palette[4] = g1; palette[5] = b1;
        palette[6] = (r0 + r1) / 2; palette[7] = (g0 + g1) / 2; palette[8] = (b0 + b1) / 2;

        uint mask = 0;

        for (int i = 0; i < 16; i++)
        {
            int index = 3;

            if (!transparent[i])
            {
                int bestError = int.MaxValue;

                for (int p = 0; p < 3; p++)
                {
                    int dr = colors[i * 3] - palette[p * 3];
                    int dg = colors[i * 3 + 1] - palette[p * 3 + 1];
                    int db = colors[i * 3 + 2] - palette[p * 3 + 2];
                    int error = dr * dr + dg * dg + db * db;

                    if (error < bestError)
                    {
                        bestError = error;
                        index = p;
                    }
                }
            }

            mask |= (uint)index << (i * 2);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], (ushort)c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], mask);
    }

    private static byte[] BuildSingleColorTable(int bits)
    {
        int levels = 1 << bits;
        byte[] table = new byte[256 * 2];

        for (int value = 0; value < 256; value++)
        {
            int bestError = int.MaxValue;

            for (int a = 0; a < levels; a++)
            {
                int ea = bits == 5 ? Expand5(a) : Expand6(a);

                for (int b = 0; b < levels; b++)
                {
                    int eb = bits == 5 ? Expand5(b) : Expand6(b);
                    int interpolated = (2 * ea + eb) / 3;
                    int error = Math.Abs(interpolated - value) * 100 + Math.Abs(ea - eb);

                    if (error < bestError)
                    {
                        bestError = error;
                        table[value * 2] = (byte)a;
                        table[value * 2 + 1] = (byte)b;
                    }
                }
            }
        }

        return table;
    }

    #endregion

    #region Alpha (BC2 explicit, BC3/BC4 interpolated)

    private static void CompressExplicitAlphaBlock(ReadOnlySpan<byte> block, Span<byte> dest)
    {
        for (int i = 0; i < 8; i++)
        {
            int a0 = (block[(i * 2) * 4 + 3] * 15 + 127) / 255;
            int a1 = (block[(i * 2 + 1) * 4 + 3] * 15 + 127) / 255;
            dest[i] = (byte)(a0 | (a1 << 4));
        }
    }

    internal static void CompressSingleChannelBlock(ReadOnlySpan<byte> block, int channel, Span<byte> dest)
    {
        Span<int> values = stackalloc int[16];
        int min = 255, max = 0;
        int innerMin = 255, innerMax = 0;

        for (int i = 0; i < 16; i++)
        {
            int value = block[i * 4 + channel];
            values[i] = value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);

            if (value != 0 && value != 255)
            {
                innerMin = Math.Min(innerMin, value);
                innerMax = Math.Max(innerMax, value);
            }
        }

        if (min == max)
        {
            dest[0] = (byte)min;
            dest[1] = (byte)min;
            dest[2..8].Clear();
            return;
        }

        Span<int> palette = stackalloc int[8];
        Span<byte> indices = stackalloc byte[16];
        Span<byte> bestIndices = stackalloc byte[16];

        BuildEightValuePalette(max, min, palette);
        long bestError = MatchSingleChannel(values, palette, bestIndices);
        int best0 = max, best1 = min;

        if (min == 0 || max == 255)
        {
            int lo = innerMin <= innerMax ? innerMin : min;
            int hi = innerMin <= innerMax ? innerMax : max;
            BuildSixValuePalette(lo, hi, palette);
            long error = MatchSingleChannel(values, palette, indices);

            if (error < bestError)
            {
                bestError = error;
                best0 = lo;
                best1 = hi;
                indices.CopyTo(bestIndices);
            }
        }

        SearchAlphaEndpoints(values, ref best0, ref best1, bestIndices, ref bestError);

        dest[0] = (byte)best0;
        dest[1] = (byte)best1;

        ulong bits = 0;

        for (int i = 0; i < 16; i++)
        {
            bits |= (ulong)bestIndices[i] << (i * 3);
        }

        for (int i = 0; i < 6; i++)
        {
            dest[2 + i] = (byte)(bits >> (i * 8));
        }
    }

    internal static void BuildEightValuePalette(int a0, int a1, Span<int> palette)
    {
        palette[0] = a0;
        palette[1] = a1;

        for (int i = 1; i < 7; i++)
        {
            palette[1 + i] = ((7 - i) * a0 + i * a1 + 3) / 7;
        }
    }

    internal static void BuildSixValuePalette(int a0, int a1, Span<int> palette)
    {
        palette[0] = a0;
        palette[1] = a1;

        for (int i = 1; i < 5; i++)
        {
            palette[1 + i] = ((5 - i) * a0 + i * a1 + 2) / 5;
        }

        palette[6] = 0;
        palette[7] = 255;
    }

    private static void SearchAlphaEndpoints(ReadOnlySpan<int> values, ref int a0, ref int a1, Span<byte> indices, ref long error)
    {
        bool eightValues = a0 > a1;
        Span<int> palette = stackalloc int[8];
        Span<byte> trialIndices = stackalloc byte[16];

        for (int round = 0; round < EndpointSearchRounds && error > 0; round++)
        {
            bool improved = false;

            for (int e = 0; e < 2; e++)
            {
                for (int delta = -1; delta <= 1; delta += 2)
                {
                    int t0 = e == 0 ? a0 + delta : a0;
                    int t1 = e == 1 ? a1 + delta : a1;

                    if (t0 < 0 || t0 > 255 || t1 < 0 || t1 > 255 || (t0 > t1) != eightValues)
                    {
                        continue;
                    }

                    if (eightValues)
                    {
                        BuildEightValuePalette(t0, t1, palette);
                    }
                    else
                    {
                        BuildSixValuePalette(t0, t1, palette);
                    }

                    long trialError = MatchSingleChannel(values, palette, trialIndices);

                    if (trialError < error)
                    {
                        error = trialError;
                        a0 = t0;
                        a1 = t1;
                        trialIndices.CopyTo(indices);
                        improved = true;
                    }
                }
            }

            if (!improved)
            {
                break;
            }
        }
    }

    private static long MatchSingleChannel(ReadOnlySpan<int> values, ReadOnlySpan<int> palette, Span<byte> indices)
    {
        long total = 0;

        for (int i = 0; i < 16; i++)
        {
            int best = 0;
            int bestError = int.MaxValue;

            for (int p = 0; p < 8; p++)
            {
                int diff = values[i] - palette[p];
                int error = diff * diff;

                if (error < bestError)
                {
                    bestError = error;
                    best = p;
                }
            }

            indices[i] = (byte)best;
            total += bestError;
        }

        return total;
    }

    #endregion
}
