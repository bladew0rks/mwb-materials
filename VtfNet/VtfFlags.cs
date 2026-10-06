namespace VtfNet;

[Flags]
public enum VtfFlags : uint
{
    None = 0,
    PointSample = 0x00000001,
    Trilinear = 0x00000002,
    ClampS = 0x00000004,
    ClampT = 0x00000008,
    Anisotropic = 0x00000010,
    HintDxt5 = 0x00000020,
    Srgb = 0x00000040,
    Normal = 0x00000080,
    NoMip = 0x00000100,
    NoLod = 0x00000200,
    MinMip = 0x00000400,
    Procedural = 0x00000800,
    OneBitAlpha = 0x00001000,
    EightBitAlpha = 0x00002000,
    EnvMap = 0x00004000,
    RenderTarget = 0x00008000,
    DepthRenderTarget = 0x00010000,
    NoDebugOverride = 0x00020000,
    SingleCopy = 0x00040000,
    NoDepthBuffer = 0x00800000,
    ClampU = 0x02000000,
    VertexTexture = 0x04000000,
    SsBump = 0x08000000,
    Border = 0x20000000,
}
