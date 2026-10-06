using mwb_materials.MwbMats;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Diagnostics;

namespace mwb_materials
{
    /*
    normal map alpha - mask for phong intensity (this will be the roughness converted to RGB)
    basetexture alpha - metalness of course :))
    exponent - red channel roughness converted to RGB. green channel metalness, blue channel 100% white (or black, or even a bentley image if you feel so devious)
    */

    class MaterialManipulation
    {
        private static readonly string AlbedoNomenclature = "_rgb";
        private static readonly string AlbedoAltNomenclature = "_c";
        private static readonly string AlbedoMetalnessNomenclature = "_rgbm";
        private static readonly string CodAlbedoSpecNomenclature = "_s~";
        private static readonly string AmbientOcclusionNomenclature = "_o";
        private static readonly string AmbientOcclusionAltNomenclature = "_ao";
        private static readonly string RoughnessNomenclature = "_r";
        private static readonly string GlossNomenclature = "_g";
        private static readonly string MetalnessNomenclature = "_alpha";
        private static readonly string MetalnessAltNomenclature = "_m";
        private static readonly string NormalNomenclature = "_n";
        private static readonly string EmissiveNomenclature = "_e";
        private static readonly string AlphatestNomenclature = "_t";
        private static readonly string TranslucentNomenclature = "_opacity";
        private static readonly string PackedOrmNomenclature = "_orm";
        private static readonly string PackedRmaNomenclature = "_rma";
        private static readonly string PackedMraoNomenclature = "_mrao";
        private static readonly string CodNogPackedNgNomenclature = "packed_ng";
        private static readonly string CodNogPackedNogNomenclature = "packed_nog";
        private static readonly string CodNogNomenclature = "_nog";
        private static readonly string CodNogNormalNomenclature = "_n&";
        private static readonly string CodNogGlossNomenclature = "_g~";

        public enum TextureChannel
        {
            Red,
            Green,
            Blue,
            Alpha
        }

        public enum OpacityMode
        {
            None,
            Alphatest,
            Translucent
        }

        public sealed class SourceTextureSet
        {
            public SourceTextureSet(PixelBuffer albedo, PixelBuffer exponent, PixelBuffer normal, PixelBuffer emissive, Color metallicColor, double averageRoughness, OpacityMode opacityMode, IntermediateTextureSet intermediates)
            {
                Albedo = albedo;
                Exponent = exponent;
                Normal = normal;
                Emissive = emissive;
                AverageMetallicColor = metallicColor;
                AverageRoughness = averageRoughness;
                OpacityMode = opacityMode;
                Intermediates = intermediates;
            }

            public PixelBuffer Albedo { get; }
            public PixelBuffer Exponent { get; }
            public PixelBuffer Normal { get; }
            public PixelBuffer Emissive { get; }
            public Color AverageMetallicColor { get; }
            public double AverageRoughness { get; }
            public OpacityMode OpacityMode { get; }
            public IntermediateTextureSet Intermediates { get; }
        }

        public sealed class IntermediateTextureSet
        {
            public IntermediateTextureSet(GrayBuffer ambientOcclusion, GrayBuffer gloss, GrayBuffer metalness)
            {
                AmbientOcclusion = ambientOcclusion;
                Gloss = gloss;
                Metalness = metalness;
            }

            public GrayBuffer AmbientOcclusion { get; }
            public GrayBuffer Gloss { get; }
            public GrayBuffer Metalness { get; }
        }

        public struct GenerateProperties
        {
            public bool bAoMasks { get; set; }
            public bool bOpenGlNormal { get; set; }
            public bool bInvertNormalBlue { get; set; }
            public bool bInvertOpacity { get; set; }
            public bool bKeepIntermediates { get; set; }
            public int ClampSize { get; set; }
            public float AoAlbedoStrength { get; set; }
            public Action<string> LogFunc { get; set; }
        }

        #region Lookup tables

        private static readonly byte[] PhongMaskLut = BuildLut(gloss => (byte)Math.Min((Math.Pow(gloss / 255.0, 2.5) * 255.0) + 1.0, 255.0));

        private static readonly double[] ExponentCurve = Enumerable.Range(0, 256).Select(gloss => Math.Pow(gloss / 255.0, 4.0)).ToArray();
        private static readonly byte[] ExponentLut = BuildLut(gloss => (byte)Math.Min((ExponentCurve[gloss] * 255.0) + 1.0, 255.0));
        private static readonly byte[] ExponentMetalLut = BuildLut2((gloss, metal) =>
        {
            double delta = ExponentCurve[gloss];
            delta *= 1.0f.Lerp(0.5f, metal / 255.0f);
            return (byte)Math.Min((delta * 255.0) + 1.0, 255.0);
        });

        private static readonly byte[] ExponentMetalLutPinned = Pin(ExponentMetalLut);

        private static readonly byte[] MultiplyLut = BuildLut2((value, mask) => (byte)Math.Min(value * (mask / 255.0f), 255.0f));

        private static readonly byte[] Color2Lut = BuildLut(metal => (byte)0f.Lerp(255f, metal / 255.0f));

        private static readonly double[] RoughnessAverageLut = Enumerable.Range(0, 256).Select(gloss => Math.Max(gloss / 255.0, 0.5)).ToArray();

        private static byte[] Pin(byte[] table)
        {
            byte[] pinned = GC.AllocateArray<byte>(table.Length + 4, pinned: true);
            table.CopyTo(pinned, 0);
            return pinned;
        }

