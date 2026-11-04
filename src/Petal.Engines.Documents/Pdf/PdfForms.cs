using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Petal.Engines.Documents.Text;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// A PDF's form: its fields read for the person to fill in, and their values written back. PDFsharp's own appearance can't
/// shape Arabic and no reader is obliged to make one, so each changed field gets an appearance drawn here, with the same
/// shaping and right-to-left order as all our other text; that is what every reader shows, and what flattening burns in.
/// </summary>
internal static partial class PdfForms
{
    private const int ReadOnlyFlag = 1, RequiredFlag = 2, MultiLineFlag = 1 << 12, PasswordFlag = 1 << 13, RadioFlag = 1 << 15, PushButtonFlag = 1 << 16,
        ComboFlag = 1 << 17, EditFlag = 1 << 18, MultiSelectFlag = 1 << 21, CombFlag = 1 << 24;

    /// <summary>A field with its widgets, as the file has it.</summary>
    internal sealed class FieldNode(PdfDictionary field, string name, PdfFieldKind kind)
    {
        public PdfDictionary Field { get; } = field;
        public string Name { get; } = name;
        public PdfFieldKind Kind { get; } = kind;
        public List<PdfDictionary> Widgets { get; } = [];
    }

    private static PdfDictionary? AcroForm(PdfDocument document) => document.Internals.Catalog.Elements.GetDictionary("/AcroForm");

    /// <summary>True when the file holds its form only as XFA, which this doesn't read: no widgets to fill in on its pages.</summary>
    public static bool IsXfaOnly(PdfDocument document)
    {
        var form = AcroForm(document);
        return form is not null && form.Elements.ContainsKey("/XFA") && FieldNodes(document).All(n => n.Widgets.Count == 0);
    }

    /// <summary>True when the form holds a signature someone has made; changing the file ends its validity.</summary>
    public static bool IsSigned(PdfDocument document) => FieldNodes(document).Any(n => n.Kind == PdfFieldKind.Signature && n.Field.Elements.ContainsKey("/V"));

