using ImageMagick;
using ImageMagick.Formats;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

internal enum TiffMode
{
    Lzw,
    Zip,
    Jpeg,
}

internal sealed record ImageWriteOptions
{
    public static ImageWriteOptions Default { get; } = new();

    /// <summary>JPEG / WEBP / HEIC / AVIF quality, 1–100.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>Write EXIF, XMP, IPTC and comments where the format can hold them.</summary>
    public bool KeepMetadata { get; init; } = true;

    public bool KeepColorProfile { get; init; } = true;

    /// <summary>
    /// zlib level for PNG. 4 is about 4× faster than 7 on photos for ~10 % larger files; the
    /// Compress tool asks for 9.
    /// </summary>
    public int PngCompressionLevel { get; init; } = 4;

    /// <summary>zlib/PNG row filter: 0–4 fixed, 5 adaptive.</summary>
    public int PngFilter { get; init; } = 5;

    /// <summary>zlib strategy: 0 default, 1 filtered, 2 Huffman only, 3 RLE.</summary>
    public int PngStrategy { get; init; }

    /// <summary>Quantise to a dithered palette of <see cref="PaletteColors"/> (lossy, much smaller): PNG8, or RLE8 BMP.</summary>
    public bool Palette { get; init; }

    public int PaletteColors { get; init; } = 256;

    /// <summary>libwebp effort, 0 (fast) – 6 (smallest).</summary>
    public int WebpMethod { get; init; } = 4;

    public bool WebpLossless { get; init; }

    public TiffMode Tiff { get; init; } = TiffMode.Lzw;

    public bool ProgressiveJpeg { get; init; }

    /// <summary>
    /// libheif/aom effort for AVIF, 0 (slowest, smallest) – 9 (fastest). 7 is ~1.5× faster than
    /// 6 for under 1 % larger files; 8 is 4× faster but ~10 % larger.
    /// </summary>
    public int AvifSpeed { get; init; } = 7;

    /// <summary>x265 preset for HEIC.</summary>
    public string HeicPreset { get; init; } = "medium";

    /// <summary>For PDF output: an untouched JPEG file whose bytes may be embedded as-is.</summary>
    public string? PdfJpegSource { get; init; }

    /// <summary>For PDF output: store pixels as JPEG instead of lossless Flate.</summary>
    public bool PdfPreferJpeg { get; init; }
}

