using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Petal.Engines.Documents.Model;
using A = DocumentFormat.OpenXml.Drawing;
using M = Petal.Engines.Documents.Model;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Petal.Engines.Documents.IO;

/// <summary>Reads Word documents: headings from styles and outline levels, lists from numbering, tables, links and pictures.</summary>
internal sealed class DocxReader
{
    private readonly MainDocumentPart _main;
    private readonly Dictionary<string, Style> _styles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _numToAbstract = [];
    private readonly Dictionary<(int Abstract, int Level), bool> _orderedLevels = [];

    private DocxReader(MainDocumentPart main)
    {
        _main = main;
        foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
            if (style.StyleId?.Value is { } id)
                _styles[id] = style;

        var numbering = main.NumberingDefinitionsPart?.Numbering;
        if (numbering is not null)
        {
            foreach (var abs in numbering.Elements<AbstractNum>())
            {
                if (abs.AbstractNumberId?.Value is not { } absId)
                    continue;
                foreach (var level in abs.Elements<Level>())
                {
                    var fmt = level.NumberingFormat?.Val?.Value;
                    _orderedLevels[(absId, level.LevelIndex?.Value ?? 0)] = fmt is not null && fmt != NumberFormatValues.Bullet && fmt != NumberFormatValues.None;
                }
            }
            foreach (var num in numbering.Elements<NumberingInstance>())
                if (num.NumberID?.Value is { } nid && num.AbstractNumId?.Val?.Value is { } aid)
                    _numToAbstract[nid] = aid;
        }
    }

