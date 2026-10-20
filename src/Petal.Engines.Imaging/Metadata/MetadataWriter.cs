using System.Text;
using ImageMagick;
using ImageMagick.Formats;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Metadata;

/// <summary>
/// Saves metadata changes. JPEG, PNG and WEBP are rewritten at the container level so the
/// compressed pixels are untouched; other formats are re-encoded.
/// </summary>
internal static class MetadataWriter
{
    /// <summary>PNG chunks that hold descriptive metadata (C2PA "caBX" included).</summary>
    private static readonly HashSet<string> PngMetadataChunks = ["eXIf", "tEXt", "zTXt", "iTXt", "tIME", "caBX"];

    public static bool IsLossless(FileFormat? format) => format == F.Jpg || format == F.Png || format == F.Webp;

    public static async Task WriteAsync(string inputPath, byte[] original, FileFormat? format, MetadataState state, string outputPath,
        bool removeAll, CancellationToken cancellationToken)
    {
        byte[]? spliced = format switch
        {
            _ when format == F.Jpg => Jpeg(original, state, removeAll),
            _ when format == F.Png && (removeAll || !state.IptcChanged) => Png(original, state, removeAll),
            _ when format == F.Webp => Webp(original, state, removeAll),
            _ when format == F.Ico => original, // icons carry no metadata
            _ => null,
        };
        cancellationToken.ThrowIfCancellationRequested();
        if (spliced is not null)
        {
            AtomicFile.WriteAllBytes(outputPath, spliced);
            return;
        }
        await ReencodeAsync(inputPath, original, format, state, outputPath, removeAll, cancellationToken);
    }

    private static byte[] Jpeg(byte[] original, MetadataState state, bool removeAll)
    {
        var jpeg = JpegFile.Parse(original);
        if (removeAll)
        {
            // Keep only what decoding needs: JFIF (minus its thumbnail), Adobe colour transform, ICC, tables.
            var kept = jpeg.Segments
                .Where(s => !(s.IsApp || s.IsComment) || s.IsIcc || s.IsAdobe || s.IsJfif)
                .Select(s => s.IsJfif ? JpegFile.CleanJfif(s) : s)
                .ToList();
            jpeg.Segments.Clear();
            jpeg.Segments.AddRange(kept);
            if (state.OrientationOnlyExif() is { } orientation)
                jpeg.Segments.Insert(ExifIndex(jpeg), JpegFile.ExifSegment(orientation));
            // Anything after EOI (MPF HDR gain maps, depth maps, maker trailers) goes too.
            return jpeg.Build(keepTrailer: false);
        }

        if (state.ExifChanged)
        {
            jpeg.Segments.RemoveAll(s => s.IsExif);
            var exif = state.ExifBytes();
            if (exif is { Length: > JpegFile.MaxSegmentPayload })
            {
                // APP1 holds at most 64 KB; the embedded thumbnail is the usual culprit.
                state.Exif!.RemoveThumbnail();
                exif = state.ExifBytes();
                if (exif is { Length: > JpegFile.MaxSegmentPayload })
                    throw new ArgumentException("The EXIF data is too large to fit in a JPEG file.");
            }
            if (exif is not null)
                jpeg.Segments.Insert(ExifIndex(jpeg), JpegFile.ExifSegment(exif));
        }
        if (state.XmpChanged)
        {
            var xmp = state.XmpBytes();
            int at = jpeg.Segments.FindIndex(s => s.IsXmp);
            jpeg.Segments.RemoveAll(s => s.IsXmp || (xmp is null && s.IsExtendedXmp));
            if (xmp is not null)
            {
                if (at < 0)
                    at = ExifIndex(jpeg) + (jpeg.Segments.Any(s => s.IsExif) ? 1 : 0);
                jpeg.Segments.Insert(Math.Min(at, jpeg.Segments.Count), JpegFile.XmpSegment(xmp));
            }
        }
        if (state.IptcChanged)
        {
            var existing = jpeg.Segments.FirstOrDefault(s => s.IsPhotoshop);
            var block = JpegFile.PhotoshopSegment(existing, state.IptcBytes(), keepOtherResources: true);
            bool empty = block.Payload.Length <= JpegFile.PhotoshopHeader.Length;
            jpeg.Replace(s => s.IsPhotoshop, empty ? [] : [block]);
        }
        if (state.IccRemoved)
            jpeg.Segments.RemoveAll(s => s.IsIcc);
        if (state.TextsChanged)
        {
            var comments = state.Texts.Where(t => t.Key.Equals("comment", StringComparison.OrdinalIgnoreCase))
                .Select(t => JpegFile.CommentSegment(t.Value)).ToList();
            jpeg.Replace(s => s.IsComment, comments);
        }
        return jpeg.Build(keepTrailer: true);
    }

