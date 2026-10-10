using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VtfNet;

namespace mwb_materials.MwbMats
{
    /*
    Converts existing Source phong materials (VertexLitGeneric vmt + vtf) into PBR source sets.

    exponent red   - phong exponent (1 + 149 * R), converted to GGX gloss
    exponent green - $phongalbedotint mask, used as metalness
    normal alpha   - phong/envmap mask ($normalmapalphaenvmapmask), varies the gloss so wear survives
    diffuse        - albedo; metal is brightened because phong lit it with $phongalbedoboost on top of a dark diffuse

    phong materials made by this tool keep the PBR albedo as is and store gloss in the exponent alpha (rimlight mask),
    so with FromMwbMats those are read back directly
    */

    sealed class PhongMaterialAlias
    {
        public PhongMaterialAlias(string vmtFileName, Dictionary<string, string> parameters)
        {
            VmtFileName = vmtFileName;
            Parameters = parameters;
        }

        public string VmtFileName { get; }
        public Dictionary<string, string> Parameters { get; }
    }

    public sealed class PhongConversionSettings
    {
        public float MetalBoost { get; set; } = 4.5f;
        public float MetalMax { get; set; } = 0.65f;
        public float GlossMaskInfluence { get; set; } = 0.3f;

        // the source was made by this tool's phong presets, so its channels can be inverted exactly instead of guessed
        public bool FromMwbMats { get; set; }
    }

    sealed class PhongMaterialSource
    {
        public PhongConversionSettings Settings;
        public Action<string> Log;
        public string MaterialsRoot;
        public Dictionary<string, string> Parameters;
        public List<PhongMaterialAlias> Materials = new List<PhongMaterialAlias>();
    }

    static class PhongMaterialImporter
    {
        private static readonly string[] KeptParameters = new[] { "$surfaceprop", "$nocull", "$alphatestreference" };
        private static readonly string[] DiffuseSuffixes = new[] { "_diffuse", "_albedo", "_color", "_basecolor", "_rgb", "_d", "_c" };
        private static readonly string[] OutputSuffixes = new[] { "_rgb", "_n", "_e" };

        public static bool HasVmtFiles(string folderPath)
        {
            return Directory.EnumerateFiles(folderPath, "*.vmt").Any();
        }

