using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace mwb_materials.MwbMats
{
    class VmtPresetApplier
    {
        private static readonly HashSet<string> ProtectedTopLevelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$basetexture",
            "$bumpmap",
            "$phongexponenttexture",
            "$envmap",
            "$normalmapalphaenvmapmask",
            "$detail",
            "$detailscale",
            "$detailblendmode",
            "$alphatest",
            "$alphatestreference",
            "$translucent"
        };

        private static readonly HashSet<string> ProtectedProxyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "envmap",
            "color"
        };

        public static string Apply(string content, VmtPreset preset, Action<string> logFunc)
        {
            if (preset == null || preset.IsDefault)
            {
                return content;
            }

            List<PendingValue> topLevelValues = BuildTopLevelValues(preset, logFunc);
            Dictionary<string, string> proxyValues = BuildProxyValues(preset, logFunc);
            string envmapTint = preset.IsSurfaceGgx ? null : GetEnvmapTint(preset);

            if (!string.IsNullOrEmpty(envmapTint))
            {
                proxyValues["color"] = envmapTint;
            }

            List<string> lines = SplitLines(content);
            HashSet<PendingValue> applied = new HashSet<PendingValue>();
            HashSet<string> appliedProxyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int depth = 0;

            for (int index = 0; index < lines.Count; index++)
            {
                string key = TryGetVmtKey(lines[index]);

                if (depth == 1 && key != null)
                {
                    PendingValue pending = topLevelValues.LastOrDefault(value => string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase));

                    if (pending != null)
                    {
                        lines[index] = FormatVmtLine(pending.Key, pending.Value, GetInlineComment(lines[index]));
                        applied.Add(pending);
                    }
                }
                else if (depth >= 3 && key != null && proxyValues.ContainsKey(key))
                {
                    lines[index] = FormatVmtLine(key, proxyValues[key], GetInlineComment(lines[index]), GetIndent(lines[index]));
                    appliedProxyKeys.Add(key);
                }

                depth += CountChar(lines[index], '{');
                depth -= CountChar(lines[index], '}');
            }

            InsertMissingValues(lines, topLevelValues.Where(value => !applied.Contains(value)).ToList());
            InsertMissingProxyValues(lines, proxyValues.Where(pair => !appliedProxyKeys.Contains(pair.Key)).ToList());

            logFunc?.Invoke("Applied VMT preset: " + preset.DisplayName);
            return string.Join("\r\n", lines);
        }

        private static List<PendingValue> BuildTopLevelValues(VmtPreset preset, Action<string> logFunc)
        {
            List<PendingValue> result = new List<PendingValue>();

            if (preset.IsSurfaceGgx)
            {
                AddSectionValues(result, preset.Custom, "custom", logFunc);
                return result;
            }

            AddFeatureSection(result, preset.Phong, "$phong", "phong", logFunc);
            AddFeatureSection(result, preset.Rimlight, "$rimlight", "rimlight", logFunc);
            AddSectionValues(result, preset.Envmap, "envmap", logFunc);
            AddSectionValues(result, preset.Custom, "custom", logFunc);

            return result;
        }

        private static Dictionary<string, string> BuildProxyValues(VmtPreset preset, Action<string> logFunc)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!preset.MwEnvMapTintProxy.IsPresent || preset.IsSurfaceGgx)
            {
                return result;
            }

            foreach (KeyValuePair<string, string> pair in preset.MwEnvMapTintProxy.Values)
            {
                if (ProtectedProxyKeys.Contains(pair.Key))
                {
                    logFunc?.Invoke("Warning: VMT preset ignored protected MwEnvMapTint proxy key: " + pair.Key);
                    continue;
                }

                result[pair.Key] = pair.Value;
            }

            return result;
        }

        private static void AddFeatureSection(List<PendingValue> result, VmtPresetSection section, string enableKey, string group, Action<string> logFunc)
        {
            if (!section.IsPresent)
            {
                return;
            }

            result.Add(new PendingValue(enableKey, section.Enabled == false ? "0" : "1", group));
            AddSectionValues(result, section, group, logFunc, enableKey);
        }

        private static void AddSectionValues(List<PendingValue> result, VmtPresetSection section, string group, Action<string> logFunc, string blockedKey = null)
        {
            if (!section.IsPresent)
            {
                return;
            }

            foreach (KeyValuePair<string, string> pair in section.Values)
            {
                if (!string.IsNullOrEmpty(blockedKey) && string.Equals(pair.Key, blockedKey, StringComparison.OrdinalIgnoreCase))
                {
                    logFunc?.Invoke("Warning: VMT preset ignored " + blockedKey + " in [" + group + "]; use enabled = true/false instead.");
                    continue;
                }

                if (!pair.Key.StartsWith("$", StringComparison.Ordinal))
                {
                    logFunc?.Invoke("Warning: VMT preset ignored top-level key without $ in [" + group + "]: " + pair.Key);
                    continue;
                }

                if (ProtectedTopLevelKeys.Contains(pair.Key))
                {
                    logFunc?.Invoke("Warning: VMT preset ignored protected key: " + pair.Key);
                    continue;
                }

                result.Add(new PendingValue(pair.Key, pair.Value, group));
            }
        }

        private static string GetEnvmapTint(VmtPreset preset)
        {
            string value;
            return preset.Envmap.IsPresent && preset.Envmap.Values.TryGetValue("$envmaptint", out value) ? value : null;
        }

        private static void InsertMissingValues(List<string> lines, List<PendingValue> values)
        {
            InsertMissingGroup(lines, values, "phong", FindLineIndex(lines, line => line.IndexOf("//rimlight", StringComparison.OrdinalIgnoreCase) >= 0));
            InsertMissingGroup(lines, values, "rimlight", FindLineIndex(lines, line => line.Contains("\"$normalmapalphaenvmapmask\"")));
            InsertMissingGroup(lines, values, "envmap", FindEnvmapInsertIndex(lines));
            InsertMissingGroup(lines, values, "custom", FindLineIndex(lines, line => line.TrimStart().StartsWith("\"Proxies\"", StringComparison.OrdinalIgnoreCase)));
        }

        private static int FindEnvmapInsertIndex(List<string> lines)
        {
            int envmapTintIndex = FindLineIndex(lines, line => line.Contains("\"$envmaptint\""));
            if (envmapTintIndex >= 0)
            {
                return envmapTintIndex + 1;
            }

            int envmapIndex = FindLineIndex(lines, line => line.Contains("\"$envmap\""));
            return envmapIndex >= 0 ? envmapIndex + 1 : -1;
        }

        private static void InsertMissingGroup(List<string> lines, List<PendingValue> values, string group, int preferredIndex)
        {
            List<PendingValue> groupValues = values.Where(value => string.Equals(value.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();

            if (groupValues.Count == 0)
            {
                return;
            }

            int insertIndex = preferredIndex >= 0 ? preferredIndex : Math.Max(lines.Count - 1, 0);
            List<string> newLines = new List<string>();

            if (string.Equals(group, "custom", StringComparison.OrdinalIgnoreCase))
            {
                newLines.Add("    // preset values");
            }

            newLines.AddRange(groupValues.Select(value => FormatVmtLine(value.Key, value.Value, string.Empty)));

            if (insertIndex > 0 && !string.IsNullOrWhiteSpace(lines[insertIndex - 1]))
            {
                newLines.Insert(0, string.Empty);
            }

            if (insertIndex < lines.Count && !string.IsNullOrWhiteSpace(lines[insertIndex]))
            {
                newLines.Add(string.Empty);
            }

            lines.InsertRange(insertIndex, newLines);
        }

        private static void InsertMissingProxyValues(List<string> lines, List<KeyValuePair<string, string>> values)
        {
            if (values.Count == 0)
            {
                return;
            }

            int insertIndex = FindMwEnvMapTintCloseBraceIndex(lines);

            if (insertIndex < 0)
            {
                return;
            }

            lines.InsertRange(insertIndex, values.Select(value => "            \"" + Escape(value.Key) + "\" \"" + Escape(value.Value) + "\""));
        }

        private static int FindMwEnvMapTintCloseBraceIndex(List<string> lines)
        {
            bool foundName = false;
            bool inBlock = false;
            int depth = 0;

            for (int index = 0; index < lines.Count; index++)
            {
                string trimmed = lines[index].Trim();

                if (!foundName && string.Equals(trimmed, "\"MwEnvMapTint\"", StringComparison.OrdinalIgnoreCase))
                {
                    foundName = true;
                    continue;
                }

                if (foundName && !inBlock && trimmed == "{")
                {
                    inBlock = true;
                    depth = 1;
                    continue;
                }

                if (!inBlock)
                {
                    continue;
                }

                depth += CountChar(lines[index], '{');
                depth -= CountChar(lines[index], '}');

                if (depth <= 0)
                {
                    return index;
                }
            }

            return -1;
        }

        private static List<string> SplitLines(string content)
        {
            return content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        }

        private static string TryGetVmtKey(string line)
        {
            Match match = Regex.Match(line, "^\\s*\"(?<key>[^\"]+)\"\\s+\"");
            return match.Success ? match.Groups["key"].Value : null;
        }

        private static string FormatVmtLine(string key, string value, string comment, string indent = "    ")
        {
            string line = indent + "\"" + Escape(key) + "\" \"" + Escape(value) + "\"";
            return string.IsNullOrEmpty(comment) ? line : line + " " + comment;
        }

        private static string GetIndent(string line)
        {
            Match match = Regex.Match(line, "^\\s*");
            return match.Success ? match.Value : string.Empty;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\"", "\\\"");
        }

        private static string GetInlineComment(string line)
        {
            int quotesSeen = 0;

            for (int index = 0; index < line.Length - 1; index++)
            {
                if (line[index] == '"')
                {
                    quotesSeen++;
                }

                if (quotesSeen >= 4 && line[index] == '/' && line[index + 1] == '/')
                {
                    return line.Substring(index).TrimEnd();
                }
            }

            return string.Empty;
        }

        private static int FindLineIndex(List<string> lines, Func<string, bool> predicate)
        {
            for (int index = 0; index < lines.Count; index++)
            {
                if (predicate(lines[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        private static int CountChar(string value, char ch)
        {
            return value.Count(candidate => candidate == ch);
        }

        private class PendingValue
        {
            public PendingValue(string key, string value, string group)
            {
                Key = key;
                Value = value;
                Group = group;
            }

            public string Key { get; }
            public string Value { get; }
            public string Group { get; }
        }
    }
}
