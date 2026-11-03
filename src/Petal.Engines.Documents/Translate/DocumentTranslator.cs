using DocumentFormat.OpenXml.Packaging;
using Petal.Core.Formats;
using Petal.Core.Text;
using Petal.Engines.Documents.Converters;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Util;
using UglyToad.PdfPig;
using F = Petal.Core.Formats.Formats;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using TextTranslator = System.Func<System.Collections.Generic.IReadOnlyList<string>, System.IProgress<double>?, System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>>>;

namespace Petal.Engines.Documents.Translate;

/// <summary>
/// A Word, PowerPoint, Excel or PDF file in another language, as a new file with its formatting kept. How the words
/// themselves are translated is up to the delegate this is given; nothing here knows of any model. ODT and RTF go through
/// the document model into a DOCX first, so they come out as DOCX, with what that model carries.
/// </summary>
public static class DocumentTranslator
{
    /// <summary>The kinds of file this can translate.</summary>
    public static bool CanTranslate(FileFormat format) =>
        format == F.Docx || format == F.Odt || format == F.Rtf || format == F.Pptx || format == F.Xlsx || format == F.Pdf;

    /// <summary>The languages written from right to left.</summary>
    public static bool IsRightToLeft(string language) => language is "ar" or "fa" or "ur";

    /// <summary>The extension the translation of a file gets: its own, but DOCX for what is read through the document model.</summary>
    public static string OutputExtension(string input)
    {
        var format = F.FromPath(input);
        return format == F.Odt || format == F.Rtf ? "docx" : Path.GetExtension(input).TrimStart('.');
    }

    /// <summary>The first words of a file, enough to tell its language; empty when none can be read (a scan, say).</summary>
    public static string FirstText(string path, int most = 4000)
    {
        try
        {
            var format = F.FromPath(path);
            IEnumerable<string> words =
                format == F.Docx ? WordWords(path) :
                format == F.Pptx ? SlideWords(path) :
                format == F.Xlsx ? SheetWords(path) :
                format == F.Pdf ? PdfWords(path) :
                format == F.Odt || format == F.Rtf ? [PlainTextIO.Render(DocumentIO.Read(path, format))] : [];
            var text = new System.Text.StringBuilder();
            foreach (var word in words)
            {
                text.Append(word).Append(' ');
                if (text.Length >= most)
                    break;
            }
            return text.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A damaged or protected file: the job says so when it is run.
            return "";
        }
    }

    private static List<string> WordWords(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return document.MainDocumentPart?.Document?.Body?.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Take(2000).Select(t => t.Text).ToList() ?? [];
    }

    private static List<string> SlideWords(string path)
    {
        using var document = PresentationDocument.Open(path, false);
        return document.PresentationPart?.SlideParts.SelectMany(s => s.Slide?.Descendants<DocumentFormat.OpenXml.Drawing.Text>() ?? []).Take(2000).Select(t => t.Text).ToList() ?? [];
    }

    private static List<string> SheetWords(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return document.WorkbookPart?.SharedStringTablePart?.SharedStringTable?.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>().Take(2000).Select(s => s.InnerText).ToList() ?? [];
    }

    private static List<string> PdfWords(string path)
    {
        using var pdf = PigDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var words = new List<string>();
        for (int n = 1; n <= Math.Min(pdf.NumberOfPages, 5) && words.Count < 1000; n++)
            words.AddRange(pdf.GetPage(n).GetWords().Select(w => w.Text));
        return words;
    }

