using System.Buffers.Binary;
using System.IO.Compression;

namespace VtfNet;

public enum VtfCompression
{
    None = 0,
    Deflate = 8,
    Zstd = 93,
}

internal static class VtfCompressionCodec
{
    public const uint AuxCompressionResource = 0x00435841;

    public static byte[] Compress(ReadOnlySpan<byte> data, VtfCompression method, int level)
    {
        if (method == VtfCompression.Zstd)
        {
            using var compressor = new ZstdSharp.Compressor(level);
            return compressor.Wrap(data).ToArray();
        }

        using MemoryStream output = new MemoryStream();

        using (ZLibStream zlib = new ZLibStream(output, new ZLibCompressionOptions { CompressionLevel = Math.Clamp(level, 0, 9) }, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    public static void Decompress(ReadOnlySpan<byte> source, Span<byte> destination, VtfCompression method)
    {
        int written;

        if (method == VtfCompression.Zstd)
        {
            using var decompressor = new ZstdSharp.Decompressor();
            written = decompressor.Unwrap(source, destination);
        }
        else
        {
            using ZLibStream zlib = new ZLibStream(new MemoryStream(source.ToArray()), CompressionMode.Decompress);
            written = 0;
            int read;

            while (written < destination.Length && (read = zlib.Read(destination[written..])) > 0)
            {
                written += read;
            }
        }

        if (written != destination.Length)
        {
            throw new InvalidDataException("Compressed VTF image data did not decompress to the expected size.");
        }
    }

    public static (VtfCompression Method, int Level) ReadSettings(ReadOnlySpan<byte> resource)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(resource);
        int level = (short)(value & 0xFFFF);
        int method = (short)(value >> 16);
        return (method <= 0 ? VtfCompression.Deflate : (VtfCompression)method, level);
    }
}