        public static List<TextureGenerationJob> Resolve(string folderPath, PhongConversionSettings settings, Action<string> logFunc)
        {
            List<TextureGenerationJob> jobs = new List<TextureGenerationJob>();
            Dictionary<string, PhongMaterialSource> sets = new Dictionary<string, PhongMaterialSource>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TextureGenerationJob> jobsByKey = new Dictionary<string, TextureGenerationJob>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hintedMwbMats = false;

            foreach (string vmt in Directory.GetFiles(folderPath, "*.vmt").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string materialsRoot = FindMaterialsRoot(vmt);

                if (materialsRoot == null)
                {
                    logFunc?.Invoke("Skipping " + Path.GetFileName(vmt) + ": not inside a materials folder, so its texture paths cannot be resolved.");
                    continue;
                }

                if (!TryReadMaterial(vmt, materialsRoot, out string shader, out Dictionary<string, string> parameters, logFunc))
                {
                    continue;
                }

                if (!shader.Equals("VertexLitGeneric", StringComparison.OrdinalIgnoreCase) ||
                    !parameters.TryGetValue("$basetexture", out string baseTexture) ||
                    !parameters.TryGetValue("$bumpmap", out string bumpMap))
                {
                    continue;
                }

                if (!hintedMwbMats && settings?.FromMwbMats != true && LooksLikeMwbMats(parameters))
                {
                    hintedMwbMats = true;
                    logFunc?.Invoke(Path.GetFileName(vmt) + " looks like it was made with MWB Mats; tick \"Made with MWB Mats\" to read its gloss and albedo back exactly.");
                }

                if (FindTexture(materialsRoot, baseTexture) == null || FindTexture(materialsRoot, bumpMap) == null)
                {
                    logFunc?.Invoke("Skipping " + Path.GetFileName(vmt) + ": $basetexture or $bumpmap vtf not found under " + materialsRoot);
                    continue;
                }

                if (parameters.TryGetValue("$phongexponenttexture", out string exponentTexture) && FindTexture(materialsRoot, exponentTexture) == null)
                {
                    logFunc?.Invoke(Path.GetFileName(vmt) + ": $phongexponenttexture " + exponentTexture + " not found, using $phongexponent instead.");
                    parameters.Remove("$phongexponenttexture");
                }

                string key = string.Join("|", new[] { "$basetexture", "$bumpmap", "$phongexponenttexture", "$phongexponent", "$alphatest", "$translucent",
                    "$basemapalphaphongmask", "$normalmapalphaenvmapmask" }.Select(name => parameters.TryGetValue(name, out string value) ? value.ToLowerInvariant() : string.Empty));
                string vmtFileName = Path.GetFileName(vmt);

                if (jobsByKey.TryGetValue(key, out TextureGenerationJob existing))
                {
                    existing.Phong.Materials.Add(new PhongMaterialAlias(vmtFileName, parameters));
                    continue;
                }

                string baseName = GetTextureBaseName(baseTexture);

                // the output lands at the same path in another addon, so it must not reuse a source texture's name
                if (OutputSuffixes.Any(suffix => File.Exists(Path.Combine(folderPath, baseName + suffix + ".vtf"))))
                {
                    baseName += "_pbr";
                }

                string uniqueName = baseName;

                for (int i = 2; !baseNames.Add(uniqueName); i++)
                {
                    uniqueName = baseName + i;
                }

                PhongMaterialSource source = new PhongMaterialSource() { Settings = settings ?? new PhongConversionSettings(), Log = logFunc, MaterialsRoot = materialsRoot, Parameters = parameters };
                source.Materials.Add(new PhongMaterialAlias(vmtFileName, parameters));
                TextureGenerationJob job = new TextureGenerationJob(Path.GetFileNameWithoutExtension(vmt), string.Empty, vmtFileName, uniqueName, new List<string>()) { Phong = source };
                jobsByKey.Add(key, job);
                jobs.Add(job);
            }

            if (jobs.Count > 0)
            {
                int materials = jobs.Sum(job => job.Phong.Materials.Count);
                logFunc?.Invoke("Found " + materials + " phong material" + (materials == 1 ? "" : "s") + " in " + folderPath + " using " + jobs.Count + " texture set" + (jobs.Count == 1 ? "" : "s") + ".");
            }

            return jobs;
        }

        public static Task<MaterialManipulation.SourceSet> LoadAsync(TextureGenerationJob job, CancellationToken cancellationToken)
        {
            return Task.Run(() => Load(job, cancellationToken), cancellationToken);
        }

