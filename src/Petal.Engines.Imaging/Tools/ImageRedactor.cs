using System.Runtime.InteropServices.WindowsRuntime;
using ImageMagick;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;

namespace Petal.Engines.Imaging;

public enum RedactionStyle
{
    /// <summary>An opaque box of <see cref="RedactionRegion.Color"/>.</summary>
    Solid,

    /// <summary>Pixelated then blurred from very few samples, so it cannot be reversed.</summary>
    Blur,
}

/// <param name="Rect">Area in upright image pixels.</param>
/// <param name="Color">ARGB colour for <see cref="RedactionStyle.Solid"/> (alpha is ignored: redactions are opaque).</param>
public sealed record RedactionRegion(PixelRect Rect, RedactionStyle Style = RedactionStyle.Solid, uint Color = 0xFF000000);

/// <summary>A run of recognised text and its bounds.</summary>
/// <param name="IsLine">True for a whole line, false for a single word.</param>
public sealed record DetectedText(PixelRect Bounds, string Text, bool IsLine);

public enum TextGranularity
{
    Lines,
    Words,
}

/// <summary>Redact Photo tool: on-device face and text detection, and irreversible covering.</summary>
public static class ImageRedactor
{
    private const int FaceDetectionMaxSize = 1600;
    private const int OcrMaxSize = 4096;

