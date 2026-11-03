using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;
using TextTranslator = System.Func<System.Collections.Generic.IReadOnlyList<string>, System.IProgress<double>?, System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>>>;

namespace Petal.Engines.Documents.Translate;

/// <summary>
/// A Word document in another language, changed in place on its copy. A paragraph is translated as a whole: the
/// translation goes in its first text run and the other runs give up their words, so the paragraph's style and the first
/// run's formatting stay. A link is translated on its own, so it keeps working and keeps its look, and so is what a field
/// shows only when it is a link or a table of contents; a page number and the like are left alone, as are field
/// instructions, pictures, tables and page setup.
/// </summary>
internal static class WordTranslation
{
    public static async Task RunAsync(string path, string from, string to, TextTranslator translate, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("The document has no main part.");
        var roots = Roots(main).ToList();
        var slots = new List<TextSlot>();
        foreach (var root in roots)
            new Reader(slots, to).Read(root);
        await DocumentTranslator.FillAsync(slots, translate, progress, cancellationToken).ConfigureAwait(false);
        SetDirection(main, roots, DocumentTranslator.Known(from, slots), to);
        document.Save();
    }

    /// <summary>Everywhere a document keeps its words: the body, headers, footers, footnotes and endnotes (text boxes are inside them).</summary>
    private static IEnumerable<OpenXmlPartRootElement> Roots(MainDocumentPart main)
    {
        if (main.Document is { } body)
            yield return body;
        foreach (var header in main.HeaderParts)
            if (header.Header is { } root)
                yield return root;
        foreach (var footer in main.FooterParts)
            if (footer.Footer is { } root)
                yield return root;
        if (main.FootnotesPart?.Footnotes is { } footnotes)
            yield return footnotes;
        if (main.EndnotesPart?.Endnotes is { } endnotes)
            yield return endnotes;
    }

    /// <summary>A field open at this point of the text; it may run over several paragraphs, as a table of contents does.</summary>
    private sealed class Field
    {
        public StringBuilder Code { get; } = new();

        public bool Showing { get; set; }

        /// <summary>What it shows is words of the document's own: not a page number or a date, which Word works out itself.</summary>
        public bool HasWords => Showing && IsLinkOrContents(Code.ToString());
    }

