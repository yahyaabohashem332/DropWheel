using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Petal.Engines.Documents.Ocr;

/// <summary>A recognised word in pixel coordinates of the bitmap it was read from.</summary>
internal readonly record struct OcrWordBox(string Text, double X, double Y, double Width, double Height, int Line);

/// <summary>A word read from a picture: its text, its box in the picture's pixels, and the line it is on (lines counted as Windows read them).</summary>
public sealed record PictureWord(string Text, double X, double Y, double Width, double Height, int Line)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CentreX => X + Width / 2;

    public double CentreY => Y + Height / 2;
}

/// <summary>Local text recognition with the OCR engine built into Windows. Nothing leaves the machine.</summary>
internal static class OcrService
{
    /// <summary>True when at least one OCR language pack is installed.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    public static int MaxImageDimension
    {
        get
        {
            try { return (int)OcrEngine.MaxImageDimension; }
            catch (Exception ex) when (ex is COMException or TypeLoadException) { return 2600; }
        }
    }

    public static IReadOnlyList<string> InstalledLanguages =>
        IsAvailable ? OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList() : [];

    /// <summary>
    /// Recognises the bitmap. With several candidate languages each is tried and the result with
    /// the most recognised letters wins, so an Arabic scan is read even on an English system.
    /// </summary>
    public static async Task<IReadOnlyList<OcrWordBox>> RecognizeAsync(SKBitmap bitmap, IReadOnlyList<string>? languages, CancellationToken cancellationToken)
    {
        using var soft = ToSoftwareBitmap(bitmap);
        var engines = CreateEngines(languages);
        IReadOnlyList<OcrWordBox> best = [];
        var bestScore = -1.0;
        foreach (var engine in engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(soft).AsTask(cancellationToken).ConfigureAwait(false);
            var words = new List<OcrWordBox>();
            var lineIndex = 0;
            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    words.Add(new OcrWordBox(word.Text, r.X, r.Y, r.Width, r.Height, lineIndex));
                }
                lineIndex++;
            }
            // Reading a script with the wrong language yields fragments full of punctuation,
            // so weigh the letter count by how clean the text is.
            var letters = words.Sum(w => w.Text.Count(char.IsLetterOrDigit));
            var total = Math.Max(1, words.Sum(w => w.Text.Count(c => !char.IsWhiteSpace(c))));
            var quality = (double)letters / total;
            var score = letters * quality * quality;
            if (score > bestScore)
            {
                best = words;
                bestScore = score;
            }
            // Clean and substantial: no need to try the other languages.
            if (letters > 40 && quality > 0.9)
                break;
        }
        return best;
    }

    /// <summary>
    /// Every installed language reads the bitmap, and each line is kept from the language that read it best: on a screen
    /// with Arabic and English (and Turkish) side by side, every line comes out in its own script. A line is scored by
    /// its clean letters, a little more when the reader's script is the line's, and a little more for the user's own
    /// language when two readings tie.
    /// </summary>
    public static async Task<IReadOnlyList<OcrWordBox>> RecognizeEveryLanguageAsync(SKBitmap bitmap, CancellationToken cancellationToken)
    {
        using var soft = ToSoftwareBitmap(bitmap);
        var engines = CreateEngines(null);
        var candidates = new List<(List<OcrWordBox> Words, double Left, double Top, double Right, double Bottom, double Score)>();
        int lineIndex = 0;
        for (int e = 0; e < engines.Count; e++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var engine = engines[e];
            bool arabicReader = engine.RecognizerLanguage.LanguageTag.Split('-')[0] is "ar" or "fa" or "ur" or "ps";
            var result = await engine.RecognizeAsync(soft).AsTask(cancellationToken).ConfigureAwait(false);
            foreach (var line in result.Lines)
            {
                var words = line.Words.Select(w => new OcrWordBox(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height, lineIndex)).ToList();
                lineIndex++;
                if (words.Count == 0)
                    continue;
                var text = string.Concat(words.Select(w => w.Text));
                int letters = text.Count(char.IsLetterOrDigit);
                double quality = (double)letters / Math.Max(1, text.Length);
                int arabic = ReadingOrder.Arabic(text), latin = ReadingOrder.Latin(text);
                double score = letters * quality * quality;
                if (arabicReader == arabic > latin)
                    score *= 1.15;
                if (e == 0)
                    score *= 1.05;
                candidates.Add((words, words.Min(w => w.X), words.Min(w => w.Y), words.Max(w => w.X + w.Width), words.Max(w => w.Y + w.Height), score));
            }
        }
        // The best readings first; a line that mostly covers one already kept is the same line read worse.
        var kept = new List<(List<OcrWordBox> Words, double Left, double Top, double Right, double Bottom, double Score)>();
        foreach (var c in candidates.OrderByDescending(c => c.Score))
        {
            double area = (c.Right - c.Left) * (c.Bottom - c.Top);
            bool same = kept.Any(k =>
            {
                double w = Math.Min(k.Right, c.Right) - Math.Max(k.Left, c.Left), h = Math.Min(k.Bottom, c.Bottom) - Math.Max(k.Top, c.Top);
                return w > 0 && h > 0 && w * h > 0.5 * Math.Min(area, (k.Right - k.Left) * (k.Bottom - k.Top));
            });
            if (!same)
                kept.Add(c);
        }
        return kept.SelectMany(k => k.Words).ToList();
    }

    private static List<OcrEngine> CreateEngines(IReadOnlyList<string>? languages)
    {
        var engines = new List<OcrEngine>();
        if (languages is { Count: > 0 })
        {
            foreach (var tag in languages)
            {
                try
                {
                    var lang = new Language(tag);
                    if (OcrEngine.IsLanguageSupported(lang) && OcrEngine.TryCreateFromLanguage(lang) is { } e)
                        engines.Add(e);
                }
                catch (ArgumentException)
                {
                    // Unknown tag.
                }
            }
        }
        if (engines.Count == 0)
        {
            if (OcrEngine.TryCreateFromUserProfileLanguages() is { } profile)
                engines.Add(profile);
            // Then every other installed language, in case the document is in another script.
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
            {
                if (engines.Any(e => e.RecognizerLanguage.LanguageTag == lang.LanguageTag))
                    continue;
                if (OcrEngine.TryCreateFromLanguage(lang) is { } e)
                    engines.Add(e);
            }
        }
        if (engines.Count == 0)
            throw new InvalidOperationException("No Windows OCR language is installed. Add one under Settings › Time & language › Language.");
        return engines;
    }

    private static SoftwareBitmap ToSoftwareBitmap(SKBitmap bitmap)
    {
        // Grey is all the recogniser needs and a quarter of the memory.
        var gray = new byte[bitmap.Width * bitmap.Height];
        using var bgra = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        var src = (bgra ?? bitmap).Bytes;
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
            gray[i] = (byte)((src[p] * 29 + src[p + 1] * 150 + src[p + 2] * 77) >> 8);
        var soft = new SoftwareBitmap(BitmapPixelFormat.Gray8, bitmap.Width, bitmap.Height);
        soft.CopyFromBuffer(gray.AsBuffer());
        return soft;
    }
}
