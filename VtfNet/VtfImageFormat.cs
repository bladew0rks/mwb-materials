namespace VtfNet;

public enum VtfImageFormat
{
    None = -1,
    RGBA8888 = 0,
    ABGR8888,
    RGB888,
    BGR888,
    RGB565,
    I8,
    IA88,
    P8,
    A8,
    RGB888Bluescreen,
    BGR888Bluescreen,
    ARGB8888,
    BGRA8888,
    DXT1,
    DXT3,
    DXT5,
    BGRX8888,
    BGR565,
    BGRX5551,
    BGRA4444,
    DXT1OneBitAlpha,
    BGRA5551,
    UV88,
    UVWQ8888,
    RGBA16161616F,
    RGBA16161616,
    UVLX8888,
    R32F,
    RGB323232F,
    RGBA32323232F,
    NvDst16,
    NvDst24,
    NvIntz,
    NvRawz,
    AtiDst16,
    AtiDst24,
    NvNull,
    ATI1N,
    ATI2N,
    HdrBGRA8888,
    BC7 = 70,
    BC6HSigned = 71,
    BC6HUnsigned = 72,
}

public sealed record VtfImageFormatInfo(
    string Name,
    int BitsPerPixel,
    int BytesPerPixel,
    int RedBits,
    int GreenBits,
    int BlueBits,
    int AlphaBits,
    bool IsCompressed,
    bool CanDecode,
    bool CanEncode)
{
    private static readonly VtfImageFormatInfo[] Table =
    [
        new("RGBA8888", 32, 4, 8, 8, 8, 8, false, true, true),
        new("ABGR8888", 32, 4, 8, 8, 8, 8, false, true, true),
        new("RGB888", 24, 3, 8, 8, 8, 0, false, true, true),
        new("BGR888", 24, 3, 8, 8, 8, 0, false, true, true),
        new("RGB565", 16, 2, 5, 6, 5, 0, false, true, true),
        new("I8", 8, 1, 0, 0, 0, 0, false, true, true),
        new("IA88", 16, 2, 0, 0, 0, 8, false, true, true),
        new("P8", 8, 1, 0, 0, 0, 0, false, false, false),
        new("A8", 8, 1, 0, 0, 0, 8, false, true, true),
        new("RGB888 Bluescreen", 24, 3, 8, 8, 8, 0, false, true, true),
        new("BGR888 Bluescreen", 24, 3, 8, 8, 8, 0, false, true, true),
        new("ARGB8888", 32, 4, 8, 8, 8, 8, false, true, true),
        new("BGRA8888", 32, 4, 8, 8, 8, 8, false, true, true),
        new("DXT1", 4, 0, 0, 0, 0, 0, true, true, true),
        new("DXT3", 8, 0, 0, 0, 0, 8, true, true, true),
        new("DXT5", 8, 0, 0, 0, 0, 8, true, true, true),
        new("BGRX8888", 32, 4, 8, 8, 8, 0, false, true, true),
        new("BGR565", 16, 2, 5, 6, 5, 0, false, true, true),
        new("BGRX5551", 16, 2, 5, 5, 5, 0, false, true, true),
        new("BGRA4444", 16, 2, 4, 4, 4, 4, false, true, true),
        new("DXT1 One Bit Alpha", 4, 0, 0, 0, 0, 1, true, true, true),
        new("BGRA5551", 16, 2, 5, 5, 5, 1, false, true, true),
        new("UV88", 16, 2, 8, 8, 0, 0, false, true, true),
        new("UVWQ8888", 32, 4, 8, 8, 8, 8, false, true, true),
        new("RGBA16161616F", 64, 8, 16, 16, 16, 16, false, true, false),
        new("RGBA16161616", 64, 8, 16, 16, 16, 16, false, true, false),
        new("UVLX8888", 32, 4, 8, 8, 8, 8, false, true, true),
        new("R32F", 32, 4, 32, 0, 0, 0, false, true, false),
        new("RGB323232F", 96, 12, 32, 32, 32, 0, false, true, false),
        new("RGBA32323232F", 128, 16, 32, 32, 32, 32, false, true, false),
        new("nVidia DST16", 16, 2, 0, 0, 0, 0, false, false, false),
        new("nVidia DST24", 24, 3, 0, 0, 0, 0, false, false, false),
        new("nVidia INTZ", 32, 4, 0, 0, 0, 0, false, false, false),
        new("nVidia RAWZ", 32, 4, 0, 0, 0, 0, false, false, false),
        new("ATI DST16", 16, 2, 0, 0, 0, 0, false, false, false),
        new("ATI DST24", 24, 3, 0, 0, 0, 0, false, false, false),
        new("nVidia NULL", 32, 4, 0, 0, 0, 0, false, false, false),
        new("ATI1N", 4, 0, 0, 0, 0, 0, true, true, true),
        new("ATI2N", 8, 0, 0, 0, 0, 0, true, true, true),
        new("HDR BGRA8888", 32, 4, 8, 8, 8, 8, false, true, true),
    ];

    private static readonly VtfImageFormatInfo Bc7 = new("BC7", 8, 0, 0, 0, 0, 8, true, true, true);
    private static readonly VtfImageFormatInfo Bc6hSigned = new("BC6H Signed", 8, 0, 16, 16, 16, 0, true, true, true);
    private static readonly VtfImageFormatInfo Bc6hUnsigned = new("BC6H Unsigned", 8, 0, 16, 16, 16, 0, true, true, true);

    public static bool IsStrataFormat(VtfImageFormat format) => format is VtfImageFormat.BC7 or VtfImageFormat.BC6HSigned or VtfImageFormat.BC6HUnsigned;

    public static VtfImageFormatInfo Get(VtfImageFormat format)
    {
        switch (format)
        {
            case VtfImageFormat.BC7:
                return Bc7;
            case VtfImageFormat.BC6HSigned:
                return Bc6hSigned;
            case VtfImageFormat.BC6HUnsigned:
                return Bc6hUnsigned;
        }

        int index = (int)format;

        if (index < 0 || index >= Table.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown VTF image format.");
        }

        return Table[index];
    }

    public int BlockSize => IsCompressed ? BitsPerPixel * 2 : 0;
}
