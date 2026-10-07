using System.Buffers.Binary;
using System.Text;
using VtfNet.Processing;

namespace VtfNet;

public sealed class VtfResource
{
    public const uint NoDataChunkFlag = 0x02000000;

    public VtfResource(uint type, byte[]? data, uint inlineValue = 0)
    {
        Type = type;
        Data = data;
        InlineValue = inlineValue;
    }

    public uint Type { get; }

    public byte[]? Data { get; }

    public uint InlineValue { get; }

    public bool HasDataChunk => (Type & NoDataChunkFlag) == 0;

    public override string ToString()
    {
        byte[] id = BitConverter.GetBytes(Type);
        string name = Encoding.ASCII.GetString(id, 0, 3).TrimEnd('\0');
        return HasDataChunk ? name + " (" + (Data?.Length ?? 0) + " bytes)" : name + " = 0x" + InlineValue.ToString("X8");
    }
}

public sealed class VtfFile
{
    public const uint LowResImageResource = 0x01;
    public const uint ImageResource = 0x30;
    private const int MaxResources = 32;

    private static readonly byte[] Signature = "VTF\0"u8.ToArray();

    public int MajorVersion { get; set; } = 7;
    public int MinorVersion { get; set; } = 3;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Depth { get; private set; } = 1;
    public VtfFlags Flags { get; set; }
    public int FrameCount { get; private set; } = 1;
    public int FaceCount { get; private set; } = 1;
    public int StartFrame { get; set; }
    public float[] Reflectivity { get; set; } = [1f, 1f, 1f];
    public float BumpmapScale { get; set; } = 1f;
    public VtfImageFormat Format { get; private set; } = VtfImageFormat.None;
    public int MipmapCount { get; private set; } = 1;

    public VtfImageFormat ThumbnailFormat { get; private set; } = VtfImageFormat.None;
    public int ThumbnailWidth { get; private set; }
    public int ThumbnailHeight { get; private set; }
    public byte[]? ThumbnailData { get; private set; }

    public byte[] ImageData { get; private set; } = [];

    public List<VtfResource> Resources { get; } = new List<VtfResource>();

    public bool HasThumbnail => ThumbnailFormat != VtfImageFormat.None && ThumbnailData != null;

    public bool SupportsResources => MajorVersion == 7 && MinorVersion >= 3;

    #region Size helpers

    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    public static int ComputeMipmapCount(int width, int height, int depth = 1)
    {
        int count = 0;

        while (true)
        {
            count++;
            width >>= 1;
            height >>= 1;
            depth >>= 1;

            if (width == 0 && height == 0 && depth == 0)
            {
                return count;
            }
        }
    }

    public static (int Width, int Height, int Depth) GetMipmapDimensions(int width, int height, int depth, int level)
    {
        return (Math.Max(1, width >> level), Math.Max(1, height >> level), Math.Max(1, depth >> level));
    }

    public static int ComputeMipmapSize(int width, int height, int depth, int level, VtfImageFormat format)
    {
        var (w, h, d) = GetMipmapDimensions(width, height, depth, level);
        return PixelFormatConverter.GetImageSize(w, h, format) * d;
    }

    private static long ComputeImageDataSize(int width, int height, int depth, int mipmaps, int frames, int faces, VtfImageFormat format)
    {
        long size = 0;

        for (int level = 0; level < mipmaps; level++)
        {
            size += ComputeMipmapSize(width, height, depth, level, format);
        }

        return size * frames * faces;
    }

    public int GetImageOffset(int frame, int face, int slice, int mipLevel)
    {
        ValidateImageIndex(frame, face, slice, mipLevel);
        long offset = 0;

        for (int level = MipmapCount - 1; level > mipLevel; level--)
        {
            offset += (long)ComputeMipmapSize(Width, Height, Depth, level, Format) * FrameCount * FaceCount;
        }

        int mipSize = ComputeMipmapSize(Width, Height, Depth, mipLevel, Format);
        int sliceSize = ComputeMipmapSize(Width, Height, 1, mipLevel, Format);

        offset += (long)mipSize * frame * FaceCount;
        offset += (long)mipSize * face;
        offset += (long)sliceSize * slice;
        return checked((int)offset);
    }

