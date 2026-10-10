using SharpGLTF.Schema2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace mwb_materials.MwbMats
{
    class TextureGenerationJob
    {
        public TextureGenerationJob(string displayName, string relativeFolder, string vmtFileName, string textureBaseName, List<string> files)
        {
            DisplayName = displayName;
            RelativeFolder = relativeFolder;
            VmtFileName = vmtFileName;
            TextureBaseName = textureBaseName;
            Files = files;
        }

        public string DisplayName { get; }
        public string RelativeFolder { get; }
        public string VmtFileName { get; }
        public string TextureBaseName { get; }
        public List<string> Files { get; }
        public PhongMaterialSource Phong { get; init; }
    }

    class ModelMaterialResolver
    {
        private static readonly string[] SmdExtensions = new string[] { ".smd" };
        private static readonly string[] GltfExtensions = new string[] { ".gltf", ".glb" };
        private static readonly string[] FbxExtensions = new string[] { ".fbx" };

        public static List<TextureGenerationJob> Resolve(string folderPath, string batchRootPath, List<string> textureFiles, Action<string> logFunc)
        {
            string fallbackName = Path.GetFileName(folderPath);
            List<TextureGenerationJob> fallback = new List<TextureGenerationJob>()
            {
                CreateJob(fallbackName, textureFiles, logFunc) ?? CreateUnsafeFallbackJob(fallbackName, textureFiles)
            };

            string[] modelFiles = Directory.GetFiles(folderPath)
                .Where(file => IsExtension(file, SmdExtensions) || IsExtension(file, GltfExtensions) || IsExtension(file, FbxExtensions))
                .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string[] fbxFiles = modelFiles.Where(file => IsExtension(file, FbxExtensions)).ToArray();

            foreach (string fbxFile in fbxFiles)
            {
                logFunc?.Invoke("FBX material binding is not supported; use glTF/GLB for automatic texture grouping: " + Path.GetFileName(fbxFile));
            }

            List<string> smdMaterials = modelFiles
                .Where(file => IsExtension(file, SmdExtensions))
                .SelectMany(file => ParseSmdMaterials(file, logFunc))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (smdMaterials.Count > 0)
            {
                logFunc?.Invoke("Found SMD materials: " + string.Join(", ", smdMaterials));
            }

            List<TextureGenerationJob> gltfJobs = modelFiles
                .Where(file => IsExtension(file, GltfExtensions))
                .SelectMany(file => ParseGltfJobs(file, batchRootPath, textureFiles, smdMaterials, logFunc))
                .ToList();

            if (gltfJobs.Count > 0)
            {
                return gltfJobs;
            }

            if (smdMaterials.Count == 1)
            {
                TextureGenerationJob smdJob = CreateJob(smdMaterials[0], textureFiles, logFunc);

                if (smdJob != null)
                {
                    logFunc?.Invoke("Using single SMD material name for folder textures: " + smdMaterials[0]);
                    return new List<TextureGenerationJob>() { smdJob };
                }
            }

            if (smdMaterials.Count > 1)
            {
                logFunc?.Invoke("Warning: multiple SMD materials found but no glTF/GLB texture bindings were available; using folder-based texture grouping for " + fallbackName + ".");
            }

            return fallback;
        }

        private static bool IsExtension(string file, string[] extensions)
        {
            string extension = Path.GetExtension(file);
            return extensions.Any(candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));
        }

        private static TextureGenerationJob CreateJob(string rawMaterialName, List<string> files, Action<string> logFunc)
        {
            string displayName;
            string relativeFolder;
            string leafName;

            if (!TrySanitizeMaterialName(rawMaterialName, out displayName, out relativeFolder, out leafName))
            {
                logFunc?.Invoke("Warning: ignored unsafe material name: " + rawMaterialName);
                return null;
            }

            return new TextureGenerationJob(displayName, relativeFolder, leafName + ".vmt", leafName, files.ToList());
        }

        private static TextureGenerationJob CreateUnsafeFallbackJob(string fallbackName, List<string> files)
        {
            string leafName = string.IsNullOrWhiteSpace(fallbackName) ? "material" : fallbackName.Trim();
            return new TextureGenerationJob(leafName, string.Empty, leafName + ".vmt", leafName, files.ToList());
        }

        private static bool TrySanitizeMaterialName(string rawName, out string displayName, out string relativeFolder, out string leafName)
        {
            displayName = null;
            relativeFolder = string.Empty;
            leafName = null;

            if (string.IsNullOrWhiteSpace(rawName))
            {
                return false;
            }

            string normalized = rawName.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (normalized.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - 4);
            }

            normalized = normalized.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
            {
                return false;
            }

            string[] parts = normalized.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 || parts.Any(part => part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                return false;
            }

            leafName = parts[parts.Length - 1];
            relativeFolder = parts.Length > 1 ? Path.Combine(parts.Take(parts.Length - 1).ToArray()) : string.Empty;
            displayName = string.IsNullOrEmpty(relativeFolder) ? leafName : Path.Combine(relativeFolder, leafName);
            return true;
        }

        private static IEnumerable<string> ParseSmdMaterials(string smdFile, Action<string> logFunc)
        {
            List<string> result = new List<string>();

            try
            {
                bool inTriangles = false;
                int vertexLinesRemaining = 0;

                foreach (string rawLine in File.ReadLines(smdFile))
                {
                    string line = rawLine.Trim();

                    if (line.Equals("triangles", StringComparison.OrdinalIgnoreCase))
                    {
                        inTriangles = true;
                        vertexLinesRemaining = 0;
                        continue;
                    }

                    if (!inTriangles)
                    {
                        continue;
                    }

                    if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (vertexLinesRemaining > 0)
                    {
                        vertexLinesRemaining--;
                        continue;
                    }

                    result.Add(line);
                    vertexLinesRemaining = 3;
                }
            }
            catch (Exception ex)
            {
                logFunc?.Invoke("Warning: could not parse SMD " + Path.GetFileName(smdFile) + ": " + ex.Message);
            }

            return result;
        }

        private static IEnumerable<TextureGenerationJob> ParseGltfJobs(string gltfFile, string batchRootPath, List<string> folderTextureFiles, List<string> smdMaterials, Action<string> logFunc)
        {
            List<TextureGenerationJob> result = new List<TextureGenerationJob>();

            try
            {
                ModelRoot.Load(gltfFile);
            }
            catch (Exception ex)
            {
                logFunc?.Invoke("Warning: SharpGLTF could not load " + Path.GetFileName(gltfFile) + ": " + ex.Message);
                return result;
            }

            Dictionary<string, object> document;

            try
            {
                document = ReadGltfJson(gltfFile);
            }
            catch (Exception ex)
            {
                logFunc?.Invoke("Warning: could not read glTF JSON from " + Path.GetFileName(gltfFile) + ": " + ex.Message);
                return result;
            }

            object[] materials = GetArray(document, "materials");
            object[] textures = GetArray(document, "textures");
            object[] images = GetArray(document, "images");

            if (materials == null || textures == null || images == null)
            {
                return result;
            }

            HashSet<string> smdMaterialSet = new HashSet<string>(smdMaterials, StringComparer.OrdinalIgnoreCase);
            bool filterToSmd = smdMaterialSet.Count > 0;
            bool foundMatchingSmdMaterial = false;

            foreach (object materialObject in materials)
            {
                Dictionary<string, object> material = materialObject as Dictionary<string, object>;

                if (material == null)
                {
                    continue;
                }

                string materialName = GetString(material, "name");

                if (string.IsNullOrWhiteSpace(materialName))
                {
                    materialName = Path.GetFileNameWithoutExtension(gltfFile);
                }

                if (filterToSmd && !smdMaterialSet.Contains(materialName))
                {
                    logFunc?.Invoke("glTF material ignored because it is not present in the SMD: " + materialName);
                    continue;
                }

                foundMatchingSmdMaterial = true;
                List<string> texturePaths = ResolveMaterialTexturePaths(material, textures, images, gltfFile, batchRootPath, folderTextureFiles, logFunc);

                if (texturePaths.Count == 0)
                {
                    logFunc?.Invoke("Warning: glTF material has no resolvable external texture files: " + materialName);
                    continue;
                }

                TextureGenerationJob job = CreateJob(materialName, texturePaths, logFunc);

                if (job != null)
                {
                    logFunc?.Invoke("Found glTF bindings: " + materialName + " -> " + string.Join(", ", texturePaths.Select(Path.GetFileName)));
                    result.Add(job);
                }
            }

            if (filterToSmd && !foundMatchingSmdMaterial)
            {
                logFunc?.Invoke("Warning: no glTF material names matched the SMD materials in " + Path.GetFileName(gltfFile) + ".");
            }

            return result;
        }

        private static Dictionary<string, object> ReadGltfJson(string gltfFile)
        {
            string extension = Path.GetExtension(gltfFile);
            string json = extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
                ? ReadGlbJson(gltfFile)
                : File.ReadAllText(gltfFile, Encoding.UTF8);

            using (JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions() { MaxDepth = 256, AllowTrailingCommas = true }))
            {
                return ToObject(document.RootElement) as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
        }

        private static object ToObject(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, object> result = new Dictionary<string, object>();

                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        result[property.Name] = ToObject(property.Value);
                    }

                    return result;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(ToObject).ToArray();
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.TryGetInt64(out long integer) ? (object)integer : element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }

        private static string ReadGlbJson(string glbFile)
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(glbFile)))
            {
                uint magic = reader.ReadUInt32();
                uint version = reader.ReadUInt32();
                reader.ReadUInt32();

                if (magic != 0x46546C67 || version != 2)
                {
                    throw new InvalidDataException("Unsupported GLB header.");
                }

                uint chunkLength = reader.ReadUInt32();
                uint chunkType = reader.ReadUInt32();

                if (chunkType != 0x4E4F534A)
                {
                    throw new InvalidDataException("First GLB chunk is not JSON.");
                }

                byte[] jsonBytes = reader.ReadBytes((int)chunkLength);
                return Encoding.UTF8.GetString(jsonBytes).TrimEnd('\0', ' ', '\t', '\r', '\n');
            }
        }

        private static List<string> ResolveMaterialTexturePaths(Dictionary<string, object> material, object[] textures, object[] images, string gltfFile, string batchRootPath, List<string> folderTextureFiles, Action<string> logFunc)
        {
            List<string> result = new List<string>();
            List<int> textureIndices = new List<int>();

            AddTextureIndex(textureIndices, GetDictionary(material, "normalTexture"));
            AddTextureIndex(textureIndices, GetDictionary(material, "occlusionTexture"));
            AddTextureIndex(textureIndices, GetDictionary(material, "emissiveTexture"));

            Dictionary<string, object> pbr = GetDictionary(material, "pbrMetallicRoughness");

            if (pbr != null)
            {
                AddTextureIndex(textureIndices, GetDictionary(pbr, "baseColorTexture"));
                AddTextureIndex(textureIndices, GetDictionary(pbr, "metallicRoughnessTexture"));
            }

            Dictionary<string, object> extensions = GetDictionary(material, "extensions");
            Dictionary<string, object> specGloss = extensions != null ? GetDictionary(extensions, "KHR_materials_pbrSpecularGlossiness") : null;

            if (specGloss != null)
            {
                AddTextureIndex(textureIndices, GetDictionary(specGloss, "diffuseTexture"));
                AddTextureIndex(textureIndices, GetDictionary(specGloss, "specularGlossinessTexture"));
            }

            foreach (int textureIndex in textureIndices.Distinct())
            {
                if (textureIndex < 0 || textureIndex >= textures.Length)
                {
                    continue;
                }

                Dictionary<string, object> texture = textures[textureIndex] as Dictionary<string, object>;
                int imageIndex = GetInt(texture, "source", -1);

                if (imageIndex < 0 || imageIndex >= images.Length)
                {
                    continue;
                }

                Dictionary<string, object> image = images[imageIndex] as Dictionary<string, object>;
                string uri = GetString(image, "uri");
                string resolved = null;

                if (!string.IsNullOrWhiteSpace(uri) && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = ResolveTextureUri(uri, gltfFile, batchRootPath, folderTextureFiles);
                }
                else
                {
                    string imageName = GetString(image, "name");
                    resolved = ResolveEmbeddedTextureName(imageName, gltfFile, batchRootPath, folderTextureFiles, logFunc);

                    if (string.IsNullOrEmpty(resolved) && image != null && image.ContainsKey("bufferView"))
                    {
                        logFunc?.Invoke("Warning: glTF image is embedded and no matching texture file was found by image name: " + imageName);
                    }
                }

                if (!string.IsNullOrEmpty(resolved) && !result.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(resolved);
                }
            }

            return result;
        }

        private static void AddTextureIndex(List<int> textureIndices, Dictionary<string, object> textureInfo)
        {
            int index = GetInt(textureInfo, "index", -1);

            if (index >= 0)
            {
                textureIndices.Add(index);
            }
        }

        private static string ResolveTextureUri(string uri, string gltfFile, string batchRootPath, List<string> folderTextureFiles)
        {
            string localUri = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string directPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gltfFile), localUri));

            if (File.Exists(directPath))
            {
                return directPath;
            }

            string rootPath = Path.GetFullPath(batchRootPath);

            if (directPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                string directFileName = Path.GetFileName(directPath);
                string matched = folderTextureFiles.FirstOrDefault(file => string.Equals(Path.GetFileName(file), directFileName, StringComparison.OrdinalIgnoreCase));

                if (matched != null)
                {
                    return matched;
                }
            }

            return null;
        }

        private static string ResolveEmbeddedTextureName(string imageName, string gltfFile, string batchRootPath, List<string> folderTextureFiles, Action<string> logFunc)
        {
            if (string.IsNullOrWhiteSpace(imageName))
            {
                return null;
            }

            string imageStem = Path.GetFileNameWithoutExtension(imageName.Trim());

            if (string.IsNullOrWhiteSpace(imageStem))
            {
                imageStem = imageName.Trim();
            }

            string matched = FindTextureByStem(imageStem, Path.GetDirectoryName(gltfFile), folderTextureFiles);

            if (matched == null)
            {
                matched = FindTextureByStem(imageStem, batchRootPath, folderTextureFiles);
            }

            if (matched != null)
            {
                logFunc?.Invoke("Resolved embedded glTF image name " + imageName + " -> " + Path.GetFileName(matched));
            }

            return matched;
        }

        private static string FindTextureByStem(string imageStem, string searchRoot, List<string> folderTextureFiles)
        {
            string matched = folderTextureFiles.FirstOrDefault(file =>
                string.Equals(Path.GetFileNameWithoutExtension(file), imageStem, StringComparison.OrdinalIgnoreCase));

            if (matched != null)
            {
                return matched;
            }

            if (string.IsNullOrEmpty(searchRoot) || !Directory.Exists(searchRoot))
            {
                return null;
            }

            try
            {
                return Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                    .Where(IsPotentialTextureFile)
                    .Where(file => !IsGeneratedFolderPath(file))
                    .OrderBy(file => file.Length)
                    .FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), imageStem, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPotentialTextureFile(string file)
        {
            return ImageLoader.HasSupportedExtension(file);
        }

        private static bool IsGeneratedFolderPath(string file)
        {
            string[] parts = Path.GetFullPath(file)
                .Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            return parts.Any(part =>
                string.Equals(part, "output", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "temp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "temp_debug", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
            {
                return null;
            }

            return obj[key] as Dictionary<string, object>;
        }

        private static object[] GetArray(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
            {
                return null;
            }

            object value = obj[key];
            return value as object[];
        }

        private static string GetString(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
            {
                return null;
            }

            return obj[key] as string;
        }

        private static int GetInt(Dictionary<string, object> obj, string key, int fallback)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToInt32(obj[key]);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