    public static DocModel Read(string path)
    {
        using var package = WordprocessingDocument.Open(path, false);
        var main = package.MainDocumentPart ?? throw new InvalidDataException("The document has no main part.");
        var reader = new DocxReader(main);
        var doc = new DocModel();
        var props = package.PackageProperties;
        doc.Metadata.Title = NullIfBlank(props.Title);
        doc.Metadata.Author = NullIfBlank(props.Creator);
        doc.Metadata.Subject = NullIfBlank(props.Subject);
        doc.Metadata.Keywords = NullIfBlank(props.Keywords);
        doc.Metadata.Language = NullIfBlank(props.Language);

        if (main.Document?.Body is { } body)
            reader.ReadBody(body.ChildElements, doc.Blocks);
        return doc;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Paragraphs carrying list numbering wait in a ListEntry until the run of list paragraphs ends.

    private void ReadBody(IEnumerable<OpenXmlElement> elements, List<Block> target)
    {
        var pendingList = new List<ListEntry>();
        var lastNumId = -1;

        void FlushList()
        {
            if (pendingList.Count == 0)
                return;
            target.AddRange(ListGrouping.Build(pendingList));
            pendingList.Clear();
            lastNumId = -1;
        }

        foreach (var element in Flatten(elements))
        {
            switch (element)
            {
                case W.Paragraph p:
                {
                    var produced = ReadParagraph(p, out var listInfo);
                    if (listInfo is { } li && produced.FirstOrDefault() is M.Paragraph lp)
                    {
                        // A different numbering instance at the top level is a new list that restarts its count.
                        if (li.Level == 0 && lastNumId >= 0 && li.NumId != lastNumId)
                            FlushList();
                        if (li.Level == 0)
                            lastNumId = li.NumId;
                        pendingList.Add(new ListEntry(li.Level, li.Ordered, lp));
                        // Pictures and page breaks inside a list paragraph follow the item.
                        if (produced.Count > 1)
                        {
                            FlushList();
                            target.AddRange(produced.Skip(1));
                        }
                    }
                    else
                    {
                        FlushList();
                        target.AddRange(produced);
                    }
                    break;
                }
                case W.Table t:
                    FlushList();
                    target.Add(ReadTable(t));
                    break;
            }
        }
        FlushList();
    }

    /// <summary>Unwraps content controls, custom XML and alternate content so only paragraphs and tables remain.</summary>
    private static IEnumerable<OpenXmlElement> Flatten(IEnumerable<OpenXmlElement> elements)
    {
        foreach (var e in elements)
        {
            switch (e)
            {
                case W.Paragraph or W.Table:
                    yield return e;
                    break;
                case SdtBlock sdt when sdt.SdtContentBlock is { } content:
                    foreach (var inner in Flatten(content.ChildElements))
                        yield return inner;
                    break;
                case CustomXmlBlock custom:
                    foreach (var inner in Flatten(custom.ChildElements))
                        yield return inner;
                    break;
                case AlternateContent alt:
                    var chosen = (OpenXmlElement?)alt.GetFirstChild<AlternateContentChoice>() ?? alt.GetFirstChild<AlternateContentFallback>();
                    if (chosen is not null)
                        foreach (var inner in Flatten(chosen.ChildElements))
                            yield return inner;
                    break;
            }
        }
    }

    private readonly record struct ListInfo(int NumId, int Level, bool Ordered);

    private List<Block> ReadParagraph(W.Paragraph p, out ListInfo? list)
    {
        list = null;
        var result = new List<Block>();
        var pPr = p.ParagraphProperties;
        var styleId = pPr?.ParagraphStyleId?.Val?.Value;

        if (pPr?.PageBreakBefore is { } pbb && (pbb.Val is null || pbb.Val.Value))
            result.Add(new M.PageBreak());

        var inlines = new List<Inline>();
        var trailing = new List<Block>();
        ReadInlines(p.ChildElements, inlines, trailing, TextStyle.None, null, styleId);
        var coalesced = inlines.Coalesce();

        var headingLevel = HeadingLevel(pPr, styleId);
        var numbering = NumberingOf(pPr, styleId);

        // A paragraph that is only a page break is not a real paragraph.
        var hasText = coalesced.Any(i => i is TextRun r && r.Text.Trim().Length > 0 || i is InlineImage);
        if (headingLevel is { } level && hasText)
        {
            var h = new Heading(level);
            coalesced.TrimInlines();
            // Heading styles are bold already; keeping the flag would double it in Markdown and HTML.
            var runs = coalesced.OfType<TextRun>().Where(r => r.Text.Trim().Length > 0).ToList();
            if (runs.Count > 0 && runs.All(r => r.Has(TextStyle.Bold)))
                foreach (var r in coalesced.OfType<TextRun>())
                    r.Style &= ~TextStyle.Bold;
            h.Inlines.AddRange(coalesced.Coalesce());
            result.Add(h);
        }
        else if (coalesced.Count == 1 && coalesced[0] is InlineImage onlyImage && numbering is null)
        {
            result.Add(new ImageBlock(onlyImage.Image));
        }
        else if (hasText || numbering is not null && coalesced.Count > 0)
        {
            var para = new M.Paragraph { Alignment = AlignmentOf(pPr, styleId) };
            para.Inlines.AddRange(coalesced);
            if (numbering is { } n)
                list = n;
            result.Add(para);
        }
        result.AddRange(trailing);
        return result;
    }

    private void ReadInlines(IEnumerable<OpenXmlElement> elements, List<Inline> output, List<Block> trailing, TextStyle inherited, string? link, string? paragraphStyle)
    {
        string? fieldLink = null;
        var inFieldCode = false;
        foreach (var e in elements)
        {
            switch (e)
            {
                case Run run:
                {
                    // Complex fields: skip instruction text, keep the result, pick up HYPERLINK targets.
                    var fieldChar = run.GetFirstChild<FieldChar>()?.FieldCharType?.Value;
                    if (fieldChar == FieldCharValues.Begin) { inFieldCode = true; fieldLink = null; }
                    else if (fieldChar == FieldCharValues.Separate) inFieldCode = false;
                    else if (fieldChar == FieldCharValues.End) { inFieldCode = false; fieldLink = null; }
                    if (inFieldCode)
                    {
                        var instr = string.Concat(run.Elements<FieldCode>().Select(f => f.Text));
                        if (ParseHyperlinkField(instr) is { } target)
                            fieldLink = target;
                        continue;
                    }
                    ReadRun(run, output, trailing, inherited | RunStyle(run.RunProperties, paragraphStyle), link ?? fieldLink);
                    break;
                }
                case Hyperlink h:
                {
                    string? target = null;
                    if (h.Id?.Value is { } relId)
                        target = _main.HyperlinkRelationships.FirstOrDefault(r => r.Id == relId)?.Uri.OriginalString;
                    else if (h.Anchor?.Value is { } anchor)
                        target = "#" + anchor;
                    ReadInlines(h.ChildElements, output, trailing, inherited, target ?? link, paragraphStyle);
                    break;
                }
                case SimpleField field:
                    ReadInlines(field.ChildElements, output, trailing, inherited, ParseHyperlinkField(field.Instruction?.Value ?? "") ?? link, paragraphStyle);
                    break;
                case SdtRun sdt when sdt.SdtContentRun is { } content:
                    ReadInlines(content.ChildElements, output, trailing, inherited, link, paragraphStyle);
                    break;
                case InsertedRun ins:
                    ReadInlines(ins.ChildElements, output, trailing, inherited, link, paragraphStyle);
                    break;
                case CustomXmlRun:
                    ReadInlines(e.ChildElements, output, trailing, inherited, link, paragraphStyle);
                    break;
                case DeletedRun or ParagraphProperties or BookmarkStart or BookmarkEnd or ProofError:
                    break;
            }
        }
    }

    private static string? ParseHyperlinkField(string instruction)
    {
        var text = instruction.Trim();
        if (!text.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase))
            return null;
        var start = text.IndexOf('"');
        var end = start >= 0 ? text.IndexOf('"', start + 1) : -1;
        if (start < 0 || end <= start)
            return null;
        var target = text[(start + 1)..end];
        if (text.Contains("\\l", StringComparison.Ordinal))
            target = "#" + target;
        return target;
    }