        private static MaterialManipulation.SourceSet Load(TextureGenerationJob job, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            PhongMaterialSource source = job.Phong;
            Dictionary<string, string> p = source.Parameters;
            float metalBoost = source.Settings.MetalBoost;
            float metalMax = source.Settings.MetalMax;
            float glossMaskInfluence = source.Settings.GlossMaskInfluence;
            bool fromMwbMats = source.Settings.FromMwbMats;

            PixelBuffer diffuse = LoadVtf(source.MaterialsRoot, p["$basetexture"]);
            PixelBuffer normal = LoadVtf(source.MaterialsRoot, p["$bumpmap"]);
            PixelBuffer exponent = p.TryGetValue("$phongexponenttexture", out string exponentName) ? LoadVtf(source.MaterialsRoot, exponentName) : null;
            cancellationToken.ThrowIfCancellationRequested();

            int width = Math.Max(diffuse.Width, Math.Max(normal.Width, exponent?.Width ?? 0));
            int height = Math.Max(diffuse.Height, Math.Max(normal.Height, exponent?.Height ?? 0));
            ResizeTo(diffuse, width, height);
            ResizeTo(normal, width, height);
            ResizeTo(exponent, width, height);

            int count = width * height;
            byte[] diffuseBytes = diffuse.Bytes;
            byte[] normalBytes = normal.Bytes;
            byte[] exponentBytes = exponent?.Bytes;
            bool maskInNormal = IsSet(p, "$normalmapalphaenvmapmask");
            bool maskInBase = IsSet(p, "$basemapalphaphongmask");
            bool alphatest = IsSet(p, "$alphatest");
            bool translucent = !alphatest && IsSet(p, "$translucent");
            float constantExponent = ParseFloat(p, "$phongexponent", 20f);

            // average spec mask, so the mask only redistributes gloss instead of darkening everything
            double maskSum = 0;

            if (maskInNormal || maskInBase)
            {
                byte[] maskBytes = maskInNormal ? normalBytes : diffuseBytes;

                for (int i = 0; i < count; i++)
                {
                    maskSum += maskBytes[i * 4 + 3];
                }
            }

            float maskScale = maskSum > 0 && !fromMwbMats ? (float)(255.0 * count / (maskSum * 2.0)) : 0f;
            MwbGlossSource mwbGloss = fromMwbMats ? FindMwbGlossSource(exponentBytes, maskInNormal, count) : MwbGlossSource.None;
            float[] toLinear = Enumerable.Range(0, 256).Select(value => SrgbToLinear(value / 255f)).ToArray();

            byte[] albedo = new byte[count * 4];
            byte[] normalOut = new byte[count * 4];
            byte[] gloss = new byte[count];
            byte[] metal = new byte[count];
            byte[] opacity = alphatest || translucent ? new byte[count] : null;

            ParallelPixels.For(count, (start, end) =>
            {
                for (int i = start; i < end; i++)
                {
                    int o = i * 4;
                    float phongExponent = exponentBytes != null ? 1f + 149f * exponentBytes[o] / 255f : constantExponent;
                    float metalness = exponentBytes != null ? exponentBytes[o + 1] / 255f : 0f;
                    float alpha = MathF.Sqrt(2f / (phongExponent + 2f));
                    float g = mwbGloss != MwbGlossSource.None ? ReadMwbGloss(mwbGloss, exponentBytes, normalBytes, o, metalness) : 1f - MathF.Sqrt(alpha);

                    if (maskScale > 0)
                    {
                        byte mask = maskInNormal ? normalBytes[o + 3] : diffuseBytes[o + 3];
                        g *= 1f - glossMaskInfluence + glossMaskInfluence * Math.Clamp(mask * maskScale / 255f, 0f, 1f);
                    }

                    for (int c = 0; c < 3; c++)
                    {
                        normalOut[o + c] = normalBytes[o + c];

                        if (fromMwbMats)
                        {
                            albedo[o + c] = diffuseBytes[o + c];
                            continue;
                        }

                        float linear = toLinear[diffuseBytes[o + c]];
                        float boosted = MathF.Min(linear * metalBoost, metalMax);
                        linear += (boosted - linear) * metalness;
                        albedo[o + c] = ToByte(LinearToSrgb(linear));
                    }

                    albedo[o + 3] = 255;
                    normalOut[o + 3] = 255;
                    gloss[i] = ToByte(g);
                    metal[i] = ToByte(metalness);

                    if (opacity != null)
                    {
                        opacity[i] = diffuseBytes[o + 3];
                    }
                }
            });

            if (fromMwbMats && mwbGloss != MwbGlossSource.ExponentAlpha)
            {
                job.Phong.Log?.Invoke(job.DisplayName + ": exponent alpha holds no gloss, reading it back from the " +
                    (mwbGloss == MwbGlossSource.ExponentRed ? "phong exponent" : mwbGloss == MwbGlossSource.NormalAlpha ? "normal alpha" : "$phongexponent") + " instead.");
            }

            string name = job.TextureBaseName;
            List<MaterialManipulation.LoadedSource> sources = new List<MaterialManipulation.LoadedSource>()
            {
                Rgba(name + "_c", MaterialManipulation.SourceRole.Albedo, new PixelBuffer(width, height, albedo), source.Parameters["$basetexture"]),
                Rgba(name + "_n", MaterialManipulation.SourceRole.Normal, new PixelBuffer(width, height, normalOut), source.Parameters["$bumpmap"]),
                Gray(name + "_g", MaterialManipulation.SourceRole.Gloss, new GrayBuffer(width, height, gloss), exponentName ?? "$phongexponent"),
                Gray(name + "_m", MaterialManipulation.SourceRole.Metalness, new GrayBuffer(width, height, metal), exponentName ?? "none")
            };

            if (opacity != null)
            {
                sources.Add(alphatest
                    ? Gray(name + "_t", MaterialManipulation.SourceRole.Alphatest, new GrayBuffer(width, height, opacity), source.Parameters["$basetexture"])
                    : Gray(name + "_opacity", MaterialManipulation.SourceRole.Translucent, new GrayBuffer(width, height, opacity), source.Parameters["$basetexture"]));
            }

            return new MaterialManipulation.SourceSet() { Sources = sources, DecodeMs = timer.ElapsedMilliseconds };
        }

