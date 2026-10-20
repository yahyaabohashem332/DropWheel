using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.Filters;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Util;
using SkiaSharp;

namespace Petal.Engines.Documents.Pdf;

public enum PdfCompressionPreset
{
    /// <summary>Smaller files with little visible loss: pictures at up to 150 DPI, JPEG quality 75.</summary>
    Balanced,

    /// <summary>Smallest files: pictures at up to 100 DPI, JPEG quality 50.</summary>
    Strong,
}

/// <summary>Compression settings. A target (bytes or percentage) makes the compressor try stronger settings until it is met.</summary>
public sealed record PdfCompressOptions
{
    public PdfCompressionPreset Preset { get; init; } = PdfCompressionPreset.Balanced;

    /// <summary>Aim for at most this many bytes.</summary>
    public long? TargetBytes { get; init; }

    /// <summary>Aim to shrink by this percentage, e.g. 40 for a file 40% smaller.</summary>
    public double? TargetReductionPercent { get; init; }

    public string? Password { get; init; }
}

/// <param name="Written">False when compression would not make the file smaller; nothing is written then and <see cref="OutputBytes"/> equals <see cref="OriginalBytes"/>.</param>
/// <param name="TargetReached">Whether the requested target was met (true when no target was set and the file shrank).</param>
public sealed record PdfCompressResult(long OriginalBytes, long OutputBytes, bool Written, bool TargetReached, int ImagesRecompressed)
{
    /// <summary>Fraction saved, 0–1.</summary>
    public double Saved => OriginalBytes == 0 ? 0 : 1 - (double)OutputBytes / OriginalBytes;
}

/// <summary>
/// Shrinks PDFs: pictures are downsampled to the resolution they are shown at and re-encoded
/// as JPEG, uncompressed streams are deflated, private application data and unused objects are
/// dropped. The result is never larger than the original.
/// </summary>
public static class PdfCompressor
{
    private readonly record struct Level(int Quality, int MaxDpi, bool StripImageMetadata);

    private static readonly Level[] Levels =
    [
        new(75, 150, false), // Balanced
        new(65, 125, false),
        new(50, 100, true),  // Strong
        new(40, 85, true),
        new(32, 72, true),
        new(25, 60, true),
    ];

