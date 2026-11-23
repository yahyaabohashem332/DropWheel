using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Office;
using F = Petal.Core.Formats.Formats;
using M = Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.Tests;

/// <summary>
/// 1.24's documents: Word's, Excel's and PowerPoint's templates, shows and files with macros; Word 97–2003 documents;
/// web pages saved as one file; reStructuredText.
/// </summary>
[Collection("Office")]
public sealed class MoreDocumentFormatsTests
{
    private static readonly DocumentEngineOptions NoOffice = new() { UseMicrosoftOffice = false, UseLibreOffice = false };

    private static async Task<string> ConvertAsync(string fixture, FileFormat from, FileFormat to, DocumentEngineOptions? options = null)
    {
        var dir = Fixtures.Instance.NewOutputDir($"{from.Id}-{to.Id}");
        var input = Fixtures.Instance.CopyTo(fixture, dir);
        var converter = Fixtures.ConverterFor(from, to, options) ?? throw new InvalidOperationException($"No converter for {from} → {to}");
        var result = await Fixtures.ConvertAsync(converter, input, from, to);
        return result.OutputPaths[0];
    }

    [Theory]
    [InlineData("docm", "docx")]
    [InlineData("xlsm", "xlsx")]
    [InlineData("pptm", "pptx")]
    public async Task Macros_are_taken_out_and_never_kept(string fromId, string toId)
    {
        var from = F.FromId(fromId)!;
        var to = F.FromId(toId)!;
        var sample = Fixtures.Instance.Get(fromId);
        Assert.Contains(ZipFile.OpenRead(sample).Entries, e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
        Assert.IsType<OfficePackageConverter>(Fixtures.ConverterFor(from, to, NoOffice));

        var output = await ConvertAsync(sample, from, to, NoOffice);
        using (var zip = ZipFile.OpenRead(output))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("vba", StringComparison.OrdinalIgnoreCase));
            using var reader = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open());
            Assert.DoesNotContain("macroEnabled", reader.ReadToEnd(), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(-1, File.ReadAllBytes(output).AsSpan().IndexOf(Fixtures.MacroProject.AsSpan(0, 20)));
        Assert.Contains(Fixtures.Token, OutputVerifier.TextOf(output, to), StringComparison.Ordinal);
        ConversionPathTests.AssertStrictlyValid(output, to);
    }

    [Fact]
    public async Task A_document_becomes_a_template_and_back_unchanged()
    {
        var docx = Fixtures.Instance.Get("docx");
        var dotx = await ConvertAsync(docx, F.Docx, F.Dotx, NoOffice);
        using (var template = WordprocessingDocument.Open(dotx, false))
            Assert.Equal(WordprocessingDocumentType.Template, template.DocumentType);
        var back = await ConvertAsync(dotx, F.Dotx, F.Docx, NoOffice);
        using var original = WordprocessingDocument.Open(docx, false);
        using var again = WordprocessingDocument.Open(back, false);
        Assert.Equal(WordprocessingDocumentType.Document, again.DocumentType);
        Assert.Equal(original.MainDocumentPart!.Document!.OuterXml, again.MainDocumentPart!.Document!.OuterXml);
    }

    [Fact]
    public async Task A_presentation_becomes_a_show_and_a_workbook_a_template()
    {
        var ppsx = await ConvertAsync(Fixtures.Instance.Get("pptx"), F.Pptx, F.Ppsx, NoOffice);
        using (var show = PresentationDocument.Open(ppsx, false))
            Assert.Equal(PresentationDocumentType.Slideshow, show.DocumentType);
        var xltx = await ConvertAsync(Fixtures.Instance.Get("xlsx"), F.Xlsx, F.Xltx, NoOffice);
        using (var template = SpreadsheetDocument.Open(xltx, false))
            Assert.Equal(SpreadsheetDocumentType.Template, template.DocumentType);
        // A template's sheets read as any workbook's.
        var csv = await ConvertAsync(xltx, F.Xltx, F.Csv, NoOffice);
        Assert.Contains("Petal", File.ReadAllText(csv), StringComparison.Ordinal);
    }