        private enum MwbGlossSource
        {
            None,
            ExponentAlpha,
            ExponentRed,
            NormalAlpha
        }

        // the phong presets of this tool tint non-metal with $color2 through the base alpha and keep metal in $phongalbedotint
        private static bool LooksLikeMwbMats(Dictionary<string, string> parameters)
        {
            return IsSet(parameters, "$blendtintbybasealpha") && IsSet(parameters, "$phongalbedotint") && parameters.ContainsKey("$color2") &&
                parameters.ContainsKey("$phongexponenttexture") && IsSet(parameters, "$normalmapalphaenvmapmask");
        }

        private static MwbGlossSource FindMwbGlossSource(byte[] exponentBytes, bool maskInNormal, int count)
        {
            if (exponentBytes == null)
            {
                return maskInNormal ? MwbGlossSource.NormalAlpha : MwbGlossSource.None;
            }

            // exponent alpha is the gloss (times AO); a flat alpha means the texture has none, e.g. an alpha-less format
            byte first = exponentBytes[3];

            for (int i = 1; i < count; i++)
            {
                if (exponentBytes[i * 4 + 3] != first)
                {
                    return MwbGlossSource.ExponentAlpha;
                }
            }

            return MwbGlossSource.ExponentRed;
        }

        // inverts the phong encodings in MaterialManipulation (CreateSourceExponent, CreateSourceNormal)
        private static float ReadMwbGloss(MwbGlossSource gloss, byte[] exponentBytes, byte[] normalBytes, int o, float metalness)
        {
            switch (gloss)
            {
                case MwbGlossSource.ExponentAlpha:
                    return exponentBytes[o + 3] / 255f;
                case MwbGlossSource.ExponentRed:
                    float exponent = Math.Max(exponentBytes[o] - 1f, 0f) / 255f / (1f - 0.5f * metalness);
                    return MathF.Pow(Math.Min(exponent, 1f), 0.25f);
                case MwbGlossSource.NormalAlpha:
                    return MathF.Pow(Math.Max(normalBytes[o + 3] - 1f, 0f) / 255f, 1f / 2.5f);
                default:
                    throw new ArgumentOutOfRangeException(nameof(gloss));
            }
        }

