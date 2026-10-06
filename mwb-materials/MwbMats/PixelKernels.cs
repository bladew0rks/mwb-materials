using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

        public static void WriteChannel(byte[] rgba, int channel, byte[] gray)
        {
            int shift = channel * 8;
            uint keep = ~(0xFFu << shift);

            ParallelPixels.For(gray.Length, (start, end) =>
            {
                Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan(start * 4, (end - start) * 4));
                ReadOnlySpan<byte> values = gray.AsSpan(start, end - start);
                int i = 0;

                if (Simd)
                {
                    ref uint target = ref MemoryMarshal.GetReference(pixels);
                    ref byte source = ref MemoryMarshal.GetReference(values);
                    Vector256<uint> vkeep = Vector256.Create(keep);

                    for (; i <= pixels.Length - 32; i += 32)
                    {
                        (Vector256<ushort> lo, Vector256<ushort> hi) = Vector256.Widen(Vector256.LoadUnsafe(ref source, (nuint)i));
                        (Vector256<uint> a, Vector256<uint> b) = Vector256.Widen(lo);
                        (Vector256<uint> c, Vector256<uint> d) = Vector256.Widen(hi);
                        Blend(ref target, i, a, shift, vkeep);
                        Blend(ref target, i + 8, b, shift, vkeep);
                        Blend(ref target, i + 16, c, shift, vkeep);
                        Blend(ref target, i + 24, d, shift, vkeep);
                    }
                }

                for (; i < pixels.Length; i++)
                {
                    pixels[i] = (pixels[i] & keep) | ((uint)values[i] << shift);
                }
            });
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

        public static byte[] Map(byte[] source, byte[] table)
        {
            byte[] result = new byte[source.Length];

            ParallelPixels.For(source.Length, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    result[i] = table[source[i]];
                }
            });

            return result;
        }

        public static void Map2InPlace(byte[] a, byte[] b, byte[] table)
        {
            ParallelPixels.For(a.Length, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    a[i] = table[(a[i] << 8) | b[i]];
                }
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Narrow(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c, Vector256<uint> d)
        {
            return Vector256.Narrow(Vector256.Narrow(a, b), Vector256.Narrow(c, d));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Blend(ref uint target, int index, Vector256<uint> value, int shift, Vector256<uint> keep)
        {
            Vector256<uint> current = Vector256.LoadUnsafe(ref target, (nuint)index);
            Vector256.StoreUnsafe((current & keep) | (value << shift), ref target, (nuint)index);
        }
    }
}
