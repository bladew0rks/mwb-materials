using System;
using System.IO;
using System.Text.Json;

namespace mwb_materials
{
    public sealed class AppSettings
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions() { WriteIndented = true };

        public string DestinationFolder { get; set; } = string.Empty;
        public string EnvMapsFolder { get; set; } = string.Empty;
        public bool AoMasks { get; set; } = true;
        public bool OpenGlNormal { get; set; }
        public bool InvertNormalBlue { get; set; }
        public bool InvertOpacity { get; set; }
        public int AoAlbedoStrength { get; set; } = 100;
        public int AlphatestReference { get; set; } = 50;
        public string ClampSize { get; set; } = "4096";
        public bool BatchMoveOutput { get; set; } = true;
        public bool BatchIncludeFolders { get; set; }
        public bool KeepIntermediates { get; set; }
        public bool UseModelMaterialNames { get; set; }
        public string AlbedoCompression { get; set; } = "DXT5";
        public string NormalCompression { get; set; } = "RGBA8888";
        public string ExponentCompression { get; set; } = "DXT5";
        public bool AlbedoMipMaps { get; set; } = true;
        public bool NormalMipMaps { get; set; }
        public bool ExponentMipMaps { get; set; } = true;
        public bool CompressVtfs { get; set; }
        public string RdoLevel { get; set; } = "Off";
        public string VmtPreset { get; set; } = string.Empty;
        public int ParallelMaterials { get; set; } = DefaultParallelMaterials;
        public string LastBatchFolder { get; set; } = string.Empty;

        public static int DefaultParallelMaterials => Math.Clamp(Environment.ProcessorCount / 8, 1, 4);

        public static string ConfigDirectory
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);

                if (string.IsNullOrEmpty(root))
                {
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                }

                return Path.Combine(root, "mwb-materials");
            }
        }

        private static string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
                }
            }
            catch (Exception)
            {
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                string temp = SettingsPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(temp, SettingsPath, true);
            }
            catch (Exception)
            {
            }
        }
    }
}