        // Writes one vmt per material sharing this texture set and carries over the parameters that matter to gameplay.
        public static void WriteMaterialVmts(TextureGenerationJob job, string generatedVmtPath, Action<string> logFunc)
        {
            string template = File.ReadAllText(generatedVmtPath);
            string folder = Path.GetDirectoryName(generatedVmtPath);

            foreach (PhongMaterialAlias material in job.Phong.Materials)
            {
                StringBuilder extra = new StringBuilder();

                foreach (string name in KeptParameters)
                {
                    if (material.Parameters.TryGetValue(name, out string value) && template.IndexOf("\"" + name + "\"", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        extra.Append("    \"").Append(name).Append("\" \"").Append(value).Append("\"\r\n");
                    }
                }

                int close = template.LastIndexOf('}');
                string content = close < 0 ? template : template.Substring(0, close).TrimEnd() + "\r\n" + extra + "}\r\n";
                string path = Path.Combine(folder, Path.GetFileNameWithoutExtension(material.VmtFileName) + ".vmt");
                TextureExporter.WriteAllBytesLocked(path, Encoding.UTF8.GetBytes(content));

                if (!path.Equals(generatedVmtPath, StringComparison.OrdinalIgnoreCase))
                {
                    logFunc?.Invoke("Wrote " + path + " (shares textures with " + Path.GetFileName(generatedVmtPath) + ")");
                }
            }
        }

        #region vmt parsing

        private static bool TryReadMaterial(string vmtPath, string materialsRoot, out string shader, out Dictionary<string, string> parameters, Action<string> logFunc, int depth = 0)
        {
            shader = null;
            parameters = null;

            try
            {
                KeyValue root = ParseKeyValues(File.ReadAllText(vmtPath, Encoding.Latin1));

                if (root == null)
                {
                    return false;
                }

                shader = root.Key;
                parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (shader.Equals("patch", StringComparison.OrdinalIgnoreCase))
                {
                    string include = root.Children.FirstOrDefault(child => child.Key.Equals("include", StringComparison.OrdinalIgnoreCase))?.Value;
                    string includePath = include == null || depth > 4 ? null : FindFile(Path.GetDirectoryName(materialsRoot), include);

                    if (includePath == null || !TryReadMaterial(includePath, materialsRoot, out shader, out parameters, logFunc, depth + 1))
                    {
                        logFunc?.Invoke("Skipping " + Path.GetFileName(vmtPath) + ": patch include " + include + " not found.");
                        return false;
                    }

                    foreach (KeyValue block in root.Children.Where(child => child.Children != null &&
                        (child.Key.Equals("insert", StringComparison.OrdinalIgnoreCase) || child.Key.Equals("replace", StringComparison.OrdinalIgnoreCase))))
                    {
                        AddParameters(block, parameters);
                    }

                    return true;
                }

                AddParameters(root, parameters);
                return true;
            }
            catch (Exception ex)
            {
                logFunc?.Invoke("Skipping " + Path.GetFileName(vmtPath) + ": " + ex.Message);
                return false;
            }
        }

        private static void AddParameters(KeyValue block, Dictionary<string, string> parameters)
        {
            foreach (KeyValue child in block.Children)
            {
                if (child.Value != null && child.Key.StartsWith("$"))
                {
                    parameters[child.Key.ToLowerInvariant()] = child.Value.Replace('\\', '/').Trim();
                }
            }
        }

        private sealed class KeyValue
        {
            public string Key;
            public string Value;
            public List<KeyValue> Children;
        }

        private static KeyValue ParseKeyValues(string text)
        {
            List<string> tokens = Tokenize(text);
            int index = 0;
            return tokens.Count >= 2 ? ParsePair(tokens, ref index) : null;
        }

        private static KeyValue ParsePair(List<string> tokens, ref int index)
        {
            KeyValue pair = new KeyValue() { Key = tokens[index++] };

            if (index >= tokens.Count)
            {
                return pair;
            }

            if (tokens[index] == "{")
            {
                index++;
                pair.Children = new List<KeyValue>();

                while (index < tokens.Count && tokens[index] != "}")
                {
                    pair.Children.Add(ParsePair(tokens, ref index));
                }

                index++;
            }
            else
            {
                pair.Value = tokens[index++];
            }

            return pair;
        }

        private static List<string> Tokenize(string text)
        {
            List<string> tokens = new List<string>();
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (c == '{' || c == '}')
                {
                    tokens.Add(c.ToString());
                    i++;
                }
                else if (c == '"')
                {
                    int end = text.IndexOf('"', i + 1);
                    end = end < 0 ? text.Length : end;
                    tokens.Add(text.Substring(i + 1, end - i - 1));
                    i = end + 1;
                }
                else
                {
                    int start = i;

                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
                    {
                        i++;
                    }

                    tokens.Add(text.Substring(start, i - start));
                }
            }

            return tokens;
        }

        #endregion

        #region files

        private static string FindMaterialsRoot(string vmtPath)
        {
            for (DirectoryInfo dir = new FileInfo(vmtPath).Directory; dir != null; dir = dir.Parent)
            {
                if (dir.Name.Equals("materials", StringComparison.OrdinalIgnoreCase))
                {
                    return dir.FullName;
                }
            }

            return null;
        }