    /// <summary>
    /// Writes the translation of <paramref name="input"/> to <paramref name="output"/>, which only appears once it is whole.
    /// </summary>
    /// <param name="from">The language the file is in, or "auto" to tell it from the file's own words.</param>
    /// <param name="to">The language wanted.</param>
    /// <param name="translate">Each line in the other language, in order. A line may hold line breaks and tabs; they are to be kept.</param>
    public static async Task TranslateAsync(string input, string output, string from, string to,
        Func<IReadOnlyList<string>, IProgress<double>?, CancellationToken, Task<IReadOnlyList<string>>> translate,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        SafeOutput.EnsureNotInput(input, output);
        var format = F.FromPath(input);
        if (format is null || !CanTranslate(format))
            throw new NotSupportedException("This kind of file can't be translated.");
        await SafeOutput.WriteAsync(output, async temp =>
        {
            if (format == F.Pdf)
            {
                await PdfTranslation.RunAsync(input, temp, from, to, translate, progress, cancellationToken).ConfigureAwait(false);
                return;
            }
            using var scratch = SafeOutput.CreateScratch();
            var source = input;
            var kind = format;
            if (format == F.Odt || format == F.Rtf)
            {
                source = scratch.File("input.docx");
                DocxWriter.Write(DocumentIO.Read(input, format), source);
                kind = F.Docx;
            }
            File.Copy(source, temp, overwrite: true);
            // A read-only original gives a read-only copy.
            File.SetAttributes(temp, FileAttributes.Normal);
            if (kind == F.Docx)
                await WordTranslation.RunAsync(temp, from, to, translate, progress, cancellationToken).ConfigureAwait(false);
            else if (kind == F.Pptx)
                await SlidesTranslation.RunAsync(temp, from, to, translate, progress, cancellationToken).ConfigureAwait(false);
            else
                await SheetsTranslation.RunAsync(temp, from, to, translate, progress, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        progress?.Report(1);
    }

    /// <summary>The language a file is in: as chosen, or told from the words about to be translated.</summary>
    internal static string Known(string from, IReadOnlyList<TextSlot> slots)
    {
        if (from is not ("auto" or ""))
            return from;
        var sample = string.Join(' ', slots.Where(s => s.HasWords).Take(200).Select(s => s.Words));
        return LanguageGuess.Of(sample) ?? "en";
    }

    /// <summary>Has every piece of text with letters in it translated, and each put back in its place.</summary>
    internal static async Task FillAsync(IReadOnlyList<TextSlot> slots, TextTranslator translate, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var wanted = slots.Where(s => s.HasWords).ToList();
        if (wanted.Count == 0)
            throw new InvalidOperationException("This file has no text to translate.");
        var translated = await translate(wanted.Select(s => s.Words).ToList(), progress, cancellationToken).ConfigureAwait(false);
        if (translated.Count != wanted.Count)
            throw new InvalidOperationException("The translation came back incomplete.");
        cancellationToken.ThrowIfCancellationRequested();
        for (int i = 0; i < wanted.Count; i++)
            wanted[i].Put(translated[i]);
    }

    /// <summary>The tag a language goes by in Office files, for spelling and for its fonts.</summary>
    internal static string Tag(string language) => language switch
    {
        "ar" => "ar-SA",
        "tr" => "tr-TR",
        "fa" => "fa-IR",
        "ur" => "ur-PK",
        "id" => "id-ID",
        "fr" => "fr-FR",
        "es" => "es-ES",
        _ => "en-US",
    };
}

/// <summary>
/// One piece of a file's text and where its translation goes. The white space around it is kept apart, so words on
/// either side of a link stay parted from it.
/// </summary>
internal sealed class TextSlot
{
    private readonly Action<string> _put;
    private readonly string _before;
    private readonly string _after;

    public TextSlot(string text, Action<string> put)
    {
        _put = put;
        Words = text.Trim();
        int start = text.Length - text.TrimStart().Length;
        _before = text[..start];
        _after = Words.Length == 0 ? "" : text[(start + Words.Length)..];
    }

    public string Words { get; }

    /// <summary>Numbers and marks alone stay as they are.</summary>
    public bool HasWords => Words.Any(char.IsLetter);

    /// <summary>A translation that came back empty leaves the original words in place.</summary>
    public void Put(string translated)
    {
        var words = translated.Trim();
        if (words.Length > 0)
            _put(_before + words + _after);
    }
}