    private void ValidateImageIndex(int frame, int face, int slice, int mipLevel)
    {
        if (frame < 0 || frame >= FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (face < 0 || face >= FaceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(face));
        }

        if (mipLevel < 0 || mipLevel >= MipmapCount)
        {
            throw new ArgumentOutOfRangeException(nameof(mipLevel));
        }

        if (slice < 0 || slice >= GetMipmapDimensions(Width, Height, Depth, mipLevel).Depth)
        {
            throw new ArgumentOutOfRangeException(nameof(slice));
        }
    }

    #endregion

    #region Image access

    public ReadOnlySpan<byte> GetImageData(int frame = 0, int face = 0, int slice = 0, int mipLevel = 0)
    {
        int offset = GetImageOffset(frame, face, slice, mipLevel);
        int size = ComputeMipmapSize(Width, Height, 1, mipLevel, Format);
        return ImageData.AsSpan(offset, size);
    }

    public byte[] GetImageRgba(int frame = 0, int face = 0, int slice = 0, int mipLevel = 0)
    {
        var (w, h, _) = GetMipmapDimensions(Width, Height, Depth, mipLevel);
        return PixelFormatConverter.ToRgba8888(GetImageData(frame, face, slice, mipLevel), w, h, Format);
    }

    public byte[]? GetThumbnailRgba()
    {
        return HasThumbnail ? PixelFormatConverter.ToRgba8888(ThumbnailData, ThumbnailWidth, ThumbnailHeight, ThumbnailFormat) : null;
    }

    #endregion

    #region Creation

    public static VtfFile Create(ReadOnlySpan<byte> rgba, int width, int height, VtfCreateOptions? options = null)
    {
        return Create([rgba[..(width * height * 4)].ToArray()], width, height, options);
    }

    public static VtfFile Create(IReadOnlyList<byte[]> images, int width, int height, VtfCreateOptions? options = null)
    {
        options ??= new VtfCreateOptions();

        if (options.MajorVersion != 7 || options.MinorVersion < 0 || options.MinorVersion > 5)
        {
            throw new ArgumentException("VTF version must be between 7.0 and 7.5.", nameof(options));
        }

        if (options.FaceCount != 1 && options.FaceCount != 6)
        {
            throw new ArgumentException("Face count must be 1 or 6.", nameof(options));
        }

        if (images.Count == 0 || images.Count % options.FaceCount != 0)
        {
            throw new ArgumentException("Image count must be a non-zero multiple of the face count.", nameof(images));
        }

        VtfImageFormatInfo formatInfo = VtfImageFormatInfo.Get(options.Format);

        if (!formatInfo.CanEncode)
        {
            throw new NotSupportedException("Encoding " + formatInfo.Name + " is not supported.");
        }

        foreach (byte[] image in images)
        {
            if (image.Length < width * height * 4)
            {
                throw new ArgumentException("Image buffer is smaller than width * height * 4.", nameof(images));
            }
        }

        var (newWidth, newHeight) = GetResizedDimensions(width, height, options);

        if (newWidth != width || newHeight != height)
        {
            images = images.Select(image => ImageResampler.Resize(image, width, height, newWidth, newHeight, options.ResizeFilter)).ToArray();
            width = newWidth;
            height = newHeight;
        }

        ValidateDimension(width, nameof(width));
        ValidateDimension(height, nameof(height));

        VtfImageFormat storedFormat = options.Format == VtfImageFormat.DXT1OneBitAlpha ? VtfImageFormat.DXT1 : options.Format;
        int frames = images.Count / options.FaceCount;
        int mipmaps = options.GenerateMipmaps ? ComputeMipmapCount(width, height) : 1;

        VtfFile file = new VtfFile
        {
            MajorVersion = options.MajorVersion,
            MinorVersion = options.MinorVersion,
            Width = width,
            Height = height,
            Depth = 1,
            FrameCount = frames,
            FaceCount = options.FaceCount,
            StartFrame = options.StartFrame,
            BumpmapScale = options.BumpmapScale,
            Format = storedFormat,
            MipmapCount = mipmaps,
            Flags = (formatInfo.AlphaBits == 1 ? VtfFlags.OneBitAlpha : 0)
                | (formatInfo.AlphaBits > 1 ? VtfFlags.EightBitAlpha : 0)
                | (options.FaceCount == 6 ? VtfFlags.EnvMap : 0)
                | (options.GenerateMipmaps ? 0 : VtfFlags.NoMip | VtfFlags.NoLod)
                | options.Flags,
        };

        byte[][][] chains = new byte[images.Count][][];
        Parallel.For(0, images.Count, i => chains[i] = BuildMipChain(images[i], width, height, mipmaps, options.MipmapFilter, options.AlphaWeightedMipmaps));

        long dataSize = ComputeImageDataSize(width, height, 1, mipmaps, frames, options.FaceCount, storedFormat);
        file.ImageData = GC.AllocateUninitializedArray<byte>(checked((int)dataSize));

        var work = new List<(int Image, int Level)>();

        for (int image = 0; image < images.Count; image++)
        {
            for (int level = 0; level < mipmaps; level++)
            {
                work.Add((image, level));
            }
        }

        Parallel.ForEach(work, item =>
        {
            var (w, h, _) = GetMipmapDimensions(width, height, 1, item.Level);
            int frame = item.Image / options.FaceCount;
            int face = item.Image % options.FaceCount;
            PixelFormatConverter.FromRgba8888(chains[item.Image][item.Level], w, h, options.Format,
                file.ImageData, file.GetImageOffset(frame, face, 0, item.Level), options.AlphaThreshold);
        });

        if (options.GenerateThumbnail)
        {
            file.GenerateThumbnail(chains[0], options.AlphaWeightedMipmaps);
        }

        if (options.ComputeReflectivity)
        {
            float[] sum = new float[3];

            foreach (byte[][] chain in chains)
            {
                float[] reflectivity = ComputeImageReflectivity(chain[0], width, height);
                sum[0] += reflectivity[0];
                sum[1] += reflectivity[1];
                sum[2] += reflectivity[2];
            }

            file.Reflectivity = [sum[0] / chains.Length, sum[1] / chains.Length, sum[2] / chains.Length];
        }
        else
        {
            file.Reflectivity = [.. options.Reflectivity.Take(3)];
        }

        return file;
    }

