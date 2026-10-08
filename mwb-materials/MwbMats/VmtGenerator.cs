using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace mwb_materials.MwbMats
{
    class VmtGenerator
    {
        private static readonly Lazy<string> VmtTemplate = new Lazy<string>(() => Encoding.UTF8.GetString(EmbeddedResources.Read("default_vmt.vmt")));
        private static readonly Lazy<string> SurfaceGgxTemplate = new Lazy<string>(() => Encoding.UTF8.GetString(EmbeddedResources.Read("surfaceggx_vmt.vmt")));

        private static void SanitizeName(ref string name)
        {
            name = name.Trim().Replace(".vmt", string.Empty);
        }

        private static string TidyLines(string content)
        {
            string newline = content.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = new List<string>();

            foreach (string line in content.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Contains("${"))
                {
                    continue;
                }

                bool blank = string.IsNullOrWhiteSpace(line);

                if (blank && lines.Count > 0 && lines[^1].Length == 0)
                {
                    continue;
                }

                lines.Add(blank ? string.Empty : line);
            }

            for (int i = lines.Count - 2; i >= 0; i--)
            {
                if (lines[i].Length == 0 && lines[i + 1].Trim() == "}")
                {
                    lines.RemoveAt(i);
                }
            }

            return string.Join(newline, lines);
        }

        public static string Generate(string path, string name, Dictionary<string, object> values, VmtPreset preset = null, Action<string> logFunc = null)
        {
            SanitizeName(ref name);
            string content = preset != null && preset.IsSurfaceGgx ? SurfaceGgxTemplate.Value : VmtTemplate.Value;

            foreach (KeyValuePair<string, object> pair in values)
            {
                content = content.Replace("${" + pair.Key + "}", pair.Value.ToString());
            }

            content = TidyLines(VmtPresetApplier.Apply(content, preset, logFunc));

            string vmtName = Path.GetFileNameWithoutExtension(name) + ".vmt";
            string vmtPath = Path.Combine(path, vmtName);

            Directory.CreateDirectory(path);
            TextureExporter.WriteAllBytesLocked(vmtPath, Encoding.UTF8.GetBytes(content));
            logFunc?.Invoke("Wrote " + vmtPath);
            return vmtPath;
        }
    }
}