    /// <summary>EXIF belongs right after SOI, or after a JFIF APP0.</summary>
    private static int ExifIndex(JpegFile jpeg) => jpeg.Segments.Count > 0 && jpeg.Segments[0].IsJfif ? 1 : 0;

    private static byte[] Png(byte[] original, MetadataState state, bool removeAll)
    {
        var png = PngFile.Parse(original);
        if (removeAll)
        {
            png.Chunks.RemoveAll(c => PngMetadataChunks.Contains(c.Type));
            if (state.OrientationOnlyExif() is { } orientation)
                png.InsertBeforeData([PngFile.ExifChunk(orientation)]);
            return png.Build();
        }

        var additions = new List<PngFile.Chunk>();
        if (state.ExifChanged)
        {
            png.Chunks.RemoveAll(c => c.Type == "eXIf" || IsRawProfile(c, "exif") || IsRawProfile(c, "APP1"));
            if (state.ExifBytes() is { } exif)
                additions.Add(PngFile.ExifChunk(exif));
        }
        if (state.XmpChanged)
        {
            png.Chunks.RemoveAll(c => PngFile.TextKeyword(c) == PngFile.XmpKeyword || IsRawProfile(c, "xmp"));
            if (state.XmpBytes() is { } xmp)
                additions.Add(PngFile.TextChunk(PngFile.XmpKeyword, Encoding.UTF8.GetString(xmp)));
        }
        if (state.IccRemoved)
            png.Chunks.RemoveAll(c => c.Type == "iCCP");
        if (state.TextsChanged)
        {
            png.Chunks.RemoveAll(c => PngFile.TextKeyword(c) is { } key && MetadataState.IsUserText(key));
            foreach (var (key, value) in state.Texts)
                additions.Add(PngFile.TextChunk(key.Equals("comment", StringComparison.OrdinalIgnoreCase) ? "Comment" : key, value));
        }
        if (additions.Count > 0)
            png.InsertBeforeData(additions);
        return png.Build();
    }

    private static bool IsRawProfile(PngFile.Chunk chunk, string name) =>
        PngFile.TextKeyword(chunk) is { } key && key.Equals("Raw profile type " + name, StringComparison.OrdinalIgnoreCase);

    private static byte[] Webp(byte[] original, MetadataState state, bool removeAll)
    {
        var webp = WebpFile.Parse(original);
        var icc = state.IccRemoved ? null : webp.Icc;
        byte[]? exif, xmp;
        if (removeAll)
        {
            exif = state.OrientationOnlyExif();
            xmp = null;
        }
        else
        {
            exif = state.ExifChanged ? state.ExifBytes() : webp.Exif;
            xmp = state.XmpChanged ? state.XmpBytes() : webp.Xmp;
        }
        return webp.Build(icc, exif, xmp, state.Width, state.Height);
    }

    private static async Task ReencodeAsync(string inputPath, byte[] original, FileFormat? format, MetadataState state, string outputPath,
        bool removeAll, CancellationToken cancellationToken)
    {
        if (format is null || !ImageFormats.CanWrite(format))
            throw new NotSupportedException("Metadata can't be changed in this file type");

        if (format == F.Tiff || format == F.Gif)
        {
            // Every page/frame is kept.
            using var frames = new MagickImageCollection();
            frames.Read(original, new MagickReadSettings { Format = ImageFormats.ToMagick(format) });
            foreach (var frame in frames)
            {
                if (removeAll)
                    MetadataHygiene.Strip((MagickImage)frame, keepColorProfile: true);
                else
                    state.ApplyTo(frame);
            }
            using var file = AtomicFile.Create(outputPath);
            await using (var fs = new FileStream(file.TempPath, FileMode.CreateNew, FileAccess.Write))
            {
                if (format == F.Tiff)
                {
                    using var tiff = new MemoryStream();
                    frames.Write(tiff, new TiffWriteDefines { PreserveCompression = true });
                    var exif = removeAll ? null : state.ExifChanged ? state.ExifBytes() : state.Exif?.ToByteArray();
                    ImageWriter.WriteTiff(tiff.ToArray(), exif, fs);
                }
                else
                {
                    frames.Write(fs, MagickFormat.Gif);
                }
            }
            file.Commit();
            return;
        }

        // HEIC/AVIF/BMP: single image. Remove All bakes the orientation in, since EXIF goes away.
        using var image = ImageLoader.Load(inputPath, format, new LoadOptions { AutoOrient = removeAll || format == F.Heic });
        if (removeAll)
            MetadataHygiene.Strip(image, keepColorProfile: true);
        else
            state.ApplyTo(image);
        await ImageWriter.WriteAsync(image, outputPath, format, new ImageWriteOptions { Quality = 92, KeepMetadata = !removeAll }, cancellationToken);
    }
}
