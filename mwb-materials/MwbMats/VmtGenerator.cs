using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace mwb_materials.MwbMats
{
    class VmtGenerator
    {
        private static readonly Lazy<string> VmtTemplate = new Lazy<string>(() => Encoding.UTF8.GetString(EmbeddedResources.Read("default_vmt.vmt")));

        private static void SanitizeName(ref string name)
        {
            name = name.Trim().Replace(".vmt", string.Empty);
        }

        public static string Generate(string path, string name, Dictionary<string, object> values, VmtPreset preset = null, Action<string> logFunc = null)
        {
            SanitizeName(ref name);
            string content = VmtTemplate.Value;

            foreach (KeyValuePair<string, object> pair in values)
            {
                content = content.Replace("${" + pair.Key + "}", pair.Value.ToString());
            }

            content = VmtPresetApplier.Apply(content, preset, logFunc);

            string vmtName = Path.GetFileNameWithoutExtension(name) + ".vmt";
            string vmtPath = Path.Combine(path, vmtName);

            Directory.CreateDirectory(path);
            TextureExporter.WriteAllBytesLocked(vmtPath, Encoding.UTF8.GetBytes(content));
            logFunc?.Invoke("Wrote " + vmtPath);
            return vmtPath;
        }
    }
}
