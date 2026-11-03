using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Drawing;
using Petal.Core.Models;
using Petal.Engines.Documents.Translate;
using Petal.Recipes.Translation;
using UglyToad.PdfPig;
using Xunit.Abstractions;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Petal.Tests;

/// <summary>
/// Translate Document, with a stand-in translator: every file built here, translated, opened again and read. The gated test
/// at the end runs the real models when PETAL_TRANSLATE names their folder.
/// </summary>
public sealed class TranslateDocumentTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("petal-translate-").FullName;
    private const string Arabic = "نص مترجم إلى العربية";

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string File_(string name) => Path.Combine(_folder, name);

    /// <summary>Each line in marks, so what was translated, and what wasn't, shows.</summary>
    private static Task<IReadOnlyList<string>> Marked(IReadOnlyList<string> lines, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(lines.Select(l => "[[" + l + "]]").ToList());
    }

    private static Task<IReadOnlyList<string>> IntoArabic(IReadOnlyList<string> lines, IProgress<double>? progress, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(lines.Select(_ => Arabic).ToList());

    private static Task<IReadOnlyList<string>> IntoEnglish(IReadOnlyList<string> lines, IProgress<double>? progress, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(lines.Select(l => "English words").ToList());

    private static void AssertValid(OpenXmlPackage package)
    {
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(package).Select(e => e.Description + " @ " + e.Path?.XPath).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(10)));
    }

    // ---------- Word ----------

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAEBgIApD5fRAAAAABJRU5ErkJggg==");

    private static W.Run TextRun(string text, W.RunProperties? properties = null)
    {
        var run = new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        if (properties is not null)
            run.RunProperties = properties;
        return run;
    }

    /// <summary>A document with one of everything that holds words.</summary>
    private string MakeDocx(string name)
    {
        var path = File_(name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new W.Styles(
            new W.DocDefaults(new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(new W.FontSize { Val = "22" }))),
            new W.Style(new W.StyleName { Val = "Title" }, new W.StyleRunProperties(new W.Bold(), new W.FontSize { Val = "48" })) { Type = W.StyleValues.Paragraph, StyleId = "Title" });
        var link = main.AddHyperlinkRelationship(new Uri("https://example.com/"), true);
        var image = main.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(Png))
            image.FeedData(stream);

        var header = main.AddNewPart<HeaderPart>();
        header.Header = new W.Header(new W.Paragraph(TextRun("Header words")));
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new W.Footer(new W.Paragraph(TextRun("Page "),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin }),
            new W.Run(new W.FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }),
            TextRun("7"),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End })));
        var notes = main.AddNewPart<FootnotesPart>();
        notes.Footnotes = new W.Footnotes(new W.Footnote(new W.Paragraph(TextRun("Footnote words"))) { Id = 1 });
        var endnotes = main.AddNewPart<EndnotesPart>();
        endnotes.Endnotes = new W.Endnotes(new W.Endnote(new W.Paragraph(TextRun("Endnote words"))) { Id = 1 });

        var body = new W.Body(
            new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Title" }), TextRun("The title")),
            new W.Paragraph(
                TextRun("Bold and red, and most of the words", new W.RunProperties(new W.Bold(), new W.Color { Val = "FF0000" }, new W.FontSize { Val = "32" })),
                TextRun(" and the rest of it.", new W.RunProperties(new W.Italic()))),
            new W.Paragraph(TextRun("See "), new W.Hyperlink(TextRun("the site", new W.RunProperties(new W.Underline { Val = W.UnderlineValues.Single }))) { Id = link.Id }, TextRun(" for more.")),
            new W.Paragraph(TextRun("With a line"), new W.Run(new W.Break()), TextRun("break\tand a tab")),
            new W.Paragraph(new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin }),
                new W.Run(new W.FieldCode(" DATE ") { Space = SpaceProcessingModeValues.Preserve }),
                new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }),
                TextRun("Tuesday"),
                new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End })),
            new W.Paragraph(new W.SimpleField(TextRun("Field words")) { Instruction = " AUTHOR " }),
            new W.Paragraph(new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin }),
                new W.Run(new W.FieldCode(" HYPERLINK \"https://example.com/\" ") { Space = SpaceProcessingModeValues.Preserve }),
                new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }),
                TextRun("A field link"),
                new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End })),
            new W.Paragraph(new W.Run(new W.Picture(new DocumentFormat.OpenXml.Vml.Shape(
                new DocumentFormat.OpenXml.Vml.TextBox(new W.TextBoxContent(new W.Paragraph(TextRun("In a text box")))))))),
            new W.Paragraph(TextRun("2024")),
            new W.Table(new W.TableProperties(new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct }),
                new W.TableGrid(new W.GridColumn { Width = "2000" }, new W.GridColumn { Width = "2000" }),
                new W.TableRow(
                    new W.TableCell(new W.Paragraph(TextRun("Left cell"))),
                    new W.TableCell(new W.Paragraph(TextRun("Right cell"))))),
            new W.Paragraph(new W.Run(new W.Drawing(Inline(main.GetIdOfPart(image))))),
            new W.SectionProperties(new W.HeaderReference { Id = main.GetIdOfPart(header), Type = W.HeaderFooterValues.Default },
                new W.FooterReference { Id = main.GetIdOfPart(footer), Type = W.HeaderFooterValues.Default },
                new W.PageSize { Width = 11906, Height = 16838 }));
        main.Document = new W.Document(body);
        return path;
    }

    private static DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline Inline(string relationship) => new(
        new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 914400, Cy = 914400 },
        new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1U, Name = "Picture" },
        new A.Graphic(new A.GraphicData(new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = 2U, Name = "p.png" },
                new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
            new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(new A.Blip { Embed = relationship }, new A.Stretch(new A.FillRectangle())),
            new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 914400, Cy = 914400 }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
        { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }));

    private static string Words(W.Paragraph p) => string.Concat(p.Descendants<W.Text>().Select(t => t.Text));

    [Fact]
    public async Task A_word_document_is_translated_with_its_formatting_kept()
    {
        var input = MakeDocx("report.docx");
        var before = File.ReadAllBytes(input);
        var result = File_("report.es.docx");
        await DocumentTranslator.TranslateAsync(input, result, "en", "es", Marked);
        Assert.Equal(before, File.ReadAllBytes(input));

        using var document = WordprocessingDocument.Open(result, false);
        AssertValid(document);
        var main = document.MainDocumentPart!;
        var body = main.Document!.Body!;
        var paragraphs = body.Elements<W.Paragraph>().ToList();

        Assert.Equal("Title", paragraphs[0].ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Equal("[[The title]]", Words(paragraphs[0]));

        // A paragraph as one, in its first run, with that run's look; the second run is gone.
        Assert.Equal("[[Bold and red, and most of the words and the rest of it.]]", Words(paragraphs[1]));
        var first = paragraphs[1].Elements<W.Run>().Single();
        Assert.NotNull(first.RunProperties!.Bold);
        Assert.Equal("FF0000", first.RunProperties.Color!.Val!.Value);
        Assert.Equal("32", first.RunProperties.FontSize!.Val!.Value);
        Assert.Equal("es-ES", first.RunProperties.Languages!.Val!.Value);

        // The link is still a link, translated on its own, with spaces kept on its sides.
        var link = paragraphs[2].Elements<W.Hyperlink>().Single();
        Assert.Equal("[[the site]]", string.Concat(link.Descendants<W.Text>().Select(t => t.Text)));
        Assert.NotNull(link.Descendants<W.Underline>().Single());
        Assert.Equal("https://example.com/", main.HyperlinkRelationships.Single(r => r.Id == link.Id).Uri.ToString());
        Assert.Equal("[[See]] [[the site]] [[for more.]]", Words(paragraphs[2]));

        // Line breaks and tabs come back as themselves, not as characters in the text.
        var parts = paragraphs[3].Elements<W.Run>().Single();
        Assert.Single(parts.Elements<W.Break>());
        Assert.Single(parts.Elements<W.TabChar>());
        Assert.DoesNotContain(parts.Elements<W.Text>(), t => t.Text.Contains('\n') || t.Text.Contains('\t'));

        // A date field keeps what it shows and its instruction; so does a simple one; a link field's words are translated.
        Assert.Equal("Tuesday", Words(paragraphs[4]));
        Assert.Contains(paragraphs[4].Descendants<W.FieldCode>(), c => c.Text.Contains("DATE"));
        Assert.Equal("Field words", Words(paragraphs[5]));
        Assert.Equal("[[A field link]]", Words(paragraphs[6]));
        Assert.Contains(paragraphs[6].Descendants<W.FieldCode>(), c => c.Text.Contains("HYPERLINK"));

        // A text box; a number alone stays; table cells; the picture; page setup.
        Assert.Equal("[[In a text box]]", string.Concat(paragraphs[7].Descendants<W.TextBoxContent>().Single().Descendants<W.Text>().Select(t => t.Text)));
        Assert.Equal("2024", Words(paragraphs[8]));
        Assert.Equal(["[[Left cell]]", "[[Right cell]]"], body.Descendants<W.TableCell>().Select(c => string.Concat(c.Descendants<W.Text>().Select(t => t.Text))));
        Assert.Single(body.Descendants<W.Drawing>());
        Assert.Single(main.ImageParts);
        Assert.Equal(11906U, body.Elements<W.SectionProperties>().Single().GetFirstChild<W.PageSize>()!.Width!.Value);

        // Everywhere else words are kept; a page number stays and its label is translated.
        Assert.Equal("[[Header words]]", Words(main.HeaderParts.Single().Header!.Elements<W.Paragraph>().Single()));
        var footer = main.FooterParts.Single().Footer!.Elements<W.Paragraph>().Single();
        Assert.Equal("[[Page]] 7", Words(footer));
        Assert.Contains(footer.Descendants<W.FieldCode>(), c => c.Text.Contains("PAGE"));
        Assert.Equal("[[Footnote words]]", Words(main.FootnotesPart!.Footnotes!.Descendants<W.Paragraph>().Single()));
        Assert.Equal("[[Endnote words]]", Words(main.EndnotesPart!.Endnotes!.Descendants<W.Paragraph>().Single()));

        // Two left-to-right languages: nothing is marked.
        Assert.Empty(body.Descendants<W.BiDi>());
        Assert.Empty(body.Descendants<W.RightToLeftText>());
    }

    [Fact]
    public async Task A_text_box_with_its_modern_and_its_older_copy_is_translated_in_both()
    {
        var path = File_("box.docx");
        var box = """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
              xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
              xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:v="urn:schemas-microsoft-com:vml" mc:Ignorable="wps">
              <w:body><w:p><w:r><w:t>Outside words</w:t></w:r><w:r><mc:AlternateContent><mc:Choice Requires="wps"><w:drawing><wp:anchor distT="0" distB="0" distL="0" distR="0" simplePos="0" relativeHeight="1" behindDoc="0" locked="0" layoutInCell="1" allowOverlap="1">
                <wp:simplePos x="0" y="0"/><wp:positionH relativeFrom="column"><wp:posOffset>0</wp:posOffset></wp:positionH><wp:positionV relativeFrom="paragraph"><wp:posOffset>0</wp:posOffset></wp:positionV>
                <wp:extent cx="914400" cy="914400"/><wp:wrapNone/><wp:docPr id="1" name="Box"/><wp:cNvGraphicFramePr/>
                <a:graphic><a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><wps:wsp><wps:cNvSpPr txBox="1"/><wps:spPr/>
                <wps:txbx><w:txbxContent><w:p><w:r><w:t>Box words</w:t></w:r></w:p></w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice>
                <mc:Fallback><w:pict><v:shape id="b"><v:textbox><w:txbxContent><w:p><w:r><w:t>Box words</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent></w:r></w:p></w:body></w:document>
            """;
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            using var stream = main.GetStream(FileMode.Create);
            using var writer = new StreamWriter(stream);
            writer.Write(box);
        }
        var result = File_("box.es.docx");
        await DocumentTranslator.TranslateAsync(path, result, "en", "es", Marked);
        using var translated = WordprocessingDocument.Open(result, false);
        var all = translated.MainDocumentPart!.Document!.Descendants<W.Text>().Select(t => t.Text).ToList();
        Assert.Equal(["[[Outside words]]", "[[Box words]]", "[[Box words]]"], all);
    }

    [Fact]
    public async Task Into_arabic_a_document_is_marked_right_to_left_and_back_out_again()
    {
        var input = MakeDocx("report.docx");
        var arabic = File_("report.ar.docx");
        await DocumentTranslator.TranslateAsync(input, arabic, "en", "ar", IntoArabic);
        using (var document = WordprocessingDocument.Open(arabic, false))
        {
            AssertValid(document);
            var main = document.MainDocumentPart!;
            var body = main.Document!.Body!;
            Assert.All(body.Elements<W.Paragraph>(), p => Assert.NotNull(p.ParagraphProperties?.BiDi));
            Assert.NotNull(body.Descendants<W.Table>().Single().GetFirstChild<W.TableProperties>()!.BiDiVisual);
            Assert.All(body.Descendants<W.Run>().Where(r => r.Elements<W.Text>().Any()), r => Assert.NotNull(r.RunProperties?.RightToLeftText));
            var run = body.Elements<W.Paragraph>().ElementAt(1).Elements<W.Run>().Single();
            Assert.Equal(Arabic, run.Elements<W.Text>().Single().Text);
            Assert.Equal("32", run.RunProperties!.FontSizeComplexScript!.Val!.Value);
            Assert.NotNull(run.RunProperties.BoldComplexScript);
            Assert.Equal("ar-SA", run.RunProperties.Languages!.Bidi!.Value);
            // The styles' size and weight too, so a heading isn't drawn smaller in Arabic script.
            var title = main.StyleDefinitionsPart!.Styles!.Elements<W.Style>().Single(s => s.StyleId == "Title").StyleRunProperties!;
            Assert.Equal("48", title.FontSizeComplexScript!.Val!.Value);
            Assert.NotNull(title.BoldComplexScript);
        }

        var english = File_("report.en.docx");
        await DocumentTranslator.TranslateAsync(arabic, english, "ar", "en", IntoEnglish);
        using (var document = WordprocessingDocument.Open(english, false))
        {
            AssertValid(document);
            var body = document.MainDocumentPart!.Document!.Body!;
            Assert.Empty(body.Descendants<W.BiDi>());
            Assert.Empty(body.Descendants<W.RightToLeftText>());
            Assert.Empty(body.Descendants<W.BiDiVisual>());
            Assert.Equal("English words", Words(body.Elements<W.Paragraph>().ElementAt(1)));
        }
    }

    [Fact]
    public async Task Arabic_set_only_in_its_complex_script_size_keeps_that_size_in_english()
    {
        var path = File_("sized.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            document.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.ParagraphProperties(new W.BiDi()),
                TextRun("نص عربي", new W.RunProperties(new W.BoldComplexScript(), new W.FontSizeComplexScript { Val = "36" }, new W.RightToLeftText())))));
        }
        var result = File_("sized.en.docx");
        await DocumentTranslator.TranslateAsync(path, result, "ar", "en", IntoEnglish);
        using var translated = WordprocessingDocument.Open(result, false);
        AssertValid(translated);
        var run = translated.MainDocumentPart!.Document!.Descendants<W.Run>().Single();
        Assert.Equal("36", run.RunProperties!.FontSize!.Val!.Value);
        Assert.NotNull(run.RunProperties.Bold);
        Assert.Null(run.RunProperties.RightToLeftText);
    }

    [Fact]
    public async Task An_odt_or_rtf_comes_out_as_a_translated_docx()
    {
        var path = File_("notes.rtf");
        File.WriteAllText(path, @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\f0\fs24 Hello from a rich text file.\par}");
        Assert.Equal("docx", DocumentTranslator.OutputExtension(path));
        Assert.Contains("Hello", DocumentTranslator.FirstText(path));
        var result = File_("notes.es.docx");
        await DocumentTranslator.TranslateAsync(path, result, "en", "es", Marked);
        using var translated = WordprocessingDocument.Open(result, false);
        Assert.Contains("[[Hello from a rich text file.]]", translated.MainDocumentPart!.Document!.InnerText);
    }

    // ---------- PowerPoint ----------

    /// <summary>A deck of one slide (title, body, table, a link, a field, notes), its layout centring the title.</summary>
    private string MakePptx(string name, Action<P.Slide>? change = null)
    {
        var path = File_(name);
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentation = document.AddPresentationPart();
        presentation.Presentation = new P.Presentation(new P.SlideMasterIdList(), new P.SlideIdList(), new P.SlideSize { Cx = 9144000, Cy = 6858000 }, new P.NotesSize { Cx = 6858000, Cy = 9144000 });
        var slide = presentation.AddNewPart<SlidePart>();
        var layout = slide.AddNewPart<SlideLayoutPart>();
        var master = layout.AddNewPart<SlideMasterPart>();
        master.AddPart(layout);
        presentation.AddPart(master);
        master.AddNewPart<ThemePart>().Theme = Theme();
        master.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(Tree(Placeholder(2, P.PlaceholderValues.Title, 914400, 500000))),
            new P.ColorMap { Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1, Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2, Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2, Accent3 = A.ColorSchemeIndexValues.Accent3, Accent4 = A.ColorSchemeIndexValues.Accent4, Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6, Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = master.GetIdOfPart(layout) }));
        layout.SlideLayout = new P.SlideLayout(new P.CommonSlideData(Tree(CenteredTitle())));
        presentation.Presentation.SlideMasterIdList!.Append(new P.SlideMasterId { Id = 2147483648U, RelationshipId = presentation.GetIdOfPart(master) });
        presentation.Presentation.SlideIdList!.Append(new P.SlideId { Id = 256U, RelationshipId = presentation.GetIdOfPart(slide) });
        var link = slide.AddHyperlinkRelationship(new Uri("https://example.com/"), true);

        var body = new P.Shape(
            new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 3U, Name = "Box" }, new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 2000000, Cy = 500000 })),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                new A.Paragraph(Run("Bold words that go on for quite a long while", true, 2400), new A.Break(), Run(" and more after a break", false, 2400)),
                new A.Paragraph(new A.ParagraphProperties { Alignment = A.TextAlignmentTypeValues.Left }, Run("See ", false, null),
                    new A.Run(new A.RunProperties(new A.HyperlinkOnClick { Id = link.Id }) { Language = "en-US" }, new A.Text("the site"))),
                new A.Paragraph(new A.Field(new A.Text("7")) { Id = "{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}", Type = "slidenum" })));
        var table = new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(new P.NonVisualDrawingProperties { Id = 4U, Name = "Table" }, new P.NonVisualGraphicFrameDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.Transform(new A.Offset { X = 0, Y = 1000000 }, new A.Extents { Cx = 2000000, Cy = 500000 }),
            new A.Graphic(new A.GraphicData(new A.Table(new A.TableProperties(), new A.TableGrid(new A.GridColumn { Width = 2000000 }),
                new A.TableRow(new A.TableCell(new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(Run("Cell words", false, null))), new A.TableCellProperties())) { Height = 500000 }))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
        var title = new P.Shape(
            new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 2U, Name = "Title" }, new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = P.PlaceholderValues.CenteredTitle })),
            new P.ShapeProperties(),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(Run("The title", false, null))));
        slide.Slide = new P.Slide(new P.CommonSlideData(Tree(title, body, table)));
        change?.Invoke(slide.Slide);

        var notes = slide.AddNewPart<NotesSlidePart>();
        notes.NotesSlide = new P.NotesSlide(new P.CommonSlideData(Tree(new P.Shape(
            new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 2U, Name = "Notes" }, new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1U })),
            new P.ShapeProperties(),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(Run("Speaker notes", false, null)))))));
        notes.AddPart(slide);
        return path;
    }

    private static P.Shape CenteredTitle() => new(
        new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 2U, Name = "Title" }, new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = P.PlaceholderValues.CenteredTitle })),
        new P.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 8000000, Cy = 1000000 })),
        new P.TextBody(new A.BodyProperties(), new A.ListStyle(new A.Level1ParagraphProperties { Alignment = A.TextAlignmentTypeValues.Center }), new A.Paragraph()));

    private static P.Shape Placeholder(uint id, P.PlaceholderValues kind, long cx, long cy) => new(
        new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = id, Name = "Place" }, new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = kind })),
        new P.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy })),
        new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()));

    private static P.ShapeTree Tree(params OpenXmlElement[] shapes)
    {
        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));
        tree.Append(shapes);
        return tree;
    }

    private static A.Run Run(string text, bool bold, int? size)
    {
        var properties = new A.RunProperties { Language = "en-US" };
        if (bold)
            properties.Bold = true;
        if (size is { } s)
            properties.FontSize = s;
        return new A.Run(properties, new A.Text(text));
    }

    private static A.Theme Theme() => new(new A.ThemeElements(
        new A.ColorScheme(new A.Dark1Color(new A.RgbColorModelHex { Val = "000000" }), new A.Light1Color(new A.RgbColorModelHex { Val = "FFFFFF" }), new A.Dark2Color(new A.RgbColorModelHex { Val = "000000" }), new A.Light2Color(new A.RgbColorModelHex { Val = "FFFFFF" }),
            new A.Accent1Color(new A.RgbColorModelHex { Val = "000000" }), new A.Accent2Color(new A.RgbColorModelHex { Val = "000000" }), new A.Accent3Color(new A.RgbColorModelHex { Val = "000000" }), new A.Accent4Color(new A.RgbColorModelHex { Val = "000000" }), new A.Accent5Color(new A.RgbColorModelHex { Val = "000000" }), new A.Accent6Color(new A.RgbColorModelHex { Val = "000000" }),
            new A.Hyperlink(new A.RgbColorModelHex { Val = "000000" }), new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "000000" })) { Name = "T" },
        new A.FontScheme(new A.MajorFont(new A.LatinFont { Typeface = "Arial" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }), new A.MinorFont(new A.LatinFont { Typeface = "Arial" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" })) { Name = "T" },
        new A.FormatScheme(new A.FillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
            new A.LineStyleList(new A.Outline(), new A.Outline(), new A.Outline()), new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
            new A.BackgroundFillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }))) { Name = "T" }))
    { Name = "T" };

    private static string Words(A.Paragraph p) => string.Concat(p.Elements().Select(e => e switch { A.Run r => r.Text?.Text, A.Break => "|", A.Field f => f.Text?.Text, _ => "" }));

    [Fact]
    public async Task A_deck_is_translated_with_links_fields_tables_and_notes_kept()
    {
        var input = MakePptx("deck.pptx");
        var result = File_("deck.es.pptx");
        var before = File.ReadAllBytes(input);
        await DocumentTranslator.TranslateAsync(input, result, "en", "es", Marked);
        Assert.Equal(before, File.ReadAllBytes(input));

        using var document = PresentationDocument.Open(result, false);
        AssertValid(document);
        var slide = document.PresentationPart!.SlideParts.Single();
        var paragraphs = slide.Slide!.Descendants<A.Paragraph>().ToList();
        Assert.Equal("[[The title]]", Words(paragraphs[0]));
        Assert.Equal("[[Bold words that go on for quite a long while| and more after a break]]", Words(paragraphs[1]));
        // The look of most of the words, on both lines; the language is told.
        var runs = paragraphs[1].Elements<A.Run>().ToList();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r =>
        {
            Assert.True(r.RunProperties!.Bold!.Value);
            Assert.Equal(2400, r.RunProperties.FontSize!.Value);
            Assert.Equal("es-ES", r.RunProperties.Language!.Value);
        });
        Assert.Equal("[[See]] [[the site]]", Words(paragraphs[2]));
        var linked = paragraphs[2].Elements<A.Run>().Last();
        Assert.Equal("https://example.com/", slide.HyperlinkRelationships.Single(r => r.Id == linked.RunProperties!.GetFirstChild<A.HyperlinkOnClick>()!.Id).Uri.ToString());
        Assert.Equal("7", Words(paragraphs[3]));
        Assert.NotNull(paragraphs[3].GetFirstChild<A.Field>());
        Assert.Equal("[[Cell words]]", Words(paragraphs[4]));
        Assert.Equal("[[Speaker notes]]", Words(slide.NotesSlidePart!.NotesSlide!.Descendants<A.Paragraph>().Single()));
        Assert.Null(paragraphs[1].ParagraphProperties?.RightToLeft);
    }

    [Fact]
    public async Task A_box_that_cannot_hold_its_translation_is_told_to_shrink_it()
    {
        var input = MakePptx("deck.pptx");
        var result = File_("deck.ar.pptx");
        await DocumentTranslator.TranslateAsync(input, result, "en", "ar", (lines, _, _) =>
            Task.FromResult<IReadOnlyList<string>>(lines.Select(l => string.Concat(Enumerable.Repeat(Arabic + " ", 3))).ToList()));
        using var document = PresentationDocument.Open(result, false);
        var shapes = document.PresentationPart!.SlideParts.Single().Slide!.Descendants<P.Shape>().ToList();
        AssertValid(document);
        var fit = shapes[1].TextBody!.GetFirstChild<A.BodyProperties>()!.GetFirstChild<A.NormalAutoFit>();
        Assert.NotNull(fit);
        Assert.InRange(fit.FontScale!.Value, 40_000, 99_000);
        // The large title box holds its text at full size: told to shrink if it comes to it, but no shrink yet.
        Assert.Null(shapes[0].TextBody!.GetFirstChild<A.BodyProperties>()!.GetFirstChild<A.NormalAutoFit>()!.FontScale);
        output.WriteLine($"Scale {fit.FontScale.Value}");
    }

    [Fact]
    public async Task Into_arabic_a_deck_turns_right_to_left_and_a_centred_title_stays_centred()
    {
        var input = MakePptx("deck.pptx");
        var arabic = File_("deck.ar.pptx");
        await DocumentTranslator.TranslateAsync(input, arabic, "en", "ar", IntoArabic);
        using (var document = PresentationDocument.Open(arabic, false))
        {
            AssertValid(document);
            var slide = document.PresentationPart!.SlideParts.Single();
            var paragraphs = slide.Slide!.Descendants<A.Paragraph>().ToList();
            Assert.All(paragraphs, p => Assert.True(p.ParagraphProperties!.RightToLeft!.Value));
            // The layout centres the title, and the slide says nothing itself.
            Assert.Null(paragraphs[0].ParagraphProperties!.Alignment);
            // Nothing said in a plain box, and an explicit left: both on the right now.
            Assert.Equal(A.TextAlignmentTypeValues.Right, paragraphs[1].ParagraphProperties!.Alignment!.Value);
            Assert.Equal(A.TextAlignmentTypeValues.Right, paragraphs[2].ParagraphProperties!.Alignment!.Value);
            Assert.True(slide.Slide.Descendants<A.TableProperties>().Single().RightToLeft!.Value);
            Assert.True(slide.NotesSlidePart!.NotesSlide!.Descendants<A.Paragraph>().Single().ParagraphProperties!.RightToLeft!.Value);
        }
        var english = File_("deck.en.pptx");
        await DocumentTranslator.TranslateAsync(arabic, english, "ar", "en", IntoEnglish);
        using (var document = PresentationDocument.Open(english, false))
        {
            var slide = document.PresentationPart!.SlideParts.Single();
            var paragraphs = slide.Slide!.Descendants<A.Paragraph>().ToList();
            Assert.All(paragraphs, p => Assert.Null(p.ParagraphProperties?.RightToLeft));
            Assert.Equal(A.TextAlignmentTypeValues.Left, paragraphs[1].ParagraphProperties!.Alignment!.Value);
            Assert.Null(slide.Slide.Descendants<A.TableProperties>().Single().RightToLeft);
        }
    }

    // ---------- Excel ----------

    private string MakeXlsx(string name, bool rtl = false)
    {
        var path = File_(name);
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new S.Workbook(new S.Sheets());
        var strings = workbook.AddNewPart<SharedStringTablePart>();
        strings.SharedStringTable = new S.SharedStringTable(
            new S.SharedStringItem(new S.Text("Plain words")),
            new S.SharedStringItem(
                new S.Run(new S.RunProperties(new S.Bold(), new S.FontSize { Val = 14 }), new S.Text("Rich ") { Space = SpaceProcessingModeValues.Preserve }),
                new S.Run(new S.RunProperties(new S.Italic()), new S.Text("words"))),
            new S.SharedStringItem(new S.Text("Amount")));
        var sheet = workbook.AddNewPart<WorksheetPart>();
        sheet.Worksheet = new S.Worksheet(
            new S.SheetViews(new S.SheetView { WorkbookViewId = 0U, RightToLeft = rtl ? true : null }),
            new S.SheetData(
                new S.Row(
                    new S.Cell { CellReference = "A1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") },
                    new S.Cell { CellReference = "B1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("1") },
                    new S.Cell { CellReference = "C1", DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text("Inline words")) }) { RowIndex = 1U },
                new S.Row(
                    new S.Cell { CellReference = "A2", CellValue = new S.CellValue("42.5") },
                    new S.Cell { CellReference = "B2", CellValue = new S.CellValue("45000") },
                    new S.Cell { CellReference = "C2", CellFormula = new S.CellFormula("SUM(A2:B2)"), CellValue = new S.CellValue("45042.5") },
                    new S.Cell { CellReference = "D2", DataType = S.CellValues.String, CellFormula = new S.CellFormula("\"Formula words\""), CellValue = new S.CellValue("Formula words") }) { RowIndex = 2U },
                new S.Row(
                    new S.Cell { CellReference = "F3", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("2") }) { RowIndex = 3U }),
            new S.TableParts(new S.TablePart { Id = "rIdTable" }) { Count = 1U });
        var table = sheet.AddNewPart<TableDefinitionPart>("rIdTable");
        table.Table = new S.Table(new S.AutoFilter { Reference = "F3:F4" }, new S.TableColumns(new S.TableColumn { Id = 1U, Name = "Amount" }) { Count = 1U })
        { Id = 1U, Name = "T1", DisplayName = "T1", Reference = "F3:F4", TotalsRowShown = false };
        workbook.Workbook.GetFirstChild<S.Sheets>()!.Append(new S.Sheet { Id = workbook.GetIdOfPart(sheet), SheetId = 1U, Name = "Sales" });
        return path;
    }

    [Fact]
    public async Task A_workbook_has_its_words_translated_and_its_numbers_and_formulas_left()
    {
        var input = MakeXlsx("book.xlsx");
        var result = File_("book.es.xlsx");
        await DocumentTranslator.TranslateAsync(input, result, "en", "es", Marked);
        using var document = SpreadsheetDocument.Open(result, false);
        AssertValid(document);
        var workbook = document.WorkbookPart!;
        var strings = workbook.SharedStringTablePart!.SharedStringTable!.Elements<S.SharedStringItem>().ToList();
        Assert.Equal("[[Plain words]]", strings[0].InnerText);
        // A rich entry collapses into its first run.
        var run = strings[1].Elements<S.Run>().Single();
        Assert.Equal("[[Rich words]]", run.Text!.Text);
        Assert.NotNull(run.RunProperties!.GetFirstChild<S.Bold>());
        // A table's heading must match the table's own column name.
        Assert.Equal("Amount", strings[2].InnerText);
        Assert.Equal("Amount", workbook.WorksheetParts.Single().TableDefinitionParts.Single().Table!.TableColumns!.Elements<S.TableColumn>().Single().Name!.Value);

        var cells = workbook.WorksheetParts.Single().Worksheet!.Descendants<S.Cell>().ToDictionary(c => c.CellReference!.Value!);
        Assert.Equal("[[Inline words]]", cells["C1"].InlineString!.InnerText);
        Assert.Equal("42.5", cells["A2"].CellValue!.Text);
        Assert.Equal("45000", cells["B2"].CellValue!.Text);
        Assert.Equal("SUM(A2:B2)", cells["C2"].CellFormula!.Text);
        Assert.Equal("45042.5", cells["C2"].CellValue!.Text);
        Assert.Equal("\"Formula words\"", cells["D2"].CellFormula!.Text);
        Assert.Equal("Formula words", cells["D2"].CellValue!.Text);
        Assert.Equal("Sales", workbook.Workbook!.GetFirstChild<S.Sheets>()!.Elements<S.Sheet>().Single().Name!.Value);
        Assert.Null(workbook.WorksheetParts.Single().Worksheet!.Descendants<S.SheetView>().Single().RightToLeft);
    }

    [Fact]
    public async Task Into_arabic_a_sheet_reads_from_the_right_and_back_out_again()
    {
        var input = MakeXlsx("book.xlsx");
        var arabic = File_("book.ar.xlsx");
        await DocumentTranslator.TranslateAsync(input, arabic, "en", "ar", IntoArabic);
        using (var document = SpreadsheetDocument.Open(arabic, false))
            Assert.True(document.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<S.SheetView>().Single().RightToLeft!.Value);
        var english = File_("book.en.xlsx");
        await DocumentTranslator.TranslateAsync(arabic, english, "ar", "en", IntoEnglish);
        using (var document = SpreadsheetDocument.Open(english, false))
            Assert.Null(document.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<S.SheetView>().Single().RightToLeft);
    }

    // ---------- PDF ----------

    /// <summary>A page with a title, a paragraph of wrapped lines, a dark box with white words on it, a number and a picture.</summary>
    private string MakePdf(string name)
    {
        var path = File_(name);
        Petal.Engines.Documents.Pdf.PdfFonts.EnsureInitialized();
        using var document = new PdfSharp.Pdf.PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(240, 230, 140)), 40, 400, 300, 40);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(20, 40, 120)), 40, 470, 300, 40);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(200, 30, 30)), 400, 400, 100, 100);
            var title = new XFont("Arial", 20, XFontStyleEx.Bold);
            var body = new XFont("Arial", 12);
            gfx.DrawString("The annual report", title, new XSolidBrush(XColor.FromArgb(0, 80, 160)), 60, 80);
            string[] lines =
            [
                "This is the first line of a paragraph that runs across the whole width of",
                "its column, and the second line goes on with the same sentence until it too",
                "comes to the end, where the third line ends the paragraph in a short while.",
            ];
            for (int i = 0; i < lines.Length; i++)
                gfx.DrawString(lines[i], body, XBrushes.Black, 60, 130 + i * 15);
            gfx.DrawString("Words on a yellow background", body, XBrushes.Black, 50, 425);
            gfx.DrawString("White words on a dark box", body, XBrushes.White, 50, 495);
            gfx.DrawString("2024", body, XBrushes.Black, 60, 600);
        }
        document.Save(path);
        return path;
    }

    private static string PageText(string path, int page = 1)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Join(' ', pdf.GetPage(page).GetWords().Select(w => w.Text));
    }

    /// <summary>The first letter of each place a word was drawn. By letters: words over words are one jumble to a word reader.</summary>
    private static List<UglyToad.PdfPig.Content.Letter> Found(UglyToad.PdfPig.Content.Page page, string word)
    {
        var letters = page.Letters.ToList();
        var all = string.Concat(letters.Select(l => l.Value.Length == 1 ? l.Value : "?"));
        return System.Text.RegularExpressions.Regex.Matches(all, word).Select(m => letters[m.Index]).ToList();
    }

    [Fact]
    public async Task A_pdf_gets_its_translation_in_place_with_its_page_kept()
    {
        var input = MakePdf("paper.pdf");
        var before = File.ReadAllBytes(input);
        var result = File_("paper.es.pdf");
        await DocumentTranslator.TranslateAsync(input, result, "en", "es", (lines, _, _) =>
            Task.FromResult<IReadOnlyList<string>>(lines.Select(_ => "Traducido palabra").ToList()));
        Assert.Equal(before, File.ReadAllBytes(input));

        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(result))
        {
            Assert.Equal(1, pdf.NumberOfPages);
            var page = pdf.GetPage(1);
            Assert.Equal(595, page.Width, 0);
            Assert.Equal(842, page.Height, 0);
            // The translation of the title is where the title was; the three lines are one paragraph, translated as one.
            var found = Found(page, "Traducido");
            Assert.Equal(4, found.Count);
            var original = page.Letters.First(l => l.Value == "T").GlyphRectangle;
            var title = found.OrderByDescending(l => l.GlyphRectangle.Top).First().GlyphRectangle;
            Assert.Equal(original.Left, title.Left, 0);
            Assert.InRange(title.Bottom, original.Bottom - 3, original.Top);
            // The number is not touched, once; the original words are still there, under the cover.
            var all = string.Concat(page.Letters.Select(l => l.Value));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(all, "2024"));
            Assert.Contains("annual", all);
        }
        // What is seen is the translation in its own colours, on its own background; the picture of the page tells.
        var picture = Petal.Engines.Documents.Pdf.PdfInfo.RenderPage(result, 0, 595);
        (byte R, byte G, byte B) Pixel(int x, int y)
        {
            int at = y * picture.Stride + x * 4;
            return (picture.Pixels[at + 2], picture.Pixels[at + 1], picture.Pixels[at]);
        }
        // The yellow background stays yellow right up to and inside the cover.
        Assert.Equal((240, 230, 140), Pixel(335, 420));
        Assert.Equal((240, 230, 140), Pixel(200, 430));
        // The dark box stays dark, with light words drawn on it; the red square isn't touched.
        Assert.Equal((20, 40, 120), Pixel(300, 490));
        Assert.Equal((200, 30, 30), Pixel(450, 450));
        Assert.True(Enumerable.Range(50, 80).SelectMany(x => Enumerable.Range(483, 14).Select(y => Pixel(x, y))).Any(p => p.R > 200 && p.G > 200 && p.B > 200), "No light words on the dark box");
    }

    [Fact]
    public async Task A_pdf_into_arabic_is_drawn_shaped_and_right_aligned_inside_its_rectangle()
    {
        var input = MakePdf("paper.pdf");
        var result = File_("paper.ar.pdf");
        // A long translation, to see it shrink and wrap rather than run out of its rectangle.
        await DocumentTranslator.TranslateAsync(input, result, "en", "ar", (lines, _, _) =>
            Task.FromResult<IReadOnlyList<string>>(lines.Select(_ => string.Concat(Enumerable.Repeat(Arabic + " ", 6)).Trim()).ToList()));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(result);
        var page = pdf.GetPage(1);
        var drawn = page.Letters.Where(l => l.Value.Length > 0 && (l.Value[0] is >= (char)0xFB50 and <= (char)0xFEFF)).ToList();
        Assert.True(drawn.Count > 100, $"{drawn.Count} shaped letters");
        // The paragraph's rectangle: its original lines, on the page.
        var original = page.GetWords().Where(w => w.Text is "This" or "column," or "comes" or "width" or "too" or "while.").ToList();
        double top = original.Max(w => w.BoundingBox.Top), bottom = original.Min(w => w.BoundingBox.Bottom), right = original.Max(w => w.BoundingBox.Right), left = original.Min(w => w.BoundingBox.Left);
        var inside = drawn.Where(l => l.Location.Y < top + 2 && l.Location.Y > bottom - 4).ToList();
        Assert.NotEmpty(inside);
        Assert.InRange(inside.Max(l => l.GlyphRectangle.Right), right - 8, right + 2);
        Assert.True(inside.Min(l => l.GlyphRectangle.Left) >= left - 2, "Over the left edge");
        // Smaller than its own size, but not below the readable least.
        Assert.InRange(inside[0].PointSize, 6, 12);
    }

    [Fact]
    public async Task A_rotated_pdf_page_gets_its_translation_upright_where_its_words_are()
    {
        var path = File_("turned.pdf");
        Petal.Engines.Documents.Pdf.PdfFonts.EnsureInitialized();
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(400);
            page.Height = XUnit.FromPoint(600);
            page.Rotate = 90;
            using (var gfx = Petal.Engines.Documents.Pdf.PdfStamper.Upright(page))
                gfx.DrawString("Sideways page words", new XFont("Arial", 14), XBrushes.Black, 50, 100);
            document.Save(path);
        }
        var result = File_("turned.es.pdf");
        await DocumentTranslator.TranslateAsync(path, result, "en", "es", (lines, _, _) => Task.FromResult<IReadOnlyList<string>>(lines.Select(_ => "Palabras").ToList()));
        double x, y;
        using (var original = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            var word = original.GetPage(1).GetWords().First();
            (x, y) = (word.BoundingBox.Left, word.BoundingBox.Bottom);
        }
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(result);
        var page2 = pdf.GetPage(1);
        Assert.Equal(90, page2.Rotation.Value);
        // As the page is displayed: 600 wide, the words at the same place, reading from left to right.
        Assert.Equal(600, page2.Width, 0);
        var drawn = Found(page2, "Palabras").Single();
        Assert.Equal(UglyToad.PdfPig.Content.TextOrientation.Horizontal, drawn.TextOrientation);
        Assert.InRange(drawn.GlyphRectangle.Left, x - 1, x + 2);
        Assert.InRange(drawn.GlyphRectangle.Bottom, y - 4, y + 4);
    }

    [Fact]
    public async Task A_pdf_with_no_words_says_so_and_leaves_no_file()
    {
        var path = File_("empty.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            document.AddPage();
            document.Save(path);
        }
        var result = File_("empty.es.pdf");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DocumentTranslator.TranslateAsync(path, result, "en", "es", Marked));
        Assert.Equal("This file has no text to translate.", error.Message);
        Assert.False(File.Exists(result));
    }

    [Fact]
    public void Urdu_and_persian_letters_are_shaped_for_drawing()
    {
        foreach (var words in new[] { "ٹھیک ہے اور بڑے لوگ نہیں", "پاسخ نگاه ژرف" })
        {
            var shaped = Petal.Engines.Documents.Text.Bidi.Shape(words);
            Assert.DoesNotContain(shaped, c => c is >= (char)0x0600 and <= (char)0x06FF);
        }
    }

    // ---------- all kinds ----------

    [Fact]
    public async Task Cancelling_leaves_no_file_and_the_original_as_it_was()
    {
        foreach (var input in new[] { MakeDocx("a.docx"), MakePptx("b.pptx"), MakeXlsx("c.xlsx"), MakePdf("d.pdf") })
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var before = File.ReadAllBytes(input);
            var result = input + ".es" + Path.GetExtension(input);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentTranslator.TranslateAsync(input, result, "en", "es", Marked, null, cancel.Token));
            Assert.False(File.Exists(result));
            Assert.Equal(before, File.ReadAllBytes(input));
            Assert.Empty(Directory.GetFiles(_folder, "*.petal-tmp"));
        }
    }

    [Fact]
    public async Task The_original_is_never_written_over()
    {
        var input = MakeDocx("report.docx");
        await Assert.ThrowsAsync<ArgumentException>(() => DocumentTranslator.TranslateAsync(input, input, "en", "es", Marked));
    }

    [Fact]
    public async Task The_language_is_told_from_the_file_when_asked()
    {
        var path = File_("arabic.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            document.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.ParagraphProperties(new W.BiDi()), TextRun("سيبدأ الاجتماع صباح غد والجميع مدعوون إلى المكتب الجديد."))));
        Assert.Equal("ar", Petal.Core.Text.LanguageGuess.Of(DocumentTranslator.FirstText(path)));
        var result = File_("arabic.en.docx");
        await DocumentTranslator.TranslateAsync(path, result, "auto", "en", IntoEnglish);
        using var translated = WordprocessingDocument.Open(result, false);
        Assert.Empty(translated.MainDocumentPart!.Document!.Descendants<W.BiDi>());
        Assert.Contains("Annual", DocumentTranslator.FirstText(MakePptxTitled()));
    }

    private string MakePptxTitled() => MakePptx("titled.pptx", slide => slide.Descendants<A.Text>().First().Text = "Annual report");

    // ---------- sentences ----------

    [Fact]
    public async Task Sentences_are_translated_apart_and_put_together_and_repeats_only_once()
    {
        var seen = new List<string>();
        var result = await DocumentTranslation.BySentenceAsync(
            ["First sentence. Second sentence!", "First sentence.", "Line one\nLine two\tcolumn", "12", "First sentence. First sentence."],
            (pieces, _, _) =>
            {
                seen.AddRange(pieces);
                return Task.FromResult<IReadOnlyList<string>>(pieces.Select(p => "<" + p + ">").ToList());
            }, null, default);
        Assert.Equal(["First sentence.", "Second sentence!", "Line one", "Line two", "column"], seen);
        Assert.Equal("<First sentence.> <Second sentence!>", result[0]);
        Assert.Equal("<First sentence.>", result[1]);
        Assert.Equal("<Line one>\n<Line two>\t<column>", result[2]);
        Assert.Equal("12", result[3]);
        Assert.Equal("<First sentence.> <First sentence.>", result[4]);
    }

    [Fact]
    public void A_sentence_too_long_for_the_model_is_parted_at_its_clauses_and_words()
    {
        var clauses = string.Join(", ", Enumerable.Repeat("a clause of a few words that goes on for a while", 20));
        Assert.All(DocumentTranslation.Pieces(clauses), p => Assert.InRange(p.Length, 1, 400));
        var words = string.Join(' ', Enumerable.Repeat("word", 500));
        var pieces = DocumentTranslation.Pieces(words);
        Assert.True(pieces.Count >= 6);
        Assert.All(pieces, p => Assert.InRange(p.Length, 1, 400));
        Assert.Equal(words, string.Join(' ', pieces));
    }

    // ---------- the real models ----------

    /// <summary>Real models, from the folder PETAL_TRANSLATE names; the results are kept in PETAL_TRANSLATE_OUT for looking at.</summary>
    [Fact]
    public async Task Real_documents_with_real_models()
    {
        if (Environment.GetEnvironmentVariable("PETAL_TRANSLATE") is not { Length: > 0 } folder)
        {
            output.WriteLine("Skipped: set PETAL_TRANSLATE to a folder for the models.");
            return;
        }
        var keep = Environment.GetEnvironmentVariable("PETAL_TRANSLATE_OUT") is { Length: > 0 } wanted ? wanted : _folder;
        Directory.CreateDirectory(keep);
        var before = ModelStore.Folder;
        ModelStore.Folder = folder;
        try
        {
            var english = RealDocx("real.docx",
                "Annual report",
                "The company grew quickly last year. Sales rose by twelve percent in Europe, and the new office opened in the spring.",
                "Everyone who works here should read the report before the meeting.");
            await Real(english, "en", "es", keep);
            await Real(english, "en", "ar", keep);
            var arabic = RealDocx("real-ar.docx",
                "التقرير السنوي",
                "نمت الشركة بسرعة في العام الماضي. وارتفعت المبيعات في أوروبا، وافتتح المكتب الجديد في الربيع.",
                "يجب على كل من يعمل هنا أن يقرأ التقرير قبل الاجتماع.");
            await Real(arabic, "ar", "en", keep);

            var deck = MakePptx("real.pptx");
            await Real(deck, "en", "ar", keep);
            await Real(deck, "en", "es", keep);
            await Real(MakeXlsx("real.xlsx"), "en", "es", keep);

            var pdf = MakePdf("real.pdf");
            foreach (var to in new[] { "es", "ar", "fa", "ur" })
                await Real(pdf, "en", to, keep);
            var report = RichPdf("real-report.pdf");
            await Real(report, "en", "es", keep);
            await Real(report, "en", "ar", keep);
            File.Copy(report, Path.Combine(keep, Path.GetFileName(report)), overwrite: true);
            File.WriteAllBytes(Path.Combine(keep, Path.GetFileName(report) + ".png"), Petal.Engines.Documents.Pdf.PdfInfo.RenderPagePng(report, 0, 1100));
        }
        finally
        {
            ModelStore.Folder = before;
        }
    }

    /// <summary>
    /// The real thing. When the English to Arabic pair isn't in the folder, English goes through French and M2M100 instead,
    /// which is real translation all the same and draws the same Arabic. A PDF, or a document laid out by the app's own PDF writer (no Office), is saved as a picture too.
    /// </summary>
    private async Task Real(string input, string from, string to, string keep)
    {
        var name = Path.GetFileNameWithoutExtension(input) + "." + to + Path.GetExtension(input);
        var result = Path.Combine(keep, name);
        File.Delete(result);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool direct = Translator.ModelsFor(from, to).All(ModelStore.IsAvailable);
        if (direct)
            await DocumentTranslation.TranslateAsync(input, result, from, to);
        else
        {
            Assert.Equal(("en", "ar"), (from, to));
            await DocumentTranslator.TranslateAsync(input, result, from, to, (lines, _, ct) => DocumentTranslation.BySentenceAsync(lines,
                (pieces, _, c) => Task.FromResult(Translator.Lines(Translator.Lines(pieces, "en", "fr", null, c), "fr", "ar", null, c)), null, ct));
        }
        var text = DocumentTranslator.FirstText(result);
        output.WriteLine($"{name} ({(direct ? "direct" : "through French")}) in {clock.Elapsed.TotalSeconds:0.0}s: {text}");
        Assert.True(File.Exists(result));
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual(DocumentTranslator.FirstText(input), text);
        if (name.EndsWith(".pdf", StringComparison.Ordinal))
            File.WriteAllBytes(result + ".png", Petal.Engines.Documents.Pdf.PdfInfo.RenderPagePng(result, 0, 1100));
        else if (name.EndsWith(".docx", StringComparison.Ordinal))
        {
            var laidOut = result + ".pdf";
            File.Delete(laidOut);
            Petal.Engines.Documents.Converters.DocumentIO.Write(Petal.Engines.Documents.Converters.DocumentIO.Read(result, Petal.Core.Formats.Formats.Docx), Petal.Core.Formats.Formats.Pdf, laidOut);
            File.WriteAllBytes(result + ".png", Petal.Engines.Documents.Pdf.PdfInfo.RenderPagePng(laidOut, 0, 800));
        }
    }

    /// <summary>A report with headings, paragraphs, a list, a table and two columns of text, laid out as a real page is.</summary>
    private string RichPdf(string name)
    {
        Petal.Engines.Documents.Pdf.PdfFonts.EnsureInitialized();
        var path = File_(name);
        using var document = new PdfSharp.Pdf.PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var heading = new XFont("Calibri", 22, XFontStyleEx.Bold);
            var section = new XFont("Calibri", 14, XFontStyleEx.Bold);
            var body = new XFont("Times New Roman", 11);
            var cell = new XFont("Calibri", 10);
            var blue = new XSolidBrush(XColor.FromArgb(20, 60, 130));
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(20, 60, 130)), 0, 0, 595, 70);
            gfx.DrawString("Quarterly business review", heading, XBrushes.White, 50, 45);
            gfx.DrawString("Market overview", section, blue, 50, 105);
            string[] paragraph =
            [
                "The market grew steadily during the third quarter, helped by strong demand in",
                "Europe and by lower shipping costs. Our sales team opened two new regional",
                "offices, and customers responded well to the new product line.",
            ];
            for (int i = 0; i < paragraph.Length; i++)
                gfx.DrawString(paragraph[i], body, XBrushes.Black, 50, 125 + i * 14);
            gfx.DrawString("Main results", section, blue, 50, 190);
            string[] list = ["Revenue increased by twelve percent.", "Costs fell in every region.", "We hired forty new employees."];
            for (int i = 0; i < list.Length; i++)
                gfx.DrawString("•  " + list[i], body, XBrushes.Black, 60, 210 + i * 16);
            string[][] rows = [["Region", "Sales", "Growth"], ["Europe", "4,200", "12%"], ["Middle East", "1,850", "9%"], ["North America", "3,100", "7%"]];
            for (int r = 0; r < rows.Length; r++)
            {
                if (r == 0)
                    gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(225, 232, 245)), 50, 275, 495, 18);
                gfx.DrawLine(XPens.Gray, 50, 293 + r * 18, 545, 293 + r * 18);
                for (int c = 0; c < 3; c++)
                    gfx.DrawString(rows[r][c], r == 0 ? new XFont("Calibri", 10, XFontStyleEx.Bold) : cell, XBrushes.Black, 55 + c * 165, 288 + r * 18);
            }
            gfx.DrawString("Outlook", section, blue, 50, 385);
            string[] left = ["We expect demand to stay strong", "through the winter, although prices", "for raw materials may rise again.", "", "The board will review the budget at", "its next meeting in January."];
            string[] right = ["New products will reach the market", "in the spring, and two more offices", "will open in the same period.", "", "Please send your comments to the", "finance team before the end of the month."];
            for (int i = 0; i < left.Length; i++)
            {
                gfx.DrawString(left[i], body, XBrushes.Black, 50, 405 + i * 14);
                gfx.DrawString(right[i], body, XBrushes.Black, 310, 405 + i * 14);
            }
            gfx.DrawString("Page 1", new XFont("Calibri", 9), XBrushes.Gray, 280, 810);
        }
        document.Save(path);
        return path;
    }

    private string RealDocx(string name, string title, params string[] paragraphs)
    {
        var path = File_(name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var body = new W.Body(new W.Paragraph(TextRun(title, new W.RunProperties(new W.Bold(), new W.FontSize { Val = "40" }))));
        foreach (var words in paragraphs)
            body.Append(new W.Paragraph(TextRun(words)));
        document.AddMainDocumentPart().Document = new W.Document(body);
        return path;
    }
}