    /// <summary>Every field the file's form lists, in its order, each with its widgets.</summary>
    public static List<FieldNode> FieldNodes(PdfDocument document)
    {
        var nodes = new List<FieldNode>();
        var form = AcroForm(document);
        if (form?.Elements.GetArray("/Fields") is not { } fields)
            return nodes;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in fields.Elements)
            if (PdfWalk.Dictionary(item) is { } field)
                Walk(field, "", nodes, seen);
        return nodes;
    }

    private static void Walk(PdfDictionary field, string parent, List<FieldNode> nodes, HashSet<PdfDictionary> seen)
    {
        if (!seen.Add(field))
            return;
        var own = PdfWalk.Text(field.Elements["/T"]);
        var name = string.IsNullOrEmpty(own) ? parent : parent.Length == 0 ? own : parent + "." + own;
        var kids = field.Elements.GetArray("/Kids");
        // Kids without a name of their own are this field's widgets; kids with one are fields under it.
        var children = kids?.Elements.Select(PdfWalk.Dictionary).OfType<PdfDictionary>().ToList() ?? [];
        var subFields = children.Where(c => c.Elements.ContainsKey("/T")).ToList();
        foreach (var child in subFields)
            Walk(child, name, nodes, seen);
        var widgets = children.Except(subFields).ToList();
        if (subFields.Count > 0 && widgets.Count == 0)
            return;
        if (Inherited(field, "/FT") is not { } type)
            return;
        var node = new FieldNode(field, name, KindOf(PdfWalk.Text(type) ?? "", Flags(field)));
        node.Widgets.AddRange(widgets);
        if (children.Count == 0)
            node.Widgets.Add(field);
        nodes.Add(node);
    }

    private static PdfFieldKind KindOf(string type, int flags) => type switch
    {
        "/Tx" => PdfFieldKind.Text,
        "/Ch" => (flags & ComboFlag) != 0 ? PdfFieldKind.Combo : PdfFieldKind.List,
        "/Sig" => PdfFieldKind.Signature,
        _ => (flags & PushButtonFlag) != 0 ? PdfFieldKind.Button : (flags & RadioFlag) != 0 ? PdfFieldKind.Radio : PdfFieldKind.CheckBox,
    };

    private static PdfItem? Inherited(PdfDictionary field, string key)
    {
        for (var at = field; at is not null; at = at.Elements.GetDictionary("/Parent"))
            if (at.Elements.ContainsKey(key))
                return at.Elements[key];
        return null;
    }

    private static double SizeOf(PdfDictionary field, PdfDocument document)
    {
        var da = PdfWalk.Text(Inherited(field, "/DA")) ?? PdfWalk.Text(AcroForm(document)?.Elements["/DA"]) ?? "";
        var part = FontPart().Match(da);
        return part.Success && double.TryParse(part.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : 0;
    }

    private static int Flags(PdfDictionary field) => (int)(PdfWalk.Number(Inherited(field, "/Ff")) ?? 0);

    /// <summary>What the person fills in: every field that has a place on a page, with those places in the file's own order.</summary>
    public static List<PdfFormField> Read(PdfDocument document)
    {
        var fields = new List<(int Order, PdfFormField Field)>();
        var nodes = FieldNodes(document);
        if (nodes.Count == 0)
            return [];
        var places = PlacesOfWidgets(document);
        foreach (var node in nodes)
        {
            var widgets = new List<(int Order, PdfFieldWidget Widget)>();
            foreach (var widget in node.Widgets)
            {
                if (!places.TryGetValue(widget, out var place) || PdfWalk.Rectangle(widget.Elements["/Rect"]) is not { } rect || IsHidden(widget))
                    continue;
                var space = PdfPageSpace.Of(document.Pages[place.Page]);
                widgets.Add((place.Page * 100000 + place.Order, new PdfFieldWidget(place.Page, space.AreaOf(rect.X1, rect.Y1, rect.X2, rect.Y2), OnState(widget))));
            }
            if (widgets.Count == 0)
                continue;
            int flags = Flags(node.Field);
            var ordered = widgets.OrderBy(w => w.Order).ToList();
            fields.Add((ordered[0].Order, new PdfFormField(node.Name, node.Kind, ValueOf(node), Options(node.Field), ordered.Select(w => w.Widget).ToList())
            {
                ReadOnly = (flags & ReadOnlyFlag) != 0,
                Required = (flags & RequiredFlag) != 0,
                MultiLine = (flags & MultiLineFlag) != 0,
                Password = (flags & PasswordFlag) != 0,
                Comb = (flags & CombFlag) != 0,
                MaxLength = (int)(PdfWalk.Number(Inherited(node.Field, "/MaxLen")) ?? 0),
                Editable = (flags & EditFlag) != 0,
                MultiSelect = (flags & MultiSelectFlag) != 0,
                Tip = PdfWalk.Text(node.Field.Elements["/TU"]),
                FontSize = SizeOf(node.Field, document),
            }));
        }
        return fields.OrderBy(f => f.Order).Select(f => f.Field).ToList();
    }

    /// <summary>Which page each annotation is on, and where in that page's list.</summary>
    private static Dictionary<PdfDictionary, (int Page, int Order)> PlacesOfWidgets(PdfDocument document)
    {
        var places = new Dictionary<PdfDictionary, (int, int)>(ReferenceEqualityComparer.Instance);
        for (int p = 0; p < document.PageCount; p++)
        {
            if (document.Pages[p].Elements.GetArray("/Annots") is not { } annots)
                continue;
            for (int i = 0; i < annots.Elements.Count; i++)
                if (PdfWalk.Dictionary(annots.Elements[i]) is { } annotation)
                    places[annotation] = (p, i);
        }
        return places;
    }

    private static bool IsHidden(PdfDictionary widget) => ((int)(PdfWalk.Number(widget.Elements["/F"]) ?? 0) & (2 | 32)) != 0;

    private static string? OnState(PdfDictionary widget)
    {
        var normal = widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
        return normal?.Elements.Keys.FirstOrDefault(k => k != "/Off")?.TrimStart('/');
    }

    private static string ValueOf(FieldNode node)
    {
        var value = PdfWalk.Text(Inherited(node.Field, "/V"));
        switch (node.Kind)
        {
            case PdfFieldKind.CheckBox or PdfFieldKind.Radio:
                var on = value?.TrimStart('/');
                if (string.IsNullOrEmpty(on))
                    on = PdfWalk.Text(node.Widgets.FirstOrDefault(w => PdfWalk.Text(w.Elements["/AS"]) is { } s && s != "/Off")?.Elements["/AS"])?.TrimStart('/');
                return string.IsNullOrEmpty(on) ? "Off" : on;
            case PdfFieldKind.List when PdfWalk.Array(Inherited(node.Field, "/V")) is { } many:
                return string.Join('\n', many.Elements.Select(PdfWalk.Text).OfType<string>());
            default:
                return value ?? "";
        }
    }

    private static List<PdfFieldOption> Options(PdfDictionary field)
    {
        var options = new List<PdfFieldOption>();
        if (PdfWalk.Array(Inherited(field, "/Opt")) is not { } list)
            return options;
        foreach (var item in list.Elements)
        {
            if (PdfWalk.Array(item) is { Elements.Count: >= 2 } pair)
                options.Add(new PdfFieldOption(PdfWalk.Text(pair.Elements[0]) ?? "", PdfWalk.Text(pair.Elements[1]) ?? ""));
            else if (PdfWalk.Text(item) is { } text)
                options.Add(new PdfFieldOption(text, text));
        }
        return options;
    }

    // ---------- writing values ----------

    /// <summary>
    /// Writes the values given and an appearance for each. Fields left alone keep theirs, unless the file asks readers to make
    /// appearances (or a field with a value has none): those are drawn too, so nothing is left for a reader to get wrong.
    /// </summary>
    public static void Fill(PdfDocument document, IReadOnlyDictionary<string, string> values)
    {
        var form = AcroForm(document);
        if (form is null)
            return;
        bool makeAll = form.Elements.ContainsKey("/NeedAppearances") && form.Elements.GetBoolean("/NeedAppearances");
        var defaults = new FormDefaults(PdfWalk.Text(form.Elements["/DA"]) ?? "", form.Elements.GetDictionary("/DR")?.Elements.GetDictionary("/Font"));
        foreach (var node in FieldNodes(document))
        {
            bool changed = values.TryGetValue(node.Name, out var value) && node.Kind is not (PdfFieldKind.Button or PdfFieldKind.Signature) && value != ValueOf(node);
            if (changed)
                SetValue(node, value!);
            if (node.Kind is PdfFieldKind.Button or PdfFieldKind.Signature)
                continue;
            if (node.Kind is PdfFieldKind.CheckBox or PdfFieldKind.Radio)
            {
                if (changed)
                    SetButtonStates(document, node, value!);
                continue;
            }
            foreach (var widget in node.Widgets)
            {
                bool hasAppearance = widget.Elements.GetDictionary("/AP")?.Elements.ContainsKey("/N") == true;
                if (changed || makeAll || (!hasAppearance && ValueOf(node).Length > 0))
                    DrawAppearance(document, node, widget, ValueOf(node), defaults);
            }
        }
        form.Elements.Remove("/NeedAppearances");
        // Hybrid forms hold their values twice; readers that prefer XFA would show the old ones.
        form.Elements.Remove("/XFA");
    }

    private static void SetValue(FieldNode node, string value)
    {
        var holder = node.Field;
        holder.Elements.Remove("/RV");
        switch (node.Kind)
        {
            case PdfFieldKind.CheckBox or PdfFieldKind.Radio:
                holder.Elements["/V"] = new PdfName("/" + (value.Length == 0 ? "Off" : value));
                break;
            case PdfFieldKind.List when value.Contains('\n'):
                var many = new PdfArray(holder.Owner);
                foreach (var one in value.Split('\n'))
                    many.Elements.Add(StringValue(one));
                holder.Elements["/V"] = many;
                holder.Elements.Remove("/I");
                break;
            default:
                holder.Elements["/V"] = StringValue(value);
                holder.Elements.Remove("/I");
                break;
        }
    }

    private static PdfString StringValue(string value) =>
        value.All(c => c is >= ' ' and < (char)127) ? new PdfString(value) : new PdfString(value, PdfStringEncoding.Unicode);

    /// <summary>Check boxes and radio buttons show the state that is on; one with no appearances at all gets a simple one.</summary>
    private static void SetButtonStates(PdfDocument document, FieldNode node, string value)
    {
        foreach (var widget in node.Widgets)
        {
            var on = OnState(widget);
            if (on is null)
            {
                on = value is "Off" or "" ? "Yes" : value;
                DrawButtonStates(document, widget, on, node.Kind == PdfFieldKind.Radio);
            }
            widget.Elements["/AS"] = new PdfName("/" + (on == value ? on : "Off"));
        }
    }

    private static void DrawButtonStates(PdfDocument document, PdfDictionary widget, string on, bool round)
    {
        if (PdfWalk.Rectangle(widget.Elements["/Rect"]) is not { } rect)
            return;
        double w = rect.X2 - rect.X1, h = rect.Y2 - rect.Y1, side = Math.Min(w, h);
        var inv = CultureInfo.InvariantCulture;
        string mark = round
            ? DotPath(w / 2, h / 2, side * 0.26)
            : string.Format(inv, "{0:0.##} w 1 J 1 j {1:0.##} {2:0.##} m {3:0.##} {4:0.##} l {5:0.##} {6:0.##} l S", side * 0.14, w / 2 - side * 0.3, h / 2 - side * 0.02, w / 2 - side * 0.08, h / 2 - side * 0.25, w / 2 + side * 0.32, h / 2 + side * 0.3);
        var normal = new PdfDictionary(document);
        normal.Elements["/" + on] = PdfEditorParts.Form(document, 0, 0, w, h, "0 0 0 rg 0 0 0 RG " + mark).Reference!;
        normal.Elements["/Off"] = PdfEditorParts.Form(document, 0, 0, w, h, " ").Reference!;
        var appearance = new PdfDictionary(document);
        appearance.Elements["/N"] = normal;
        widget.Elements["/AP"] = appearance;
    }

    private static string DotPath(double x, double y, double r)
    {
        const double k = 0.5523;
        var inv = CultureInfo.InvariantCulture;
        return string.Format(inv, "{0:0.###} {1:0.###} m {2:0.###} {3:0.###} {4:0.###} {5:0.###} {6:0.###} {7:0.###} c {8:0.###} {9:0.###} {10:0.###} {11:0.###} {12:0.###} {13:0.###} c {14:0.###} {15:0.###} {16:0.###} {17:0.###} {18:0.###} {19:0.###} c {20:0.###} {21:0.###} {22:0.###} {23:0.###} {24:0.###} {25:0.###} c h f",
            x + r, y,
            x + r, y + k * r, x + k * r, y + r, x, y + r,
            x - k * r, y + r, x - r, y + k * r, x - r, y,
            x - r, y - k * r, x - k * r, y - r, x, y - r,
            x + k * r, y - r, x + r, y - k * r, x + r, y);
    }

    // ---------- appearances ----------

    private sealed record FormDefaults(string Appearance, PdfDictionary? Fonts);

    [GeneratedRegex(@"/(?<font>[^\s/]+)\s+(?<size>[\d.]+)\s+Tf")]
    private static partial Regex FontPart();

    [GeneratedRegex(@"(?<r>[\d.]+)\s+(?<g>[\d.]+)\s+(?<b>[\d.]+)\s+rg")]
    private static partial Regex RgbPart();

    [GeneratedRegex(@"(?<!\S)(?<g>[\d.]+)\s+g(?!\S)")]
    private static partial Regex GrayPart();

    private sealed record FieldLook(string Family, double Size, bool Bold, bool Italic, XColor Ink);

    private static FieldLook LookOf(PdfDictionary field, PdfDictionary widget, FormDefaults defaults, bool arabicScript)
    {
        var da = PdfWalk.Text(widget.Elements["/DA"]) ?? PdfWalk.Text(Inherited(field, "/DA")) ?? defaults.Appearance;
        var fontPart = FontPart().Match(da);
        string fontName = fontPart.Success ? fontPart.Groups["font"].Value : "Helv";
        double size = fontPart.Success && double.TryParse(fontPart.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var base14 = defaults.Fonts?.Elements.GetDictionary("/" + fontName);
        var name = (base14 is null ? null : PdfWalk.Text(base14.Elements["/BaseFont"])?.TrimStart('/')) ?? fontName;
        name = name switch { "Helv" => "Helvetica", "HeBo" => "Helvetica-Bold", "TiRo" => "Times-Roman", "TiBo" => "Times-Bold", "Cour" => "Courier", _ => name };
        var ink = XColors.Black;
        var rgb = RgbPart().Match(da);
        if (rgb.Success)
            ink = XColor.FromArgb(Byte(rgb.Groups["r"].Value), Byte(rgb.Groups["g"].Value), Byte(rgb.Groups["b"].Value));
        else if (GrayPart().Match(da) is { Success: true } gray)
            ink = XColor.FromArgb(Byte(gray.Groups["g"].Value), Byte(gray.Groups["g"].Value), Byte(gray.Groups["g"].Value));
        bool bold = name.Contains("Bold", StringComparison.OrdinalIgnoreCase);
        bool italic = !arabicScript && (name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase));
        return new FieldLook(PdfFonts.FamilyLike(name, arabicScript), size, bold, italic, ink);
    }

    private static int Byte(string unit) => Math.Clamp((int)Math.Round(double.Parse(unit, CultureInfo.InvariantCulture) * 255), 0, 255);

    private static XColor? ColourOf(PdfArray? components)
    {
        if (components is null)
            return null;
        var c = components.Elements.Select(i => PdfWalk.Number(i) ?? 0).ToList();
        int Unit(double v) => Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return c.Count switch
        {
            1 => XColor.FromArgb(Unit(c[0]), Unit(c[0]), Unit(c[0])),
            3 => XColor.FromArgb(Unit(c[0]), Unit(c[1]), Unit(c[2])),
            4 => XColor.FromArgb(Unit((1 - c[0]) * (1 - c[3])), Unit((1 - c[1]) * (1 - c[3])), Unit((1 - c[2]) * (1 - c[3]))),
            _ => null,
        };
    }

    /// <summary>
    /// A widget's look with its value in it: the field's background and border, its text set as the form says (font, size, side;
    /// a size of 0 meaning as large as fits), in cells for a comb, and shaped where it is Arabic script. Drawn with PDFsharp on a
    /// page of the field's size that exists only for this, and turned into the widget's appearance stream.
    /// </summary>
    private static void DrawAppearance(PdfDocument document, FieldNode node, PdfDictionary widget, string value, FormDefaults defaults)
    {
        if (PdfWalk.Rectangle(widget.Elements["/Rect"]) is not { } rect)
            return;
        double width = rect.X2 - rect.X1, height = rect.Y2 - rect.Y1;
        if (width < 1 || height < 1)
            return;
        var characteristics = widget.Elements.GetDictionary("/MK");
        int turn = (((int)(PdfWalk.Number(characteristics?.Elements["/R"]) ?? 0) % 360) + 360) % 360;
        double w = turn is 90 or 270 ? height : width, h = turn is 90 or 270 ? width : height;
        var shown = node.Kind is PdfFieldKind.Combo ? LabelOf(node.Field, value) : value;
        bool arabicScript = Bidi.ContainsRtl(shown) || Options(node.Field).Any(o => Bidi.ContainsRtl(o.Label));
        var look = LookOf(node.Field, widget, defaults, arabicScript);

        var scratch = document.AddPage();
        scratch.Width = XUnit.FromPoint(w);
        scratch.Height = XUnit.FromPoint(h);
        using (var gfx = XGraphics.FromPdfPage(scratch))
        {
            if (ColourOf(characteristics?.Elements.GetArray("/BG")) is { } background)
                gfx.DrawRectangle(new XSolidBrush(background), 0, 0, w, h);
            double border = PdfWalk.Number(widget.Elements.GetDictionary("/BS")?.Elements["/W"]) ?? 1;
            if (ColourOf(characteristics?.Elements.GetArray("/BC")) is { } line && border > 0)
                gfx.DrawRectangle(new XPen(line, border), border / 2, border / 2, w - border, h - border);
            double pad = Math.Max(1, border) + 1;
            var inside = new XRect(pad, pad, Math.Max(1, w - 2 * pad), Math.Max(1, h - 2 * pad));
            switch (node.Kind)
            {
                case PdfFieldKind.List:
                    DrawList(gfx, node, value, look, inside);
                    break;
                default:
                    var flags = Flags(node.Field);
                    if ((flags & PasswordFlag) != 0)
                        shown = new string('*', shown.Length);
                    int side = (int)(PdfWalk.Number(Inherited(node.Field, "/Q")) ?? 0);
                    int cells = (flags & CombFlag) != 0 ? (int)(PdfWalk.Number(Inherited(node.Field, "/MaxLen")) ?? 0) : 0;
                    if (cells > 0)
                        DrawComb(gfx, shown, look, new XRect(0, 0, w, h), cells);
                    else
                        DrawText(gfx, shown, look, inside, side, (flags & MultiLineFlag) != 0);
                    break;
            }
        }
        var content = scratch.Contents.CreateSingleContent().Stream?.UnfilteredValue ?? [];
        var resources = scratch.Elements.GetDictionary("/Resources");
        var appearance = PdfEditorParts.Form(document, 0, 0, w, h, content, resources);
        if (turn != 0)
        {
            double[] m = turn switch
            {
                90 => [0, 1, -1, 0, width, 0],
                180 => [-1, 0, 0, -1, width, height],
                _ => [0, -1, 1, 0, 0, height],
            };
            appearance.Elements["/Matrix"] = new PdfArray(document, m.Select(v => (PdfItem)new PdfReal(v)).ToArray());
        }
        var holder = new PdfDictionary(document);
        holder.Elements["/N"] = appearance.Reference!;
        widget.Elements["/AP"] = holder;
        document.Pages.Remove(scratch);
    }

    private static string LabelOf(PdfDictionary field, string value) => Options(field).FirstOrDefault(o => o.Export == value)?.Label ?? value;

    private static XFont FontFor(FieldLook look, double size) =>
        new(look.Family, size, (look.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (look.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular), new XPdfFontOptions(PdfFontEncoding.Unicode));

    private static void DrawText(XGraphics gfx, string text, FieldLook look, XRect box, int side, bool multiLine)
    {
        if (text.Length == 0)
            return;
        var brush = new XSolidBrush(look.Ink);
        bool rtl = Bidi.IsRtlParagraph(text);
        double size = look.Size;
        if (multiLine)
        {
            if (size <= 0)
            {
                size = 12;
                while (size > 4 && Lines(text, look, size, box.Width, rtl).Count * size * 1.15 > box.Height)
                    size -= 0.5;
            }
            var font = FontFor(look, size);
            double baseline = box.Top + size * 0.9;
            foreach (var line in Lines(text, look, size, box.Width, rtl))
            {
                DrawLine(gfx, line, font, brush, box, side, rtl, baseline);
                baseline += size * 1.15;
            }
            return;
        }
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        var visual = PdfFonts.Drawable(Bidi.ToVisual(flat, rtl));
        if (size <= 0)
        {
            // As large as fits: the height first, then the width.
            size = Math.Clamp(box.Height * 0.82, 4, 14);
            double measured = PdfFonts.Measure(visual, look.Family, size, look.Bold, look.Italic);
            if (measured > box.Width)
                size = Math.Max(4, size * box.Width / measured);
        }
        var single = FontFor(look, size);
        DrawLine(gfx, visual, single, brush, box, side, rtl, box.Top + box.Height / 2 + size * 0.34);
    }

    private static List<string> Lines(string text, FieldLook look, double size, double width, bool rtl) =>
        PdfFonts.BreakRtl(text, look.Family, size, look.Bold, look.Italic, width, rtl).Select(PdfFonts.Drawable).ToList();

    /// <summary>A form's "left" means the side a line starts on: right-to-left text at the start sits at the right.</summary>
    private static void DrawLine(XGraphics gfx, string line, XFont font, XBrush brush, XRect box, int side, bool rtl, double baseline)
    {
        double wide = gfx.MeasureString(line, font).Width;
        double x = side switch
        {
            1 => box.Left + (box.Width - wide) / 2,
            2 => rtl ? box.Left : box.Right - wide,
            _ => rtl ? box.Right - wide : box.Left,
        };
        gfx.DrawString(line, font, brush, x, baseline);
    }

    private static void DrawComb(XGraphics gfx, string text, FieldLook look, XRect box, int cells)
    {
        if (text.Length == 0)
            return;
        double cell = box.Width / cells;
        double size = look.Size > 0 ? look.Size : Math.Clamp(Math.Min(box.Height * 0.8, cell * 1.1), 4, 16);
        var font = FontFor(look, size);
        var brush = new XSolidBrush(look.Ink);
        var visual = PdfFonts.Drawable(Bidi.ToVisual(text.Length > cells ? text[..cells] : text));
        bool rtl = Bidi.ContainsRtl(text);
        double baseline = box.Top + box.Height / 2 + size * 0.34;
        for (int i = 0; i < visual.Length; i++)
        {
            int at = rtl ? cells - visual.Length + i : i;
            var one = visual[i].ToString();
            gfx.DrawString(one, font, brush, box.Left + at * cell + (cell - gfx.MeasureString(one, font).Width) / 2, baseline);
        }
    }

    private static void DrawList(XGraphics gfx, FieldNode node, string value, FieldLook look, XRect box)
    {
        var options = Options(node.Field);
        var selected = value.Split('\n').ToHashSet();
        double size = look.Size > 0 ? look.Size : 11;
        double step = size * 1.25, top = box.Top;
        var font = FontFor(look, size);
        var brush = new XSolidBrush(look.Ink);
        var bar = new XSolidBrush(XColor.FromArgb(153, 193, 218));
        foreach (var option in options)
        {
            if (top + step > box.Bottom + step * 0.25)
                break;
            if (selected.Contains(option.Export))
                gfx.DrawRectangle(bar, box.Left, top, box.Width, step);
            bool rtl = Bidi.IsRtlParagraph(option.Label);
            DrawLine(gfx, PdfFonts.Drawable(Bidi.ToVisual(option.Label, rtl)), font, brush, box, 0, rtl, top + size * 0.95);
            top += step;
        }
    }

    /// <summary>Takes a field's widget off its page and out of the form: what redacting does to a field under a box.</summary>
    public static void RemoveWidget(PdfDocument document, PdfDictionary widget)
    {
        var form = AcroForm(document);
        if (form is null)
            return;
        var field = widget;
        // A widget of its own field goes with it, and a field whose last widget goes goes too.
        if (!widget.Elements.ContainsKey("/T") && widget.Elements.GetDictionary("/Parent") is { } parent)
        {
            var kids = parent.Elements.GetArray("/Kids");
            RemoveFrom(kids, widget);
            if (kids is { Elements.Count: > 0 })
                return;
            field = parent;
        }
        field.Elements.Remove("/V");
        field.Elements.Remove("/DV");
        while (true)
        {
            var above = field.Elements.GetDictionary("/Parent");
            var list = above is null ? form.Elements.GetArray("/Fields") : above.Elements.GetArray("/Kids");
            RemoveFrom(list, field);
            if (above is null || list is { Elements.Count: > 0 })
                return;
            field = above;
        }
    }

    private static void RemoveFrom(PdfArray? list, PdfDictionary item)
    {
        if (list is null)
            return;
        for (int i = list.Elements.Count - 1; i >= 0; i--)
            if (ReferenceEquals(PdfWalk.Dictionary(list.Elements[i]), item))
                list.Elements.RemoveAt(i);
    }
}