    /// <summary>
    /// Finds faces with Windows' built-in detector. Boxes are in upright pixels of the full
    /// image and padded by 15 % so hair and chin are covered too.
    /// </summary>
    public static Task<IReadOnlyList<PixelRect>> DetectFacesAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            using var image = ImageLoader.Load(path);
            var (bitmap, scale) = Downscaled(image, FaceDetectionMaxSize);
            var faces = await DetectFacesAsync(bitmap, cancellationToken);
            return (IReadOnlyList<PixelRect>)faces.Select(r => Scale(r, scale).ClampTo((int)image.Width, (int)image.Height)).ToList();
        }, cancellationToken);

    /// <summary>Finds faces in a buffer the UI already holds; boxes are in that buffer's pixels.</summary>
    public static async Task<IReadOnlyList<PixelRect>> DetectFacesAsync(BgraBitmap bitmap, CancellationToken cancellationToken = default)
    {
        if (!FaceDetector.IsSupported)
            return [];
        using var bgra = ToSoftwareBitmap(bitmap);
        var format = FaceDetector.GetSupportedBitmapPixelFormats().Contains(BitmapPixelFormat.Gray8) ? BitmapPixelFormat.Gray8 : BitmapPixelFormat.Nv12;
        using var input = SoftwareBitmap.Convert(bgra, format);
        var detector = await FaceDetector.CreateAsync().AsTask(cancellationToken);
        var faces = await detector.DetectFacesAsync(input).AsTask(cancellationToken);
        return faces
            .Select(f => new PixelRect((int)f.FaceBox.X, (int)f.FaceBox.Y, (int)f.FaceBox.Width, (int)f.FaceBox.Height))
            .Select(r => r.Inflate((int)Math.Round(r.Width * 0.15), (int)Math.Round(r.Height * 0.15)).ClampTo(bitmap.Width, bitmap.Height))
            .Where(r => !r.IsEmpty)
            .ToList();
    }

    /// <summary>Finds text with Windows OCR in every installed recogniser language; boxes are in upright pixels of the full image.</summary>
    public static Task<IReadOnlyList<DetectedText>> DetectTextAsync(string path, TextGranularity granularity = TextGranularity.Lines,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            using var image = ImageLoader.Load(path);
            var (bitmap, scale) = Downscaled(image, Math.Min(OcrMaxSize, (int)OcrEngine.MaxImageDimension));
            var found = await DetectTextAsync(bitmap, granularity, cancellationToken);
            return (IReadOnlyList<DetectedText>)found
                .Select(t => t with { Bounds = Scale(t.Bounds, scale).ClampTo((int)image.Width, (int)image.Height) })
                .ToList();
        }, cancellationToken);

    /// <summary>
    /// All the text in a picture, line by line in reading order, for copying. Every installed OCR
    /// language reads it and the cleanest result wins, so an Arabic photo is read correctly on an
    /// English system and the other way round.
    /// </summary>
    public static Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            using var image = ImageLoader.Load(path);
            var (bitmap, _) = Downscaled(image, Math.Min(OcrMaxSize, (int)OcrEngine.MaxImageDimension));
            var engines = OcrEngines();
            if (engines.Count == 0)
                throw new InvalidOperationException("No Windows OCR language is installed. Add one under Settings › Time & language › Language.");
            using var input = ToSoftwareBitmap(bitmap);
            string best = "";
            double bestScore = -1;
            foreach (var engine in engines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ocr = await engine.RecognizeAsync(input).AsTask(cancellationToken);
                var text = string.Join(Environment.NewLine, ocr.Lines.Select(l => l.Text));
                // A script read with the wrong language comes out as fragments and punctuation.
                int letters = text.Count(char.IsLetterOrDigit);
                int visible = Math.Max(1, text.Count(c => !char.IsWhiteSpace(c)));
                double quality = (double)letters / visible;
                double score = letters * quality * quality;
                if (score > bestScore)
                {
                    best = text;
                    bestScore = score;
                }
            }
            return best.Trim();
        }, cancellationToken);

    /// <summary>Text detection on a buffer the UI already holds; boxes are in that buffer's pixels.</summary>
    public static async Task<IReadOnlyList<DetectedText>> DetectTextAsync(BgraBitmap bitmap, TextGranularity granularity = TextGranularity.Lines,
        CancellationToken cancellationToken = default)
    {
        var engines = OcrEngines();
        if (engines.Count == 0)
            return [];
        using var input = ToSoftwareBitmap(bitmap);
        var results = new List<DetectedText>();
        foreach (var engine in engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ocr = await engine.RecognizeAsync(input).AsTask(cancellationToken);
            foreach (var line in ocr.Lines)
            {
                var words = line.Words.Select(w => (Rect: ToPixels(w.BoundingRect), w.Text)).Where(w => !w.Rect.IsEmpty).ToList();
                if (words.Count == 0)
                    continue;
                var candidates = granularity == TextGranularity.Words
                    ? words.Select(w => new DetectedText(w.Rect, w.Text, false))
                    : [new DetectedText(words.Aggregate(default(PixelRect), (acc, w) => acc.Union(w.Rect)), line.Text, true)];
                // Several languages often find the same text; keep the first hit.
                foreach (var c in candidates)
                    if (!results.Any(r => Overlap(r.Bounds, c.Bounds) > 0.5))
                        results.Add(c);
            }
        }
        return results.OrderBy(r => r.Bounds.Y).ThenBy(r => r.Bounds.X).ToList();
    }

    /// <summary>Covers every region and saves in the output path's format (default: the source's).</summary>
    public static Task RedactAsync(string inputPath, string outputPath, IReadOnlyList<RedactionRegion> regions,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var format = ImageFormats.Detect(inputPath);
            using var image = ImageLoader.Load(inputPath, format);
            progress?.Report(0.3);
            foreach (var region in regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Apply(image, region);
            }
            // Thumbnails embedded in EXIF/Photoshop/XMP data would still show what was covered.
            MetadataHygiene.AfterGeometryChange(image);
            RemoveEmbeddedPreviews(image);
            progress?.Report(0.6);
            var target = ToolSupport.OutputFormat(outputPath, format);
            await ImageWriter.WriteAsync(image, outputPath, target, ToolSupport.WriteOptions(image, format, target), cancellationToken);
            progress?.Report(1);
        }, cancellationToken);

    internal static void Apply(MagickImage image, RedactionRegion region)
    {
        var r = region.Rect.ClampTo((int)image.Width, (int)image.Height);
        if (r.IsEmpty)
            return;
        if (region.Style == RedactionStyle.Solid)
            FillSolid(image, r, region.Color);
        else
            Obscure(image, r);
    }

    private static void FillSolid(MagickImage image, PixelRect r, uint argb)
    {
        int channels = (int)image.ChannelCount;
        var data = new byte[r.Width * r.Height * channels];
        byte red = (byte)(argb >> 16), green = (byte)(argb >> 8), blue = (byte)argb;
        bool gray = image.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray;
        for (int i = 0; i < data.Length; i += channels)
        {
            if (gray)
            {
                data[i] = (byte)Math.Round(0.299 * red + 0.587 * green + 0.114 * blue);
                if (channels > 1) data[i + 1] = 255;
                continue;
            }
            data[i] = red;
            data[i + 1] = green;
            data[i + 2] = blue;
            for (int c = 3; c < channels; c++)
                data[i + c] = 255;
        }
        using var pixels = image.GetPixels();
        pixels.SetArea(r.X, r.Y, (uint)r.Width, (uint)r.Height, data);
    }

    /// <summary>
    /// Averages the area down to one sample per block (about six across the short side), then
    /// scales that back up with a soft filter: a smooth blur built from far too little
    /// information to recover faces or text. (Much faster than a large Gaussian blur.)
    /// </summary>
    private static void Obscure(MagickImage image, PixelRect r)
    {
        int block = Math.Max(8, Math.Min(r.Width, r.Height) / 6);
        using var area = (MagickImage)image.CloneArea(r.X, r.Y, (uint)r.Width, (uint)r.Height);
        area.ResetPage();
        area.Scale(new MagickGeometry((uint)Math.Max(1, r.Width / block), (uint)Math.Max(1, r.Height / block)) { IgnoreAspectRatio = true });
        area.VirtualPixelMethod = VirtualPixelMethod.Edge;
        area.FilterType = FilterType.Gaussian;
        area.Resize(new MagickGeometry((uint)r.Width, (uint)r.Height) { IgnoreAspectRatio = true });
        image.Composite(area, r.X, r.Y, CompositeOperator.Copy);
    }

    private static void RemoveEmbeddedPreviews(MagickImage image)
    {
        // Photoshop resources can carry a thumbnail; IPTC itself lives on in the "iptc" profile.
        if (image.GetProfile("8bim") is not null)
        {
            var iptc = image.GetIptcProfile();
            image.RemoveProfile("8bim");
            if (iptc is not null)
                image.SetProfile(iptc);
        }
        var xmp = image.GetXmpProfile();
        if (xmp is not null && System.Text.Encoding.UTF8.GetString(xmp.ToByteArray()).Contains("xmpGImg:image", StringComparison.Ordinal))
            image.RemoveProfile("xmp");
    }

    private static List<OcrEngine> OcrEngines()
    {
        var engines = new List<OcrEngine>();
        if (OcrEngine.TryCreateFromUserProfileLanguages() is { } preferred)
            engines.Add(preferred);
        foreach (var language in OcrEngine.AvailableRecognizerLanguages.Take(4))
        {
            if (engines.Any(e => e.RecognizerLanguage.LanguageTag == language.LanguageTag))
                continue;
            if (OcrEngine.TryCreateFromLanguage(new Language(language.LanguageTag)) is { } engine)
                engines.Add(engine);
        }
        return engines;
    }

    private static (BgraBitmap Bitmap, double Scale) Downscaled(MagickImage image, int maxSize)
    {
        int longEdge = (int)Math.Max(image.Width, image.Height);
        if (longEdge <= maxSize)
            return (ImagePreview.ToBgra(image), 1);
        using var small = (MagickImage)image.Clone();
        small.Resize(new MagickGeometry((uint)maxSize, (uint)maxSize));
        return (ImagePreview.ToBgra(small), (double)longEdge / Math.Max(small.Width, small.Height));
    }

    private static SoftwareBitmap ToSoftwareBitmap(BgraBitmap bitmap) =>
        SoftwareBitmap.CreateCopyFromBuffer(bitmap.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Premultiplied);

    private static PixelRect ToPixels(Windows.Foundation.Rect rect)
    {
        int x0 = (int)Math.Floor(rect.X), y0 = (int)Math.Floor(rect.Y);
        int x1 = (int)Math.Ceiling(rect.X + rect.Width), y1 = (int)Math.Ceiling(rect.Y + rect.Height);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    private static PixelRect Scale(PixelRect r, double s) => s == 1 ? r : new PixelRect(
        (int)Math.Floor(r.X * s), (int)Math.Floor(r.Y * s), (int)Math.Ceiling(r.Width * s), (int)Math.Ceiling(r.Height * s));

    private static double Overlap(PixelRect a, PixelRect b)
    {
        int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y), x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
        if (x1 <= x0 || y1 <= y0)
            return 0;
        double inter = (double)(x1 - x0) * (y1 - y0);
        return inter / Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
    }
}
