using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace mwb_materials.MwbMats
{
    static class PixelKernels
    {
        private static bool Simd => Vector256.IsHardwareAccelerated;

        public static void Invert(byte[] data)
        {
            ParallelPixels.For(data.Length, (start, end) =>
            {
                Span<byte> span = data.AsSpan(start, end - start);
                int i = 0;

                if (Simd)
                {
                    ref byte origin = ref MemoryMarshal.GetReference(span);

                    for (; i <= span.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
                    {
                        Vector256.StoreUnsafe(~Vector256.LoadUnsafe(ref origin, (nuint)i), ref origin, (nuint)i);
                    }
                }

                for (; i < span.Length; i++)
                {
                    span[i] = (byte)(255 - span[i]);
                }
            });
        }

        public static void InvertChannel(byte[] rgba, int channel)
        {
            uint mask = 0xFFu << (channel * 8);

            ParallelPixels.For(rgba.Length / 4, (start, end) =>
            {
                Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan(start * 4, (end - start) * 4));
                int i = 0;

                if (Simd)
                {
                    Vector256<uint> vmask = Vector256.Create(mask);
                    ref uint origin = ref MemoryMarshal.GetReference(pixels);

                    for (; i <= pixels.Length - Vector256<uint>.Count; i += Vector256<uint>.Count)
                    {
                        Vector256.StoreUnsafe(Vector256.LoadUnsafe(ref origin, (nuint)i) ^ vmask, ref origin, (nuint)i);
                    }
                }

                for (; i < pixels.Length; i++)
                {
                    pixels[i] ^= mask;
                }
            });
        }

        public static byte[] ExtractChannel(byte[] rgba, int channel)
        {
            byte[] gray = new byte[rgba.Length / 4];
            int shift = channel * 8;

            ParallelPixels.For(gray.Length, (start, end) =>
            {
                ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan(start * 4, (end - start) * 4));
                Span<byte> dest = gray.AsSpan(start, end - start);
                int i = 0;

                if (Simd)
                {
                    ref uint source = ref MemoryMarshal.GetReference(pixels);
                    ref byte target = ref MemoryMarshal.GetReference(dest);
                    Vector256<uint> low = Vector256.Create(0xFFu);

                    for (; i <= pixels.Length - 32; i += 32)
                    {
                        Vector256<uint> a = (Vector256.LoadUnsafe(ref source, (nuint)i) >>> shift) & low;
                        Vector256<uint> b = (Vector256.LoadUnsafe(ref source, (nuint)(i + 8)) >>> shift) & low;
                        Vector256<uint> c = (Vector256.LoadUnsafe(ref source, (nuint)(i + 16)) >>> shift) & low;
                        Vector256<uint> d = (Vector256.LoadUnsafe(ref source, (nuint)(i + 24)) >>> shift) & low;
                        Vector256.StoreUnsafe(Narrow(a, b, c, d), ref target, (nuint)i);
                    }
                }

                for (; i < pixels.Length; i++)
                {
                    dest[i] = (byte)(pixels[i] >> shift);
                }
            });

            return gray;
        }

        public static void FillChannel(byte[] rgba, int channel, byte value)
        {
            int shift = channel * 8;
            uint keep = ~(0xFFu << shift);
            uint fill = (uint)value << shift;

            ParallelPixels.For(rgba.Length / 4, (start, end) =>
            {
                Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan(start * 4, (end - start) * 4));
                int i = 0;

                if (Simd)
                {
                    ref uint origin = ref MemoryMarshal.GetReference(pixels);
                    Vector256<uint> vkeep = Vector256.Create(keep);
                    Vector256<uint> vfill = Vector256.Create(fill);

                    for (; i <= pixels.Length - Vector256<uint>.Count; i += Vector256<uint>.Count)
                    {
                        Vector256.StoreUnsafe((Vector256.LoadUnsafe(ref origin, (nuint)i) & vkeep) | vfill, ref origin, (nuint)i);
                    }
                }

                for (; i < pixels.Length; i++)
                {
                    pixels[i] = (pixels[i] & keep) | fill;
                }
            });
        }

        public static bool CanLookup512 => Vector512.IsHardwareAccelerated && Avx512Vbmi.IsSupported;

        public readonly struct ByteTable
        {
            public readonly Vector512<byte> T0, T1, T2, T3;

            public ByteTable(byte[] table)
            {
                T0 = Vector512.Create<byte>(table.AsSpan(0, 64));
                T1 = Vector512.Create<byte>(table.AsSpan(64, 64));
                T2 = Vector512.Create<byte>(table.AsSpan(128, 64));
                T3 = Vector512.Create<byte>(table.AsSpan(192, 64));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<byte> Lookup(Vector512<byte> indices, in ByteTable table)
        {
            Vector512<byte> low = Avx512Vbmi.PermuteVar64x8x2(table.T0, indices, table.T1);
            Vector512<byte> high = Avx512Vbmi.PermuteVar64x8x2(table.T2, indices, table.T3);
            return Vector512.ConditionalSelect(Vector512.GreaterThan(indices, Vector512.Create((byte)127)), high, low);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Widen4(Vector512<byte> value, out Vector512<uint> q0, out Vector512<uint> q1, out Vector512<uint> q2, out Vector512<uint> q3)
        {
            (Vector512<ushort> low, Vector512<ushort> high) = Vector512.Widen(value);
            (q0, q1) = Vector512.Widen(low);
            (q2, q3) = Vector512.Widen(high);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<uint> Multiply(Vector512<uint> value, Vector512<uint> mask)
        {
            Vector512<float> product = Vector512.ConvertToSingle(value.AsInt32()) * (Vector512.ConvertToSingle(mask.AsInt32()) / Vector512.Create(255.0f));
            return Vector512.ConvertToInt32(Vector512.Min(product, Vector512.Create(255.0f))).AsUInt32();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<uint> Scale(Vector512<uint> channel, Vector512<float> factor)
        {
            return Vector512.ConvertToInt32(Vector512.Min(Vector512.ConvertToSingle(channel.AsInt32()) * factor, Vector512.Create(255.0f))).AsUInt32();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Narrow(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c, Vector256<uint> d)
        {
            return Vector256.Narrow(Vector256.Narrow(a, b), Vector256.Narrow(c, d));
        }

    }
}
