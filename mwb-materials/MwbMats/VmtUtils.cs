using System;
using System.Drawing;
using System.Linq;

namespace mwb_materials.MwbMats
{
    class VmtUtils
    {
        private static readonly EnvMapFile[] EnvMaps = new EnvMapFile[]
        {
            new EnvMapFile{ Name = "specularity_00", Content = EmbeddedResources.Read("envmaps.specularity_00.vtf"), Roughness = 0.5 },
            new EnvMapFile{ Name = "specularity_25", Content = EmbeddedResources.Read("envmaps.specularity_25.vtf"), Roughness = 0.7 },
            new EnvMapFile{ Name = "specularity_50", Content = EmbeddedResources.Read("envmaps.specularity_50.vtf"), Roughness = 0.8 },
            new EnvMapFile{ Name = "specularity_75", Content = EmbeddedResources.Read("envmaps.specularity_75.vtf"), Roughness = 0.85 },
            new EnvMapFile{ Name = "specularity_100", Content = EmbeddedResources.Read("envmaps.specularity_100.vtf"), Roughness = 0.9 },
        };

        public class EnvMapFile
        {
            public string Name { get; internal set; }
            public double Roughness { get; internal set; }
            public byte[] Content { get; internal set; }
        }

        public static string GetVMTVector(Color color)
        {
            System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
            return "[" + Math.Round(color.R / 255.0, 3).ToString(invariant) + " " + Math.Round(color.G / 255.0, 3).ToString(invariant) + " " + Math.Round(color.B / 255.0, 3).ToString(invariant) + "]";
        }

        public static EnvMapFile GetEnvMapTextureFromRoughness(double averageRoughness)
        {
            EnvMapFile file = EnvMaps[0];

            foreach (EnvMapFile envmap in EnvMaps)
            {
                if (averageRoughness >= envmap.Roughness)
                {
                    file = envmap;
                }
            }

            return file;
        }

        public static string GetVMTPath(string originalPath, string description = null, Action<string> logFunc = null)
        {
            if (string.IsNullOrEmpty(originalPath))
            {
                return string.Empty;
            }

            string[] parts = originalPath.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            int materialsIndex = Array.FindLastIndex(parts, part => string.Equals(part, "materials", StringComparison.OrdinalIgnoreCase));

            if (materialsIndex >= 0)
            {
                return string.Join("\\", parts.Skip(materialsIndex + 1));
            }

            string folder = parts.Length > 0 ? parts[^1] : string.Empty;
            logFunc?.Invoke("Warning: " + (description ?? "Output") + " folder \"" + originalPath + "\" is not inside a \"materials\" folder, so VMT paths use \"" + folder + "\"");
            return folder;
        }

        public static string JoinVMTPath(string folder, string name)
        {
            return string.IsNullOrEmpty(folder) ? name : folder + "\\" + name;
        }
    }
}