    private static void ValidateDimension(int size, string name)
    {
        if (size <= 0 || size > 0xFFFF || (!IsPowerOfTwo(size) && size % 4 != 0))
        {
            throw new ArgumentException("Invalid image " + name + " " + size + ". It must be a power of two or a multiple of four (or enable resizing).", name);
        }
    }

    private static (int Width, int Height) GetResizedDimensions(int width, int height, VtfCreateOptions options)
    {
        static int Resize(int size, VtfResizeMethod method, int clamp)
        {
            int result = size;

            switch (method)
            {
                case VtfResizeMethod.NearestPowerOfTwo:
                case VtfResizeMethod.BiggestPowerOfTwo:
                case VtfResizeMethod.SmallestPowerOfTwo:
                    if (!IsPowerOfTwo(size))
                    {
                        int next = 1;

                        while (next < size)
                        {
                            next <<= 1;
                        }

                        int previous = Math.Max(1, next >> 1);
                        result = method switch
                        {
                            VtfResizeMethod.BiggestPowerOfTwo => next,
                            VtfResizeMethod.SmallestPowerOfTwo => previous,
                            _ => size - previous < next - size ? previous : next,
                        };
                    }

                    if (clamp > 0 && result > clamp)
                    {
                        result = clamp;
                    }

                    break;
                case VtfResizeMethod.NearestMultipleOfFour:
                    result = Math.Max(4, (size + 2) / 4 * 4);
                    break;
            }

            return result;
        }

        return (Resize(width, options.ResizeMethod, options.ResizeClamp), Resize(height, options.ResizeMethod, options.ResizeClamp));
    }

    private static byte[] Downscale(byte[] rgba, int width, int height, int newWidth, int newHeight, ResampleFilter filter, bool alphaWeighted)
    {
        return alphaWeighted
            ? ImageResampler.ResizeAlphaWeighted(rgba, width, height, newWidth, newHeight, filter)
            : ImageResampler.Resize(rgba, width, height, newWidth, newHeight, filter);
    }

    private static byte[][] BuildMipChain(byte[] image, int width, int height, int mipmaps, ResampleFilter filter, bool alphaWeighted)
    {
        byte[][] chain = new byte[mipmaps][];
        chain[0] = image;

        for (int level = 1; level < mipmaps; level++)
        {
            var (pw, ph, _) = GetMipmapDimensions(width, height, 1, level - 1);
            var (w, h, _) = GetMipmapDimensions(width, height, 1, level);
            chain[level] = Downscale(chain[level - 1], pw, ph, w, h, filter, alphaWeighted);
        }

        return chain;
    }