/// <summary>Encodes a decoded image to any output format Petal supports.</summary>
internal static class ImageWriter
{
    public static async Task WriteAsync(MagickImage image, string path, FileFormat format, ImageWriteOptions? options, CancellationToken cancellationToken)
    {
        using var file = AtomicFile.Create(path);
        await using (var fs = new FileStream(file.TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            await EncodeAsync(image, fs, format, options ?? ImageWriteOptions.Default, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        file.Commit();
    }

    public static async Task<byte[]> EncodeToBytesAsync(MagickImage image, FileFormat format, ImageWriteOptions? options, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await EncodeAsync(image, ms, format, options ?? ImageWriteOptions.Default, cancellationToken);
        return ms.ToArray();
    }

    public static async Task EncodeAsync(MagickImage image, Stream output, FileFormat format, ImageWriteOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ImageFormats.CanWrite(format))
            throw new NotSupportedException($"Cannot write {format.Label} images");

        MagickImage work = image;
        bool owned = false;
        try
        {
            void Own()
            {
                if (owned) return;
                work = (MagickImage)image.Clone();
                owned = true;
            }

            if (work.HasAlpha && !ImageFormats.SupportsAlpha(format))
            {
                Own();
                FlattenOnWhite(work);
            }
            if (!options.KeepMetadata)
            {
                Own();
                MetadataHygiene.Strip(work, options.KeepColorProfile);
            }
            else if (!options.KeepColorProfile && work.GetColorProfile() is not null)
            {
                Own();
                work.RemoveProfile("icc");
            }

            if (format == F.Heic)
            {
                await HeifWriter.EncodeAsync(work, output, options, cancellationToken);
                return;
            }
            if (format == F.Pdf)
            {
                PdfDocumentWriter.WriteSinglePage(work, output, options);
                return;
            }
            if (format == F.Ico)
            {
                IcoWriter.Write(work, output, options);
                return;
            }
            if (format == F.Avif)
            {
                await WriteAvifAsync(work, output, options, cancellationToken);
                return;
            }

            if (options.Palette && (format == F.Png || format == F.Bmp))
            {
                Own();
                Quantize(work, options.PaletteColors);
            }
            WriteWithMagick(work, output, format, options);
        }
        finally
        {
            if (owned)
                work.Dispose();
        }
    }

    internal static void FlattenOnWhite(MagickImage image)
    {
        if (!image.HasAlpha)
            return;
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
        image.Alpha(AlphaAction.Off);
    }

    internal static void Quantize(MagickImage image, int colors) =>
        image.Quantize(new QuantizeSettings
        {
            Colors = (uint)Math.Clamp(colors, 2, 256),
            DitherMethod = DitherMethod.FloydSteinberg,
            ColorSpace = ColorSpace.sRGB,
        });

    private static void WriteWithMagick(MagickImage image, Stream output, FileFormat format, ImageWriteOptions options)
    {
        int quality = Math.Clamp(options.Quality, 1, 100);
        switch (format.Id)
        {
            case "jpg":
                image.Quality = (uint)quality;
                image.Settings.Interlace = options.ProgressiveJpeg ? Interlace.Jpeg : Interlace.NoInterlace;
                image.Write(output, new JpegWriteDefines { OptimizeCoding = true });
                break;

            case "png":
                // PNG "quality" encodes zlib level and filter; set them explicitly instead (filter 5 = adaptive).
                image.Settings.SetDefine(MagickFormat.Png, "compression-level", Math.Clamp(options.PngCompressionLevel, 0, 9).ToString());
                image.Settings.SetDefine(MagickFormat.Png, "compression-filter", Math.Clamp(options.PngFilter, 0, 5).ToString());
                image.Settings.SetDefine(MagickFormat.Png, "compression-strategy", Math.Clamp(options.PngStrategy, 0, 4).ToString());
                // ImageMagick otherwise adds date:create/date:modify text chunks with the file system times.
                image.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date");
                image.Write(output, options.Palette ? MagickFormat.Png8 : MagickFormat.Png);
                break;

            case "webp":
                image.Quality = (uint)quality;
                image.Write(output, new WebPWriteDefines
                {
                    Method = Math.Clamp(options.WebpMethod, 0, 6),
                    Lossless = options.WebpLossless,
                    AlphaQuality = 100,
                });
                break;

            case "tiff":
                var mode = options.Tiff == TiffMode.Jpeg && image.HasAlpha ? TiffMode.Zip : options.Tiff;
                image.Settings.Compression = mode switch
                {
                    TiffMode.Jpeg => CompressionMethod.JPEG,
                    TiffMode.Zip => CompressionMethod.Zip,
                    _ => CompressionMethod.LZW,
                };
                image.Quality = (uint)quality;
                // Horizontal differencing makes LZW/Deflate far more effective on photos; it is
                // invalid for the palette TIFFs ImageMagick writes for few-colour images.
                bool predict = mode != TiffMode.Jpeg && image.ClassType != ClassType.Pseudo;
                image.Settings.SetDefine(MagickFormat.Tiff, "predictor", predict ? "2" : "1");
                WriteTiff(image.ToByteArray(MagickFormat.Tiff), image.GetExifProfile()?.ToByteArray(), output);
                break;

            case "bmp":
                // RLE8 needs a palette image; ImageMagick only compresses PseudoClass BMPs.
                image.Settings.Compression = options.Palette ? CompressionMethod.RLE : CompressionMethod.NoCompression;
                image.Write(output, MagickFormat.Bmp3);
                break;

            case "gif":
                image.Write(output, MagickFormat.Gif);
                break;

            case "jxl":
                // Quality 100 is mathematically lossless in JPEG XL; below it, as close to the quality asked.
                image.Quality = (uint)quality;
                image.Settings.SetDefine(MagickFormat.Jxl, "effort", "7");
                image.Write(output, MagickFormat.Jxl);
                break;

            case "jp2":
                // A palette PNG can load at 2 bits a channel, which JPEG 2000 would keep: full colour, 8 bits.
                image.ClassType = ClassType.Direct;
                image.Depth = 8;
                image.Quality = (uint)quality;
                image.Write(output, MagickFormat.Jp2);
                break;

            case "dds":
                // A texture: DXT5 (with alpha) or DXT1, with the smaller copies (mipmaps) games read at a distance.
                image.Settings.SetDefine(MagickFormat.Dds, "compression", image.HasAlpha ? "dxt5" : "dxt1");
                image.Write(output, MagickFormat.Dds);
                break;

            case "tga":
                image.Settings.Compression = CompressionMethod.RLE;
                image.Write(output, MagickFormat.Tga);
                break;

            case "qoi":
                image.Write(output, MagickFormat.Qoi);
                break;

            case "pcx":
                image.Write(output, MagickFormat.Pcx);
                break;

            default:
                throw new NotSupportedException($"Cannot write {format.Label} images");
        }
    }

    /// <summary>ImageMagick's TIFF coder drops EXIF; splice it into the first IFD ourselves.</summary>
    internal static void WriteTiff(byte[] tiff, byte[]? exif, Stream output)
    {
        if (exif is { Length: > 8 })
        {
            try
            {
                tiff = Metadata.TiffExif.Inject(tiff, exif);
            }
            catch (InvalidDataException)
            {
                // Unreadable EXIF: the image itself is still fine.
            }
        }
        output.Write(tiff);
    }

    private static async Task WriteAvifAsync(MagickImage image, Stream output, ImageWriteOptions options, CancellationToken cancellationToken)
    {
        image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
        image.Settings.SetDefine(MagickFormat.Heic, "speed", Math.Clamp(options.AvifSpeed, 0, 9).ToString());
        byte[] encoded;
        try
        {
            encoded = image.ToByteArray(MagickFormat.Avif);
        }
        catch (MagickException) when (FfmpegStillEncoder.CanEncodeAvif)
        {
            // libheif/aom refused (e.g. unusual dimensions); FFmpeg's AV1 encoders are a second opinion.
            await FfmpegStillEncoder.EncodeAvifAsync(image, output, options.Quality, cancellationToken);
            return;
        }
        await output.WriteAsync(encoded, cancellationToken);
    }
}
