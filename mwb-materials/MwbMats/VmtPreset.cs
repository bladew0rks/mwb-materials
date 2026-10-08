using System.Collections.Generic;

namespace mwb_materials.MwbMats
{
    class VmtPreset
    {
        public const string VertexLitGeneric = "VertexLitGeneric";
        public const string SurfaceGgx = "SurfaceGGX";

        public static readonly VmtPreset Default = new VmtPreset("Default", string.Empty, string.Empty);

        public VmtPreset(string displayName, string id, string filePath)
        {
            DisplayName = displayName;
            Id = id;
            FilePath = filePath;
            Phong = new VmtPresetSection();
            Rimlight = new VmtPresetSection();
            Envmap = new VmtPresetSection();
            Custom = new VmtPresetSection();
            MwEnvMapTintProxy = new VmtPresetSection();
            Shader = VertexLitGeneric;
        }

        public string DisplayName { get; }
        public string Id { get; }
        public string FilePath { get; }
        public VmtPresetSection Phong { get; }
        public VmtPresetSection Rimlight { get; }
        public VmtPresetSection Envmap { get; }
        public VmtPresetSection Custom { get; }
        public VmtPresetSection MwEnvMapTintProxy { get; }
        public string Shader { get; set; }

        public bool IsSurfaceGgx
        {
            get { return string.Equals(Shader, SurfaceGgx, System.StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsDefault
        {
            get { return string.IsNullOrEmpty(Id); }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    class VmtPresetSection
    {
        public VmtPresetSection()
        {
            Values = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        }

        public bool IsPresent { get; set; }
        public bool? Enabled { get; set; }
        public Dictionary<string, string> Values { get; }
    }
}
