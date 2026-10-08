using VtfNet.Processing;

namespace VtfNet;

public enum VtfResizeMethod
{
    None,
    NearestPowerOfTwo,
    BiggestPowerOfTwo,
    SmallestPowerOfTwo,
    NearestMultipleOfFour,
}

public sealed class VtfCreateOptions
{
    public int MajorVersion { get; set; } = 7;
    public int MinorVersion { get; set; } = 3;

    public VtfImageFormat Format { get; set; } = VtfImageFormat.RGBA8888;

    public VtfFlags Flags { get; set; }

    public bool GenerateMipmaps { get; set; } = true;
    public ResampleFilter MipmapFilter { get; set; } = ResampleFilter.Box;

    public bool AlphaWeightedMipmaps { get; set; }

    public VtfResizeMethod ResizeMethod { get; set; } = VtfResizeMethod.None;
    public ResampleFilter ResizeFilter { get; set; } = ResampleFilter.Triangle;

    public int ResizeClamp { get; set; }

    public bool GenerateThumbnail { get; set; } = true;
    public bool ComputeReflectivity { get; set; } = true;

    public float[] Reflectivity { get; set; } = [1f, 1f, 1f];

    public float BumpmapScale { get; set; } = 1f;
    public int StartFrame { get; set; }

    public int FaceCount { get; set; } = 1;

    public int AlphaThreshold { get; set; } = 128;

    public VtfCompression Compression { get; set; } = VtfCompression.None;
    public int CompressionLevel { get; set; } = 6;
}
