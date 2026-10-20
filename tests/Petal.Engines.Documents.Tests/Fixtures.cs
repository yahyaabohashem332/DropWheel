using System.Diagnostics;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MD = MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Office;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Documents.Sheets;
using Petal.Engines.Documents.Text;
using SkiaSharp;
using F = Petal.Core.Formats.Formats;
using M = Petal.Engines.Documents.Model;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Petal.Engines.Documents.Tests;

/// <summary>Sample files of every input format, generated once per test run in a temp folder.</summary>
public sealed class Fixtures
{
    private static readonly Lazy<Fixtures> Shared = new(() => new Fixtures(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static Fixtures Instance => Shared.Value;

    /// <summary>A word every document fixture contains, so outputs can be checked for it.</summary>
    public const string Token = "Petal";

    public string Dir { get; }

    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Lock _gate = new();

    private Fixtures()
    {
        var root = Path.Combine(Path.GetTempPath(), "petal-doc-tests");
        // Outputs of the latest runs stay for inspection; older ones are removed.
        if (Directory.Exists(root))
            foreach (var old in Directory.EnumerateDirectories(root).Where(d => Directory.GetCreationTime(d) < DateTime.Now.AddDays(-1)))
                try { Directory.Delete(old, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Dir);
        PdfFonts.EnsureInitialized();

        _files["png-image"] = Save("picture.png", SamplePng());
        _files["docx"] = Path.Combine(Dir, "sample.docx");
        BuildDocx(_files["docx"]);
        _files["pdf"] = Path.Combine(Dir, "sample.pdf");
        BuildPdf(_files["pdf"]);
        _files["scanned"] = Path.Combine(Dir, "scanned.pdf");
        BuildScannedPdf(_files["scanned"]);
        _files["xlsx"] = Path.Combine(Dir, "sample.xlsx");
        BuildXlsx(_files["xlsx"]);
        _files["csv"] = Save("sample.csv", TextFile.Utf8Bom.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(Csv)).ToArray());
        _files["tsv"] = Save("sample.tsv", System.Text.Encoding.UTF8.GetBytes(Tsv));
        _files["ods"] = Path.Combine(Dir, "sample.ods");
        OdsSheets.Write(DelimitedText.Read(_files["csv"], ','), _files["ods"]);
        _files["md"] = Save("sample.md", System.Text.Encoding.UTF8.GetBytes(Markdown));
        _files["html"] = Save("sample.html", System.Text.Encoding.UTF8.GetBytes(Html));
        _files["txt"] = Save("sample.txt", System.Text.Encoding.UTF8.GetBytes(PlainText));
        var rich = RichModel();
        _files["odt"] = Path.Combine(Dir, "sample.odt");
        OdtWriter.Write(rich, _files["odt"]);
        _files["rtf"] = Path.Combine(Dir, "sample.rtf");
        RtfWriter.Write(rich, _files["rtf"]);
        _files["epub"] = Path.Combine(Dir, "sample.epub");
        Epub.Write(rich, _files["epub"]);
        _files["pptx"] = Path.Combine(Dir, "sample.pptx");
        PdfToPptx.WriteAsync(_files["pdf"], _files["pptx"], PdfToPptxMode.EditableText, 110, 85, new PdfContentOptions { Ocr = false }, null, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The fixture for a format, or null when it cannot be made on this machine (legacy Office formats need Office).</summary>
    public string? For(FileFormat format)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(format.Id, out var path))
                return path;
            string? made = format.Id switch
            {
                "xls" => MakeLegacy(OfficeApp.Excel, _files["xlsx"], "sample.xls", 56),
                "ppt" => MakeLegacy(OfficeApp.PowerPoint, _files["pptx"], "sample.ppt", 1),
                _ => null,
            };
            if (made is not null)
                _files[format.Id] = made;
            return made;
        }
    }

    public string Get(string key) => _files[key];

    private string? MakeLegacy(OfficeApp app, string source, string name, int fileFormat)
    {
        if (!OfficeAutomation.IsInstalled(app))
            return null;
        var path = Path.Combine(Dir, name);
        OfficeAutomation.SaveAsAsync(app, source, path, fileFormat, TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
        return File.Exists(path) ? path : null;
    }

    /// <summary>A fresh empty folder for one test's outputs.</summary>
    public string NewOutputDir(string name)
    {
        var dir = Path.Combine(Dir, "out", name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Copies a fixture into its own folder so outputs land beside it, like in the app.</summary>
    public string CopyTo(string fixture, string dir)
    {
        var target = Path.Combine(dir, Path.GetFileName(fixture));
        File.Copy(fixture, target);
        return target;
    }

    private string Save(string name, byte[] bytes)
    {
        var path = Path.Combine(Dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ───────────────────────────── content ─────────────────────────────

    public const string Csv = "Product,Quantity,Price,Note\r\n\"Petal pen\",12,1.5,blue\r\n\"Ink, black\",7,0.99,\"line one\nline two\"\r\nمرحبا,3,4.25,\"He said \"\"hi\"\"\"\r\n007,0,-2,  padded  \r\n";

    public const string Tsv = "Product\tQuantity\tPrice\r\nPetal pen\t12\t1.5\r\nInk\t7\t0.99\r\n";

    public const string Markdown = """
        # Petal Markdown Guide

        Intro paragraph with **bold**, *italic* and a [link](https://example.com).

        ## Shopping list

        - Apples
        - Bananas
          - Green ones
        - Cherries

        1. First step
        2. Second step

        | Name | Count |
        | ---- | ----- |
        | Petal | 3 |
        | Leaf | 5 |

        > A quoted line.

        ```
        code block
        ```
        """;

    public const string Html = """
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>Petal HTML Page</title></head>
        <body>
        <h1>Petal HTML Page</h1>
        <p>Some <b>bold</b> and <i>italic</i> text with <a href="https://example.com">a link</a>.</p>
        <ul><li>One</li><li>Two<ul><li>Two point one</li></ul></li></ul>
        <ol start="3"><li>Third</li><li>Fourth</li></ol>
        <table><thead><tr><th>City</th><th>Petals</th></tr></thead><tbody><tr><td>Paris</td><td>12</td></tr><tr><td colspan="2">Merged</td></tr></tbody></table>
        <p dir="rtl">مرحبا بالعالم</p>
        </body></html>
        """;

    public const string PlainText = "Petal plain text\n\nFirst paragraph line one\nline two of the same paragraph.\n\nSecond paragraph.\n";

    internal static DocModel RichModel()
    {
        var doc = new DocModel();
        doc.Metadata.Title = "Petal Rich Document";
        doc.Metadata.Author = "Petal Tests";
        doc.Blocks.Add(Heading(1, "Petal Rich Document"));
        var p = new M.Paragraph();
        p.Inlines.Add(new TextRun("Plain, "));
        p.Inlines.Add(new TextRun("bold", TextStyle.Bold));
        p.Inlines.Add(new TextRun(", "));
        p.Inlines.Add(new TextRun("italic", TextStyle.Italic));
        p.Inlines.Add(new TextRun(" and "));
        p.Inlines.Add(new TextRun("linked", TextStyle.None, "https://example.com/petal"));
        p.Inlines.Add(new TextRun(" text."));
        doc.Blocks.Add(p);
        doc.Blocks.Add(Heading(2, "Lists"));
        var bullets = new M.ListBlock(false);
        foreach (var t in new[] { "Alpha", "Beta", "Gamma" })
        {
            var item = new M.ListItem();
            item.Blocks.Add(Para(t));
            bullets.Items.Add(item);
        }
        var nested = new M.ListBlock(true);
        nested.Items.Add(new M.ListItem { Blocks = { Para("Beta one") } });
        bullets.Items[1].Blocks.Add(nested);
        doc.Blocks.Add(bullets);
        var numbered = new M.ListBlock(true);
        numbered.Items.Add(new M.ListItem { Blocks = { Para("Step one") } });
        numbered.Items.Add(new M.ListItem { Blocks = { Para("Step two") } });
        doc.Blocks.Add(numbered);
        doc.Blocks.Add(Heading(2, "Table"));
        var table = new M.Table();
        table.Rows.Add(Row(true, "Name", "Value"));
        table.Rows.Add(Row(false, "Petal", "42"));
        table.Rows.Add(Row(false, "Stem", "7"));
        doc.Blocks.Add(table);
        if (ImageData.Create(SamplePng()) is { } image)
            doc.Blocks.Add(new ImageBlock(image));
        doc.Blocks.Add(new M.PageBreak());
        doc.Blocks.Add(Heading(2, "Second page"));
        doc.Blocks.Add(Para("The end."));
        return doc;
    }

    internal static Heading Heading(int level, string text)
    {
        var h = new Heading(level);
        h.Inlines.Add(new TextRun(text));
        return h;
    }

    internal static M.Paragraph Para(string text)
    {
        var p = new M.Paragraph();
        p.Inlines.Add(new TextRun(text));
        return p;
    }

    private static M.TableRow Row(bool header, params string[] cells)
    {
        var row = new M.TableRow { IsHeader = header };
        foreach (var c in cells)
            row.Cells.Add(new M.TableCell { Blocks = { Para(c) } });
        return row;
    }

    internal static byte[] SamplePng()
    {
        using var bitmap = new SKBitmap(120, 80);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = new SKColor(230, 120, 40), IsAntialias = true };
            canvas.DrawCircle(40, 40, 30, paint);
            paint.Color = new SKColor(40, 120, 200);
            canvas.DrawRect(70, 15, 40, 50, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>A Word document built directly with the Open XML SDK: styles, numbering, a table and a picture.</summary>
    private static void BuildDocx(string path)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new Style(new StyleName { Val = "heading 1" }, new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(new OutlineLevel { Val = 0 }), new StyleRunProperties(new Bold(), new FontSize { Val = "40" }))
            { Type = StyleValues.Paragraph, StyleId = "Heading1" },
            new Style(new StyleName { Val = "heading 2" }, new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(new OutlineLevel { Val = 1 }), new StyleRunProperties(new Bold(), new FontSize { Val = "32" }))
            { Type = StyleValues.Paragraph, StyleId = "Heading2" });
        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        numbering.Numbering = new Numbering(
            new AbstractNum(new Level(new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "•" }) { LevelIndex = 0 },
                new Level(new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "o" }) { LevelIndex = 1 }) { AbstractNumberId = 1 },
            new AbstractNum(new Level(new NumberingFormat { Val = NumberFormatValues.Decimal }, new LevelText { Val = "%1." }) { LevelIndex = 0 }) { AbstractNumberId = 2 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
            new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 });

        W.Paragraph P(string text, string? style = null, int? numId = null, int level = 0)
        {
            var pPr = new ParagraphProperties();
            if (style is not null) pPr.ParagraphStyleId = new ParagraphStyleId { Val = style };
            if (numId is not null) pPr.NumberingProperties = new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = numId });
            return new W.Paragraph(pPr, new Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        }

        var body = new Body(
            P("Petal Word Report", "Heading1"),
            new W.Paragraph(
                new Run(new W.Text("A paragraph with ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new RunProperties(new Bold()), new W.Text("bold")),
                new Run(new W.Text(" and ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new RunProperties(new Italic()), new W.Text("italic")),
                new Run(new W.Text(" words.") { Space = SpaceProcessingModeValues.Preserve })),
            P("Fruit", "Heading2"),
            P("Apple", numId: 1),
            P("Banana", numId: 1),
            P("Green banana", numId: 1, level: 1),
            P("Cherry", numId: 1),
            P("Steps", "Heading2"),
            P("Wash", numId: 2),
            P("Slice", numId: 2),
            new W.Table(
                new TableProperties(new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })),
                new TableGrid(new GridColumn { Width = "3000" }, new GridColumn { Width = "3000" }),
                new W.TableRow(new TableRowProperties(new TableHeader()), Cell("Item"), Cell("Count")),
                new W.TableRow(Cell("Petal"), Cell("5")),
                new W.TableRow(Cell("Leaf"), Cell("9"))),
            P("Closing paragraph."),
            new SectionProperties(new W.PageSize { Width = 11906, Height = 16838 }, new PageMargin { Top = 1440, Bottom = 1440, Left = 1440, Right = 1440 }));

        // A picture, the way Word stores one.
        var imagePart = main.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(SamplePng()))
            imagePart.FeedData(ms);
        var relId = main.GetIdOfPart(imagePart);
        var drawing = new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = 1143000, Cy = 762000 },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1U, Name = "Picture 1", Description = "shapes" },
            new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(
                new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                    new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                        new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = 0U, Name = "shapes.png" },
                        new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
                    new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(new DocumentFormat.OpenXml.Drawing.Blip { Embed = relId }, new DocumentFormat.OpenXml.Drawing.Stretch(new DocumentFormat.OpenXml.Drawing.FillRectangle())),
                    new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(
                        new DocumentFormat.OpenXml.Drawing.Transform2D(new DocumentFormat.OpenXml.Drawing.Offset { X = 0, Y = 0 }, new DocumentFormat.OpenXml.Drawing.Extents { Cx = 1143000, Cy = 762000 }),
                        new DocumentFormat.OpenXml.Drawing.PresetGeometry { Preset = DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
        body.InsertBefore(new W.Paragraph(new Run(drawing)), body.Elements<SectionProperties>().First());

        main.Document = new W.Document(body);
        package.PackageProperties.Title = "Petal Word Report";
        main.Document.Save();

        static W.TableCell Cell(string text) => new(new W.Paragraph(new Run(new W.Text(text))));
    }

    /// <summary>A text PDF made with MigraDoc: headings, lists, a table and generous spacing.</summary>
    private static void BuildPdf(string path)
    {
        var document = new MD.Document();
        document.Info.Title = "Quarterly Petal Report";
        document.Info.Author = "Petal Tests";
        var normal = document.Styles[MD.StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 11;
        normal.ParagraphFormat.SpaceAfter = 6;
        var section = document.AddSection();
        section.PageSetup.PageFormat = MD.PageFormat.A4;

        var title = section.AddParagraph("Quarterly Petal Report");
        title.Format.Font.Size = 22;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = 12;
        section.AddParagraph("This report summarises how the petals grew during the quarter. It has several sentences so that the paragraph wraps across more than one line on the page.");

        var h2 = section.AddParagraph("Highlights");
        h2.Format.Font.Size = 16;
        h2.Format.Font.Bold = true;
        h2.Format.SpaceBefore = 10;
        foreach (var item in new[] { "Growth was strong", "Colours were bright", "Stems held firm" })
        {
            var li = section.AddParagraph();
            li.Format.LeftIndent = MD.Unit.FromPoint(28);
            li.Format.FirstLineIndent = MD.Unit.FromPoint(-14);
            li.AddText("•");
            li.AddTab();
            li.AddText(item);
            li.Format.TabStops.AddTabStop(MD.Unit.FromPoint(28));
        }
        var steps = section.AddParagraph("Next steps");
        steps.Format.Font.Size = 16;
        steps.Format.Font.Bold = true;
        steps.Format.SpaceBefore = 10;
        for (var i = 1; i <= 2; i++)
        {
            var li = section.AddParagraph();
            li.Format.LeftIndent = MD.Unit.FromPoint(28);
            li.Format.FirstLineIndent = MD.Unit.FromPoint(-14);
            li.Format.TabStops.AddTabStop(MD.Unit.FromPoint(28));
            li.AddText($"{i}.");
            li.AddTab();
            li.AddText(i == 1 ? "Water daily" : "Measure weekly");
        }

        var tableHeading = section.AddParagraph("Results");
        tableHeading.Format.Font.Size = 16;
        tableHeading.Format.Font.Bold = true;
        tableHeading.Format.SpaceBefore = 10;
        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.TopPadding = 4;
        table.BottomPadding = 4;
        table.LeftPadding = 4;
        table.AddColumn(MD.Unit.FromCentimeter(5));
        table.AddColumn(MD.Unit.FromCentimeter(3));
        table.AddColumn(MD.Unit.FromCentimeter(3));
        string[][] rows = [["Flower", "Petals", "Height"], ["Rose", "32", "1,250"], ["Tulip", "6", "450"], ["Daisy", "21", "300"]];
        for (var r = 0; r < rows.Length; r++)
        {
            var row = table.AddRow();
            for (var c = 0; c < 3; c++)
            {
                var p = row.Cells[c].AddParagraph(rows[r][c]);
                if (r == 0) p.Format.Font.Bold = true;
            }
        }
        section.AddParagraph().Format.SpaceAfter = 12;
        section.AddParagraph("Closing remarks after the table.");
        section.AddPageBreak();
        var second = section.AddParagraph("Appendix");
        second.Format.Font.Size = 16;
        second.Format.Font.Bold = true;
        section.AddParagraph("The second page has a little more Petal text.");
        section.AddPageBreak();
        section.AddParagraph("Third page text.");

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }

    /// <summary>A "scan": the text exists only as pixels in a page-sized picture.</summary>
    private static void BuildScannedPdf(string path)
    {
        const int width = 1654, height = 2339; // A4 at 200 DPI
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var typeface = SKTypeface.FromFamilyName("Arial");
            using var title = new SKFont(typeface, 64);
            using var body = new SKFont(typeface, 40);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText("SCANNED PETAL DOCUMENT", 140, 260, SKTextAlign.Left, title, paint);
            canvas.DrawText("The quick brown fox jumps over the lazy dog.", 140, 420, SKTextAlign.Left, body, paint);
            canvas.DrawText("Optical character recognition reads this line.", 140, 500, SKTextAlign.Left, body, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        using var pdf = new PdfSharp.Pdf.PdfDocument();
        var page = pdf.AddPage();
        page.Width = XUnit.FromPoint(595.28);
        page.Height = XUnit.FromPoint(841.89);
        using (var gfx = XGraphics.FromPdfPage(page))
        using (var ximage = XImage.FromStream(new MemoryStream(png)))
            gfx.DrawImage(ximage, 0, 0, page.Width.Point, page.Height.Point);
        pdf.Save(path);
    }

    private static void BuildXlsx(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Stock");
        ws.Cell(1, 1).Value = "Product";
        ws.Cell(1, 2).Value = "Quantity";
        ws.Cell(1, 3).Value = "Price";
        ws.Cell(1, 4).Value = "Note";
        ws.Cell(2, 1).Value = "Petal pen";
        ws.Cell(2, 2).Value = 12;
        ws.Cell(2, 3).Value = 1.5;
        ws.Cell(2, 4).Value = "مرحبا";
        ws.Cell(3, 1).Value = "Ink, black";
        ws.Cell(3, 2).Value = 7;
        ws.Cell(3, 3).Value = 0.99;
        ws.Cell(3, 4).Value = new DateTime(2024, 5, 1);
        ws.Row(1).Style.Font.Bold = true;
        wb.SaveAs(path);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    public static HashSet<int> OfficeProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var name in new[] { "WINWORD", "EXCEL", "POWERPNT" })
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var p in processes)
            {
                ids.Add(p.Id);
                p.Dispose();
            }
        }
        return ids;
    }

    /// <summary>Waits briefly for Office processes started after <paramref name="before"/> to exit; returns those still running.</summary>
    public static List<int> LingeringOfficeProcesses(HashSet<int> before, int waitMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var extra = OfficeProcessIds().Except(before).ToList();
            if (extra.Count == 0 || sw.ElapsedMilliseconds > waitMs)
                return extra;
            Thread.Sleep(250);
        }
    }

    public static IConverter? ConverterFor(FileFormat from, FileFormat to, DocumentEngineOptions? options = null) =>
        DocumentEngine.CreateConverters(options).FirstOrDefault(c => c.CanConvert(from, to));

    public static Task<JobResult> ConvertAsync(IConverter converter, string input, FileFormat from, FileFormat to, ConversionSettings? settings = null) =>
        converter.ConvertAsync(new ConversionJob(input, from, to, OutputNaming.ForConversion(input, to), settings ?? new ConversionSettings()), null, CancellationToken.None);
}