    private static bool IsLinkOrContents(string code)
    {
        var name = code.Trim().Split(' ', 2)[0];
        return name.Equals("HYPERLINK", StringComparison.OrdinalIgnoreCase) || name.Equals("TOC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Goes through a part's paragraphs, gathering the runs that are translated together.</summary>
    private sealed class Reader(List<TextSlot> slots, string to)
    {
        private readonly Stack<Field> _fields = new();
        private List<Run> _runs = [];

        public void Read(OpenXmlElement root)
        {
            foreach (var paragraph in root.Descendants<Paragraph>().ToList())
            {
                Walk(paragraph);
                Flush();
            }
        }

        private void Walk(OpenXmlElement parent)
        {
            foreach (var child in parent.ChildElements)
            {
                switch (child)
                {
                    case Run run:
                        Take(run);
                        break;
                    case Hyperlink:
                        Flush();
                        Walk(child);
                        Flush();
                        break;
                    case SimpleField simple:
                        Flush();
                        if (IsLinkOrContents(simple.Instruction?.Value ?? ""))
                            Walk(child);
                        Flush();
                        break;
                    case SdtRun { SdtContentRun: { } content }:
                        Flush();
                        Walk(content);
                        Flush();
                        break;
                    // Words crossed out in tracked changes, and moved away, are not the text as it stands.
                    case InsertedRun or MoveToRun or CustomXmlRun:
                        Walk(child);
                        break;
                }
            }
        }

        private void Take(Run run)
        {
            bool fieldPart = false;
            foreach (var child in run.ChildElements)
            {
                if (child is FieldChar { FieldCharType: { } kind })
                {
                    fieldPart = true;
                    Flush();
                    if (kind.Value == FieldCharValues.Begin)
                        _fields.Push(new Field());
                    else if (_fields.Count > 0 && kind.Value == FieldCharValues.Separate)
                        _fields.Peek().Showing = true;
                    else if (_fields.Count > 0 && kind.Value == FieldCharValues.End)
                        _fields.Pop();
                }
                else if (child is FieldCode code)
                {
                    fieldPart = true;
                    if (_fields.Count > 0)
                        _fields.Peek().Code.Append(code.Text);
                }
            }
            if (fieldPart || _fields.Any(f => !f.HasWords))
                return;
            if (!Words(run).Any())
            {
                // A tab that goes to a fixed place on the line parts what is on its two sides.
                if (run.GetFirstChild<PositionalTab>() is not null)
                    Flush();
                return;
            }
            _runs.Add(run);
        }

        private void Flush()
        {
            if (_runs.Count == 0)
                return;
            var runs = _runs;
            _runs = [];
            slots.Add(new TextSlot(string.Concat(runs.SelectMany(Words).Select(WordsOf)), words => Put(runs, words, to)));
        }
    }

    /// <summary>A run's parts that are text; pictures, note marks and the like are not, and stay.</summary>
    private static IEnumerable<OpenXmlElement> Words(Run run) => run.ChildElements.Where(c =>
        c is WordText or TabChar or CarriageReturn or NoBreakHyphen or SoftHyphen || (c is Break br && (br.Type is null || br.Type.Value == BreakValues.TextWrapping)));

    private static string WordsOf(OpenXmlElement part) => part switch
    {
        WordText text => text.Text,
        TabChar => "\t",
        Break or CarriageReturn => "\n",
        NoBreakHyphen => "-",
        _ => "",
    };

    /// <summary>The translation in the first run, in the place of its words; the other runs lose theirs, and go if nothing is left.</summary>
    private static void Put(List<Run> runs, string words, string to)
    {
        var first = runs[0];
        // The look of most of the words: a bold lead-in doesn't make the whole paragraph bold.
        var most = runs.MaxBy(r => r.Elements<WordText>().Sum(t => t.Text.Length))!;
        if (most != first)
        {
            first.RunProperties?.Remove();
            if (most.RunProperties is { } look)
                first.PrependChild((RunProperties)look.CloneNode(true));
        }
        var old = runs.SelectMany(Words).ToList();
        foreach (var piece in Pieces(words))
            first.InsertBefore(piece, old[0]);
        foreach (var part in old)
            part.Remove();
        foreach (var run in runs.Skip(1))
            if (run.ChildElements.All(c => c is RunProperties))
                run.Remove();
        var properties = first.RunProperties ??= new RunProperties();
        var languages = properties.Languages ??= new Languages();
        if (DocumentTranslator.IsRightToLeft(to))
            languages.Bidi = DocumentTranslator.Tag(to);
        else
            languages.Val = DocumentTranslator.Tag(to);
    }

    private static IEnumerable<OpenXmlElement> Pieces(string words)
    {
        int start = 0;
        for (int i = 0; i <= words.Length; i++)
        {
            if (i < words.Length && words[i] is not ('\n' or '\t'))
                continue;
            if (i > start)
                yield return new WordText(words[start..i]) { Space = SpaceProcessingModeValues.Preserve };
            if (i < words.Length)
                yield return words[i] == '\n' ? new Break() : new TabChar();
            start = i + 1;
        }
    }

    /// <summary>
    /// Into a right-to-left language paragraphs, runs and tables are marked so, and Arabic script is drawn at the size
    /// and weight the Latin was. Out of one the marks go, and the Latin takes the size and weight the Arabic script was
    /// drawn at. Between two languages written the same way nothing changes.
    /// </summary>
    private static void SetDirection(MainDocumentPart main, List<OpenXmlPartRootElement> roots, string from, string to)
    {
        var styles = main.StyleDefinitionsPart?.Styles;
        if (DocumentTranslator.IsRightToLeft(to))
        {
            foreach (var root in roots)
            {
                foreach (var paragraph in root.Descendants<Paragraph>())
                    (paragraph.ParagraphProperties ??= new ParagraphProperties()).BiDi = new BiDi();
                foreach (var run in root.Descendants<Run>().Where(r => r.Elements<WordText>().Any()))
                    (run.RunProperties ??= new RunProperties()).RightToLeftText = new RightToLeftText();
                foreach (var table in root.Descendants<Table>())
                {
                    if (table.GetFirstChild<TableProperties>() is { } properties)
                        properties.BiDiVisual = new BiDiVisual();
                }
                foreach (var section in root.Descendants<SectionProperties>())
                    if (section.GetFirstChild<BiDi>() is null)
                        section.AddChild(new BiDi(), false);
            }
            foreach (var properties in Formatting(roots, styles))
                ToComplexScript(properties);
        }
        else if (DocumentTranslator.IsRightToLeft(from))
        {
            foreach (var properties in Formatting(roots, styles))
                FromComplexScript(properties);
            var everywhere = roots.Cast<OpenXmlElement>().ToList();
            if (styles is not null)
                everywhere.Add(styles);
            if (main.NumberingDefinitionsPart?.Numbering is { } numbering)
                everywhere.Add(numbering);
            foreach (var root in everywhere)
            {
                foreach (var mark in root.Descendants().Where(e => e is BiDi or RightToLeftText or BiDiVisual).ToList())
                    mark.Remove();
            }
        }
    }

    /// <summary>Every set of character formatting: on runs, in the styles and the document's defaults.</summary>
    private static IEnumerable<OpenXmlCompositeElement> Formatting(List<OpenXmlPartRootElement> roots, Styles? styles)
    {
        foreach (var root in roots)
            foreach (var properties in root.Descendants<RunProperties>())
                yield return properties;
        if (styles is null)
            yield break;
        foreach (var properties in styles.Descendants<StyleRunProperties>())
            yield return properties;
        foreach (var properties in styles.Descendants<RunPropertiesBaseStyle>())
            yield return properties;
    }

    private static OnOffValue? Copy(OnOffValue? value) => value is { HasValue: true } ? new OnOffValue(value.Value) : null;

    /// <summary>Word draws Arabic script with the complex-script twins of size, bold and italic: copied over when missing.</summary>
    private static void ToComplexScript(OpenXmlCompositeElement properties)
    {
        if (properties.GetFirstChild<FontSize>() is { } size && properties.GetFirstChild<FontSizeComplexScript>() is null)
            properties.AddChild(new FontSizeComplexScript { Val = size.Val?.Value }, false);
        if (properties.GetFirstChild<Bold>() is { } bold && properties.GetFirstChild<BoldComplexScript>() is null)
            properties.AddChild(new BoldComplexScript { Val = Copy(bold.Val) }, false);
        if (properties.GetFirstChild<Italic>() is { } italic && properties.GetFirstChild<ItalicComplexScript>() is null)
            properties.AddChild(new ItalicComplexScript { Val = Copy(italic.Val) }, false);
    }

    /// <summary>The other way: what the complex-script twins said is what the reader saw.</summary>
    private static void FromComplexScript(OpenXmlCompositeElement properties)
    {
        if (properties.GetFirstChild<FontSizeComplexScript>() is { } size)
        {
            properties.GetFirstChild<FontSize>()?.Remove();
            properties.AddChild(new FontSize { Val = size.Val?.Value }, false);
        }
        if (properties.GetFirstChild<BoldComplexScript>() is { } bold)
        {
            properties.GetFirstChild<Bold>()?.Remove();
            properties.AddChild(new Bold { Val = Copy(bold.Val) }, false);
        }
        if (properties.GetFirstChild<ItalicComplexScript>() is { } italic)
        {
            properties.GetFirstChild<Italic>()?.Remove();
            properties.AddChild(new Italic { Val = Copy(italic.Val) }, false);
        }
    }
}
