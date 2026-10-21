using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Petal.Engines.Documents.Model;
using Petal.Engines.Documents.Text;
using Petal.Engines.Documents.Util;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;
using M = Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>Writes the document model as a Word document with real styles, lists and tables.</summary>
internal sealed class DocxWriter
{
    private const int BulletAbstract = 1;
    private const int OrderedAbstract = 2;
    private const long EmuPerPoint = 12700;

    private readonly MainDocumentPart _main;
    private readonly List<NumberingInstance> _numbering = [];
    private int _nextNumId = 1;
    private uint _nextDrawingId = 1;
    private readonly int _contentWidthTwips;

    private DocxWriter(MainDocumentPart main)
    {
        _main = main;
        _contentWidthTwips = (int)(Paper.ContentWidthPt * 20);
    }

    public static void Write(DocModel doc, string path)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        var writer = new DocxWriter(main);
        main.Document = new W.Document(new Body());
        writer.AddStyles();

        var body = main.Document.Body!;
        foreach (var element in writer.Blocks(doc.Blocks, 0, null))
            body.Append(element);
        if (!body.Elements<W.Paragraph>().Any() && !body.Elements<W.Table>().Any())
            body.Append(new W.Paragraph());
        body.Append(SectionProperties());

        writer.AddNumbering();