    private void ReadRun(Run run, List<Inline> output, List<Block> trailing, TextStyle style, string? link)
    {
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case W.Text t:
                    output.Add(new TextRun(t.Text, style, link));
                    break;
                case TabChar or PositionalTab:
                    output.Add(new TextRun("\t", style, link));
                    break;
                case Break br when br.Type?.Value == BreakValues.Page:
                    trailing.Add(new M.PageBreak());
                    break;
                case Break or CarriageReturn:
                    output.Add(new M.LineBreak());
                    break;
                case NoBreakHyphen:
                    output.Add(new TextRun("‑", style, link));
                    break;
                case SoftHyphen:
                    break;
                case SymbolChar sym when sym.Char?.Value is { } code && int.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out var cp):
                    // Symbol fonts map their glyphs to the private use area at F000.
                    output.Add(new TextRun(cp >= 0xF000 ? "•" : char.ConvertFromUtf32(cp), style, link));
                    break;
                case Drawing drawing:
                    AddDrawing(drawing, output, trailing, style, link);
                    break;
                case Picture pict:
                    foreach (var id in pict.Descendants().Where(d => d.LocalName == "imagedata")
                                 .Select(d => d.GetAttributes().FirstOrDefault(a => a.LocalName == "id").Value))
                        if (LoadImage(id) is { } image)
                            output.Add(new InlineImage(image));
                    break;
                case AlternateContent alt:
                {
                    var chosen = (OpenXmlElement?)alt.GetFirstChild<AlternateContentChoice>() ?? alt.GetFirstChild<AlternateContentFallback>();
                    if (chosen is not null)
                        ReadRun(new Run(chosen.ChildElements.Select(c => c.CloneNode(true))), output, trailing, style, link);
                    break;
                }
            }
        }
    }

    private void AddDrawing(Drawing drawing, List<Inline> output, List<Block> trailing, TextStyle style, string? link)
    {
        foreach (var blip in drawing.Descendants<A.Blip>())
        {
            if (LoadImage(blip.Embed?.Value) is not { } image)
                continue;
            var extent = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
            if (extent?.Cx?.Value is { } cx && extent.Cy?.Value is { } cy && cx > 0 && cy > 0)
            {
                image.WidthPt = cx / 12700.0;
                image.HeightPt = cy / 12700.0;
            }
            image.Alt = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().FirstOrDefault()?.Description?.Value;
            output.Add(new InlineImage(image));
        }
        // Text boxes: their paragraphs follow the paragraph that anchors them.
        foreach (var box in drawing.Descendants<TextBoxContent>())
            ReadBody(box.ChildElements, trailing);
    }

    private ImageData? LoadImage(string? relId)
    {
        if (string.IsNullOrEmpty(relId))
            return null;
        try
        {
            if (_main.GetPartById(relId) is not ImagePart part)
                return null;
            using var stream = part.GetStream();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ImageData.Create(ms.ToArray());
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private TextStyle RunStyle(RunProperties? rPr, string? paragraphStyle)
    {
        var style = TextStyle.None;
        // Character style first, then paragraph style, then direct formatting wins.
        foreach (var source in StyleChain(rPr?.RunStyle?.Val?.Value).Concat(StyleChain(paragraphStyle)).Reverse())
            style = ApplyRunFormatting(style, source.StyleRunProperties);
        if (rPr is not null)
            style = ApplyRunFormatting(style, rPr);
        return style;
    }

    private static TextStyle ApplyRunFormatting(TextStyle style, OpenXmlElement? props)
    {
        if (props is null)
            return style;
        if (props.GetFirstChild<Italic>() is { } i) style = Set(style, TextStyle.Italic, i.Val is null || i.Val.Value);
        if (props.GetFirstChild<Bold>() is { } b) style = Set(style, TextStyle.Bold, b.Val is null || b.Val.Value);
        if (props.GetFirstChild<Strike>() is { } s) style = Set(style, TextStyle.Strike, s.Val is null || s.Val.Value);
        if (props.GetFirstChild<DoubleStrike>() is { } ds) style = Set(style, TextStyle.Strike, ds.Val is null || ds.Val.Value);
        if (props.GetFirstChild<Underline>() is { } u) style = Set(style, TextStyle.Underline, u.Val is null || u.Val.Value != UnderlineValues.None);
        if (props.GetFirstChild<VerticalTextAlignment>()?.Val?.Value is { } va)
        {
            style &= ~(TextStyle.Superscript | TextStyle.Subscript);
            if (va == VerticalPositionValues.Superscript) style |= TextStyle.Superscript;
            else if (va == VerticalPositionValues.Subscript) style |= TextStyle.Subscript;
        }
        if (props.GetFirstChild<RunFonts>()?.Ascii?.Value is { } font && IsMonospace(font))
            style |= TextStyle.Code;
        return style;
    }

    private static TextStyle Set(TextStyle style, TextStyle flag, bool on) => on ? style | flag : style & ~flag;

    private static bool IsMonospace(string font) =>
        font.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || font.Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
        font.Contains("Mono", StringComparison.OrdinalIgnoreCase) || font.Contains("Lucida Console", StringComparison.OrdinalIgnoreCase);

    /// <summary>The style and the styles it is based on, nearest first.</summary>
    private IEnumerable<Style> StyleChain(string? styleId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (styleId is not null && seen.Add(styleId) && _styles.TryGetValue(styleId, out var style))
        {
            yield return style;
            styleId = style.BasedOn?.Val?.Value;
        }
    }

    private int? HeadingLevel(ParagraphProperties? pPr, string? styleId)
    {
        if (pPr?.OutlineLevel?.Val?.Value is { } direct && direct < 9)
            return Math.Min(direct + 1, 6);
        foreach (var style in StyleChain(styleId))
        {
            var name = style.StyleName?.Val?.Value ?? "";
            if (name.Equals("Title", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(8), out var n) && n is >= 1 and <= 9)
                return Math.Min(n, 6);
            if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } lvl && lvl < 9)
                return Math.Min(lvl + 1, 6);
        }
        return null;
    }

    private ListInfo? NumberingOf(ParagraphProperties? pPr, string? styleId)
    {
        var numPr = pPr?.NumberingProperties;
        int? numId = numPr?.NumberingId?.Val?.Value;
        int? level = numPr?.NumberingLevelReference?.Val?.Value;
        if (numId is null)
        {
            foreach (var style in StyleChain(styleId))
            {
                var sp = style.StyleParagraphProperties?.NumberingProperties;
                if (sp?.NumberingId?.Val?.Value is { } sid)
                {
                    numId = sid;
                    level ??= sp.NumberingLevelReference?.Val?.Value;
                    break;
                }
            }
        }
        if (numId is not { } id || id == 0)
            return null;
        var lvl = level ?? 0;
        var ordered = _numToAbstract.TryGetValue(id, out var abs) && _orderedLevels.TryGetValue((abs, lvl), out var o) && o;
        return new ListInfo(id, lvl, ordered);
    }

    private M.Alignment AlignmentOf(ParagraphProperties? pPr, string? styleId)
    {
        var jc = pPr?.Justification?.Val?.Value;
        if (jc is null)
            foreach (var style in StyleChain(styleId))
                if ((jc = style.StyleParagraphProperties?.Justification?.Val?.Value) is not null)
                    break;
        var rtl = pPr?.BiDi is not null;
        if (jc == JustificationValues.Center) return M.Alignment.Center;
        if (jc == JustificationValues.Both || jc == JustificationValues.Distribute) return M.Alignment.Justify;
        if (jc == JustificationValues.Right || jc == JustificationValues.End) return rtl ? M.Alignment.Start : M.Alignment.End;
        if (jc == JustificationValues.Left && rtl) return M.Alignment.End;
        return M.Alignment.Start;
    }

    private M.Table ReadTable(W.Table t)
    {
        var table = new M.Table();
        var rows = t.Elements<W.TableRow>().ToList();
        // Vertical merges: remember which grid column a merge started in so later rows can extend it.
        var openMerges = new Dictionary<int, M.TableCell>();
        foreach (var tr in rows)
        {
            var row = new M.TableRow { IsHeader = tr.TableRowProperties?.GetFirstChild<TableHeader>() is not null };
            var col = tr.TableRowProperties?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
            foreach (var tc in tr.Elements<W.TableCell>())
            {
                var props = tc.TableCellProperties;
                var span = props?.GridSpan?.Val?.Value ?? 1;
                var vmerge = props?.VerticalMerge;
                if (vmerge is not null && (vmerge.Val is null || vmerge.Val.Value == MergedCellValues.Continue))
                {
                    if (openMerges.TryGetValue(col, out var origin))
                        origin.RowSpan++;
                    col += span;
                    continue;
                }
                var cell = new M.TableCell { ColSpan = span };
                ReadBody(tc.ChildElements, cell.Blocks);
                row.Cells.Add(cell);
                if (vmerge?.Val?.Value == MergedCellValues.Restart)
                    openMerges[col] = cell;
                else
                    openMerges.Remove(col);
                col += span;
            }
            table.Rows.Add(row);
        }
        if (table.Rows.Count > 0 && !table.Rows.Any(r => r.IsHeader) && LooksLikeHeader(t))
            table.Rows[0].IsHeader = true;
        return table;
    }

    private static bool LooksLikeHeader(W.Table t)
    {
        var look = t.GetFirstChild<TableProperties>()?.TableLook;
        if (look?.FirstRow?.Value == true)
        {
            var first = t.Elements<W.TableRow>().FirstOrDefault();
            // Only treat it as a header when the first row is styled differently (bold text).
            return first?.Descendants<Run>().Any() == true &&
                   first.Descendants<Run>().Where(r => r.InnerText.Trim().Length > 0).All(r => r.RunProperties?.Bold is not null);
        }
        return false;
    }
}