        private static byte[] BuildLut(Func<int, byte> function)
        {
            byte[] table = new byte[256];

            for (int i = 0; i < 256; i++)
            {
                table[i] = function(i);
            }

            return table;
        }

        private static byte[] BuildLut2(Func<int, int, byte> function)
        {
            byte[] table = new byte[256 * 256];

            for (int a = 0; a < 256; a++)
            {
                for (int b = 0; b < 256; b++)
                {
                    table[(a << 8) | b] = function(a, b);
                }
            }

            return table;
        }

        #endregion

        #region Map generation

        private static byte[] BuildAoTable(float strength)
        {
            return BuildLut2((occlusion, color) =>
            {
                float factor = 1.0f.Lerp(occlusion / 255.0f, strength);
                return (byte)Math.Min(color * factor, 255.0f);
            });
        }

        private static PixelBuffer CreateSourceAlbedo(PixelBuffer albedo, GrayBuffer ambientOcclusion, GrayBuffer metalness, GrayBuffer opacity, GenerateProperties props)
        {
            if (albedo == null)
            {
                return null;
            }

            float strength = Math.Min(Math.Max(props.AoAlbedoStrength, 0.0f), 1.0f);
            byte[] rgba = albedo.Bytes;
            byte[] ao = ambientOcclusion?.Bytes;
            byte[] aoTable = ao != null ? BuildAoTable(strength) : null;

            //opacity mask -> basetexture alpha (white = opaque, black = transparent)
            byte[] alphaSource = opacity?.Bytes ?? metalness?.Bytes;

            //color2
            byte[] alphaTable = opacity == null && metalness != null ? Color2Lut : null;

            ParallelPixels.For(albedo.PixelCount, (start, end) =>
            {
                int i = PixelKernels.CanLookup512 ? ComposeAlbedo512(rgba, ao, strength, alphaSource, alphaTable, start, end) : start;

                for (; i < end; i++)
                {
                    int o = i * 4;

                    if (ao != null)
                    {
                        int row = ao[i] << 8;
                        rgba[o] = aoTable[row | rgba[o]];
                        rgba[o + 1] = aoTable[row | rgba[o + 1]];
                        rgba[o + 2] = aoTable[row | rgba[o + 2]];
                    }

                    rgba[o + 3] = alphaSource == null ? (byte)0 : alphaTable == null ? alphaSource[i] : alphaTable[alphaSource[i]];
                }
            });

            return albedo;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int ComposeAlbedo512(byte[] rgba, byte[] ao, float strength, byte[] alphaSource, byte[] alphaTable, int start, int end)
        {
            PixelKernels.ByteTable table = alphaTable != null ? new PixelKernels.ByteTable(alphaTable) : default;
            Vector512<float> keep = Vector512.Create(1.0f * (1.0f - strength));
            Vector512<float> weight = Vector512.Create(strength);
            Vector512<uint> rgbMask = Vector512.Create(0x00FFFFFFu);
            Vector512<uint> low = Vector512.Create(0xFFu);
            ref uint pixels = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetArrayDataReference(rgba));
            Span<Vector512<uint>> alphas = stackalloc Vector512<uint>[4];
            Span<Vector512<uint>> occlusion = stackalloc Vector512<uint>[4];
            int i = start;

            for (; i + 64 <= end; i += 64)
            {
                Vector512<byte> alpha = alphaSource == null ? Vector512<byte>.Zero : Vector512.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(alphaSource), (nuint)i);

                if (alphaTable != null)
                {
                    alpha = PixelKernels.Lookup(alpha, table);
                }

                PixelKernels.Widen4(alpha, out alphas[0], out alphas[1], out alphas[2], out alphas[3]);

                if (ao != null)
                {
                    PixelKernels.Widen4(Vector512.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(ao), (nuint)i), out occlusion[0], out occlusion[1], out occlusion[2], out occlusion[3]);
                }

                for (int q = 0; q < 4; q++)
                {
                    nuint offset = (nuint)(i + q * 16);
                    Vector512<uint> pixel = Vector512.LoadUnsafe(ref pixels, offset);
                    Vector512<uint> rgb = pixel & rgbMask;

                    if (ao != null)
                    {
                        Vector512<float> factor = keep + (Vector512.ConvertToSingle(occlusion[q].AsInt32()) / Vector512.Create(255.0f)) * weight;
                        rgb = PixelKernels.Scale(pixel & low, factor)
                            | (PixelKernels.Scale((pixel >>> 8) & low, factor) << 8)
                            | (PixelKernels.Scale((pixel >>> 16) & low, factor) << 16);
                    }

                    (rgb | (alphas[q] << 24)).StoreUnsafe(ref pixels, offset);
                }
            }

