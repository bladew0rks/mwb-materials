using System;
using System.IO;
using System.Linq;

namespace mwb_materials.MwbMats
{
    class QcMaterialResolver
    {
        public static string ResolveMaterialDirectory(string folderPath, string batchRootPath, Action<string> logFunc)
        {
            string current = Path.GetFullPath(folderPath);

            while (!string.IsNullOrEmpty(current))
            {
                string resolved = ResolveMaterialDirectoryInFolder(current, logFunc);

                if (!string.IsNullOrEmpty(resolved))
                {
                    return resolved;
                }

                DirectoryInfo parent = Directory.GetParent(current);

                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }

            return null;
        }

        private static string ResolveMaterialDirectoryInFolder(string folderPath, Action<string> logFunc)
        {
            string[] qcFiles;

            try
            {
                qcFiles = Directory.GetFiles(folderPath, "*.qc", new EnumerationOptions() { MatchCasing = MatchCasing.CaseInsensitive })
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return null;
            }

            foreach (string qcFile in qcFiles)
            {
                string resolved = ResolveMaterialDirectoryFromQc(qcFile, logFunc);

                if (!string.IsNullOrEmpty(resolved))
                {
                    return resolved;
                }
            }

            return null;
        }

        private static string ResolveMaterialDirectoryFromQc(string qcFile, Action<string> logFunc)
        {
            string[] lines;

            try
            {
                lines = File.ReadAllLines(qcFile);
            }
            catch (Exception ex)
            {
                logFunc?.Invoke("Warning: could not read QC " + Path.GetFileName(qcFile) + ": " + ex.Message);
                return null;
            }

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();

                if (!line.StartsWith("$cdmaterials", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string addonRoot = FindPrecedingRootComment(lines, index);
                string cdMaterials = ParseCdMaterials(line);

                if (string.IsNullOrWhiteSpace(addonRoot) || string.IsNullOrWhiteSpace(cdMaterials))
                {
                    continue;
                }

                string materialDirectory;

                if (!TryBuildMaterialDirectory(addonRoot, cdMaterials, out materialDirectory))
                {
                    logFunc?.Invoke("Warning: ignored unsafe QC material destination in " + Path.GetFileName(qcFile) + ".");
                    continue;
                }

                logFunc?.Invoke("QC material destination: " + Path.GetFileName(qcFile) + " -> " + materialDirectory);
                return materialDirectory;
            }

            return null;
        }

        private static string FindPrecedingRootComment(string[] lines, int cdMaterialsIndex)
        {
            for (int index = cdMaterialsIndex - 1; index >= 0; index--)
            {
                string line = lines[index].Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                if (!line.StartsWith("//"))
                {
                    return null;
                }

                string comment = line.Substring(2).Trim();
                return Path.IsPathRooted(comment) ? comment : null;
            }

            return null;
        }

        private static string ParseCdMaterials(string line)
        {
            string value = line.Substring("$cdmaterials".Length).Trim();

            if (value.StartsWith("\""))
            {
                int endQuote = value.IndexOf('"', 1);

                if (endQuote > 1)
                {
                    value = value.Substring(1, endQuote - 1);
                }
            }
            else
            {
                int commentIndex = value.IndexOf("//", StringComparison.Ordinal);

                if (commentIndex >= 0)
                {
                    value = value.Substring(0, commentIndex);
                }

                value = value.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }

            return value == null ? null : value.Trim();
        }

        private static bool TryBuildMaterialDirectory(string addonRoot, string cdMaterials, out string materialDirectory)
        {
            materialDirectory = null;

            if (!Path.IsPathRooted(addonRoot))
            {
                return false;
            }

            string normalizedCdMaterials = cdMaterials
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(normalizedCdMaterials) || Path.IsPathRooted(normalizedCdMaterials))
            {
                return false;
            }

            string[] parts = normalizedCdMaterials.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 || parts.Any(part => part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                return false;
            }

            string root = Path.GetFullPath(addonRoot);
            string materialsRoot = Path.Combine(root, "materials");
            string combined = Path.GetFullPath(Path.Combine(materialsRoot, Path.Combine(parts)));

            if (!combined.StartsWith(materialsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            materialDirectory = combined;
            return true;
        }
    }
}