    /// <summary>A Word 97–2003 document Word itself saved: Arabic, a table and a link (a field in the file).</summary>
    private static string? WordDoc(string name)
    {
        if (!OfficeAutomation.IsInstalled(OfficeApp.Word))
            return null;
        var doc = new DocModel();
        doc.Blocks.Add(Para("Petal older document"));
        doc.Blocks.Add(Para("مرحبا بالعالم، هذا مستند قديم"));
        var linked = new M.Paragraph();
        linked.Inlines.Add(new TextRun("Read "));
        linked.Inlines.Add(new TextRun("the guide", TextStyle.None, "https://example.com/guide"));
        linked.Inlines.Add(new TextRun(" first."));
        doc.Blocks.Add(linked);
        var table = new M.Table();
        foreach (var (a, b) in new[] { ("Item", "Count"), ("Petal", "5"), ("Leaf", "9") })
        {
            var row = new M.TableRow();
            foreach (var text in new[] { a, b })
            {
                var cell = new M.TableCell();
                cell.Blocks.Add(Para(text));
                row.Cells.Add(cell);
            }
            table.Rows.Add(row);
        }
        doc.Blocks.Add(table);
        doc.Blocks.Add(Para("After the table."));
        var dir = Fixtures.Instance.NewOutputDir("worddoc");
        var docx = Path.Combine(dir, name + ".docx");
        DocxWriter.Write(doc, docx);
        var path = Path.Combine(dir, name + ".doc");
        OfficeAutomation.SaveAsAsync(OfficeApp.Word, docx, path, 0, TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
        return path;

        static M.Paragraph Para(string text)
        {
            var p = new M.Paragraph();
            p.Inlines.Add(new TextRun(text));
            return p;
        }
    }

    [Fact]
    public void A_doc_gives_its_words_without_Word()
    {
        var path = WordDoc("older");
        if (path is null)
            return; // the sample needs Word to be made
        Assert.True(CompoundFile.Is(File.ReadAllBytes(path)));
        var doc = WordBinaryReader.Read(path);
        var paragraphs = doc.Blocks.OfType<M.Paragraph>().Select(p => p.Inlines.PlainText().Trim()).ToList();
        Assert.Contains("Petal older document", paragraphs);
        Assert.Contains("مرحبا بالعالم، هذا مستند قديم", paragraphs);
        // The link's words, not its field's code.
        Assert.Contains("Read the guide first.", paragraphs);
        Assert.DoesNotContain(paragraphs, p => p.Contains("HYPERLINK", StringComparison.Ordinal));
        var table = Assert.Single(doc.Blocks.OfType<M.Table>());
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["Petal", "5"], table.Rows[1].Cells.Select(c => c.Blocks.PlainText().Trim()));
        Assert.Equal("After the table.", paragraphs[^1]);
    }

