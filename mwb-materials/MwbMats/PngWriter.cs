using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace mwb_materials.MwbMats
{
    static class PngWriter
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static void Save(PixelBuffer image, string path)
        {
            Save(image.Bytes, image.Width, image.Height, 4, path);
        }

        public static void Save(GrayBuffer image, string path)
        {
            Save(image.Bytes, image.Width, image.Height, 1, path);
        }

        private static void Save(byte[] pixels, int width, int height, int channels, string path)
        {
            using (FileStream file = File.Create(path))
            {
                file.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

                byte[] header = new byte[13];
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
                header[8] = 8;
                header[9] = channels == 1 ? (byte)0 : (byte)6;
                WriteChunk(file, "IHDR", header);

                using (MemoryStream compressed = new MemoryStream())
                {
                    using (ZLibStream zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
                    {
                        int rowBytes = width * channels;

                        for (int y = 0; y < height; y++)
                        {
                            zlib.WriteByte(0);
                            zlib.Write(pixels, y * rowBytes, rowBytes);
                        }
                    }

                    WriteChunk(file, "IDAT", compressed.ToArray());
                }

                WriteChunk(file, "IEND", Array.Empty<byte>());
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);

            uint crc = UpdateCrc(0xFFFFFFFF, typeBytes);
            crc = UpdateCrc(crc, data) ^ 0xFFFFFFFF;
            byte[] crcBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);

            stream.Write(length);
            stream.Write(typeBytes);
            stream.Write(data);
            stream.Write(crcBytes);
        }

        private static uint UpdateCrc(uint crc, byte[] data)
        {
            foreach (byte value in data)
            {
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];

            for (uint n = 0; n < 256; n++)
            {
                uint c = n;

                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
