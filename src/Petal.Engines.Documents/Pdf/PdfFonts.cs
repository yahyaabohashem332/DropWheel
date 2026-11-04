using System.Collections.Concurrent;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using Petal.Engines.Documents.Text;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// Font set-up for PDFsharp/MigraDoc: Windows fonts, per-script fallbacks (PDFsharp has no glyph
/// fallback of its own), and measured line breaking for right-to-left paragraphs.
/// </summary>
internal static class PdfFonts
{
    public const string Body = "Calibri";
    public const string Mono = "Consolas";
    public const string Arabic = "Arial";

    private static readonly Lock Gate = new();
    private static bool _initialized;
    private static readonly ConcurrentDictionary<string, bool> Availability = new(StringComparer.OrdinalIgnoreCase);
    private static XGraphics? _measure;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            _initialized = true;
            try
            {
                if (GlobalFontSettings.FontResolver is null)
                    GlobalFontSettings.FontResolver = Resolver = new WindowsFontResolver();
            }
            catch (InvalidOperationException)
            {
                // Another component configured fonts first; theirs is used.
            }
        }
    }

    private static WindowsFontResolver? Resolver;

    public static bool IsAvailable(string family) => Availability.GetOrAdd(family, f =>
    {
        EnsureInitialized();
        if (Resolver is not null)
            return Resolver.HasFamily(f);
        try
        {
            lock (Gate)
            {
                var font = new XFont(f, 10);
                return string.Equals(font.FontFamily.Name, f, StringComparison.OrdinalIgnoreCase) || font.FontFamily.Name.Length > 0;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    });

    /// <summary>The font for a piece of text that the body font cannot show, or null for the body font.</summary>
    public static string? FallbackFor(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            string? candidate = v switch
            {
                >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF => Arabic,
                >= 0x0E00 and <= 0x0E7F => "Tahoma",
                >= 0x0900 and <= 0x0DFF => "Nirmala UI",
                >= 0x3040 and <= 0x30FF => FirstAvailable("Yu Gothic", "MS Gothic", "Microsoft YaHei"),
                >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF => FirstAvailable("Malgun Gothic", "Microsoft YaHei"),
                >= 0x2E80 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF => FirstAvailable("Microsoft YaHei", "SimSun", "Yu Gothic"),
                >= 0x2190 and <= 0x2BFF => "Segoe UI Symbol",
                >= 0x1F000 => "Segoe UI Emoji",
                _ => null,
            };
            if (candidate is not null)
                return IsAvailable(candidate) ? candidate : null;
        }
        return null;
    }

    /// <summary>A font this PC has that looks like one a PDF names: Arial, which has every Arabic letter, for Arabic script or for what isn't known.</summary>
    public static string FamilyLike(string? fontName, bool arabicScript)
    {
        if (arabicScript)
            return Arabic;
        var name = (fontName ?? "").Replace(" ", "");
        var known = new (string Part, string Family)[]
        {
            ("Calibri", "Calibri"), ("Cambria", "Cambria"), ("Georgia", "Georgia"), ("Garamond", "Garamond"), ("Verdana", "Verdana"), ("Tahoma", "Tahoma"),
            ("Segoe", "Segoe UI"), ("Times", "Times New Roman"), ("Courier", "Courier New"), ("Consolas", "Consolas"), ("Arial", "Arial"), ("Helvetica", "Arial"),
        };
        foreach (var (part, family) in known)
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase) && IsAvailable(family))
                return family;
        return "Arial";
    }

    /// <summary>The marks that only steer joining and direction have nothing to draw, and would show as boxes.</summary>
    public static string Drawable(string line) =>
        new(line.Where(c => c is not ((char)0x200C or (char)0x200D or (char)0x200E or (char)0x200F)).ToArray());

    private static string FirstAvailable(params string[] names) => names.FirstOrDefault(IsAvailable) ?? names[0];

    /// <summary>Splits text into runs that each need one font (null = the body font).</summary>
    public static IEnumerable<(string Text, string? Font)> SplitByFont(string text)
    {
        if (text.All(c => c < 0x0590))
        {
            yield return (text, null);
            yield break;
        }
        var sb = new StringBuilder();
        string? current = null;
        var first = true;
        foreach (var rune in text.EnumerateRunes())
        {
            var s = rune.ToString();
            // Spaces and punctuation stay with whatever run they are in.
            var font = Rune.IsLetterOrDigit(rune) || rune.Value > 0x2000 ? FallbackFor(s) : current;
            if (!first && font != current && sb.Length > 0)
            {
                yield return (sb.ToString(), current);
                sb.Clear();
            }
            current = font;
            first = false;
            sb.Append(s);
        }
        if (sb.Length > 0)
            yield return (sb.ToString(), current);
    }

    public static double Measure(string text, string family, double size, bool bold, bool italic)
    {
        EnsureInitialized();
        lock (Gate)
        {
            _measure ??= XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
            var style = (bold ? XFontStyleEx.Bold : 0) | (italic ? XFontStyleEx.Italic : 0);
            var font = new XFont(family, size, style);
            return _measure.MeasureString(text, font).Width;
        }
    }

    /// <summary>
    /// Breaks right-to-left text into lines no wider than <paramref name="width"/> points and returns
    /// each line shaped and in visual order, ready to be drawn left to right. The paragraph's direction is that of its
    /// first strong letter unless <paramref name="rightToLeft"/> says otherwise.
    /// </summary>
    public static List<string> BreakRtl(string text, string family, double size, bool bold, bool italic, double width, bool? rightToLeft = null)
    {
        var lines = new List<string>();
        var rtl = rightToLeft ?? Bidi.IsRtlParagraph(text);
        foreach (var hardLine in TextFile.NormalizeNewlines(text).Split('\n'))
        {
            var words = hardLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = "";
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                // A small safety margin: MigraDoc measures with the same fonts but rounds differently.
                if (current.Length > 0 && Measure(Bidi.ToVisual(candidate, rtl), family, size, bold, italic) > width * 0.97)
                {
                    lines.Add(Bidi.ToVisual(current, rtl));
                    current = word;
                }
                else
                    current = candidate;
            }
            lines.Add(Bidi.ToVisual(current, rtl));
        }
        return lines;
    }
}