    private void GenerateThumbnail(byte[][] chain, bool alphaWeighted)
    {
        int w = Width, h = Height;

        while (w > 16 || h > 16)
        {
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }

        byte[]? source = null;

        for (int level = 0; level < chain.Length; level++)
        {
            var (mw, mh, _) = GetMipmapDimensions(Width, Height, 1, level);

            if (mw == w && mh == h)
            {
                source = chain[level];
                break;
            }
        }

        if (source == null)
        {
            byte[] current = chain[0];
            int cw = Width, ch = Height;

            while (cw % 2 == 0 && ch % 2 == 0 && cw / 2 >= w * 4 && ch / 2 >= h * 4)
            {
                current = Downscale(current, cw, ch, cw / 2, ch / 2, ResampleFilter.Box, alphaWeighted);
                cw /= 2;
                ch /= 2;
            }

            source = Downscale(current, cw, ch, w, h, ResampleFilter.CatmullRom, alphaWeighted);
        }

        ThumbnailFormat = VtfImageFormat.DXT1;
        ThumbnailWidth = w;
        ThumbnailHeight = h;
        ThumbnailData = PixelFormatConverter.FromRgba8888(source, w, h, VtfImageFormat.DXT1);
    }

    public static float[] ComputeImageReflectivity(ReadOnlySpan<byte> rgba, int width, int height)
    {
        float[] table = new float[256];

        for (int i = 0; i < 256; i++)
        {
            table[i] = MathF.Pow(i / 255f, 2.2f);
        }

        float[] rows = GC.AllocateUninitializedArray<float>(height * 3);
        ReflectivityRows(rgba, width, height, table, rows);

        double x = 0, y = 0, z = 0;

        for (int row = 0; row < height; row++)
        {
            x += rows[row * 3];
            y += rows[row * 3 + 1];
            z += rows[row * 3 + 2];
        }

        return [(float)(x / height), (float)(y / height), (float)(z / height)];
    }

    private static unsafe void ReflectivityRows(ReadOnlySpan<byte> rgba, int width, int height, float[] table, float[] rows)
    {
        if (height < 64 || width < 64)
        {
            ReflectivityRange(rgba, width, 0, height, table, rows);
            return;
        }

        fixed (byte* pointer = rgba)
        {
            byte* source = pointer;
            int length = rgba.Length;
            int chunk = Math.Max(16, height / (Environment.ProcessorCount * 4));
            Parallel.For(0, (height + chunk - 1) / chunk, c =>
            {
                int first = c * chunk;
                ReflectivityRange(new ReadOnlySpan<byte>(source, length), width, first, Math.Min(height, first + chunk), table, rows);
            });
        }
    }

    private static void ReflectivityRange(ReadOnlySpan<byte> rgba, int width, int start, int end, float[] table, float[] rows)
    {
        for (int row = start; row < end; row++)
        {
            float rx = 0, ry = 0, rz = 0;
            ReadOnlySpan<byte> line = rgba.Slice(row * width * 4, width * 4);

            for (int i = 0; i < width; i++)
            {
                rx += table[line[i * 4]];
                ry += table[line[i * 4 + 1]];
                rz += table[line[i * 4 + 2]];
            }

            rows[row * 3] = rx / width;
            rows[row * 3 + 1] = ry / width;
            rows[row * 3 + 2] = rz / width;
        }
    }

    #endregion

    #region Reading

    public static VtfFile Load(string path) => Load(File.ReadAllBytes(path));

    public static VtfFile Load(Stream stream)
    {
        using MemoryStream memory = new MemoryStream();
        stream.CopyTo(memory);
        return Load(memory.ToArray());
    }

    public static VtfFile Load(ReadOnlySpan<byte> data, bool headerOnly = false)
    {
        if (data.Length < 16 || !data[..4].SequenceEqual(Signature))
        {
            throw new InvalidDataException("File signature does not match 'VTF'.");
        }

        VtfFile file = new VtfFile
        {
            MajorVersion = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            MinorVersion = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
        };

        if (file.MajorVersion != 7 || file.MinorVersion < 0 || file.MinorVersion > 5)
        {
            throw new InvalidDataException("Unsupported VTF version " + file.MajorVersion + "." + file.MinorVersion + ".");
        }

        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);

        if (headerSize < 63 || headerSize > data.Length)
        {
            throw new InvalidDataException("VTF header size " + headerSize + " is invalid.");
        }

