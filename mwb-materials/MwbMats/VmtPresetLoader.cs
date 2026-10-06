using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tomlyn;
using Tomlyn.Model;

namespace mwb_materials.MwbMats
{
    class VmtPresetLoader
    {
        public static List<VmtPreset> LoadPresets(IEnumerable<string> baseDirectories, Action<string> logFunc)
        {
            Dictionary<string, VmtPreset> byId = new Dictionary<string, VmtPreset>(StringComparer.OrdinalIgnoreCase);

            foreach (string baseDirectory in baseDirectories.Where(directory => !string.IsNullOrEmpty(directory)))
            {
                string presetsPath = Path.Combine(baseDirectory, "presets");

                if (!Directory.Exists(presetsPath))
                {
                    continue;
                }

                foreach (string file in Directory.GetFiles(presetsPath, "*.toml", new EnumerationOptions() { MatchCasing = MatchCasing.CaseInsensitive }).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        VmtPreset preset = LoadPreset(file, logFunc);

                        if (preset != null)
                        {
                            byId[preset.Id] = preset;
                        }
                    }
                    catch (Exception ex)
                    {
                        logFunc?.Invoke("Warning: could not load VMT preset " + Path.GetFileName(file) + ": " + ex.Message);
                    }
                }
            }

            List<VmtPreset> presets = byId.Values.ToList();

            if (!presets.Any(IsDefaultPreset))
            {
                presets.Add(VmtPreset.Default);
            }

            return presets
                .OrderBy(preset => IsDefaultPreset(preset) ? 0 : 1)
                .ThenBy(preset => preset.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool IsDefaultPreset(VmtPreset preset)
        {
            return preset != null && (preset.IsDefault
                || string.Equals(preset.Id, "Default.toml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(preset.DisplayName, "Default", StringComparison.OrdinalIgnoreCase));
        }

        private static VmtPreset LoadPreset(string file, Action<string> logFunc)
        {
            TomlTable root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file));

            if (root == null)
            {
                logFunc?.Invoke("Warning: VMT preset did not parse as a TOML table: " + Path.GetFileName(file));
                return null;
            }

            string displayName = GetString(root, "name");

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = Path.GetFileNameWithoutExtension(file);
            }

            VmtPreset preset = new VmtPreset(displayName.Trim(), Path.GetFileName(file), file);

            LoadSection(root, "phong", preset.Phong, file, logFunc);
            LoadSection(root, "rimlight", preset.Rimlight, file, logFunc);
            LoadSection(root, "envmap", preset.Envmap, file, logFunc);
            LoadSection(root, "custom", preset.Custom, file, logFunc);

            TomlTable proxies = GetTable(root, "proxies");

            if (proxies != null)
            {
                LoadSection(proxies, "MwEnvMapTint", preset.MwEnvMapTintProxy, file, logFunc);
            }

            return preset;
        }

        private static void LoadSection(TomlTable parent, string name, VmtPresetSection target, string file, Action<string> logFunc)
        {
            TomlTable table = GetTable(parent, name);

            if (table == null)
            {
                return;
            }

            target.IsPresent = true;

            foreach (KeyValuePair<string, object> pair in table)
            {
                if (string.Equals(pair.Key, "enabled", StringComparison.OrdinalIgnoreCase))
                {
                    bool enabled;

                    if (TryGetBoolean(pair.Value, out enabled))
                    {
                        target.Enabled = enabled;
                    }
                    else
                    {
                        logFunc?.Invoke("Warning: ignored non-boolean enabled value in " + Path.GetFileName(file) + " [" + name + "].");
                    }

                    continue;
                }

                string value;

                if (TryGetVmtValue(pair.Value, out value))
                {
                    target.Values[pair.Key] = value;
                }
                else
                {
                    logFunc?.Invoke("Warning: ignored unsupported VMT preset value " + pair.Key + " in " + Path.GetFileName(file) + " [" + name + "].");
                }
            }
        }

        private static TomlTable GetTable(TomlTable table, string key)
        {
            object value;
            return table != null && table.TryGetValue(key, out value) ? value as TomlTable : null;
        }

        private static string GetString(TomlTable table, string key)
        {
            object value;
            return table != null && table.TryGetValue(key, out value) ? value as string : null;
        }

        private static bool TryGetBoolean(object value, out bool result)
        {
            if (value is bool)
            {
                result = (bool)value;
                return true;
            }

            if (value is string)
            {
                return bool.TryParse((string)value, out result);
            }

            result = false;
            return false;
        }

        private static bool TryGetVmtValue(object rawValue, out string value)
        {
            value = null;

            if (rawValue == null)
            {
                return false;
            }

            if (rawValue is string)
            {
                value = (string)rawValue;
                return true;
            }

            if (rawValue is bool)
            {
                value = ((bool)rawValue) ? "1" : "0";
                return true;
            }

            if (rawValue is IFormattable)
            {
                value = ((IFormattable)rawValue).ToString(null, CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }
    }
}