    [Fact]
    public async Task A_doc_goes_through_Word_whole_and_without_it_as_its_words()
    {
        var path = WordDoc("whole");
        if (path is null)
            return;
        var docx = await ConvertAsync(path, F.Doc, F.Docx);
        using (var word = WordprocessingDocument.Open(docx, false))
        {
            var body = word.MainDocumentPart!.Document!.Body!;
            Assert.Single(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>());
            Assert.Contains(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Hyperlink>(), h => h.InnerText == "the guide");
        }
        ConversionPathTests.AssertStrictlyValid(docx, F.Docx);
        var txt = await ConvertAsync(path, F.Doc, F.Txt, NoOffice);
        Assert.Contains("مرحبا بالعالم", File.ReadAllText(txt), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_web_page_keeps_its_picture_and_words()
    {
        var mhtml = Fixtures.Instance.Get("mhtml");
        var model = MhtmlReader.Read(mhtml);
        Assert.Equal("Petal Saved Page", model.Metadata.Title);
        var text = model.PlainText();
        Assert.Contains("مرحبا بالعالم", text, StringComparison.Ordinal);
        Assert.NotEmpty(model.AllImages());
        Assert.Contains(model.AllInlines().OfType<TextRun>(), r => r.Link == "https://example.com/petal");
        Assert.Single(model.Blocks.OfType<M.Table>());

        var docx = await ConvertAsync(mhtml, F.Mhtml, F.Docx, NoOffice);
        using var word = WordprocessingDocument.Open(docx, false);
        Assert.Single(word.MainDocumentPart!.ImageParts);
    }

    [Fact]
    public void A_web_page_Word_saved_names_its_pictures_by_content_id()
    {
        var png = File.ReadAllBytes(Fixtures.Instance.Get("png-image"));
        var message = "MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=\"next\"\r\n\r\n" +
            "--next\r\nContent-Type: text/html; charset=\"windows-1256\"\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" +
            "<html><body><p>=E3=D1=CD=C8=C7 Petal</p><img src=3D\"cid:picture@petal\"></body></html>\r\n" +
            "--next\r\nContent-Type: image/png\r\nContent-ID: <picture@petal>\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
            Convert.ToBase64String(png) + "\r\n--next--\r\n";
        var path = Path.Combine(Fixtures.Instance.NewOutputDir("cid"), "word.mht");
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(message));
        var model = MhtmlReader.Read(path);
        Assert.Contains("مرحبا Petal", model.PlainText(), StringComparison.Ordinal);
        Assert.NotEmpty(model.AllImages());
    }

    [Fact]
    public void ReStructuredText_reads_as_docutils_shows_it()
    {
        var doc = RstReader.Parse(Fixtures.Rst);
        Assert.Equal("Petal Tests", doc.Metadata.Author);
        var headings = doc.Blocks.OfType<Heading>().Select(h => (h.Level, h.Inlines.PlainText())).ToList();
        Assert.Equal([(1, "Petal RST Guide"), (2, "Shopping list")], headings);

        var intro = doc.Blocks.OfType<M.Paragraph>().First(p => p.Inlines.PlainText().StartsWith("Intro", StringComparison.Ordinal));
        var runs = intro.Inlines.OfType<TextRun>().ToList();
        Assert.Contains(runs, r => r.Text == "bold" && r.Has(TextStyle.Bold));
        Assert.Contains(runs, r => r.Text == "italic" && r.Has(TextStyle.Italic));
        Assert.Contains(runs, r => r.Text == "code" && r.Has(TextStyle.Code));
        Assert.Contains(runs, r => r.Text == "link" && r.Link == "https://example.com");
        Assert.Equal("Intro paragraph with bold, italic, code and a link.", intro.Inlines.PlainText());

        var lists = doc.Blocks.OfType<ListBlock>().ToList();
        Assert.Equal(2, lists.Count);
        Assert.False(lists[0].Ordered);
        Assert.Equal(3, lists[0].Items.Count);
        Assert.Equal("Green ones", Assert.Single(lists[0].Items[1].Blocks.OfType<ListBlock>().Single().Items).Blocks.PlainText().Trim());
        Assert.True(lists[1].Ordered);
        Assert.Equal(["First step", "Second step"], lists[1].Items.Select(i => i.Blocks.PlainText().Trim()));

        var table = Assert.Single(doc.Blocks.OfType<M.Table>());
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal(["Petal", "3"], table.Rows[1].Cells.Select(c => c.Blocks.PlainText().Trim()));

        var note = Assert.Single(doc.Blocks.OfType<Quote>());
        Assert.Equal("Note\nNotes become quotes.", note.Blocks.PlainText().Trim());
        Assert.Equal("code block", Assert.Single(doc.Blocks.OfType<CodeBlock>()).Text);
        Assert.Contains(doc.Blocks.OfType<M.Paragraph>(), p => p.Inlines.PlainText() == "Example:");
        Assert.Equal("مرحبا بالعالم", doc.Blocks.OfType<M.Paragraph>().Last().Inlines.PlainText());
    }

    [Fact]
    public void ReStructuredText_has_simple_tables_definitions_and_escapes()
    {
        const string text = """
            Title
            -----

            =====  =====
            City   Count
            =====  =====
            Paris  12
            Rome   7
                   more
            =====  =====

            term
                Its definition.

            A \*star\*, an escaped\ word and PEP :pep:`8`.

            .. toctree::

               hidden

            .. code-block:: python

               print("hi")
            """;
        var doc = RstReader.Parse(text);
        Assert.Equal("Title", Assert.Single(doc.Blocks.OfType<Heading>()).Inlines.PlainText());
        var table = Assert.Single(doc.Blocks.OfType<M.Table>());
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal("7 more", table.Rows[2].Cells[1].Blocks.PlainText().Trim());
        Assert.Contains(doc.Blocks.OfType<M.Paragraph>(), p => p.Inlines.PlainText() == "term");
        Assert.Equal("Its definition.", Assert.Single(doc.Blocks.OfType<Quote>()).Blocks.PlainText().Trim());
        Assert.Contains(doc.Blocks.OfType<M.Paragraph>(), p => p.Inlines.PlainText() == "A *star*, an escapedword and PEP PEP 8.");
        Assert.DoesNotContain("hidden", doc.PlainText(), StringComparison.Ordinal);
        var code = Assert.Single(doc.Blocks.OfType<CodeBlock>());
        Assert.Equal(("print(\"hi\")", "python"), (code.Text, code.Language));
    }

    [Fact]
    public void ReStructuredText_written_reads_back_the_same()
    {
        var rich = Fixtures.RichModel();
        var rst = RstWriter.ToRst(rich);
        // The title over- and underlined, sections underlined, lines at least as long as their titles.
        Assert.StartsWith("===================\nPetal Rich Document\n===================\n", rst, StringComparison.Ordinal);
        var back = RstReader.Parse(rst);
        Assert.Equal(Squash(rich.PlainText()), Squash(back.PlainText()));
        Assert.Equal(rich.Blocks.OfType<Heading>().Select(h => h.Level), back.Blocks.OfType<Heading>().Select(h => h.Level));
        Assert.Equal(rich.Blocks.OfType<ListBlock>().Select(l => (l.Ordered, l.Items.Count)), back.Blocks.OfType<ListBlock>().Select(l => (l.Ordered, l.Items.Count)));
        Assert.Equal(rich.Blocks.OfType<M.Table>().Select(t => t.Rows.Count), back.Blocks.OfType<M.Table>().Select(t => t.Rows.Count));
        var link = back.Blocks.OfType<M.Paragraph>().SelectMany(p => p.Inlines).OfType<TextRun>().Single(r => r.Link is not null);
        Assert.Equal(("linked", "https://example.com/petal"), (link.Text, link.Link));

        // Markup against a word needs an escaped space; stars in text are escaped.
        var tight = new DocModel();
        var p = new M.Paragraph();
        p.Inlines.Add(new TextRun("Pre"));
        p.Inlines.Add(new TextRun("bold", TextStyle.Bold));
        p.Inlines.Add(new TextRun("post 2*3 snake_ "));
        tight.Blocks.Add(p);
        var written = RstWriter.ToRst(tight);
        Assert.Equal("Pre\\ **bold**\\ post 2\\*3 snake\\_\n", written);
        Assert.Equal("Preboldpost 2*3 snake_", RstReader.Parse(written).PlainText().Trim());

        static string Squash(string s) => new([.. s.Where(c => !char.IsWhiteSpace(c))]);
    }

    [Fact]
    public async Task Markdown_becomes_reStructuredText()
    {
        var rst = await ConvertAsync(Fixtures.Instance.Get("md"), F.Md, F.Rst, NoOffice);
        var text = File.ReadAllText(rst);
        Assert.StartsWith("====================\nPetal Markdown Guide\n====================\n", text, StringComparison.Ordinal);
        Assert.Contains("+-------+-------+", text, StringComparison.Ordinal);
        Assert.Contains("`link <https://example.com>`__", text, StringComparison.Ordinal);
        var back = RstReader.Read(rst);
        Assert.Equal(2, back.Blocks.OfType<Heading>().Count());
        Assert.Single(back.Blocks.OfType<CodeBlock>());
    }
}