        private static string FindTexture(string materialsRoot, string textureName)
        {
            return FindFile(materialsRoot, textureName + ".vtf");
        }

        // Source paths are case insensitive, the file system may not be
        private static string FindFile(string root, string relativePath)
        {
            string current = root;

            foreach (string part in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                string exact = Path.Combine(current, part);

                if (File.Exists(exact) || Directory.Exists(exact))
                {
                    current = exact;
                    continue;
                }

                if (!Directory.Exists(current))
                {
                    return null;
                }

                string match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(entry => Path.GetFileName(entry).Equals(part, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    return null;
                }

                current = match;
            }

            return File.Exists(current) ? current : null;
        }

        private static PixelBuffer LoadVtf(string materialsRoot, string textureName)
        {
            string path = FindTexture(materialsRoot, textureName) ?? throw new FileNotFoundException("vtf not found", textureName);
            VtfFile vtf = VtfFile.Load(path);
            return new PixelBuffer(vtf.Width, vtf.Height, vtf.GetImageRgba());
        }

        private static string GetTextureBaseName(string textureName)
        {
            string name = Path.GetFileName(textureName.Replace('\\', '/')).ToLowerInvariant();

            foreach (string suffix in DiffuseSuffixes)
            {
                if (name.EndsWith(suffix) && name.Length > suffix.Length)
                {
                    return name.Substring(0, name.Length - suffix.Length);
                }
            }

            return name;
        }

        #endregion

        #region helpers

        private static MaterialManipulation.LoadedSource Rgba(string name, MaterialManipulation.SourceRole role, PixelBuffer buffer, string origin)
        {
            return new MaterialManipulation.LoadedSource()
            {
                File = origin + " (" + name + ")",
                Name = name,
                FileName = name,
                Role = role,
                Rgba = buffer,
                HasAlpha = false,
                Stats = ComputeStats(buffer.Width, buffer.Height, buffer.Bytes, 4)
            };
        }

        private static MaterialManipulation.LoadedSource Gray(string name, MaterialManipulation.SourceRole role, GrayBuffer buffer, string origin)
        {
            return new MaterialManipulation.LoadedSource()
            {
                File = origin + " (" + name + ")",
                Name = name,
                FileName = name,
                Role = role,
                Gray = buffer,
                Stats = ComputeStats(buffer.Width, buffer.Height, buffer.Bytes, 1)
            };
        }

        private static SourceStats ComputeStats(int width, int height, byte[] bytes, int stride)
        {
            SourceStats stats = new SourceStats() { Width = width, Height = height };

            for (int i = 0; i < width * height; i++)
            {
                int value = stride == 4 ? (bytes[i * 4] + bytes[i * 4 + 1] + bytes[i * 4 + 2]) / 3 : bytes[i];
                int alpha = stride == 4 ? bytes[i * 4 + 3] : 255;
                stats.GrayMin = Math.Min(stats.GrayMin, value);
                stats.GrayMax = Math.Max(stats.GrayMax, value);
                stats.GraySum += value;
                stats.AlphaMin = Math.Min(stats.AlphaMin, alpha);
                stats.AlphaMax = Math.Max(stats.AlphaMax, alpha);
                stats.AlphaSum += alpha;
            }

            return stats;
        }

        private static void ResizeTo(PixelBuffer image, int width, int height)
        {
            if (image != null && (image.Width != width || image.Height != height))
            {
                image.Resize(width, height);
            }
        }

        private static bool IsSet(Dictionary<string, string> parameters, string name)
        {
            return parameters.TryGetValue(name, out string value) && value.Trim() == "1";
        }

        private static float ParseFloat(Dictionary<string, string> parameters, string name, float fallback)
        {
            return parameters.TryGetValue(name, out string value) &&
                float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float result) ? result : fallback;
        }

        private static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        private static float LinearToSrgb(float c)
        {
            return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        }

        private static byte ToByte(float value)
        {
            return (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);
        }

        #endregion
    }
}
