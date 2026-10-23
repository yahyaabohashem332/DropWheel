using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Text;
using Petal.Engines.Imaging;

namespace Petal.Recipes;

/// <summary>
/// A name from what a file holds, for Rename's {content}: a document's title (or, with the AI model, a
/// short name it suggests), an invoice's sender, a screenshot's first words, what a photo shows (with the
/// picture model), all read on this PC. Null when nothing useful is found; the old name stays then.
/// </summary>
public static partial class SmartNamer
{
    /// <summary>What a photo can show, in English and Arabic; the picture model picks the closest.</summary>
    public static readonly (string English, string Arabic)[] Subjects =
    [
        ("Beach", "شاطئ"), ("Mountains", "جبال"), ("Forest", "غابة"), ("City", "مدينة"), ("Street", "شارع"), ("Building", "مبنى"),
        ("Food", "طعام"), ("Coffee", "قهوة"), ("Cat", "قطة"), ("Dog", "كلب"), ("Bird", "طائر"), ("Car", "سيارة"),
        ("Flowers", "زهور"), ("Garden", "حديقة"), ("Sunset", "غروب"), ("Night sky", "سماء الليل"), ("Snow", "ثلج"), ("Desert", "صحراء"),
        ("Lake", "بحيرة"), ("Sea", "بحر"), ("Park", "منتزه"), ("Group photo", "صورة جماعية"), ("Portrait", "صورة شخصية"), ("Baby", "طفل"),
        ("Wedding", "زفاف"), ("Birthday", "عيد ميلاد"), ("Party", "حفلة"), ("Concert", "حفل موسيقي"), ("Football", "كرة قدم"), ("Mosque", "مسجد"),
        ("Museum", "متحف"), ("Document", "مستند"), ("Receipt", "إيصال"), ("Whiteboard", "سبورة"), ("Computer", "حاسوب"), ("Phone", "هاتف"),
        ("Book", "كتاب"), ("Airplane", "طائرة"), ("Train", "قطار"), ("Boat", "قارب"), ("Horse", "حصان"), ("Camel", "جمل"),
        ("Kitchen", "مطبخ"), ("Living room", "غرفة معيشة"), ("Office", "مكتب"), ("Classroom", "فصل دراسي"), ("Shop", "متجر"), ("Fruit", "فاكهة"),
    ];

    private static float[][]? _subjectMeaning;

    public static async Task<string?> NameAsync(string path, ConverterRegistry converters, bool arabic, bool useModel, CancellationToken cancellationToken = default)
    {
        var format = Formats.FromPath(path);
        if (format is null)
            return null;
        try
        {
            if (format.Family is FormatFamily.Image)
                return await PictureNameAsync(path, arabic, cancellationToken);
            if (DocumentText.CanRead(format, converters))
            {
                var text = await DocumentText.ReadAsync(path, converters, new ConversionSettings(), null, cancellationToken);
                return await DocumentNameAsync(text, arabic, useModel, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
        return null;
    }

    private static async Task<string?> PictureNameAsync(string path, bool arabic, CancellationToken cancellationToken)
    {
        var file = Path.GetFileName(path);
        bool screenshot = file.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase) || file.StartsWith("لقطة", StringComparison.Ordinal)
            || Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);
        if (screenshot)
        {
            var text = await ImageRedactor.ReadTextAsync(path, cancellationToken);
            if (FirstWords(text) is { } words)
                return (arabic ? "لقطة " : "Screenshot ") + words;
        }
        if (!Meaning.IsReady)
            return null;
        var picture = await Task.Run(() => Meaning.EmbedImage(path), cancellationToken);
        _subjectMeaning ??= Subjects.Select(s => Meaning.EmbedText("a photo of " + s.English.ToLowerInvariant())).ToArray();
        int best = -1;
        float bestScore = float.MinValue;
        for (int i = 0; i < _subjectMeaning.Length; i++)
        {
            float score = Meaning.Similarity(picture, _subjectMeaning[i]);
            if (score > bestScore)
                (best, bestScore) = (i, score);
        }
        return best < 0 || bestScore < 0.18f ? null : arabic ? Subjects[best].Arabic : Subjects[best].English;
    }

    /// <summary>A document's name: an invoice by its sender and date, else what the model suggests, else its first real line.</summary>
    public static async Task<string?> DocumentNameAsync(string text, bool arabic, bool useModel, CancellationToken cancellationToken = default)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        if (Invoice(text, arabic) is { } invoice)
            return invoice;
        if (useModel && AskPetal.ModelReady)
        {
            var head = text.Length > 2000 ? text[..2000] : text;
            var answer = await AskPetal.WriteAsync(
                "Suggest a short, clear file name of 2 to 6 words for this document, in the document's own language. Reply with the name only: no extension, no quotes, no date.",
                head, 30, cancellationToken);
            if (Tidy(answer) is { Length: >= 3 } suggested)
                return suggested;
        }
        return FirstWords(text);
    }

    /// <summary>"Invoice Acme 2026-05-03" for an invoice or a receipt.</summary>
    internal static string? Invoice(string text, bool arabic)
    {
        var head = text.Length > 1500 ? text[..1500] : text;
        if (!InvoiceWord().IsMatch(head))
            return null;
        var from = head.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length >= 3 && l.Count(char.IsLetter) >= 3 && !InvoiceWord().IsMatch(l));
        var date = DatePattern().Match(head) is { Success: true } m ? m.Value.Replace('/', '-').Replace('.', '-') : null;
        var parts = new List<string> { arabic ? "فاتورة" : "Invoice" };
        if (from is not null)
            parts.Add(Tidy(string.Join(' ', from.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4))) ?? "");
        if (date is not null)
            parts.Add(date);
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    /// <summary>The first line that reads like words, cut to six of them.</summary>
    internal static string? FirstWords(string text)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            int letters = line.Count(char.IsLetter);
            if (letters < 4 || letters < line.Length * 0.5)
                continue;
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6);
            return Tidy(string.Join(' ', words));
        }
        return null;
    }

    /// <summary>Safe for a file name: no characters Windows refuses, no quotes, at most 60 characters.</summary>
    public static string? Tidy(string? name)
    {
        if (name is null)
            return null;
        var clean = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c is not '"' and not '“' and not '”' and not '«' and not '»').ToArray());
        clean = string.Join(' ', clean.Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.', '-', '_');
        if (clean.Length > 60)
            clean = clean[..60].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    [GeneratedRegex(@"\b(invoice|receipt|bill|tax invoice)\b|فاتورة|إيصال|ايصال", RegexOptions.IgnoreCase)]
    private static partial Regex InvoiceWord();

    [GeneratedRegex(@"\b(20\d\d[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/.]\d{1,2}[-/.]20\d\d)\b")]
    private static partial Regex DatePattern();
}