    public static Task<PdfCompressResult> CompressAsync(string input, string output, PdfCompressOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(input, output);
            options ??= new PdfCompressOptions();
            var original = File.ReadAllBytes(input);
            long? target = options.TargetBytes ?? (options.TargetReductionPercent is { } pct ? (long)(original.Length * (1 - Math.Clamp(pct, 0, 99) / 100)) : null);

            var start = options.Preset == PdfCompressionPreset.Strong ? 2 : 0;
            var end = target is null ? start : Levels.Length - 1;
            byte[]? best = null;
            var bestImages = 0;
            for (var i = start; i <= end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (bytes, images) = Pass(original, Levels[i], options.Password, cancellationToken);
                if (best is null || bytes.Length < best.Length)
                {
                    best = bytes;
                    bestImages = images;
                }
                progress?.Report(Math.Min(0.95, (i - start + 1.0) / (end - start + 1)));
                if (target is null || best.Length <= target)
                    break;
            }

            if (best is null || best.Length >= original.Length)
            {
                progress?.Report(1);
                return new PdfCompressResult(original.Length, original.Length, false, target is not null && original.Length <= target, 0);
            }
            SafeOutput.WriteBytes(output, best);
            progress?.Report(1);
            return new PdfCompressResult(original.Length, best.Length, true, target is null || best.Length <= target, bestImages);
        }, cancellationToken);

    private static (byte[] Bytes, int Images) Pass(byte[] original, Level level, string? password, CancellationToken ct)
    {
        using var document = PdfSharpIO.Open(original, PdfDocumentOpenMode.Modify, password);
        document.Options.CompressContentStreams = true;
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        document.Options.NoCompression = false;
        document.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;

        var displayed = DisplayedSizes(document);
        var images = 0;
        foreach (var obj in document.Internals.GetAllObjects())
        {
            ct.ThrowIfCancellationRequested();
            if (obj is not PdfDictionary dict || dict.Stream is null)
                continue;
            var subtype = dict.Elements.GetName("/Subtype");
            if (subtype == "/Image")
            {
                if (RecompressImage(dict, displayed.GetValueOrDefault(dict), level))
                    images++;
                if (level.StripImageMetadata)
                    dict.Elements.Remove("/Metadata");
                continue;
            }
            if (dict.Elements.GetName("/Type") is "/Metadata" or "/XRef" or "/ObjStm")
                continue;
            DeflateIfRaw(dict);
        }

        // Private application data and thumbnails: invisible, sometimes huge.
        document.Internals.Catalog.Elements.Remove("/PieceInfo");
        foreach (PdfPage page in document.Pages)
        {
            page.Elements.Remove("/PieceInfo");
            page.Elements.Remove("/Thumb");
        }

        var bytes = PdfSharpIO.SaveToBytes(document);
        return (RestoreProducer(original, bytes), images);
    }

    /// <summary>PDFsharp stamps its name into Info and XMP; put the original document information and XMP back.</summary>
    private static byte[] RestoreProducer(byte[] original, byte[] saved)
    {
        try
        {
            using var source = PdfReader.Open(new MemoryStream(original, false), PdfDocumentOpenMode.Import);
            var entries = PdfMetadataEditor.InfoEntries(source.Info);
            var xmp = PdfMetadataEditor.RawXmp(source);
            return PdfSharpIO.Finish(saved, entries, xmp is null ? PdfSharpIO.XmpAction.Remove : PdfSharpIO.XmpAction.Replace, xmp);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return saved;
        }
    }

    private static void DeflateIfRaw(PdfDictionary dict)
    {
        if (dict.Elements.ContainsKey("/Filter"))
            return;
        var data = dict.Stream!.Value;
        if (data.Length < 64)
            return;
        var deflated = Filtering.FlateDecode.Encode(data, PdfFlateEncodeMode.BestCompression);
        if (deflated.Length < data.Length * 0.9)
        {
            dict.Stream.Value = deflated;
            dict.Elements.SetName("/Filter", "/FlateDecode");
        }
    }

    /// <summary>Largest size (points) each image is drawn at, by walking page and form content streams.</summary>
    private static Dictionary<PdfDictionary, (double Width, double Height)> DisplayedSizes(PdfDocument document)
    {
        var sizes = new Dictionary<PdfDictionary, (double, double)>(ReferenceEqualityComparer.Instance);
        foreach (PdfPage page in document.Pages)
        {
            try
            {
                var resources = page.Elements.GetDictionary("/Resources");
                Walk(ContentReader.ReadContent(page), resources, Identity, sizes, depth: 0);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Unparseable content: its images are recompressed without downsampling.
            }
        }
        return sizes;
    }

    private static readonly double[] Identity = [1, 0, 0, 1, 0, 0];

    private static double[] Multiply(double[] m, double[] n) =>
    [
        m[0] * n[0] + m[1] * n[2], m[0] * n[1] + m[1] * n[3],
        m[2] * n[0] + m[3] * n[2], m[2] * n[1] + m[3] * n[3],
        m[4] * n[0] + m[5] * n[2] + n[4], m[4] * n[1] + m[5] * n[3] + n[5],
    ];

    private static void Walk(CSequence content, PdfDictionary? resources, double[] ctm, Dictionary<PdfDictionary, (double, double)> sizes, int depth)
    {
        if (depth > 8)
            return;
        var stack = new Stack<double[]>();
        var current = ctm;
        foreach (var item in content)
        {
            if (item is not COperator op)
                continue;
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.q:
                    stack.Push(current);
                    break;
                case OpCodeName.Q:
                    if (stack.Count > 0) current = stack.Pop();
                    break;
                case OpCodeName.cm when op.Operands.Count == 6:
                    var m = op.Operands.Select(Number).ToArray();
                    current = Multiply(m, current);
                    break;
                case OpCodeName.Do when op.Operands.Count == 1 && op.Operands[0] is CName name:
                {
                    var xobject = resources?.Elements.GetDictionary("/XObject")?.Elements.GetDictionary(name.Name);
                    if (xobject is null)
                        break;
                    var subtype = xobject.Elements.GetName("/Subtype");
                    if (subtype == "/Image")
                    {
                        var w = Math.Sqrt(current[0] * current[0] + current[1] * current[1]);
                        var h = Math.Sqrt(current[2] * current[2] + current[3] * current[3]);
                        var prev = sizes.GetValueOrDefault(xobject);
                        sizes[xobject] = (Math.Max(prev.Item1, w), Math.Max(prev.Item2, h));
                    }
                    else if (subtype == "/Form" && xobject.Stream is not null)
                    {
                        var matrix = xobject.Elements.GetArray("/Matrix") is { Elements.Count: 6 } arr
                            ? Enumerable.Range(0, 6).Select(i => arr.Elements.GetReal(i)).ToArray()
                            : Identity;
                        var formResources = xobject.Elements.GetDictionary("/Resources") ?? resources;
                        Walk(ContentReader.ReadContent(xobject.Stream.UnfilteredValue), formResources, Multiply(matrix, current), sizes, depth + 1);
                    }
                    break;
                }
            }
        }
    }

    private static double Number(CObject o) => o switch
    {
        CInteger i => i.Value,
        CReal r => r.Value,
        _ => 0,
    };

    private static bool RecompressImage(PdfDictionary dict, (double Width, double Height) shown, Level level)
    {
        var e = dict.Elements;
        if (e.GetBoolean("/ImageMask") || e.ContainsKey("/Mask") || e.ContainsKey("/Decode") || e.ContainsKey("/SMaskInData"))
            return false;
        var width = e.GetInteger("/Width");
        var height = e.GetInteger("/Height");
        if (width < 16 || height < 16)
            return false;

        var filterItem = e["/Filter"];
        var filter = filterItem switch
        {
            PdfName n => n.Value,
            PdfArray { Elements.Count: 1 } a => a.Elements.GetName(0),
            null => "",
            _ => "?",
        };
        if (filter is not ("" or "/DCTDecode" or "/FlateDecode"))
            return false;

        var components = Components(e["/ColorSpace"] is PdfReference r ? r.Value : e["/ColorSpace"]);
        if (components is not (1 or 3))
            return false;
        var original = dict.Stream!.Value;

        SKBitmap? bitmap = null;
        try
        {
            if (filter == "/DCTDecode")
            {
                bitmap = SKBitmap.Decode(original);
            }
            else
            {
                if (e.GetInteger("/BitsPerComponent") != 8)
                    return false;
                var raw = filter == "" ? original : Filtering.Decode(original, filterItem!, e["/DecodeParms"]);
                if (raw is null || raw.Length < (long)width * height * components)
                    return false;
                bitmap = FromSamples(raw, width, height, components);
            }
            if (bitmap is null)
                return false;

            // Downsample to the shown size at the level's DPI; pictures never shown keep their size.
            var target = bitmap;
            if (shown.Width > 0 && shown.Height > 0)
            {
                var maxW = (int)Math.Ceiling(shown.Width / 72 * level.MaxDpi);
                var maxH = (int)Math.Ceiling(shown.Height / 72 * level.MaxDpi);
                var scale = Math.Min((double)maxW / bitmap.Width, (double)maxH / bitmap.Height);
                if (scale < 0.9)
                {
                    var w = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                    var h = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
                    target = bitmap.Resize(new SKImageInfo(w, h, bitmap.ColorType, bitmap.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell));
                }
            }
            try
            {
                using var image = SKImage.FromBitmap(target);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, level.Quality);
                var jpeg = data.ToArray();
                if (jpeg.Length >= original.Length * 0.9)
                    return false;
                dict.Stream.Value = jpeg;
                e.SetName("/Filter", "/DCTDecode");
                e.Remove("/DecodeParms");
                e.SetInteger("/Width", target.Width);
                e.SetInteger("/Height", target.Height);
                e.SetInteger("/BitsPerComponent", 8);
                return true;
            }
            finally
            {
                if (!ReferenceEquals(target, bitmap))
                    target.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private static int Components(PdfItem? colorSpace) => colorSpace switch
    {
        PdfName { Value: "/DeviceGray" or "/CalGray" } => 1,
        PdfName { Value: "/DeviceRGB" or "/CalRGB" } => 3,
        PdfArray a when a.Elements.GetName(0) == "/ICCBased" => (a.Elements[1] is PdfReference r ? r.Value : a.Elements[1]) is PdfDictionary icc ? icc.Elements.GetInteger("/N") : 0,
        PdfArray a when a.Elements.GetName(0) is "/CalRGB" => 3,
        PdfArray a when a.Elements.GetName(0) is "/CalGray" => 1,
        _ => 0,
    };

    private static SKBitmap FromSamples(byte[] raw, int width, int height, int components)
    {
        if (components == 1)
        {
            var gray = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
            var stride = gray.RowBytes;
            var pixels = gray.GetPixelSpan();
            for (var y = 0; y < height; y++)
                raw.AsSpan(y * width, width).CopyTo(pixels.Slice(y * stride, width));
            return gray;
        }
        var rgba = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var span = rgba.GetPixelSpan();
        var rowBytes = rgba.RowBytes;
        for (var y = 0; y < height; y++)
        {
            var src = y * width * 3;
            var dst = y * rowBytes;
            for (var x = 0; x < width; x++, src += 3, dst += 4)
            {
                span[dst] = raw[src];
                span[dst + 1] = raw[src + 1];
                span[dst + 2] = raw[src + 2];
                span[dst + 3] = 255;
            }
        }
        return rgba;
    }
}