        var props = package.PackageProperties;
        props.Title = doc.EffectiveTitle();
        props.Creator = doc.Metadata.Author;
        props.Subject = doc.Metadata.Subject;
        props.Keywords = doc.Metadata.Keywords;
        props.Language = doc.Metadata.Language;
        props.Created = DateTime.UtcNow;
        props.Modified = DateTime.UtcNow;
        main.Document.Save();
    }

    private static SectionProperties SectionProperties()
    {
        var (w, h) = Paper.PageSizePt;
        var margin = (int)(Paper.MarginPt * 20);
        return new SectionProperties(
            new W.PageSize { Width = (UInt32Value)(uint)Math.Round(w * 20), Height = (UInt32Value)(uint)Math.Round(h * 20) },
            new PageMargin
            {
                Top = margin, Bottom = margin, Left = (UInt32Value)(uint)margin, Right = (UInt32Value)(uint)margin,
                Header = 708, Footer = 708, Gutter = 0,
            });
    }

    private IEnumerable<OpenXmlElement> Blocks(IEnumerable<Block> blocks, int depth, string? paragraphStyle)
    {
        foreach (var block in blocks)
        {
            foreach (var element in BlockElements(block, depth, paragraphStyle))
                yield return element;
        }
    }

    private IEnumerable<OpenXmlElement> BlockElements(Block block, int depth, string? paragraphStyle)
    {
        switch (block)
        {
            case Heading h:
                yield return ParagraphOf(h.Inlines, "Heading" + h.Level, M.Alignment.Start);
                break;
            case M.Paragraph p:
                yield return ParagraphOf(p.Inlines, paragraphStyle, p.Alignment);
                break;
            case M.ListBlock l:
                foreach (var e in ListElements(l, 0))
                    yield return e;
                break;
            case M.Table t:
                yield return TableOf(t);
                // Word merges adjacent tables; an empty paragraph keeps them apart.
                yield return new W.Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" }));
                break;
            case ImageBlock img:
                if (ImageRun(img.Image, _contentWidthTwips / 20.0) is { } run)
                    yield return new W.Paragraph(run);
                break;
            case M.CodeBlock c:
                foreach (var line in TextFile.NormalizeNewlines(c.Text).Split('\n'))
                    yield return new W.Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "SourceCode" }), TextRunOf(line, TextStyle.None));
                break;
            case Quote q:
                foreach (var e in Blocks(q.Blocks, depth, "Quote"))
                    yield return e;
                break;
            case HorizontalRule:
                yield return new W.Paragraph(new ParagraphProperties(
                    new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = "BFBFBF" })));
                break;
            case M.PageBreak:
                yield return new W.Paragraph(new Run(new Break { Type = BreakValues.Page }));
                break;
        }
    }

    private IEnumerable<OpenXmlElement> ListElements(M.ListBlock list, int level)
    {
        var numId = NewNumbering(list.Ordered, list.Start);
        foreach (var item in list.Items)
        {
            var first = true;
            foreach (var block in item.Blocks)
            {
                if (block is M.ListBlock nested)
                {
                    foreach (var e in ListElements(nested, Math.Min(level + 1, 8)))
                        yield return e;
                    first = false;
                    continue;
                }
                if (first && block is M.Paragraph p)
                {
                    var para = ParagraphOf(p.Inlines, "ListParagraph", p.Alignment);
                    var pPr = para.ParagraphProperties ??= new ParagraphProperties();
                    pPr.NumberingProperties = new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = numId });
                    yield return para;
                }
                else
                {
                    foreach (var e in BlockElements(block, level, "ListParagraph"))
                    {
                        if (e is W.Paragraph cont)
                        {
                            var pPr = cont.ParagraphProperties ??= new ParagraphProperties();
                            pPr.Indentation = new Indentation { Left = (720 * (level + 1)).ToString() };
                        }
                        yield return e;
                    }
                }
                first = false;
            }
            if (item.Blocks.Count == 0)
            {
                yield return new W.Paragraph(new ParagraphProperties(
                    new ParagraphStyleId { Val = "ListParagraph" },
                    new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = numId })));
            }
        }
    }

    private int NewNumbering(bool ordered, int start)
    {
        var id = _nextNumId++;
        var instance = new NumberingInstance(new AbstractNumId { Val = ordered ? OrderedAbstract : BulletAbstract }) { NumberID = id };
        if (ordered)
        {
            // Every ordered list restarts at its own start number.
            instance.Append(new LevelOverride(new StartOverrideNumberingValue { Val = start }) { LevelIndex = 0 });
        }
        _numbering.Add(instance);
        return id;
    }

    private W.Paragraph ParagraphOf(IEnumerable<Inline> inlines, string? style, M.Alignment alignment)
    {
        var list = inlines as IList<Inline> ?? inlines.ToList();
        var para = new W.Paragraph();
        var pPr = new ParagraphProperties();
        if (style is not null)
            pPr.ParagraphStyleId = new ParagraphStyleId { Val = style };
        var rtl = Bidi.IsRtlParagraph(list.PlainText());
        if (rtl)
            pPr.BiDi = new BiDi();
        if (alignment != M.Alignment.Start)
        {
            pPr.Justification = new Justification
            {
                Val = alignment switch
                {
                    M.Alignment.Center => JustificationValues.Center,
                    M.Alignment.End => rtl ? JustificationValues.Left : JustificationValues.Right,
                    _ => JustificationValues.Both,
                },
            };
        }
        if (pPr.HasChildren)
            para.Append(pPr);

        foreach (var inline in list)
        {
            switch (inline)
            {
                case TextRun run when run.Link is not null:
                    para.Append(HyperlinkOf(run));
                    break;
                case TextRun run:
                    para.Append(TextRunOf(run.Text, run.Style));
                    break;
                case M.LineBreak:
                    para.Append(new Run(new Break()));
                    break;
                case InlineImage img:
                    if (ImageRun(img.Image, _contentWidthTwips / 20.0) is { } r)
                        para.Append(r);
                    break;
            }
        }
        return para;
    }

    private OpenXmlElement HyperlinkOf(TextRun run)
    {
        var link = run.Link!;
        var styled = TextRunOf(run.Text, run.Style, "Hyperlink");
        if (link.StartsWith('#'))
            return new Hyperlink(styled) { Anchor = link[1..], History = true };
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            if (!Uri.TryCreate(link, UriKind.Relative, out uri))
                return TextRunOf(run.Text, run.Style);
        }
        var rel = _main.AddHyperlinkRelationship(uri, isExternal: true);
        return new Hyperlink(styled) { Id = rel.Id, History = true };
    }

    private static Run TextRunOf(string text, TextStyle style, string? runStyle = null)
    {
        var run = new Run();
        var rPr = new RunProperties();
        if (runStyle is not null) rPr.RunStyle = new RunStyle { Val = runStyle };
        if ((style & TextStyle.Code) != 0) rPr.RunFonts = new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" };
        if ((style & TextStyle.Bold) != 0) { rPr.Bold = new Bold(); rPr.BoldComplexScript = new BoldComplexScript(); }
        if ((style & TextStyle.Italic) != 0) { rPr.Italic = new Italic(); rPr.ItalicComplexScript = new ItalicComplexScript(); }
        if ((style & TextStyle.Strike) != 0) rPr.Strike = new Strike();
        if ((style & TextStyle.Underline) != 0) rPr.Underline = new Underline { Val = UnderlineValues.Single };
        if ((style & TextStyle.Superscript) != 0) rPr.VerticalTextAlignment = new VerticalTextAlignment { Val = VerticalPositionValues.Superscript };
        if ((style & TextStyle.Subscript) != 0) rPr.VerticalTextAlignment = new VerticalTextAlignment { Val = VerticalPositionValues.Subscript };
        if (Bidi.ContainsRtl(text)) rPr.RightToLeftText = new RightToLeftText();
        if (rPr.HasChildren)
            run.Append(rPr);

        // Tabs are their own element in WordprocessingML.
        var parts = text.Replace("\r", "").Replace('\n', ' ').Split('\t');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) run.Append(new TabChar());
            if (parts[i].Length > 0)
                run.Append(new W.Text(Sanitize(parts[i])) { Space = SpaceProcessingModeValues.Preserve });
        }
        return run;
    }

    /// <summary>XML 1.0 cannot carry most control characters.</summary>
    internal static string Sanitize(string text)
    {
        if (!text.Any(c => c < 0x20 && c is not '\t' and not '\n' and not '\r' || c is '\uFFFE' or '\uFFFF'))
            return text;
        return new string(text.Where(c => !(c < 0x20 && c is not '\t' and not '\n' and not '\r') && c is not '\uFFFE' and not '\uFFFF').ToArray());
    }

    private Run? ImageRun(ImageData image, double maxWidthPt)
    {
        var part = _main.AddImagePart(image.IsJpeg ? ImagePartType.Jpeg : ImagePartType.Png);
        using (var ms = new MemoryStream(image.Bytes))
            part.FeedData(ms);
        var relId = _main.GetIdOfPart(part);
        var (w, h) = image.FitSize(maxWidthPt, Paper.ContentHeightPt);
        long cx = (long)(w * EmuPerPoint), cy = (long)(h * EmuPerPoint);
        var id = _nextDrawingId++;
        var name = $"Picture {id}";

        var inline = new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = id, Name = name, Description = image.Alt ?? "" },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = name },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U,
        };
        return new Run(new Drawing(inline));
    }

    private W.Table TableOf(M.Table table)
    {
        var (rows, columns) = M.TableLayout.Layout(table);
        columns = Math.Max(1, columns);
        var colWidth = _contentWidthTwips / columns;

        var t = new W.Table();
        t.Append(new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableLook { Val = "04A0", FirstRow = true, LastRow = false, FirstColumn = false, LastColumn = false, NoHorizontalBand = false, NoVerticalBand = true }));
        var grid = new TableGrid();
        for (var i = 0; i < columns; i++)
            grid.Append(new GridColumn { Width = colWidth.ToString() });
        t.Append(grid);

        for (var r = 0; r < rows.Count; r++)
        {
            var tr = new W.TableRow();
            if (table.Rows[r].IsHeader)
                tr.Append(new TableRowProperties(new TableHeader()));
            var col = 0;
            foreach (var slot in rows[r])
            {
                // Fill holes in ragged rows so every row spans the whole grid.
                while (col < slot.Column)
                {
                    tr.Append(EmptyCell(colWidth));
                    col++;
                }
                var tcPr = new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = (colWidth * slot.ColSpan).ToString() });
                if (slot.ColSpan > 1)
                    tcPr.Append(new GridSpan { Val = slot.ColSpan });
                if (slot.Cell.RowSpan > 1 || slot.IsContinuation)
                    tcPr.Append(new VerticalMerge { Val = slot.IsContinuation ? MergedCellValues.Continue : MergedCellValues.Restart });
                var tc = new W.TableCell(tcPr);
                if (!slot.IsContinuation)
                {
                    foreach (var e in Blocks(slot.Cell.Blocks, 0, null))
                    {
                        if (table.Rows[r].IsHeader && e is W.Paragraph hp)
                            foreach (var run in hp.Elements<Run>())
                                (run.RunProperties ??= new RunProperties()).Bold = new Bold();
                        if (e is W.Table)
                            tc.Append(e);
                        else if (e is W.Paragraph)
                            tc.Append(e);
                    }
                }
                if (!tc.Elements<W.Paragraph>().Any())
                    tc.Append(new W.Paragraph());
                // A cell must end with a paragraph.
                if (tc.LastChild is not W.Paragraph)
                    tc.Append(new W.Paragraph());
                tr.Append(tc);
                col += slot.ColSpan;
            }
            while (col < columns)
            {
                tr.Append(EmptyCell(colWidth));
                col++;
            }
            t.Append(tr);
        }
        return t;
    }

    private static W.TableCell EmptyCell(int width) =>
        new(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = width.ToString() }), new W.Paragraph());

    private void AddNumbering()
    {
        var part = _main.AddNewPart<NumberingDefinitionsPart>();
        var numbering = new Numbering();
        numbering.Append(BulletDefinition());
        numbering.Append(OrderedDefinition());
        foreach (var n in _numbering)
            numbering.Append(n);
        part.Numbering = numbering;
        part.Numbering.Save();
    }

    private static AbstractNum BulletDefinition()
    {
        var abs = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = BulletAbstract };
        string[] bullets = ["•", "◦", "▪"];
        for (var lvl = 0; lvl < 9; lvl++)
        {
            abs.Append(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.Bullet },
                new LevelText { Val = bullets[lvl % 3] },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation { Left = (720 * (lvl + 1)).ToString(), Hanging = "360" }),
                new NumberingSymbolRunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }))
            { LevelIndex = lvl });
        }
        return abs;
    }

    private static AbstractNum OrderedDefinition()
    {
        var abs = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = OrderedAbstract };
        NumberFormatValues[] formats = [NumberFormatValues.Decimal, NumberFormatValues.LowerLetter, NumberFormatValues.LowerRoman];
        for (var lvl = 0; lvl < 9; lvl++)
        {
            abs.Append(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = formats[lvl % 3] },
                new LevelText { Val = $"%{lvl + 1}." },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation { Left = (720 * (lvl + 1)).ToString(), Hanging = "360" }))
            { LevelIndex = lvl });
        }
        return abs;
    }

    private void AddStyles()
    {
        var part = _main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();
        styles.Append(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Calibri", ComplexScript = "Arial" },
                new FontSize { Val = "22" },
                new FontSizeComplexScript { Val = "22" },
                new Languages { Val = "en-US", Bidi = "ar-SA" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines { After = "160", Line = "264", LineRule = LineSpacingRuleValues.Auto }))));

        styles.Append(new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        (string Size, string Color, bool Italic)[] headings =
        [
            ("40", "1F3864", false), ("32", "2F5496", false), ("28", "1F3864", false),
            ("24", "2F5496", true), ("22", "2F5496", false), ("22", "1F3864", true),
        ];
        for (var i = 0; i < headings.Length; i++)
        {
            var (size, color, italic) = headings[i];
            // Children in schema order (rFonts, b, bCs, i, color, sz, szCs): Word repairs files that differ.
            var rPr = new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Arial" },
                new Bold(), new BoldComplexScript());
            if (italic)
                rPr.Append(new Italic());
            rPr.Append(new Color { Val = color }, new FontSize { Val = size }, new FontSizeComplexScript { Val = size });
            styles.Append(new Style(
                new StyleName { Val = $"heading {i + 1}" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new UIPriority { Val = 9 },
                new PrimaryStyle(),
                new StyleParagraphProperties(
                    new KeepNext(), new KeepLines(),
                    new SpacingBetweenLines { Before = i == 0 ? "360" : "240", After = "120" },
                    new OutlineLevel { Val = i }),
                rPr)
            { Type = StyleValues.Paragraph, StyleId = $"Heading{i + 1}" });
        }

        styles.Append(new Style(
            new StyleName { Val = "List Paragraph" },
            new BasedOn { Val = "Normal" },
            new UIPriority { Val = 34 },
            new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { After = "60" }, new Indentation { Left = "720" }, new ContextualSpacing()))
        { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });

        styles.Append(new Style(
            new StyleName { Val = "Quote" },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleParagraphProperties(
                new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 18, Space = 8, Color = "D0D0D0" }),
                new Indentation { Left = "567", Right = "567" }),
            new StyleRunProperties(new Italic(), new Color { Val = "404040" }))
        { Type = StyleValues.Paragraph, StyleId = "Quote" });

        styles.Append(new Style(
            new StyleName { Val = "Source Code" },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F5F5F5" },
                new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
            new StyleRunProperties(
                new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" },
                new FontSize { Val = "20" }))
        { Type = StyleValues.Paragraph, StyleId = "SourceCode" });

        styles.Append(new Style(
            new StyleName { Val = "Hyperlink" },
            new UIPriority { Val = 99 },
            new StyleRunProperties(new Color { Val = "0563C1" }, new Underline { Val = UnderlineValues.Single }))
        { Type = StyleValues.Character, StyleId = "Hyperlink" });

        styles.Append(new Style(
            new StyleName { Val = "Table Grid" },
            new UIPriority { Val = 39 },
            new StyleParagraphProperties(new SpacingBetweenLines { After = "0" }),
            new StyleTableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" }),
                new TableCellMarginDefault(
                    new TopMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 100, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 100, Type = TableWidthValues.Dxa })))
        { Type = StyleValues.Table, StyleId = "TableGrid" });

        part.Styles = styles;
        part.Styles.Save();
    }
}