            return i;
        }

        private static PixelBuffer CreateSourceNormal(PixelBuffer normal, GrayBuffer gloss, GrayBuffer ambientOcclusion, GenerateProperties props)
        {
            if (normal == null || gloss == null)
            {
                return normal;
            }

            //phong
            byte[] rgba = normal.Bytes;
            byte[] glossBytes = gloss.Bytes;
            byte[] ao = props.bAoMasks ? ambientOcclusion?.Bytes : null;

            ParallelPixels.For(normal.PixelCount, (start, end) =>
            {
                int i = PixelKernels.CanLookup512 ? ComposeNormal512(rgba, glossBytes, ao, start, end) : start;

                for (; i < end; i++)
                {
                    byte mask = PhongMaskLut[glossBytes[i]];
                    rgba[i * 4 + 3] = ao == null ? mask : MultiplyLut[(mask << 8) | ao[i]];
                }
            });

            return normal;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int ComposeNormal512(byte[] rgba, byte[] gloss, byte[] ao, int start, int end)
        {
            PixelKernels.ByteTable phong = new PixelKernels.ByteTable(PhongMaskLut);
            Vector512<uint> rgbMask = Vector512.Create(0x00FFFFFFu);
            ref uint pixels = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetArrayDataReference(rgba));
            Span<Vector512<uint>> masks = stackalloc Vector512<uint>[4];
            Span<Vector512<uint>> occlusion = stackalloc Vector512<uint>[4];
            int i = start;

            for (; i + 64 <= end; i += 64)
            {
                PixelKernels.Widen4(PixelKernels.Lookup(Vector512.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(gloss), (nuint)i), phong),
                    out masks[0], out masks[1], out masks[2], out masks[3]);

                if (ao != null)
                {
                    PixelKernels.Widen4(Vector512.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(ao), (nuint)i), out occlusion[0], out occlusion[1], out occlusion[2], out occlusion[3]);
                }

                for (int q = 0; q < 4; q++)
                {
                    nuint offset = (nuint)(i + q * 16);
                    Vector512<uint> mask = ao == null ? masks[q] : PixelKernels.Multiply(masks[q], occlusion[q]);
                    ((Vector512.LoadUnsafe(ref pixels, offset) & rgbMask) | (mask << 24)).StoreUnsafe(ref pixels, offset);
                }
            }

            return i;
        }

        private static PixelBuffer CreateSourceExponent(GrayBuffer gloss, GrayBuffer metalness, GrayBuffer ambientOcclusion, GenerateProperties props)
        {
            if (gloss == null && metalness == null)
            {
                return null;
            }

            int width = gloss?.Width ?? metalness.Width;
            int height = gloss?.Height ?? metalness.Height;
            PixelBuffer sourceExponent = new PixelBuffer(width, height);
            byte[] rgba = sourceExponent.Bytes;
            byte[] glossBytes = gloss?.Bytes;
            byte[] metal = metalness?.Bytes;
            byte[] ao = props.bAoMasks ? ambientOcclusion?.Bytes : null;

            ParallelPixels.For(sourceExponent.PixelCount, (start, end) =>
            {
                int i = PixelKernels.CanLookup512 && glossBytes != null ? ComposeExponent512(rgba, glossBytes, metal, ao, start, end) : start;

                for (; i < end; i++)
                {
                    int o = i * 4;

                    if (glossBytes != null)
                    {
                        //phong exponent
                        byte g = glossBytes[i];
                        rgba[o] = metal != null ? ExponentMetalLut[(g << 8) | metal[i]] : ExponentLut[g];

                        //rimlight
                        rgba[o + 3] = ao == null ? g : MultiplyLut[(g << 8) | ao[i]];
                    }

                    if (metal != null)
                    {
                        //phong albedo tint
                        rgba[o + 1] = metal[i];
                    }
                }
            });

            return sourceExponent;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe int ComposeExponent512(byte[] rgba, byte[] gloss, byte[] metal, byte[] ao, int start, int end)
        {
            PixelKernels.ByteTable exponent = new PixelKernels.ByteTable(ExponentLut);
            ref uint pixels = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetArrayDataReference(rgba));
            Span<Vector512<uint>> glossQuarters = stackalloc Vector512<uint>[4];
            Span<Vector512<uint>> exponentQuarters = stackalloc Vector512<uint>[4];
            Span<Vector512<uint>> metalQuarters = stackalloc Vector512<uint>[4];
            Span<Vector512<uint>> occlusion = stackalloc Vector512<uint>[4];
            int* metalTable = (int*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(ExponentMetalLutPinned));
            int i = start;

            for (; i + 64 <= end; i += 64)
            {
                Vector512<byte> g = Vector512.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(gloss), (nuint)i);
                PixelKernels.Widen4(g, out glossQuarters[0], out glossQuarters[1], out glossQuarters[2], out glossQuarters[3]);

                if (metal != null)
                {
                    PixelKernels.Widen4(Vector512.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(metal), (nuint)i), out metalQuarters[0], out metalQuarters[1], out metalQuarters[2], out metalQuarters[3]);
                }
                else
                {
                    PixelKernels.Widen4(PixelKernels.Lookup(g, exponent), out exponentQuarters[0], out exponentQuarters[1], out exponentQuarters[2], out exponentQuarters[3]);
                }

                if (ao != null)
                {
                    PixelKernels.Widen4(Vector512.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(ao), (nuint)i), out occlusion[0], out occlusion[1], out occlusion[2], out occlusion[3]);
                }

                for (int q = 0; q < 4; q++)
                {
                    Vector512<uint> red, green = Vector512<uint>.Zero;

                    if (metal != null)
                    {
                        Vector512<int> index = ((glossQuarters[q] << 8) | metalQuarters[q]).AsInt32();
                        Vector512<int> gathered = Vector512.Create(Avx2.GatherVector256(metalTable, index.GetLower(), 1), Avx2.GatherVector256(metalTable, index.GetUpper(), 1));
                        red = gathered.AsUInt32() & Vector512.Create(0xFFu);
                        green = metalQuarters[q];
                    }
                    else
                    {
                        red = exponentQuarters[q];
                    }

                    Vector512<uint> rim = ao == null ? glossQuarters[q] : PixelKernels.Multiply(glossQuarters[q], occlusion[q]);
                    (red | (green << 8) | (rim << 24)).StoreUnsafe(ref pixels, (nuint)(i + q * 16));
                }
            }

            return i;
        }

        private static Color GetAverageMetallicColor(PixelBuffer albedo, GrayBuffer metalness)
        {
            if (metalness == null || albedo == null)
            {
                return Color.FromArgb(25, 25, 25);
            }

            byte[] color = albedo.Bytes;
            byte[] metal = metalness.Bytes;

            (double R, double G, double B)[] partials = ParallelPixels.Map(albedo.PixelCount, (start, end) =>
            {
                double red = 0.0, green = 0.0, blue = 0.0;

                for (int i = start; i < end; i++)
                {
                    double value = metal[i] / 255.0;

                    if (value > 0.5)
                    {
                        red += color[i * 4] * value;
                        green += color[i * 4 + 1] * value;
                        blue += color[i * 4 + 2] * value;
                    }
                    else
                    {
                        red += 25.0;
                        green += 25.0;
                        blue += 25.0;
                    }
                }

                return (red, green, blue);
            });

            double count = albedo.PixelCount;

            return Color.FromArgb(
                (int)(partials.Sum(p => p.R) / count),
                (int)(partials.Sum(p => p.G) / count),
                (int)(partials.Sum(p => p.B) / count));
        }

        private static double GetAverageRoughness(GrayBuffer gloss)
        {
            if (gloss == null)
            {
                return 0.5;
            }

            byte[] bytes = gloss.Bytes;
            double[] partials = ParallelPixels.Map(gloss.PixelCount, (start, end) =>
            {
                double sum = 0.0;

                for (int i = start; i < end; i++)
                {
                    sum += RoughnessAverageLut[bytes[i]];
                }

                return sum;
            });

            return partials.Sum() / gloss.PixelCount;
        }

        #endregion

        #region Source handling

        private static void LogSourceReport(string file, string role, string channelsUsed, SourceStats stats, Action<string> logFunc)
        {
            if (logFunc == null)
            {
                return;
            }

            logFunc("Source " + Path.GetFileName(file) +
                ": role=" + role +
                ", size=" + stats.Width + "x" + stats.Height +
                ", channels=" + channelsUsed +
                ", gray min/avg/max=" + FormatStats(stats.GrayMin, stats.GraySum, stats.GrayMax, stats.Count) +
                ", alpha min/avg/max=" + FormatStats(stats.AlphaMin, stats.AlphaSum, stats.AlphaMax, stats.Count));
        }

        private static string FormatStats(int min, long sum, int max, int count)
        {
            double average = count > 0 ? (double)sum / count : 0.0;

            return (count > 0 ? min : 255).ToString(CultureInfo.InvariantCulture) + "/" +
                average.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                (count > 0 ? max : 0).ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryAssignTexture<T>(ref T current, ref string currentSource, ref int currentPriority,
            T candidate, string role, string candidateSource, int candidatePriority, Action<string> logFunc) where T : class
        {
            if (candidate == null)
            {
                return false;
            }

            if (current == null)
            {
                current = candidate;
                currentSource = candidateSource;
                currentPriority = candidatePriority;
                return true;
            }

            if (candidatePriority > currentPriority)
            {
                logFunc?.Invoke("Precedence: " + role + " using " + candidateSource + " over " + currentSource + ".");
                current = candidate;
                currentSource = candidateSource;
                currentPriority = candidatePriority;
                return true;
            }

            logFunc?.Invoke("Precedence: " + role + " keeping " + currentSource + "; ignoring " + candidateSource + ".");
            return false;
        }

        private static string DescribeTextureSource(string source)
        {
            return string.IsNullOrEmpty(source) ? "none" : source;
        }

        private static GrayBuffer ExtractChannel(PixelBuffer src, TextureChannel channel)
        {
            return new GrayBuffer(src.Width, src.Height, PixelKernels.ExtractChannel(src.Bytes, (int)channel));
        }

        private static void SplitPackedTexture(PixelBuffer packed, string nomenclature,
            ref GrayBuffer ao, ref GrayBuffer roughness, ref GrayBuffer metalness)
        {
            if (nomenclature == PackedOrmNomenclature)
            {
                ao = ExtractChannel(packed, TextureChannel.Red);
                roughness = ExtractChannel(packed, TextureChannel.Green);
                metalness = ExtractChannel(packed, TextureChannel.Blue);
            }
            else if (nomenclature == PackedRmaNomenclature)
            {
                roughness = ExtractChannel(packed, TextureChannel.Red);
                metalness = ExtractChannel(packed, TextureChannel.Green);
                ao = ExtractChannel(packed, TextureChannel.Blue);
            }
            else if (nomenclature == PackedMraoNomenclature)
            {
                metalness = ExtractChannel(packed, TextureChannel.Red);
                roughness = ExtractChannel(packed, TextureChannel.Green);
                ao = ExtractChannel(packed, TextureChannel.Blue);
            }
        }

        private static bool IsCodNogTextureName(string name)
        {
            return name.Contains(CodNogPackedNgNomenclature) ||
                name.Contains(CodNogPackedNogNomenclature) ||
                name.EndsWith(CodNogNomenclature) ||
                name.Contains(CodNogNormalNomenclature) ||
                name.Contains(CodNogGlossNomenclature);
        }

        private static string GetCodNogRole(string name)
        {
            if (name.Contains(CodNogPackedNgNomenclature))
            {
                return CodNogPackedNgNomenclature;
            }

            if (name.Contains(CodNogPackedNogNomenclature))
            {
                return CodNogPackedNogNomenclature;
            }

            if (name.EndsWith(CodNogNomenclature))
            {
                return CodNogNomenclature;
            }

            if (name.Contains(CodNogNormalNomenclature))
            {
                return CodNogNormalNomenclature;
            }

            return CodNogGlossNomenclature;
        }

        private static bool IsRgbmTextureName(string name)
        {
            return name.EndsWith(AlbedoMetalnessNomenclature) ||
                name.Contains(CodAlbedoSpecNomenclature);
        }

        private static byte EncodeNormalComponent(float value)
        {
            value = (value * 0.5f) + 0.5f;
            value = Math.Min(Math.Max(value, 0.0f), 1.0f);
            return (byte)Math.Min((value * 255.0f) + 0.5f, 255.0f);
        }

        private static PixelBuffer CreateCodNogNormal(PixelBuffer src)
        {
            PixelBuffer result = new PixelBuffer(src.Width, src.Height);
            byte[] source = src.Bytes;
            byte[] dst = result.Bytes;

            ParallelPixels.For(src.PixelCount, (start, end) =>
            {
                for (int cursor = start * 4; cursor < end * 4; cursor += 4)
                {
                    float normalX = (source[cursor + (int)TextureChannel.Green] / 255.0f * 2.0f) - 1.0f;
                    float normalY = (source[cursor + (int)TextureChannel.Alpha] / 255.0f * 2.0f) - 1.0f;

                    float x = (normalX + normalY) * 0.5f;
                    float y = (normalX - normalY) * 0.5f;
                    float z = 1.0f - Math.Abs(x) - Math.Abs(y);
                    float length = (float)Math.Sqrt((x * x) + (y * y) + (z * z));

                    if (length > 0.0f)
                    {
                        x /= length;
                        y /= length;
                        z /= length;
                    }

                    dst[cursor + (int)TextureChannel.Red] = EncodeNormalComponent(x);
                    dst[cursor + (int)TextureChannel.Green] = EncodeNormalComponent(y);
                    dst[cursor + (int)TextureChannel.Blue] = EncodeNormalComponent(z);
                    dst[cursor + (int)TextureChannel.Alpha] = 255;
                }
            });

            return result;
        }

        private static void SplitCodNogTexture(PixelBuffer packed,
            ref GrayBuffer ao, ref GrayBuffer gloss, ref PixelBuffer normal)
        {
            gloss = ExtractChannel(packed, TextureChannel.Red);
            ao = ExtractChannel(packed, TextureChannel.Blue);
            normal = CreateCodNogNormal(packed);
        }

        private static string GetPackedChannelDescription(string nomenclature)
        {
            if (nomenclature == PackedOrmNomenclature)
            {
                return "R=AO, G=roughness, B=metalness";
            }

            if (nomenclature == PackedRmaNomenclature)
            {
                return "R=roughness, G=metalness, B=AO";
            }

            return "R=metalness, G=roughness, B=AO";
        }

        private static string GetPackedSourceDescription(string nomenclature, string role, string fileName)
        {
            if (nomenclature == PackedOrmNomenclature)
            {
                if (role == "ao")
                {
                    return "red(" + fileName + ")";
                }

                if (role == "roughness")
                {
                    return "green(" + fileName + ")";
                }

                return "blue(" + fileName + ")";
            }

            if (nomenclature == PackedRmaNomenclature)
            {
                if (role == "roughness")
                {
                    return "red(" + fileName + ")";
                }

                if (role == "metalness")
                {
                    return "green(" + fileName + ")";
                }

                return "blue(" + fileName + ")";
            }

            if (role == "metalness")
            {
                return "red(" + fileName + ")";
            }

            if (role == "roughness")
            {
                return "green(" + fileName + ")";
            }

            return "blue(" + fileName + ")";
        }

        private enum SourceRole
        {
            None,
            CodNog,
            Packed,
            Rgbm,
            Albedo,
            AmbientOcclusion,
            Roughness,
            Gloss,
            Metalness,
            Normal,
            Emissive,
            Alphatest,
            Translucent
        }

        private static SourceRole Classify(string name)
        {
            if (IsCodNogTextureName(name))
            {
                return SourceRole.CodNog;
            }

            if (name.EndsWith(PackedOrmNomenclature) || name.EndsWith(PackedRmaNomenclature) || name.EndsWith(PackedMraoNomenclature))
            {
                return SourceRole.Packed;
            }

            if (IsRgbmTextureName(name))
            {
                return SourceRole.Rgbm;
            }

            if (name.EndsWith(AlbedoNomenclature) || name.EndsWith(AlbedoAltNomenclature))
            {
                return SourceRole.Albedo;
            }

            if (name.EndsWith(AmbientOcclusionNomenclature) || name.EndsWith(AmbientOcclusionAltNomenclature))
            {
                return SourceRole.AmbientOcclusion;
            }

            if (name.EndsWith(RoughnessNomenclature))
            {
                return SourceRole.Roughness;
            }

            if (name.EndsWith(GlossNomenclature))
            {
                return SourceRole.Gloss;
            }

            if (name.EndsWith(MetalnessNomenclature) || name.EndsWith(MetalnessAltNomenclature))
            {
                return SourceRole.Metalness;
            }

            if (name.EndsWith(NormalNomenclature))
            {
                return SourceRole.Normal;
            }

            if (name.EndsWith(EmissiveNomenclature))
            {
                return SourceRole.Emissive;
            }

            if (name.EndsWith(AlphatestNomenclature))
            {
                return SourceRole.Alphatest;
            }

            if (name.EndsWith(TranslucentNomenclature))
            {
                return SourceRole.Translucent;
            }

            return SourceRole.None;
        }

        private static bool IsGrayRole(SourceRole role)
        {
            return role == SourceRole.AmbientOcclusion || role == SourceRole.Roughness || role == SourceRole.Gloss ||
                role == SourceRole.Metalness || role == SourceRole.Alphatest || role == SourceRole.Translucent;
        }

        private sealed class LoadedSource
        {
            public string File;
            public string Name;
            public string FileName;
            public SourceRole Role;
            public PixelBuffer Rgba;
            public GrayBuffer Gray;
            public SourceStats Stats;
            public bool HasAlpha;
        }

        private static void ResizeTo(PixelBuffer image, int width, int height)
        {
            if (image != null && (image.Width != width || image.Height != height))
            {
                image.Resize(width, height);
            }
        }

        private static void ResizeTo(GrayBuffer image, int width, int height)
        {
            if (image != null && (image.Width != width || image.Height != height))
            {
                image.Resize(width, height);
            }
        }

        #endregion

        public static async Task<SourceTextureSet> GenerateTextures(List<string> files, GenerateProperties props, CancellationToken cancellationToken = default)
        {
            const int PackedPriority = 10;
            const int DerivedPriority = 20;
            const int ExplicitPriority = 30;

            PixelBuffer albedo = null;
            PixelBuffer normal = null;
            PixelBuffer emissive = null;
            GrayBuffer ambientOcclusion = null;
            GrayBuffer roughness = null;
            GrayBuffer gloss = null;
            GrayBuffer metalness = null;
            GrayBuffer alphatestOpacity = null;
            GrayBuffer translucentOpacity = null;

            string albedoSource = null;
            string ambientOcclusionSource = null;
            string roughnessSource = null;
            string glossSource = null;
            string metalnessSource = null;
            string normalSource = null;
            string emissiveSource = null;
            string alphatestOpacitySource = null;
            string translucentOpacitySource = null;

            int albedoPriority = 0;
            int ambientOcclusionPriority = 0;
            int roughnessPriority = 0;
            int glossPriority = 0;
            int metalnessPriority = 0;
            int normalPriority = 0;
            int emissivePriority = 0;
            int alphatestOpacityPriority = 0;
            int translucentOpacityPriority = 0;

            int biggestWidth = 0;
            int biggestHeight = 0;

            void Grow(int width, int height)
            {
                biggestWidth = Math.Max(biggestWidth, width);
                biggestHeight = Math.Max(biggestHeight, height);
            }

            List<LoadedSource> sources = files
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(file =>
                {
                    string name = Path.GetFileNameWithoutExtension(file).ToLower();
                    return new LoadedSource() { File = file, Name = name, FileName = Path.GetFileName(file), Role = Classify(name) };
                })
                .Where(source => source.Role != SourceRole.None)
                .ToList();

            Stopwatch stageTimer = Stopwatch.StartNew();

            await Task.WhenAll(sources.Select(source => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsGrayRole(source.Role))
                {
                    source.Gray = ImageLoader.LoadGray(source.File, out source.Stats);
                }
                else
                {
                    source.Rgba = ImageLoader.LoadRgba(source.File, out source.HasAlpha, out source.Stats);
                }
            }, cancellationToken)));

            long decodeMs = stageTimer.ElapsedMilliseconds;
            stageTimer.Restart();

            foreach (LoadedSource source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string name = source.Name;
                string file = source.File;
                string fileName = source.FileName;
                PixelBuffer rgba = source.Rgba;
                GrayBuffer gray = source.Gray;
                source.Rgba = null;
                source.Gray = null;

                switch (source.Role)
                {
                    case SourceRole.CodNog:
                    {
                        LogSourceReport(file, GetCodNogRole(name), "R=gloss, B=AO, G/A=normal", source.Stats, props.LogFunc);
                        Grow(rgba.Width, rgba.Height);
                        GrayBuffer packedAo = null;
                        GrayBuffer packedGloss = null;
                        PixelBuffer packedNormal = null;
                        SplitCodNogTexture(rgba, ref packedAo, ref packedGloss, ref packedNormal);
                        TryAssignTexture(ref ambientOcclusion, ref ambientOcclusionSource, ref ambientOcclusionPriority, packedAo, "AO", "blue(" + fileName + ")", PackedPriority, props.LogFunc);
                        TryAssignTexture(ref gloss, ref glossSource, ref glossPriority, packedGloss, "gloss", "red(" + fileName + ")", PackedPriority, props.LogFunc);
                        TryAssignTexture(ref normal, ref normalSource, ref normalPriority, packedNormal, "normal", fileName + " (decoded NOG)", PackedPriority, props.LogFunc);
                        break;
                    }
                    case SourceRole.Packed:
                    {
                        string packedType = name.EndsWith(PackedOrmNomenclature) ? PackedOrmNomenclature
                            : name.EndsWith(PackedRmaNomenclature) ? PackedRmaNomenclature
                            : PackedMraoNomenclature;

                        LogSourceReport(file, packedType + " packed", GetPackedChannelDescription(packedType), source.Stats, props.LogFunc);
                        Grow(rgba.Width, rgba.Height);
                        GrayBuffer packedAo = null;
                        GrayBuffer packedRoughness = null;
                        GrayBuffer packedMetalness = null;
                        SplitPackedTexture(rgba, packedType, ref packedAo, ref packedRoughness, ref packedMetalness);
                        TryAssignTexture(ref ambientOcclusion, ref ambientOcclusionSource, ref ambientOcclusionPriority, packedAo, "AO", GetPackedSourceDescription(packedType, "ao", fileName), PackedPriority, props.LogFunc);
                        TryAssignTexture(ref roughness, ref roughnessSource, ref roughnessPriority, packedRoughness, "roughness", GetPackedSourceDescription(packedType, "roughness", fileName), PackedPriority, props.LogFunc);
                        TryAssignTexture(ref metalness, ref metalnessSource, ref metalnessPriority, packedMetalness, "metalness", GetPackedSourceDescription(packedType, "metalness", fileName), PackedPriority, props.LogFunc);

                        if (ambientOcclusion != null)
                        {
                            Grow(ambientOcclusion.Width, ambientOcclusion.Height);
                        }

                        break;
                    }
                    case SourceRole.Rgbm:
                    {
                        string rgbmRole = name.EndsWith(AlbedoMetalnessNomenclature) ? AlbedoMetalnessNomenclature : CodAlbedoSpecNomenclature;
                        LogSourceReport(file, rgbmRole, "RGB=albedo, A=metalness", source.Stats, props.LogFunc);
                        ImageLoader.SplitRgbm(rgba, source.HasAlpha, out PixelBuffer rgbmAlbedo, out GrayBuffer rgbmMetalness);
                        Grow(rgbmAlbedo.Width, rgbmAlbedo.Height);
                        TryAssignTexture(ref albedo, ref albedoSource, ref albedoPriority, rgbmAlbedo, "albedo", "rgb(" + fileName + ")", DerivedPriority, props.LogFunc);
                        TryAssignTexture(ref metalness, ref metalnessSource, ref metalnessPriority, rgbmMetalness, "metalness", "alpha(" + fileName + ")", DerivedPriority, props.LogFunc);
                        break;
                    }
                    case SourceRole.Albedo:
                        LogSourceReport(file, name.EndsWith(AlbedoNomenclature) ? AlbedoNomenclature : AlbedoAltNomenclature, "RGB=albedo", source.Stats, props.LogFunc);
                        Grow(rgba.Width, rgba.Height);
                        TryAssignTexture(ref albedo, ref albedoSource, ref albedoPriority, rgba, "albedo", fileName, ExplicitPriority, props.LogFunc);
                        break;
                    case SourceRole.AmbientOcclusion:
                        LogSourceReport(file, name.EndsWith(AmbientOcclusionNomenclature) ? AmbientOcclusionNomenclature : AmbientOcclusionAltNomenclature, "grayscale/RGB=AO", source.Stats, props.LogFunc);
                        Grow(gray.Width, gray.Height);
                        TryAssignTexture(ref ambientOcclusion, ref ambientOcclusionSource, ref ambientOcclusionPriority, gray, "AO", fileName, ExplicitPriority, props.LogFunc);
                        break;
                    case SourceRole.Roughness:
                        LogSourceReport(file, RoughnessNomenclature, "grayscale/RGB=roughness, inverted to gloss", source.Stats, props.LogFunc);
                        Grow(gray.Width, gray.Height);
                        TryAssignTexture(ref roughness, ref roughnessSource, ref roughnessPriority, gray, "roughness", fileName, ExplicitPriority, props.LogFunc);
                        break;
                    case SourceRole.Gloss:
                        LogSourceReport(file, GlossNomenclature, "grayscale/RGB=gloss", source.Stats, props.LogFunc);
                        Grow(gray.Width, gray.Height);
                        TryAssignTexture(ref gloss, ref glossSource, ref glossPriority, gray, "gloss", fileName, ExplicitPriority, props.LogFunc);
                        break;
                    case SourceRole.Metalness:
                        LogSourceReport(file, name.EndsWith(MetalnessNomenclature) ? MetalnessNomenclature : MetalnessAltNomenclature, "grayscale/RGB=metalness", source.Stats, props.LogFunc);
                        Grow(gray.Width, gray.Height);
                        TryAssignTexture(ref metalness, ref metalnessSource, ref metalnessPriority, gray, "metalness", fileName, ExplicitPriority, props.LogFunc);
                        break;
                    case SourceRole.Normal:
                        LogSourceReport(file, NormalNomenclature, "RGB=normal", source.Stats, props.LogFunc);
                        Grow(rgba.Width, rgba.Height);
                        TryAssignTexture(ref normal, ref normalSource, ref normalPriority, rgba, "normal", fileName, ExplicitPriority, props.LogFunc);
                        break;
                    case SourceRole.Emissive:
                        LogSourceReport(file, EmissiveNomenclature, "RGB=emissive", source.Stats, props.LogFunc);
                        Grow(rgba.Width, rgba.Height);
                        TryAssignTexture(ref emissive, ref emissiveSource, ref emissivePriority, rgba, "emissive", fileName, ExplicitPriority, props.LogFunc);
                        break;
                    case SourceRole.Alphatest:
                        LogSourceReport(file, AlphatestNomenclature, "grayscale/RGB=alphatest opacity", source.Stats, props.LogFunc);
                        Grow(gray.Width, gray.Height);
                        TryAssignTexture(ref alphatestOpacity, ref alphatestOpacitySource, ref alphatestOpacityPriority, gray, "alphatest opacity", fileName, ExplicitPriority, props.LogFunc);
                        break;
                    case SourceRole.Translucent:
                        LogSourceReport(file, TranslucentNomenclature, "grayscale/RGB=translucent opacity", source.Stats, props.LogFunc);
                        Grow(gray.Width, gray.Height);
                        TryAssignTexture(ref translucentOpacity, ref translucentOpacitySource, ref translucentOpacityPriority, gray, "translucent opacity", fileName, ExplicitPriority, props.LogFunc);
                        break;
                }
            }

            if (gloss != null && roughness != null)
            {
                props.LogFunc?.Invoke("Precedence: gloss using " + glossSource + "; roughness " + roughnessSource + " is ignored because a gloss map is present.");
            }

            //resolve opacity mode (prefer alphatest if both are present)
            GrayBuffer opacity = null;
            OpacityMode opacityMode = OpacityMode.None;

            if (alphatestOpacity != null)
            {
                opacity = alphatestOpacity;
                opacityMode = OpacityMode.Alphatest;

                if (translucentOpacity != null)
                {
                    props.LogFunc?.Invoke("Precedence: opacity using alphatest " + alphatestOpacitySource + "; ignoring translucent " + translucentOpacitySource + ".");
                }
            }
            else if (translucentOpacity != null)
            {
                opacity = translucentOpacity;
                opacityMode = OpacityMode.Translucent;
            }

            string glossSummary = gloss != null
                ? glossSource
                : roughness != null ? "inverted roughness(" + roughnessSource + ")" : null;
            string opacitySummary = opacityMode == OpacityMode.Alphatest
                ? "alphatest " + alphatestOpacitySource
                : opacityMode == OpacityMode.Translucent ? "translucent " + translucentOpacitySource : null;

            props.LogFunc?.Invoke("Texture summary: albedo: " + DescribeTextureSource(albedoSource) +
                ", metalness: " + DescribeTextureSource(metalnessSource) +
                ", normal: " + DescribeTextureSource(normalSource) +
                ", gloss: " + DescribeTextureSource(glossSummary) +
                ", AO: " + DescribeTextureSource(ambientOcclusionSource) +
                ", opacity: " + DescribeTextureSource(opacitySummary) +
                ", emissive: " + DescribeTextureSource(emissiveSource));

            long sortMs = stageTimer.ElapsedMilliseconds;
            stageTimer.Restart();

            int targetWidth = Math.Max(biggestWidth, 1);
            int targetHeight = Math.Max(biggestHeight, 1);
            double ratio = Math.Min((double)props.ClampSize / targetWidth, (double)props.ClampSize / targetHeight);

            if (ratio < 1.0)
            {
                targetWidth = Math.Max(1, (int)(targetWidth * ratio));
                targetHeight = Math.Max(1, (int)(targetHeight * ratio));
            }

            ResizeTo(albedo, targetWidth, targetHeight);
            ResizeTo(normal, targetWidth, targetHeight);
            ResizeTo(emissive, targetWidth, targetHeight);

            foreach (GrayBuffer mask in new[] { ambientOcclusion, roughness, gloss, metalness, opacity })
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResizeTo(mask, targetWidth, targetHeight);
            }

            if (roughness != null)
            {
                PixelKernels.Invert(roughness.Bytes);
            }

            if (normal != null && props.bOpenGlNormal)
            {
                PixelKernels.InvertChannel(normal.Bytes, (int)TextureChannel.Green);
            }

            if (normal != null && props.bInvertNormalBlue)
            {
                PixelKernels.InvertChannel(normal.Bytes, (int)TextureChannel.Blue);
            }

            if (opacity != null && props.bInvertOpacity)
            {
                PixelKernels.Invert(opacity.Bytes);
            }

            cancellationToken.ThrowIfCancellationRequested();

            GrayBuffer glossOrRoughness = gloss ?? roughness;

            Color averageMetallicColor = GetAverageMetallicColor(albedo, metalness);
            double averageRoughness = GetAverageRoughness(glossOrRoughness);

            Task<PixelBuffer> albedoTask = Task.Run(() => CreateSourceAlbedo(albedo, ambientOcclusion, metalness, opacity, props));
            Task<PixelBuffer> normalTask = Task.Run(() => CreateSourceNormal(normal, glossOrRoughness, ambientOcclusion, props));
            Task<PixelBuffer> exponentTask = Task.Run(() => CreateSourceExponent(glossOrRoughness, metalness, ambientOcclusion, props));

            await Task.WhenAll(albedoTask, normalTask, exponentTask);
            props.LogFunc?.Invoke("Timing: decode " + decodeMs + " ms, split " + sortMs + " ms, process " + stageTimer.ElapsedMilliseconds + " ms");

            IntermediateTextureSet intermediates = null;

            if (props.bKeepIntermediates)
            {
                intermediates = new IntermediateTextureSet(ambientOcclusion, glossOrRoughness, metalness);
            }

            return new SourceTextureSet(albedoTask.Result, exponentTask.Result, normalTask.Result, emissive,
                averageMetallicColor, averageRoughness, opacityMode, intermediates);
        }
    }
}