        file.Width = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
        file.Height = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
        file.Flags = (VtfFlags)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        file.FrameCount = Math.Max((int)BinaryPrimitives.ReadUInt16LittleEndian(data[24..]), 1);
        file.StartFrame = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]);
        file.Reflectivity =
        [
            BinaryPrimitives.ReadSingleLittleEndian(data[32..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[36..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[40..]),
        ];
        file.BumpmapScale = BinaryPrimitives.ReadSingleLittleEndian(data[48..]);
        file.Format = (VtfImageFormat)BinaryPrimitives.ReadInt32LittleEndian(data[52..]);
        file.MipmapCount = Math.Max((int)data[56], 1);
        file.ThumbnailFormat = (VtfImageFormat)BinaryPrimitives.ReadInt32LittleEndian(data[57..]);
        file.ThumbnailWidth = data[61];
        file.ThumbnailHeight = data[62];
        file.Depth = file.MinorVersion >= 2 && headerSize >= 65 ? Math.Max((int)BinaryPrimitives.ReadUInt16LittleEndian(data[63..]), 1) : 1;

        if (file.Width == 0 || file.Height == 0)
        {
            throw new InvalidDataException("VTF has zero width or height.");
        }

        if (file.Format < VtfImageFormat.None || (int)file.Format > (int)VtfImageFormat.HdrBGRA8888)
        {
            throw new InvalidDataException("Unknown VTF image format " + (int)file.Format + ".");
        }

        if (file.ThumbnailFormat < VtfImageFormat.None || (int)file.ThumbnailFormat > (int)VtfImageFormat.HdrBGRA8888 || file.ThumbnailWidth == 0 || file.ThumbnailHeight == 0)
        {
            file.ThumbnailFormat = VtfImageFormat.None;
        }

        int thumbnailSize = file.ThumbnailFormat != VtfImageFormat.None
            ? PixelFormatConverter.GetImageSize(file.ThumbnailWidth, file.ThumbnailHeight, file.ThumbnailFormat)
            : 0;

        long thumbnailOffset = -1;
        long imageOffset = -1;

        if (file.SupportsResources)
        {
            int resourceCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[68..]);

            if (resourceCount > MaxResources || 80 + resourceCount * 8 > data.Length)
            {
                throw new InvalidDataException("VTF resource directory is corrupt.");
            }

            for (int i = 0; i < resourceCount; i++)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(data[(80 + i * 8)..]);
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[(84 + i * 8)..]);

                if (type == LowResImageResource)
                {
                    thumbnailOffset = value;
                }
                else if (type == ImageResource)
                {
                    imageOffset = value;
                }
                else if ((type & VtfResource.NoDataChunkFlag) != 0)
                {
                    file.Resources.Add(new VtfResource(type, null, value));
                }
                else
                {
                    if (value + 4L > data.Length)
                    {
                        throw new InvalidDataException("VTF resource data lies outside the file.");
                    }

                    int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(int)value..]);

                    if (value + 4L + size > data.Length)
                    {
                        throw new InvalidDataException("VTF resource data lies outside the file.");
                    }

                    file.Resources.Add(new VtfResource(type, data.Slice((int)value + 4, size).ToArray()));
                }
            }

            if (thumbnailOffset < 0)
            {
                file.ThumbnailFormat = VtfImageFormat.None;
                thumbnailSize = 0;
            }
        }
        else
        {
            thumbnailOffset = headerSize;
            imageOffset = headerSize + thumbnailSize;
        }

        file.FaceCount = 1;

        if (file.Flags.HasFlag(VtfFlags.EnvMap))
        {
            bool sphereMap = file.MinorVersion >= 1 && file.MinorVersion < 5 && file.StartFrame != 0xFFFF;
            file.FaceCount = sphereMap ? 7 : 6;

            long available = imageOffset >= 0 ? data.Length - imageOffset : 0;

            if (sphereMap && ComputeImageDataSize(file.Width, file.Height, file.Depth, file.MipmapCount, file.FrameCount, 7, file.Format) > available)
            {
                file.FaceCount = 6;
            }
        }

        if (headerOnly)
        {
            return file;
        }

        if (file.ThumbnailFormat != VtfImageFormat.None)
        {
            if (thumbnailOffset + thumbnailSize > data.Length)
            {
                throw new InvalidDataException("VTF is too small for its thumbnail data.");
            }

            file.ThumbnailData = data.Slice((int)thumbnailOffset, thumbnailSize).ToArray();
        }

        if (file.Format != VtfImageFormat.None && imageOffset >= 0)
        {
            VtfImageFormatInfo info = VtfImageFormatInfo.Get(file.Format);

            if (!info.IsCompressed && info.BytesPerPixel == 0)
            {
                throw new NotSupportedException("VTF image format " + info.Name + " is not supported.");
            }

            long imageSize = ComputeImageDataSize(file.Width, file.Height, file.Depth, file.MipmapCount, file.FrameCount, file.FaceCount, file.Format);

            if (imageOffset + imageSize > data.Length)
            {
                throw new InvalidDataException("VTF is too small for its image data.");
            }

            file.ImageData = data.Slice((int)imageOffset, (int)imageSize).ToArray();
        }

        return file;
    }

    #endregion

    #region Writing

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Save(stream);
    }

    public byte[] ToArray()
    {
        using MemoryStream stream = new MemoryStream();
        Save(stream);
        return stream.ToArray();
    }

    public void Save(Stream stream)
    {
        if (MajorVersion != 7 || MinorVersion < 0 || MinorVersion > 5)
        {
            throw new InvalidOperationException("VTF version must be between 7.0 and 7.5.");
        }

        if (Format == VtfImageFormat.None || ImageData.Length == 0)
        {
            throw new InvalidOperationException("No image to save.");
        }

        long expectedSize = ComputeImageDataSize(Width, Height, Depth, MipmapCount, FrameCount, FaceCount, Format);

        if (ImageData.Length != expectedSize)
        {
            throw new InvalidOperationException("Image data size does not match the header.");
        }

        byte[] thumbnail = HasThumbnail ? ThumbnailData! : [];

        var entries = new List<(uint Type, byte[]? Chunk, uint Inline)>();

        if (SupportsResources)
        {
            if (HasThumbnail)
            {
                entries.Add((LowResImageResource, thumbnail, 0));
            }

            entries.Add((ImageResource, ImageData, 0));

            foreach (VtfResource resource in Resources)
            {
                entries.Add((resource.Type, resource.HasDataChunk ? resource.Data ?? [] : null, resource.InlineValue));
            }

            entries.Sort((a, b) => a.Type.CompareTo(b.Type));

            if (entries.Count > MaxResources)
            {
                throw new InvalidOperationException("Too many VTF resources.");
            }
        }

        int headerSize = MinorVersion switch
        {
            0 or 1 => 64,
            2 => 80,
            _ => 80 + entries.Count * 8,
        };

        byte[] header = new byte[headerSize];
        Span<byte> h = header;

        Signature.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)MajorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h[16..], (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(h[18..], (ushort)Height);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h[24..], (ushort)FrameCount);
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], (ushort)StartFrame);
        BinaryPrimitives.WriteSingleLittleEndian(h[32..], Reflectivity[0]);
        BinaryPrimitives.WriteSingleLittleEndian(h[36..], Reflectivity[1]);
        BinaryPrimitives.WriteSingleLittleEndian(h[40..], Reflectivity[2]);
        BinaryPrimitives.WriteSingleLittleEndian(h[48..], BumpmapScale);
        BinaryPrimitives.WriteInt32LittleEndian(h[52..], (int)Format);
        h[56] = (byte)MipmapCount;
        BinaryPrimitives.WriteInt32LittleEndian(h[57..], (int)(HasThumbnail ? ThumbnailFormat : VtfImageFormat.None));
        h[61] = (byte)(HasThumbnail ? ThumbnailWidth : 0);
        h[62] = (byte)(HasThumbnail ? ThumbnailHeight : 0);

        if (MinorVersion >= 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h[63..], (ushort)Depth);
        }

        if (SupportsResources)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[68..], (uint)entries.Count);
            uint offset = (uint)headerSize;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                BinaryPrimitives.WriteUInt32LittleEndian(h[(80 + i * 8)..], entry.Type);

                if (entry.Chunk == null)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(h[(84 + i * 8)..], entry.Inline);
                    continue;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(h[(84 + i * 8)..], offset);
                bool rawImage = entry.Type == LowResImageResource || entry.Type == ImageResource;
                offset += (uint)entry.Chunk.Length + (rawImage ? 0u : 4u);
            }

            stream.Write(header);
            Span<byte> length = stackalloc byte[4];

            foreach (var entry in entries)
            {
                if (entry.Chunk == null)
                {
                    continue;
                }

                if (entry.Type != LowResImageResource && entry.Type != ImageResource)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)entry.Chunk.Length);
                    stream.Write(length);
                }

                stream.Write(entry.Chunk);
            }
        }
        else
        {
            stream.Write(header);
            stream.Write(thumbnail);
            stream.Write(ImageData);
        }
    }

    #endregion
}
