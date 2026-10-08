using System;
using System.Collections.Concurrent;
using System.IO;
using VtfNet;

namespace mwb_materials.MwbMats
{
    static class TextureExporter
    {
        public static readonly string FormatDXT1 = "DXT1";
        public static readonly string FormatDXT5 = "DXT5";
        public static readonly string FormatRGBA8888 = "RGBA8888";
        public static readonly string FormatBC7 = "BC7";
        public static readonly string FormatBC6H = "BC6H";

        public static readonly string[] Formats = new string[] { FormatDXT5, FormatRGBA8888, FormatDXT1, FormatBC7, FormatBC6H };

        private static readonly ConcurrentDictionary<string, object> PathLocks = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        public static VtfImageFormat ParseFormat(string format)
        {
            if (string.Equals(format, FormatDXT1, StringComparison.OrdinalIgnoreCase))
            {
                return VtfImageFormat.DXT1;
            }

            if (string.Equals(format, FormatRGBA8888, StringComparison.OrdinalIgnoreCase))
            {
                return VtfImageFormat.RGBA8888;
            }

            if (string.Equals(format, FormatBC7, StringComparison.OrdinalIgnoreCase))
            {
                return VtfImageFormat.BC7;
            }

            if (string.Equals(format, FormatBC6H, StringComparison.OrdinalIgnoreCase))
            {
                return VtfImageFormat.BC6HUnsigned;
            }

            return VtfImageFormat.DXT5;
        }

        public static string Export(PixelBuffer image, string outputFolder, string outputName, string format, bool mipmaps, Action<string> logFunc, bool alphaIsCoverage = false)
        {
            VtfCreateOptions options = new VtfCreateOptions()
            {
                Format = ParseFormat(format),
                GenerateMipmaps = mipmaps,
                AlphaWeightedMipmaps = alphaIsCoverage,
                ResizeMethod = IsValidVtfSize(image.Width) && IsValidVtfSize(image.Height) ? VtfResizeMethod.None : VtfResizeMethod.NearestPowerOfTwo,
            };

            if (options.ResizeMethod != VtfResizeMethod.None)
            {
                logFunc?.Invoke("Resizing " + outputName + " (" + image.Width + "x" + image.Height + ") to the nearest power of two for VTF.");
            }

            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            VtfFile vtf = VtfFile.Create(new[] { image.Bytes }, image.Width, image.Height, options);
            string path = Path.Combine(outputFolder, outputName + ".vtf");

            Directory.CreateDirectory(outputFolder);
            long encodeMs = timer.ElapsedMilliseconds;
            timer.Restart();
            WriteLocked(path, vtf.Save);
            logFunc?.Invoke("Timing: " + outputName + " encode " + encodeMs + " ms, write " + timer.ElapsedMilliseconds + " ms");
            logFunc?.Invoke("Wrote " + path + " (" + format + ", " + vtf.Width + "x" + vtf.Height + ", " + vtf.MipmapCount + " mip" + (vtf.MipmapCount == 1 ? "" : "s") + ")");
            return path;
        }

        public static void WriteAllBytesLocked(string path, byte[] bytes)
        {
            WriteLocked(path, stream => stream.Write(bytes, 0, bytes.Length));
        }

        public static void WriteLocked(string path, Action<Stream> write)
        {
            object gate = PathLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());

            lock (gate)
            {
                using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    write(stream);
                }
            }
        }

        private static bool IsValidVtfSize(int size)
        {
            return size > 0 && size <= 0xFFFF && ((size & (size - 1)) == 0 || size % 4 == 0);
        }
    }
}
