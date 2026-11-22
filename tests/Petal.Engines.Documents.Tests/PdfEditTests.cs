using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Pdf;
using SkiaSharp;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using PdfDocument = PdfSharp.Pdf.PdfDocument;

namespace Petal.Engines.Documents.Tests;

/// <summary>
/// Edit PDF's engine, on files made here. Forms: every kind of field filled and read back, Arabic shaped, flattening. What is added
/// lands where it was placed, on a turned page and a cropped one too. Retyped lines: the old words really gone. Redaction:
/// looked for in PdfPig's text, the raw file and every decompressed stream. The original file is never changed.
/// </summary>
public sealed class PdfEditTests
{
    private const string Secret = "Zebrafish42";
    private const int Red = 0xE02020;

    // ---------- fixtures ----------

    private static PdfArray Numbers(PdfDocument document, params double[] values) => new(document, values.Select(v => (PdfItem)new PdfReal(v)).ToArray());

    private static PdfDictionary Widget(PdfDocument document, PdfPage page, double x1, double y1, double x2, double y2)
    {
        var widget = new PdfDictionary(document);
        widget.Elements["/Type"] = new PdfName("/Annot");
        widget.Elements["/Subtype"] = new PdfName("/Widget");
        widget.Elements["/Rect"] = Numbers(document, x1, y1, x2, y2);
        widget.Elements["/F"] = new PdfInteger(4);
        document.Internals.AddObject(widget);
        if (page.Elements.GetArray("/Annots") is not { } annots)
            page.Elements["/Annots"] = annots = new PdfArray(document);
        annots.Elements.Add(widget.Reference!);
        return widget;
    }

    private static PdfDictionary ButtonLook(PdfDocument document, string on)
    {
        var normal = new PdfDictionary(document);
        foreach (var state in new[] { on, "Off" })
        {
            var form = new PdfDictionary(document);
            form.Elements["/Type"] = new PdfName("/XObject");
            form.Elements["/Subtype"] = new PdfName("/Form");
            form.Elements["/BBox"] = Numbers(document, 0, 0, 14, 14);
            form.CreateStream(Encoding.ASCII.GetBytes(state == "Off" ? " " : "0 0 0 rg 2 2 10 10 re f"));
            document.Internals.AddObject(form);
            normal.Elements["/" + state] = form.Reference!;
        }
        var appearance = new PdfDictionary(document);
        appearance.Elements["/N"] = normal;
        return appearance;
    }

    /// <summary>A form with one field of each kind. Its text is real, so a search of it can be told apart from a search of its fields.</summary>
    internal static string MakeForm(string dir, string name = "form.pdf", Action<PdfPage>? tweak = null)
    {
        PdfFonts.EnsureInitialized();
        var path = Path.Combine(dir, name);
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var gfx = XGraphics.FromPdfPage(page))
            gfx.DrawString("Visa application", new XFont("Arial", 18), XBrushes.Black, 72, 60);
        var fields = new PdfArray(document);

        void Add(PdfDictionary field)
        {
            fields.Elements.Add(field.Reference!);
        }

        var name_ = Widget(document, page, 72, 700, 372, 722);
        name_.Elements["/FT"] = new PdfName("/Tx");
        name_.Elements["/T"] = new PdfString("name");
        name_.Elements["/DA"] = new PdfString("/Helv 12 Tf 0 0 1 rg");
        name_.Elements["/TU"] = new PdfString("Full name");
        Add(name_);

        var notes = Widget(document, page, 72, 600, 372, 680);
        notes.Elements["/FT"] = new PdfName("/Tx");
        notes.Elements["/T"] = new PdfString("notes");
        notes.Elements["/Ff"] = new PdfInteger(1 << 12);
        notes.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
        Add(notes);

        var comb = Widget(document, page, 72, 560, 272, 585);
        comb.Elements["/FT"] = new PdfName("/Tx");
        comb.Elements["/T"] = new PdfString("passport");
        comb.Elements["/Ff"] = new PdfInteger(1 << 24);
        comb.Elements["/MaxLen"] = new PdfInteger(8);
        comb.Elements["/DA"] = new PdfString("/Helv 12 Tf 0 g");
        Add(comb);

        var check = Widget(document, page, 72, 520, 86, 534);
        check.Elements["/FT"] = new PdfName("/Btn");
        check.Elements["/T"] = new PdfString("agree");
        check.Elements["/AS"] = new PdfName("/Off");
        check.Elements["/AP"] = ButtonLook(document, "Yes");
        Add(check);

        // Radio buttons: one field with its widgets as kids.
        var radio = new PdfDictionary(document);
        radio.Elements["/FT"] = new PdfName("/Btn");
        radio.Elements["/T"] = new PdfString("gender");
        radio.Elements["/Ff"] = new PdfInteger(1 << 15);
        document.Internals.AddObject(radio);
        var kids = new PdfArray(document);
        foreach (var (state, x) in new[] { ("F", 72.0), ("M", 112.0) })
        {
            var kid = Widget(document, page, x, 480, x + 14, 494);
            kid.Elements["/Parent"] = radio.Reference!;
            kid.Elements["/AS"] = new PdfName("/Off");
            kid.Elements["/AP"] = ButtonLook(document, state);
            kids.Elements.Add(kid.Reference!);
        }
        radio.Elements["/Kids"] = kids;
        Add(radio);

        var combo = Widget(document, page, 72, 440, 272, 462);
        combo.Elements["/FT"] = new PdfName("/Ch");
        combo.Elements["/T"] = new PdfString("country");
        combo.Elements["/Ff"] = new PdfInteger(1 << 17);
        combo.Elements["/DA"] = new PdfString("/Helv 11 Tf 0 g");
        combo.Elements["/Opt"] = new PdfArray(document, new PdfArray(document, new PdfString("TR"), new PdfString("Turkey")), new PdfArray(document, new PdfString("SY"), new PdfString("Syria")));
        Add(combo);

        var list = Widget(document, page, 72, 360, 272, 425);
        list.Elements["/FT"] = new PdfName("/Ch");
        list.Elements["/T"] = new PdfString("purpose");
        list.Elements["/DA"] = new PdfString("/Helv 11 Tf 0 g");
        list.Elements["/Opt"] = new PdfArray(document, new PdfString("Study"), new PdfString("Work"), new PdfString("Family"));
        Add(list);

        var form = new PdfDictionary(document);
        form.Elements["/Fields"] = fields;
        form.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
        document.Internals.Catalog.Elements["/AcroForm"] = form;
        tweak?.Invoke(page);
        document.Save(path);
        return path;
    }

    /// <summary>Three pages with words on them: plain A4, A4 turned by 90°, and A4 cropped with a box that doesn't start at zero.</summary>
    internal static string MakeTrickyPages(string dir, string name = "pages.pdf")
    {
        PdfFonts.EnsureInitialized();
        var path = Path.Combine(dir, name);
        using var document = new PdfDocument();
        for (int i = 0; i < 3; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            if (i == 1)
                page.Rotate = 90;
            if (i == 2)
                page.Elements.SetRectangle("/CropBox", new PdfRectangle(new XPoint(50, 60), new XPoint(545, 782)));
            using var gfx = PdfStamper.Upright(page);
            var font = new XFont("Arial", 12);
            gfx.DrawString($"Alpha line on page {i + 1} here", font, new XSolidBrush(XColor.FromArgb(20, 90, 200)), 80, 100);
            gfx.DrawString($"Second {Secret} third fourth", font, XBrushes.Black, 80, 130);
            gfx.DrawString("Closing words of the page", font, XBrushes.Black, 80, 160);
        }
        document.Save(path);
        return path;
    }

    /// <summary>A page written by hand: kerned TJ arrays, Td, TD, T*, Tc, Tw, ' and a cm, in a font whose widths the file gives.</summary>
    private static string MakeHandWritten(string dir)
    {
        var path = Path.Combine(dir, "hand.pdf");
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        var font = new PdfDictionary(document);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/TrueType");
        font.Elements["/BaseFont"] = new PdfName("/Arial");
        font.Elements["/FirstChar"] = new PdfInteger(32);
        font.Elements["/LastChar"] = new PdfInteger(126);
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        font.Elements["/Widths"] = new PdfArray(document, Enumerable.Range(32, 95).Select(_ => (PdfItem)new PdfInteger(556)).ToArray());
        document.Internals.AddObject(font);
        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = font.Reference!;
        page.Resources.Elements["/Font"] = fonts;
        var operators = "q 1 0 0 1 72 700 cm BT /F1 12 Tf 1 Tc 2 Tw [(Kerned ) -200 (Zebra) 120 (fish42) -300 ( after)] TJ 0 -20 TD (Next line stays) Tj "
            + "T* (Moved Zebrafish42 too) ' T* 0.5 1 (Third way Zebrafish42 here) \" ET Q";
        PdfWalk.SetContent(page, Encoding.ASCII.GetBytes(operators));
        document.Save(path);
        return path;
    }

    private static byte[] SolidJpeg(SKColor colour)
    {
        using var bitmap = new SKBitmap(200, 150);
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    // ---------- reading results ----------

    private static PdfEditSession Open(string path, string? password = null) => PdfEditSession.Open(path, password);

    private static async Task<string> Save(string path, PdfEditRequest request, string name = "out.pdf")
    {
        var output = Path.Combine(Path.GetDirectoryName(path)!, name);
        using var session = Open(path);
        await session.SaveAsync(request, output);
        return output;
    }

    private static string Text(string path, int page = 0)
    {
        using var pdf = PigDocument.Open(path);
        return string.Join(' ', pdf.GetPage(page + 1).GetWords().Select(w => w.Text));
    }

    /// <summary>A word as PdfPig reads it, in points from the top left of the page as displayed.</summary>
    private static (double Left, double Top, double Right, double Bottom) Place(string path, int page, string word)
    {
        using var pdf = PigDocument.Open(path);
        var p = pdf.GetPage(page + 1);
        var found = p.GetWords().First(w => w.Text == word);
        return (found.BoundingBox.Left, p.Height - found.BoundingBox.Top, found.BoundingBox.Right, p.Height - found.BoundingBox.Bottom);
    }

    private static void AssertSamePlace((double Left, double Top, double Right, double Bottom) expected, (double Left, double Top, double Right, double Bottom) actual)
    {
        Assert.Equal(expected.Left, actual.Left, 2);
        Assert.Equal(expected.Top, actual.Top, 2);
        Assert.Equal(expected.Right, actual.Right, 2);
        Assert.Equal(expected.Bottom, actual.Bottom, 2);
    }

    private static PdfPageBitmap Picture(string path, int page, int width) => PdfInfo.RenderPage(path, page, width);

    private static (byte R, byte G, byte B) Pixel(PdfPageBitmap picture, double x, double y)
    {
        int at = (int)y * picture.Stride + (int)x * 4;
        return (picture.Pixels[at + 2], picture.Pixels[at + 1], picture.Pixels[at]);
    }

    private static bool AnyInk(PdfPageBitmap picture, double left, double top, double right, double bottom)
    {
        for (int y = (int)top; y < (int)bottom; y++)
            for (int x = (int)left; x < (int)right; x++)
                if (Pixel(picture, x, y) is var p && (p.R < 200 || p.G < 200 || p.B < 200))
                    return true;
        return false;
    }

    /// <summary>Everything in a file a program could read: the raw bytes, each stream decompressed, each string in every encoding it might be in.</summary>
    private static string Everything(string path)
    {
        // Every byte, but for the trailer's /ID: random hex each save, so a short secret like "532" turns up in it by chance.
        var all = new StringBuilder(System.Text.RegularExpressions.Regex.Replace(Encoding.Latin1.GetString(File.ReadAllBytes(path)),
            @"/ID\s*\[\s*<[0-9A-Fa-f]*>\s*<[0-9A-Fa-f]*>\s*\]", "/ID[]"));
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(PdfItem? item)
        {
            switch (item)
            {
                case PdfReference reference:
                    Walk(reference.Value);
                    break;
                case PdfString s:
                    all.Append('\n').Append(s.Value);
                    break;
                case PdfStringObject s:
                    all.Append('\n').Append(s.Value);
                    break;
                case PdfDictionary dictionary when seen.Add(dictionary):
                    foreach (var key in dictionary.Elements.Keys)
                        Walk(dictionary.Elements[key]);
                    if (dictionary.Stream is { } stream)
                    {
                        all.Append('\n').Append(Encoding.Latin1.GetString(stream.UnfilteredValue));
                        all.Append('\n').Append(Encoding.UTF8.GetString(stream.UnfilteredValue));
                    }
                    break;
                case PdfArray array when seen.Add(array):
                    foreach (var element in array.Elements)
                        Walk(element);
                    break;
            }
        }
        foreach (var obj in document.Internals.GetAllObjects())
            Walk(obj);
        Walk(document.Internals.Catalog);
        Walk(document.Info);
        return all.ToString();
    }

    private static void AssertGone(string path, string secret)
    {
        Assert.DoesNotContain(secret, Everything(path), StringComparison.OrdinalIgnoreCase);
        using var pdf = PigDocument.Open(path);
        for (int n = 1; n <= pdf.NumberOfPages; n++)
        {
            var page = pdf.GetPage(n);
            Assert.DoesNotContain(secret, string.Concat(page.Letters.Select(l => l.Value)), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, string.Join(' ', page.GetWords().Select(w => w.Text)), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static List<PdfDictionary> Annotations(string path, int page)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var annots = document.Pages[page].Elements.GetArray("/Annots");
        return annots?.Elements.Select(PdfWalk.Dictionary).OfType<PdfDictionary>().ToList() ?? [];
    }

    // ---------- forms ----------

    [Fact]
    public void Every_kind_of_field_is_read_with_its_place_options_and_order()
    {
        var path = MakeForm(Fixtures.Instance.NewOutputDir("edit-read"));
        using var session = Open(path);
        Assert.Equal(PdfEditRights.Full, session.Rights);
        Assert.Equal(["name", "notes", "passport", "agree", "gender", "country", "purpose"], session.Fields.Select(f => f.Name));
        var name = session.Fields[0];
        Assert.Equal(PdfFieldKind.Text, name.Kind);
        Assert.Equal("Full name", name.Tip);
        // Rect [72 700 372 722] on an 842-point page.
        Assert.Equal(new PdfArea(72, 120, 300, 22), name.Widgets[0].Area);
        Assert.True(session.Fields[1].MultiLine);
        Assert.True(session.Fields[2].Comb);
        Assert.Equal(8, session.Fields[2].MaxLength);
        Assert.Equal(PdfFieldKind.CheckBox, session.Fields[3].Kind);
        Assert.Equal("Off", session.Fields[3].Value);
        Assert.Equal("Yes", session.Fields[3].Widgets[0].OnState);
        Assert.Equal(PdfFieldKind.Radio, session.Fields[4].Kind);
        Assert.Equal(["F", "M"], session.Fields[4].Widgets.Select(w => w.OnState));
        Assert.Equal(PdfFieldKind.Combo, session.Fields[5].Kind);
        Assert.Equal("Turkey", session.Fields[5].Options[0].Label);
        Assert.Equal("TR", session.Fields[5].Options[0].Export);
        Assert.Equal(PdfFieldKind.List, session.Fields[6].Kind);
    }

    [Fact]
    public async Task Each_kind_of_field_is_filled_and_reads_back_and_the_original_is_untouched()
    {
        var path = MakeForm(Fixtures.Instance.NewOutputDir("edit-fill"));
        var before = File.ReadAllBytes(path);
        var output = await Save(path, new PdfEditRequest
        {
            Fields = new Dictionary<string, string>
            {
                ["name"] = "Yahya Abo Hashem",
                ["notes"] = "First line of the notes\nand a second line",
                ["passport"] = "N1234567",
                ["agree"] = "Yes",
                ["gender"] = "M",
                ["country"] = "TR",
                ["purpose"] = "Work",
            },
        });
        Assert.Equal(before, File.ReadAllBytes(path));
        using var session = Open(output);
        var values = session.Fields.ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("Yahya Abo Hashem", values["name"]);
        Assert.Equal("First line of the notes\nand a second line", values["notes"]);
        Assert.Equal("N1234567", values["passport"]);
        Assert.Equal("Yes", values["agree"]);
        Assert.Equal("M", values["gender"]);
        Assert.Equal("TR", values["country"]);
        Assert.Equal("Work", values["purpose"]);
        var picture = Picture(output, 0, 595);
        Assert.True(AnyInk(picture, 72, 122, 372, 140), "the name isn't drawn");
        Assert.True(AnyInk(picture, 74, 310, 84, 320), "the tick box isn't on");
        Assert.True(AnyInk(picture, 114, 350, 124, 360), "the second radio button isn't on");
        Assert.False(AnyInk(picture, 74, 350, 84, 360), "the first radio button is on");
        Assert.True(AnyInk(picture, 72, 382, 272, 400), "the combo box's choice isn't drawn");
        Assert.True(AnyInk(picture, 72, 417, 272, 480), "the list's choice isn't drawn");
    }

    [Fact]
    public async Task An_arabic_value_is_kept_as_typed_and_shown_shaped_and_right_to_left()
    {
        var path = MakeForm(Fixtures.Instance.NewOutputDir("edit-arabic"));
        const string value = "يحيى أبو هاشم";
        var output = await Save(path, new PdfEditRequest { Fields = new Dictionary<string, string> { ["name"] = value } });
        using (var session = Open(output))
            Assert.Equal(value, session.Fields[0].Value);
        // It shows at the right of its box, where its line starts.
        var picture = Picture(output, 0, 595);
        Assert.True(AnyInk(picture, 300, 122, 372, 140), "nothing at the right of the box");
        Assert.False(AnyInk(picture, 74, 122, 200, 140), "something at the left of the box");
        // In an appearance of its own, with the letters' joined forms in an embedded font.
        var flat = await Save(path, new PdfEditRequest { Fields = new Dictionary<string, string> { ["name"] = value }, Flatten = true }, "flat.pdf");
        using var pdf = PigDocument.Open(flat);
        var letters = pdf.GetPage(1).Letters.Where(l => l.Value.Length == 1 && l.Value[0] > 0x600).ToList();
        Assert.NotEmpty(letters);
        Assert.Contains(letters, l => l.Value[0] is >= 'ﹰ' and <= '﻿');
        Assert.DoesNotContain(letters, l => l.Value[0] == 'ه');
        // The first letter typed is the rightmost.
        var ya = letters.OrderByDescending(l => l.StartBaseLine.X).First();
        Assert.Equal("ي", ya.Value.Normalize(NormalizationForm.FormKC));
    }

    [Theory]
    [InlineData("فاطمة الزهراء")] // Arabic
    [InlineData("پدرام چگونگی")] // Persian
    [InlineData("اردو میں ٹیکسٹ")] // Urdu
    public async Task Other_arabic_script_languages_come_out_joined_too(string value)
    {
        var path = MakeForm(Fixtures.Instance.NewOutputDir("edit-script"));
        var flat = await Save(path, new PdfEditRequest { Fields = new Dictionary<string, string> { ["name"] = value }, Flatten = true });
        using var pdf = PigDocument.Open(flat);
        Assert.Contains(pdf.GetPage(1).Letters, l => l.Value.Length == 1 && l.Value[0] is >= 'ﭐ' and <= '﻿');
    }

    [Fact]
    public async Task Flattening_leaves_no_fields_and_puts_their_text_on_the_page_in_place()
    {
        var path = MakeForm(Fixtures.Instance.NewOutputDir("edit-flat"));
        var output = await Save(path, new PdfEditRequest
        {
            Fields = new Dictionary<string, string> { ["name"] = "Flattened", ["agree"] = "Yes", ["country"] = "SY", ["passport"] = "AB12" },
            Items = [new PdfNoteItem(0, new PdfArea(500, 50, 22, 22), "Look here", 0xFFD23F), new PdfMarkupItem(0, PdfMarkup.Highlight, [new PdfArea(72, 42, 120, 22)], 0xFFD23F)],
            Flatten = true,
        });
        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.False(document.Internals.Catalog.Elements.ContainsKey("/AcroForm"));
            Assert.False(document.Pages[0].Elements.ContainsKey("/Annots"));
        }
        using var session = Open(output);
        Assert.Empty(session.Fields);
        var words = Text(output);
        Assert.Contains("Flattened", words);
        Assert.Contains("Syria", words);
        Assert.Contains("Visa", words);
        Assert.Contains("Look here", words);
        var place = Place(output, 0, "Flattened");
        Assert.InRange(place.Left, 72, 80);
        Assert.InRange(place.Top, 120, 142);
        // Two letters in the comb, each in its own eighth of the box.
        var a = Place(output, 0, "A");
        Assert.InRange(a.Left, 72, 72 + 25);
        var picture = Picture(output, 0, 595);
        Assert.True(AnyInk(picture, 74, 310, 84, 320));
    }

    [Fact]
    public async Task A_field_on_a_turned_page_and_a_widget_turned_by_the_form_stay_in_their_box()
    {
        var dir = Fixtures.Instance.NewOutputDir("edit-turned-field");
        var path = MakeForm(dir, tweak: page => page.Rotate = 90);
        using (var session = Open(path))
        {
            // On a page turned clockwise, the box runs down the left of what was its bottom edge.
            Assert.Equal(new PdfArea(700, 72, 22, 300), session.Fields[0].Widgets[0].Area);
        }
        var output = await Save(path, new PdfEditRequest { Fields = new Dictionary<string, string> { ["name"] = "Turned" } });
        Assert.True(AnyInk(Picture(output, 0, 842), 700, 72, 722, 372));

        // A form that turns its own widget by 90 degrees: the words read up the box, inside it, on an upright page.
        var own = MakeForm(dir, "own.pdf", page =>
        {
            var widget = PdfWalk.Dictionary(page.Elements.GetArray("/Annots")!.Elements[0])!;
            widget.Elements["/Rect"] = Numbers(page.Owner, 100, 300, 130, 600);
            var look = new PdfDictionary(page.Owner);
            look.Elements["/R"] = new PdfInteger(90);
            widget.Elements["/MK"] = look;
        });
        var flat = await Save(own, new PdfEditRequest { Fields = new Dictionary<string, string> { ["name"] = "Going up" }, Flatten = true }, "own out.pdf");
        using var pdf = PigDocument.Open(flat);
        var letters = pdf.GetPage(1).Letters.Where(l => l.Value is "G" or "p" && l.StartBaseLine.Y < 650).ToList();
        Assert.Equal(2, letters.Count);
        foreach (var letter in letters)
        {
            Assert.InRange(letter.StartBaseLine.X, 100, 130);
            Assert.InRange(letter.StartBaseLine.Y, 300, 600);
        }
        var first = letters.First(l => l.Value == "G");
        Assert.True(letters.First(l => l.Value == "p").StartBaseLine.Y > first.StartBaseLine.Y, "It doesn't read upward.");
    }

    [Fact]
    public async Task A_filled_field_under_a_box_goes_when_flattened_too()
    {
        var path = MakeForm(Fixtures.Instance.NewOutputDir("redact-flat"));
        using var session = Open(path);
        var output = Path.Combine(Path.GetDirectoryName(path)!, "out.pdf");
        var place = session.Fields[0].Widgets[0].Area;
        await session.SaveAsync(new PdfEditRequest
        {
            Fields = new Dictionary<string, string> { ["name"] = Secret, ["country"] = "TR" },
            Items = [new PdfRedactItem(0, place)],
            Flatten = true,
        }, output);
        AssertGone(output, Secret);
        Assert.Contains("Turkey", Text(output));
    }

    [Fact]
    public async Task A_line_whose_words_cannot_be_taken_out_is_covered_and_says_so()
    {
        var dir = Fixtures.Instance.NewOutputDir("edit-covered");
        var path = Path.Combine(dir, "wrapped.pdf");
        PdfFonts.EnsureInitialized();
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            // Inside a form, where a retyped line's letters aren't taken out.
            var scratch = document.AddPage();
            scratch.Width = XUnit.FromPoint(595);
            scratch.Height = XUnit.FromPoint(842);
            using (var gfx = XGraphics.FromPdfPage(scratch))
                gfx.DrawString("Old words here", new XFont("Arial", 12), XBrushes.Black, 72, 100);
            var content = scratch.Contents.CreateSingleContent().Stream!.UnfilteredValue;
            var form = PdfEditorParts.Form(document, 0, 0, 595, 842, content, scratch.Elements.GetDictionary("/Resources"));
            document.Pages.Remove(scratch);
            var drawings = new PdfDictionary(document);
            drawings.Elements["/W1"] = form.Reference!;
            page.Resources.Elements["/XObject"] = drawings;
            PdfWalk.SetContent(page, Encoding.ASCII.GetBytes("/W1 Do"));
            document.Save(path);
        }
        using var session = Open(path);
        var line = session.Lines(0)[0];
        var output = Path.Combine(dir, "out.pdf");
        var result = await session.SaveAsync(new PdfEditRequest
        {
            Items = [new PdfReplaceItem(0, line.Area, line.Words.Select(w => w.Area).ToList(), "New", line.Size, line.Colour, false, false, line.FontName, false)],
        }, output);
        Assert.Equal(1, result.CoveredLines);
        var letters = Text(output);
        Assert.Contains('N', letters);
        // What shows is the new word; the old ones, which stay in the file, are under the cover.
        var picture = Picture(output, 0, 595);
        Assert.True(AnyInk(picture, line.Area.Left, line.Area.Top, line.Area.Left + 25, line.Area.Bottom));
        Assert.False(AnyInk(picture, line.Area.Left + 40, line.Area.Top, line.Area.Right, line.Area.Bottom));
    }

    [Fact]
    public async Task A_translated_page_gives_the_translation_alone_and_one_that_cannot_keeps_its_cover()
    {
        var dir = Fixtures.Instance.NewOutputDir("translate-out");
        var plain = MakeTrickyPages(dir);
        static Task<IReadOnlyList<string>> Translate(IReadOnlyList<string> lines, IProgress<double>? _, CancellationToken __) =>
            Task.FromResult<IReadOnlyList<string>>(lines.Select(_ => "Traducido").ToList());
        var output = Path.Combine(dir, "plain.es.pdf");
        await Petal.Engines.Documents.Translate.DocumentTranslator.TranslateAsync(plain, output, "en", "es", Translate);
        foreach (int page in new[] { 0, 1, 2 })
        {
            var text = Text(output, page);
            Assert.Contains("Traducido", text.Replace(" ", ""));
            Assert.DoesNotContain("Alpha", text);
            Assert.DoesNotContain("Closing", text);
        }

        // Text inside a Form XObject can't be cut here: it stays, under a cover of the page's colour.
        var wrapped = Path.Combine(dir, "wrapped.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            var scratch = document.AddPage();
            scratch.Width = XUnit.FromPoint(595);
            scratch.Height = XUnit.FromPoint(842);
            using (var gfx = XGraphics.FromPdfPage(scratch))
                gfx.DrawString("Wrapped words of a line that is here", new XFont("Arial", 12), XBrushes.Black, 72, 100);
            var form = PdfEditorParts.Form(document, 0, 0, 595, 842, scratch.Contents.CreateSingleContent().Stream!.UnfilteredValue, scratch.Elements.GetDictionary("/Resources"));
            document.Pages.Remove(scratch);
            var drawings = new PdfDictionary(document);
            drawings.Elements["/W1"] = form.Reference!;
            page.Resources.Elements["/XObject"] = drawings;
            PdfWalk.SetContent(page, Encoding.ASCII.GetBytes("/W1 Do"));
            document.Save(wrapped);
        }
        var covered = Path.Combine(dir, "wrapped.es.pdf");
        await Petal.Engines.Documents.Translate.DocumentTranslator.TranslateAsync(wrapped, covered, "en", "es", Translate);
        var both = Text(covered);
        Assert.Contains("T", both);
        Assert.Contains("W", both);
        Assert.True(both.Count(c => c == 'p') >= 1, both);
        var place = Place(wrapped, 0, "line");
        Assert.False(AnyInk(Picture(covered, 0, 595), place.Left, place.Top, place.Right, place.Bottom), "The old word still shows.");
    }

    [Fact]
    public async Task A_scanned_page_is_read_with_ocr_and_its_pixels_under_a_box_go()
    {
        if (!Petal.Engines.Documents.Ocr.OcrService.IsAvailable)
            return;
        var dir = Fixtures.Instance.NewOutputDir("redact-scan");
        var path = Path.Combine(dir, "scan.pdf");
        using (var bitmap = new SKBitmap(1654, 2339))
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(SKTypeface.FromFamilyName("Arial"), 60))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            bitmap.Erase(SKColors.White);
            canvas.DrawText("Please write to someone@example.com soon", 120, 400, font, paint);
            canvas.DrawText("Nothing else matters on this page", 120, 700, font, paint);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            using var document = new PdfDocument();
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using (var gfx = XGraphics.FromPdfPage(page))
            using (var picture = XImage.FromStream(new MemoryStream(png.ToArray())))
                gfx.DrawImage(picture, 0, 0, 595, 842);
            document.Save(path);
        }
        using var session = Open(path);
        var found = await session.FindPrivateAsync();
        var box = Assert.Single(found);
        Assert.Contains("someone@example.com", box.Text);
        var output = Path.Combine(dir, "out.pdf");
        var result = await session.SaveAsync(new PdfEditRequest { Items = [new PdfRedactItem(0, box.Area)] }, output);
        Assert.Equal(1, result.PicturePages);
        var after = Picture(output, 0, 595);
        Assert.Equal((0, 0, 0), Pixel(after, box.Area.Left + box.Area.Width / 2, box.Area.Top + box.Area.Height / 2));
        Assert.True(AnyInk(after, 40, 240, 500, 260), "The rest of the scan is gone.");
    }

    // ---------- adding ----------

    /// <summary>What must land where: each kind of thing on each kind of page.</summary>
    [Theory]
    [InlineData(0)] // plain
    [InlineData(1)] // turned by 90°
    [InlineData(2)] // cropped, its box starting at 50,60
    public async Task What_is_added_is_where_it_was_placed(int page)
    {
        var dir = Fixtures.Instance.NewOutputDir("edit-place" + page);
        var path = MakeTrickyPages(dir);
        using (var session = Open(path))
            Assert.Equal(page == 1 ? 842 : page == 2 ? 495 : 595, session.Pages[page].Width, 0);
        using var bitmap = new SKBitmap(40, 40);
        bitmap.Erase(SKColors.Lime);
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        var output = await Save(path, new PdfEditRequest
        {
            Items =
            [
                new PdfTextItem(page, new PdfArea(120, 200, 200, 20), "Added", 14, Red, true),
                new PdfMarkItem(page, new PdfArea(120, 240, 20, 20), PdfMark.Dot, Red),
                new PdfCoverItem(page, new PdfArea(160, 240, 30, 20), 0x2040FF),
                new PdfPictureItem(page, new PdfArea(200, 240, 40, 20), png.ToArray()),
                new PdfMarkupItem(page, PdfMarkup.Highlight, [new PdfArea(120, 280, 80, 14)], 0xFFD23F),
                new PdfNoteItem(page, new PdfArea(120, 310, 24, 24), "A note", 0xFFD23F),
                new PdfLineItem(page, 120, 350, 200, 350, true, 2, 0x00AA00),
                new PdfShapeItem(page, new PdfArea(120, 370, 60, 30), false, 2, 0x00AA00),
                new PdfShapeItem(page, new PdfArea(200, 370, 60, 30), true, 2, 0x00AA00),
                new PdfInkItem(page, [[(280, 370), (340, 370), (340, 400)]], 3, 0x00AA00),
            ],
        });
        var added = Place(output, page, "Added");
        Assert.InRange(added.Left, 119, 122);
        Assert.InRange(added.Top, 198, 214);

        using (var session = Open(output))
            Assert.Equal(page == 1 ? 842 : page == 2 ? 495 : 595, session.Pages[page].Width, 0);
        var picture = Picture(output, page, page == 1 ? 842 : page == 2 ? 495 : 595);
        Assert.Equal((0xE0, 0x20, 0x20), Pixel(picture, 130, 250)); // the dot
        Assert.Equal((0x20, 0x40, 0xFF), Pixel(picture, 175, 250)); // the cover
        var lime = Pixel(picture, 220, 250); // the picture
        Assert.True(lime.G > 240 && lime.R < 20 && lime.B < 20, $"{lime}");
        var highlight = Pixel(picture, 160, 287);
        Assert.True(highlight.R > 240 && highlight.B < 100, $"{highlight}");
        Assert.NotEqual((255, 255, 255), Pixel(picture, 132, 322)); // the note's icon
        Assert.Equal((0, 0xAA, 0), Pixel(picture, 150, 350)); // the line
        Assert.NotEqual((255, 255, 255), Pixel(picture, 120, 385)); // the rectangle's left side
        Assert.Equal((255, 255, 255), Pixel(picture, 150, 385)); // the rectangle isn't filled
        Assert.NotEqual((255, 255, 255), Pixel(picture, 200, 385)); // the ellipse's left end
        Assert.Equal((0, 0xAA, 0), Pixel(picture, 310, 370)); // the pen

        // Real annotations of the right kinds, at the places given when turned back.
        var annotations = Annotations(output, page).Select(a => a.Elements.GetName("/Subtype")).ToList();
        Assert.Equal(["/Highlight", "/Text", "/Line", "/Square", "/Circle", "/Ink"], annotations);
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var space = PdfPageSpace.Of(document.Pages[page]);
        var note = Annotations(output, page)[1];
        Assert.Equal("A note", note.Elements.GetString("/Contents"));
        var rect = PdfWalk.Rectangle(note.Elements["/Rect"])!.Value;
        var shown = space.AreaOf(rect.X1, rect.Y1, rect.X2, rect.Y2);
        Assert.Equal(120, shown.Left, 1);
        Assert.Equal(310, shown.Top, 1);
    }

    [Fact]
    public async Task Typed_arabic_text_is_shaped_and_starts_at_the_right_of_its_box()
    {
        var path = MakeTrickyPages(Fixtures.Instance.NewOutputDir("edit-typed"));
        var output = await Save(path, new PdfEditRequest { Items = [new PdfTextItem(0, new PdfArea(100, 300, 300, 40), "مكتوب هنا", 16, 0)] });
        using var pdf = PigDocument.Open(output);
        var letters = pdf.GetPage(1).Letters.Where(l => l.Value.Length == 1 && l.Value[0] > 0x600).ToList();
        Assert.Contains(letters, l => l.Value[0] is >= 'ﹰ' and <= '﻿');
        Assert.Equal(400, letters.Max(l => l.EndBaseLine.X), 0);
    }

    // ---------- retyping ----------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_retyped_line_loses_its_old_words_and_gets_the_new_ones_in_its_colour_and_place(int page)
    {
        var path = MakeTrickyPages(Fixtures.Instance.NewOutputDir("edit-retype" + page));
        PdfTextLine line;
        using (var session = Open(path))
            line = session.Lines(page).First(l => l.Text.StartsWith("Alpha", StringComparison.Ordinal));
        Assert.Equal(12, line.Size, 0);
        Assert.Equal(0x145AC8, line.Colour);
        var output = await Save(path, new PdfEditRequest
        {
            Items = [new PdfReplaceItem(page, line.Area, line.Words.Select(w => w.Area).ToList(), "Brand new words", line.Size, line.Colour, line.Bold, line.Italic, line.FontName, line.RightToLeft)],
        });
        var words = Text(output, page);
        Assert.DoesNotContain("Alpha", words);
        Assert.Contains("Brandnewwords", words.Replace(" ", ""));
        Assert.Contains("Closing words", words);
        Assert.Contains(Secret, words);
        using (var read = PigDocument.Open(output))
            Assert.DoesNotContain("Alpha", string.Concat(read.GetPage(page + 1).Letters.Select(l => l.Value)));
        var place = Place(output, page, "Brand");
        Assert.Equal(line.Area.Left, place.Left, 0);
        Assert.InRange(place.Top, line.Area.Top - 2, line.Area.Top + 6);
        using var pdf = PigDocument.Open(output);
        var letter = pdf.GetPage(page + 1).Letters.First(l => l.Value == "B");
        var (r, g, b) = letter.Color.ToRGBValues();
        Assert.Equal(0x14, (int)Math.Round(r * 255));
        Assert.Equal(0x5A, (int)Math.Round(g * 255));
        Assert.Equal(0xC8, (int)Math.Round(b * 255));
        Assert.Equal(12, letter.PointSize, 0);
    }

    [Fact]
    public void Letters_are_taken_out_of_kerned_arrays_and_every_other_kind_of_text_operator_with_the_rest_kept_in_place()
    {
        var path = MakeHandWritten(Fixtures.Instance.NewOutputDir("edit-hand"));
        var bytes = File.ReadAllBytes(path);
        List<PdfFound> found;
        using (var session = Open(path))
            found = session.FindAsync("Zebrafish42").GetAwaiter().GetResult().ToList();
        Assert.Equal(3, found.Count);
        var (erased, failed) = PdfContentEraser.Erase(bytes, new Dictionary<int, List<PdfArea>> { [0] = found.Select(f => f.Area).ToList() }, EraseMode.Everything);
        Assert.Empty(failed);
        var result = Path.Combine(Path.GetDirectoryName(path)!, "erased.pdf");
        File.WriteAllBytes(result, erased);
        AssertGone(result, "Zebrafish42");
        AssertGone(result, "Zebra");
        var text = Text(result);
        Assert.Contains("Kerned", text);
        Assert.Contains("after", text);
        Assert.Contains("Next line stays", text);
        Assert.Contains("Third way", text);
        Assert.Contains("here", text);
        // Everything else exactly where it was.
        AssertSamePlace(Place(path, 0, "after"), Place(result, 0, "after"));
        AssertSamePlace(Place(path, 0, "here"), Place(result, 0, "here"));
        AssertSamePlace(Place(path, 0, "too"), Place(result, 0, "too"));
    }

    [Fact]
    public void A_retyped_line_in_a_font_written_with_two_byte_codes_loses_its_words_too()
    {
        var dir = Fixtures.Instance.NewOutputDir("edit-unicode");
        var path = Path.Combine(dir, "unicode.pdf");
        PdfFonts.EnsureInitialized();
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 12, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            gfx.DrawString("Keep this. Remove that.", font, XBrushes.Black, 72, 100);
            gfx.Dispose();
            document.Save(path);
        }
        using var session = Open(path);
        var that = session.Lines(0)[0].Words.Where(w => w.Text is "Remove" or "that.").Select(w => w.Area).ToList();
        var (erased, failed) = PdfContentEraser.Erase(File.ReadAllBytes(path), new Dictionary<int, List<PdfArea>> { [0] = that }, EraseMode.TextOnly);
        Assert.Empty(failed);
        var result = Path.Combine(dir, "out.pdf");
        File.WriteAllBytes(result, erased);
        Assert.Equal("Keep this.", Text(result));
    }

    // ---------- redaction ----------

    /// <summary>A secret in the page's words, its invisible layer, a comment, a form field, a bookmark, the document's details and its XMP.</summary>
    private static string MakeSecretFile(string dir)
    {
        PdfFonts.EnsureInitialized();
        var path = Path.Combine(dir, "secret.pdf");
        using (var document = new PdfDocument())
        {
            document.Info.Title = "About " + Secret;
            document.Info.Author = "Author " + Secret;
            for (int i = 0; i < 2; i++)
            {
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(595);
                page.Height = XUnit.FromPoint(842);
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 12);
                gfx.DrawString($"Before {Secret} after on page {i + 1}", font, XBrushes.Black, 72, 100);
                gfx.DrawString("A line nobody minds", font, XBrushes.Black, 72, 140);
                document.Outlines.Add("Chapter " + Secret, page);
            }
            var note = new PdfDictionary(document);
            note.Elements["/Type"] = new PdfName("/Annot");
            note.Elements["/Subtype"] = new PdfName("/Text");
            note.Elements["/Rect"] = Numbers(document, 400, 400, 420, 420);
            note.Elements["/Contents"] = new PdfString("Comment says " + Secret);
            document.Internals.AddObject(note);
            document.Pages[0].Elements["/Annots"] = new PdfArray(document, note.Reference!);
            document.Save(path);
        }
        Assert.Contains(Secret, Everything(path));
        return path;
    }

    [Fact]
    public async Task Found_words_are_gone_from_everything_and_the_rest_stays_as_vector()
    {
        var path = MakeSecretFile(Fixtures.Instance.NewOutputDir("redact-vector"));
        var before = File.ReadAllBytes(path);
        using var session = Open(path);
        var found = await session.FindAsync(Secret);
        Assert.Equal(2, found.Count);
        var output = Path.Combine(Path.GetDirectoryName(path)!, "out.pdf");
        var result = await session.SaveAsync(new PdfEditRequest { Items = found.Select(f => (PdfPageItem)new PdfRedactItem(f.Page, f.Area)).ToList(), Secrets = [Secret] }, output);
        Assert.Equal(0, result.PicturePages);
        Assert.Equal(before, File.ReadAllBytes(path));
        AssertGone(output, Secret);
        for (int i = 0; i < 2; i++)
        {
            var text = Text(output, i);
            Assert.Contains("Before", text);
            Assert.Contains("after on page", text);
            Assert.Contains("A line nobody minds", text);
            // Black where it was, and no picture on the page.
            var box = found.First(f => f.Page == i).Area;
            var picture = Picture(output, i, 595);
            Assert.Equal((0, 0, 0), Pixel(picture, box.Left + box.Width / 2, box.Top + box.Height / 2));
            using var pdf = PigDocument.Open(output);
            Assert.Empty(pdf.GetPage(i + 1).GetImages());
        }
        // The hidden details went too; words clear of the boxes kept their places.
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.False(document.Internals.Catalog.Elements.ContainsKey("/Metadata"));
        Assert.Empty(Annotations(output, 0));
        AssertSamePlace(Place(path, 0, "Before"), Place(output, 0, "Before"));
        AssertSamePlace(Place(path, 0, "after"), Place(output, 0, "after"));
    }

    [Fact]
    public async Task A_box_alone_takes_what_is_under_it_and_only_what_is_under_it_from_a_turned_and_a_cropped_page()
    {
        var path = MakeTrickyPages(Fixtures.Instance.NewOutputDir("redact-tricky"));
        using var session = Open(path);
        var found = await session.FindAsync(Secret);
        Assert.Equal(3, found.Count);
        var output = Path.Combine(Path.GetDirectoryName(path)!, "out.pdf");
        await session.SaveAsync(new PdfEditRequest { Items = found.Select(f => (PdfPageItem)new PdfRedactItem(f.Page, f.Area.Inflated(0.5))).ToList() }, output);
        AssertGone(output, "Zebra");
        for (int i = 0; i < 3; i++)
        {
            var text = Text(output, i);
            Assert.Contains("Alpha", text);
            Assert.Contains("Second", text);
            Assert.Contains("third fourth", text);
        }
        var picture = Picture(output, 1, 842);
        var box = found.First(f => f.Page == 1).Area;
        Assert.Equal((0, 0, 0), Pixel(picture, box.Left + box.Width / 2, box.Top + box.Height / 2));
    }

    [Fact]
    public async Task A_picture_under_a_box_loses_its_pixels_and_words_outside_stay_searchable()
    {
        var dir = Fixtures.Instance.NewOutputDir("redact-picture");
        var path = Path.Combine(dir, "photo.pdf");
        PdfFonts.EnsureInitialized();
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using (var gfx = XGraphics.FromPdfPage(page))
            using (var image = XImage.FromStream(new MemoryStream(SolidJpeg(new SKColor(30, 160, 90)))))
            {
                gfx.DrawImage(image, 100, 200, 400, 300);
                var font = new XFont("Arial", 12);
                gfx.DrawString($"Over the picture {Secret} word", font, XBrushes.White, 120, 300);
                gfx.DrawString("Outside the picture stays", font, XBrushes.Black, 100, 600);
            }
            document.Save(path);
        }
        var before = File.ReadAllBytes(path);
        var box = new PdfArea(150, 280, 250, 60);
        var output = Path.Combine(dir, "out.pdf");
        using var session = Open(path);
        var result = await session.SaveAsync(new PdfEditRequest { Items = [new PdfRedactItem(0, box)] }, output);
        Assert.Equal(1, result.PicturePages);
        Assert.Equal(before, File.ReadAllBytes(path));
        AssertGone(output, Secret);

        // The same place: green on the original, black now, and the page's one picture holds no green there either.
        var was = Picture(path, 0, 595);
        var now = Picture(output, 0, 595);
        Assert.True(Pixel(was, 300, 320) is { G: > 120, R: < 80 });
        Assert.Equal((0, 0, 0), Pixel(now, 300, 320));
        Assert.True(Pixel(now, 110, 250) is { G: > 120, R: < 80 }, "the picture outside the box is gone");
        using (var pdf = PigDocument.Open(output))
        {
            var image = Assert.Single(pdf.GetPage(1).GetImages());
            // Stored as a JPEG as it is; otherwise as plain samples, three bytes a pixel.
            var raw = image.RawMemory.ToArray();
            double k = image.WidthInSamples / 595.0;
            Func<int, int, (byte R, byte G, byte B)> sample;
            if (raw is [0xFF, 0xD8, ..])
            {
                var decoded = SKBitmap.Decode(raw);
                sample = (x, y) => (decoded.GetPixel(x, y).Red, decoded.GetPixel(x, y).Green, decoded.GetPixel(x, y).Blue);
            }
            else
            {
                Assert.True(image.TryGetBytesAsMemory(out var samples));
                var bytes = samples.ToArray();
                int width = image.WidthInSamples;
                sample = (x, y) => (bytes[(y * width + x) * 3], bytes[(y * width + x) * 3 + 1], bytes[(y * width + x) * 3 + 2]);
            }
            for (double x = box.Left + 1; x < box.Right - 1; x += 3)
                for (double y = box.Top + 1; y < box.Bottom - 1; y += 3)
                {
                    var pixel = sample((int)(x * k), (int)(y * k));
                    Assert.True(pixel.R < 12 && pixel.G < 12 && pixel.B < 12, $"{pixel} at {x},{y}");
                }
        }
        // The invisible layer holds what is outside the box, and only that.
        var text = Text(output);
        Assert.Contains("Outside the picture stays", text);
        Assert.Contains("Over", text);
        Assert.DoesNotContain("word", text);
    }

    [Fact]
    public async Task Text_in_a_form_xobject_under_a_box_leaves_by_the_picture_way()
    {
        var dir = Fixtures.Instance.NewOutputDir("redact-form");
        var path = Path.Combine(dir, "wrapped.pdf");
        PdfFonts.EnsureInitialized();
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            // The whole page in a form, as some producers write it.
            var scratch = document.AddPage();
            scratch.Width = XUnit.FromPoint(595);
            scratch.Height = XUnit.FromPoint(842);
            using (var gfx = XGraphics.FromPdfPage(scratch))
            {
                var font = new XFont("Arial", 12);
                gfx.DrawString($"Wrapped {Secret} text", font, XBrushes.Black, 72, 100);
                gfx.DrawString("Second line in the form", font, XBrushes.Black, 72, 140);
            }
            var content = scratch.Contents.CreateSingleContent().Stream!.UnfilteredValue;
            var form = PdfEditorParts.Form(document, 0, 0, 595, 842, content, scratch.Elements.GetDictionary("/Resources"));
            document.Pages.Remove(scratch);
            var drawings = new PdfDictionary(document);
            drawings.Elements["/W1"] = form.Reference!;
            page.Resources.Elements["/XObject"] = drawings;
            PdfWalk.SetContent(page, Encoding.ASCII.GetBytes("/W1 Do"));
            document.Save(path);
        }
        Assert.Contains(Secret, Text(path));
        using var session = Open(path);
        var found = await session.FindAsync(Secret);
        var output = Path.Combine(dir, "out.pdf");
        var result = await session.SaveAsync(new PdfEditRequest { Items = found.Select(f => (PdfPageItem)new PdfRedactItem(f.Page, f.Area)).ToList() }, output);
        Assert.Equal(1, result.PicturePages);
        AssertGone(output, Secret);
        Assert.Contains("Second line in the form", Text(output));
    }

    [Theory]
    [InlineData("/Helvetica", null)]
    [InlineData("/Times-Roman", "/WinAnsiEncoding")]
    [InlineData("/Courier-Bold", "/MacRomanEncoding")]
    public async Task A_standard_font_named_without_widths_keeps_its_page_as_vector(string family, string? encoding)
    {
        var dir = Fixtures.Instance.NewOutputDir("redact-standard14" + family.Trim('/'));
        var path = Path.Combine(dir, "standard.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            var font = new PdfDictionary(document);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type1");
            font.Elements["/BaseFont"] = new PdfName(family);
            if (encoding is not null)
                font.Elements["/Encoding"] = new PdfName(encoding);
            document.Internals.AddObject(font);
            var fonts = new PdfDictionary(document);
            fonts.Elements["/F1"] = font.Reference!;
            page.Resources.Elements["/Font"] = fonts;
            PdfWalk.SetContent(page, Encoding.ASCII.GetBytes($"BT /F1 12 Tf 72 700 Td (Plain {Secret} words) Tj 0 -20 Td (More here) Tj 0 -20 Td (Old line) Tj ET"));
            document.Save(path);
        }
        using var session = Open(path);
        var found = await session.FindAsync(Secret);
        var line = session.Lines(0).Single(l => l.Text == "Old line");
        var output = Path.Combine(dir, "out.pdf");
        var result = await session.SaveAsync(new PdfEditRequest
        {
            Items =
            [
                new PdfRedactItem(0, found.Single().Area),
                new PdfReplaceItem(0, line.Area, line.Words.Select(w => w.Area).ToList(), "New text", line.Size, line.Colour, false, false, line.FontName, false),
            ],
        }, output);
        Assert.Equal(new PdfEditResult(0, 0), result);
        AssertGone(output, Secret);
        var left = Text(output);
        Assert.Contains("words", left);
        Assert.Contains("More here", left);
        Assert.Contains("New text", left);
        Assert.DoesNotContain("Old line", left);
    }

    [Theory]
    [InlineData("محمد الخطيب", "محمد الخطیب")]
    [InlineData("كتاب مكتبة", "کتاب مکتبة")]
    [InlineData("أحمد", "احمد")]
    [InlineData("مُحَمَّد", "محمد")]
    public void A_search_doesnt_care_how_arabic_letters_are_stored(string asked, string stored) =>
        Assert.Single(PdfTextReader.Find([new PdfTextReader.TextRun(stored, Enumerable.Repeat(new PdfArea(0, 0, 5, 5), stored.Length).ToArray())], asked));

    [Fact]
    public async Task A_font_without_widths_falls_to_the_picture_way_and_the_word_still_goes()
    {
        var dir = Fixtures.Instance.NewOutputDir("redact-standard");
        var path = Path.Combine(dir, "standard.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            var font = new PdfDictionary(document);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type1");
            font.Elements["/BaseFont"] = new PdfName("/HouseFont");
            document.Internals.AddObject(font);
            var fonts = new PdfDictionary(document);
            fonts.Elements["/F1"] = font.Reference!;
            page.Resources.Elements["/Font"] = fonts;
            PdfWalk.SetContent(page, Encoding.ASCII.GetBytes($"BT /F1 12 Tf 72 700 Td (Plain {Secret} words) Tj 0 -20 Td (More here) Tj ET"));
            document.Save(path);
        }
        using var session = Open(path);
        var found = await session.FindAsync(Secret);
        Assert.Single(found);
        var output = Path.Combine(dir, "out.pdf");
        await session.SaveAsync(new PdfEditRequest { Items = [new PdfRedactItem(0, found[0].Area)] }, output);
        AssertGone(output, Secret);
        Assert.Contains("More here", Text(output));
    }

    [Fact]
    public async Task A_field_on_the_area_and_a_value_found_elsewhere_go_with_the_box()
    {
        var path = MakeForm(Fixtures.Instance.NewOutputDir("redact-field"));
        var filled = await Save(path, new PdfEditRequest { Fields = new Dictionary<string, string> { ["name"] = "Mr " + Secret, ["notes"] = "Also " + Secret + " here", ["country"] = "TR" } }, "filled.pdf");
        Assert.Contains(Secret, Everything(filled));
        using var session = Open(filled);
        var found = await session.FindAsync(Secret);
        Assert.Equal(2, found.Count);
        var output = Path.Combine(Path.GetDirectoryName(path)!, "out.pdf");
        await session.SaveAsync(new PdfEditRequest { Items = found.Select(f => (PdfPageItem)new PdfRedactItem(f.Page, f.Area)).ToList(), Secrets = [Secret] }, output);
        AssertGone(output, Secret);
        using var after = Open(output);
        Assert.DoesNotContain(after.Fields, f => f.Name is "name" or "notes");
        Assert.Contains(after.Fields, f => f.Name == "country" && f.Value == "TR");
    }

    [Fact]
    public async Task Metadata_stays_when_the_person_keeps_it_except_where_it_holds_what_is_redacted()
    {
        var dir = Fixtures.Instance.NewOutputDir("redact-keepinfo");
        var path = Path.Combine(dir, "info.pdf");
        PdfFonts.EnsureInitialized();
        using (var document = new PdfDocument())
        {
            document.Info.Title = "Title with " + Secret;
            document.Info.Subject = "A subject to keep";
            var page = document.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Words {Secret} here", new XFont("Arial", 12), XBrushes.Black, 72, 100);
            gfx.Dispose();
            document.Save(path);
        }
        using var session = Open(path);
        var found = await session.FindAsync(Secret);
        var output = Path.Combine(dir, "out.pdf");
        await session.SaveAsync(new PdfEditRequest { Items = found.Select(f => (PdfPageItem)new PdfRedactItem(f.Page, f.Area)).ToList(), Secrets = [Secret], RemoveHiddenDetails = false }, output);
        AssertGone(output, Secret);
        using var after = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal("A subject to keep", after.Info.Subject);
    }

    [Fact]
    public async Task Find_private_details_finds_each_kind_with_its_place()
    {
        var dir = Fixtures.Instance.NewOutputDir("redact-private");
        var path = Path.Combine(dir, "details.pdf");
        PdfFonts.EnsureInitialized();
        string[] lines =
        [
            "Write to someone@example.com today",
            "Call +90 532 123 45 67 now",
            "Card 4111 1111 1111 1111 please",
            "IBAN TR33 0006 1005 1978 6457 8413 26 here",
            "Server 192.168.10.20 down",
            "password: hunter2xyz",
            "Nothing private on this line",
        ];
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 11);
            for (int i = 0; i < lines.Length; i++)
                gfx.DrawString(lines[i], font, XBrushes.Black, 60, 80 + 24 * i);
            gfx.Dispose();
            document.Save(path);
        }
        using var session = Open(path);
        var found = await session.FindPrivateAsync();
        Assert.Equal(6, found.Count);
        var output = Path.Combine(dir, "out.pdf");
        await session.SaveAsync(new PdfEditRequest { Items = found.Select(f => (PdfPageItem)new PdfRedactItem(f.Page, f.Area)).ToList() }, output);
        foreach (var gone in new[] { "someone@example.com", "532", "4111", "TR33", "192.168", "hunter2xyz" })
            AssertGone(output, gone);
        var text = Text(output);
        foreach (var kept in new[] { "Write to", "today", "Call", "now", "Card", "please", "IBAN", "here", "Server", "down", "password:", "Nothing private on this line" })
            Assert.Contains(kept, text);
    }

    [Fact]
    public async Task What_was_added_under_a_box_goes_too()
    {
        var path = MakeTrickyPages(Fixtures.Instance.NewOutputDir("redact-added"));
        using var session = Open(path);
        var output = Path.Combine(Path.GetDirectoryName(path)!, "out.pdf");
        await session.SaveAsync(new PdfEditRequest
        {
            Items =
            [
                new PdfTextItem(0, new PdfArea(100, 300, 200, 20), "Typed Mistake", 12, 0),
                new PdfNoteItem(0, new PdfArea(100, 340, 20, 20), "Comment mistake", 0xFFD23F),
                new PdfRedactItem(0, new PdfArea(90, 295, 260, 70)),
            ],
        }, output);
        AssertGone(output, "Mistake");
        Assert.Empty(Annotations(output, 0));
    }

    // ---------- protected files ----------

    [Fact]
    public async Task A_protected_pdf_asks_for_its_password_and_its_edited_copy_keeps_it()
    {
        var dir = Fixtures.Instance.NewOutputDir("edit-password");
        var plain = MakeForm(dir, "plain.pdf");
        var locked = Path.Combine(dir, "locked.pdf");
        await PdfSecurityTools.ProtectAsync(plain, locked, new PdfProtectOptions("sesame", AllowEditing: true));

        Assert.True(Assert.Throws<PdfOpenException>(() => Open(locked)).NeedsPassword);
        Assert.True(Assert.Throws<PdfOpenException>(() => Open(locked, "wrong")).NeedsPassword);
        using var session = Open(locked, "sesame");
        Assert.Equal(PdfEditRights.Full, session.Rights);
        Assert.Equal(7, session.Fields.Count);
        var output = Path.Combine(dir, "out.pdf");
        await session.SaveAsync(new PdfEditRequest { Fields = new Dictionary<string, string> { ["name"] = "Behind a password" } }, output);
        Assert.True(PdfSecurityTools.NeedsPassword(output));
        using var again = Open(output, "sesame");
        Assert.Equal("Behind a password", again.Fields[0].Value);
    }

    [Fact]
    public async Task A_form_locked_by_its_author_can_be_filled_as_far_as_the_author_allows()
    {
        var dir = Fixtures.Instance.NewOutputDir("edit-locked");
        var plain = MakeForm(dir, "plain.pdf");
        var locked = Path.Combine(dir, "locked.pdf");
        using (var document = PdfReader.Open(plain, PdfDocumentOpenMode.Modify))
        {
            document.SecurityHandler.SetEncryptionToV5(true);
            document.SecuritySettings.OwnerPassword = "owner-only";
            document.SecuritySettings.PermitModifyDocument = false;
            document.SecuritySettings.PermitAnnotations = false;
            document.SecuritySettings.PermitFormsFill = true;
            document.SecuritySettings.PermitPrint = false;
            document.SecuritySettings.PermitExtractContent = false;
            document.Save(locked);
        }
        using var session = Open(locked);
        Assert.Equal(PdfEditRights.Fill, session.Rights);
        var output = Path.Combine(dir, "out.pdf");
        await session.SaveAsync(new PdfEditRequest { Fields = new Dictionary<string, string> { ["name"] = "Filled while locked" } }, output);
        // It opens as it did, its limits still on it.
        using var again = Open(output);
        Assert.Equal(PdfEditRights.Fill, again.Rights);
        Assert.Equal("Filled while locked", again.Fields[0].Value);
        using var limits = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.False(limits.SecuritySettings.PermitPrint);
        Assert.False(limits.SecuritySettings.PermitExtractContent);
    }

    [Fact]
    public void A_file_with_no_rights_for_changes_says_so()
    {
        var dir = Fixtures.Instance.NewOutputDir("edit-norights");
        var plain = MakeForm(dir, "plain.pdf");
        var locked = Path.Combine(dir, "locked.pdf");
        using (var document = PdfReader.Open(plain, PdfDocumentOpenMode.Modify))
        {
            document.SecurityHandler.SetEncryptionToV5(true);
            document.SecuritySettings.OwnerPassword = "owner-only";
            document.SecuritySettings.PermitModifyDocument = false;
            document.SecuritySettings.PermitAnnotations = false;
            document.SecuritySettings.PermitFormsFill = false;
            document.Save(locked);
        }
        using var session = Open(locked);
        Assert.Equal(PdfEditRights.None, session.Rights);
    }

    // ---------- looking at the results ----------

    /// <summary>Set PETAL_EDITPDF_LOOK to a folder to get these results as pictures, to look at by eye.</summary>
    [Fact]
    public async Task Results_as_pictures_when_asked()
    {
        var look = Environment.GetEnvironmentVariable("PETAL_EDITPDF_LOOK");
        if (string.IsNullOrEmpty(look))
            return;
        Directory.CreateDirectory(look);
        var dir = Fixtures.Instance.NewOutputDir("edit-look");
        void Keep(string pdf, string name, int page = 0, int width = 800) => File.WriteAllBytes(Path.Combine(look, name + ".png"), PdfInfo.RenderPagePng(pdf, page, width));

        var form = MakeForm(dir);
        Keep(await Save(form, new PdfEditRequest
        {
            Fields = new Dictionary<string, string>
            {
                ["name"] = "Yahya Abo Hashem",
                ["notes"] = "First line of the notes\nand a second line that is long enough to wrap around inside the box when it is drawn here",
                ["passport"] = "N1234567",
                ["agree"] = "Yes",
                ["gender"] = "M",
                ["country"] = "TR",
                ["purpose"] = "Work",
            },
        }, "en.pdf"), "form-en");
        Keep(await Save(form, new PdfEditRequest
        {
            Fields = new Dictionary<string, string>
            {
                ["name"] = "يحيى أبو هاشم",
                ["notes"] = "السطر الأول من الملاحظات\nوسطر ثان طويل بما يكفي ليلتف داخل المربع عندما يُرسم هنا، مع كلمة English في وسطه",
                ["passport"] = "N1234567",
                ["agree"] = "Yes",
                ["gender"] = "F",
                ["country"] = "SY",
                ["purpose"] = "Family",
            },
        }, "ar.pdf"), "form-ar");
        Keep(await Save(form, new PdfEditRequest
        {
            Fields = new Dictionary<string, string> { ["name"] = "محمد أحمد", ["notes"] = "پدرام چگونگی و اردو میں ٹیکسٹ", ["agree"] = "Yes", ["country"] = "TR" },
            Flatten = true,
        }, "flat.pdf"), "form-flat");

        var pages = MakeTrickyPages(dir);
        foreach (int page in new[] { 0, 1, 2 })
        {
            using var session = Open(pages);
            var line = session.Lines(page).First(l => l.Text.StartsWith("Alpha", StringComparison.Ordinal));
            var found = await session.FindAsync(Secret);
            var output = Path.Combine(dir, $"marks{page}.pdf");
            await session.SaveAsync(new PdfEditRequest
            {
                Items =
                [
                    new PdfReplaceItem(page, line.Area, line.Words.Select(w => w.Area).ToList(), "Retyped line in its colour", line.Size, line.Colour, line.Bold, line.Italic, line.FontName, false),
                    new PdfTextItem(page, new PdfArea(80, 200, 300, 40), "Typed English and عربي هنا", 14, Red, true),
                    new PdfTextItem(page, new PdfArea(80, 230, 300, 60), "نص عربي مكتوب في مربع وهو طويل بما يكفي ليأخذ سطرين من الكتابة على الأقل", 12, 0),
                    new PdfMarkItem(page, new PdfArea(80, 280, 18, 18), PdfMark.Check, 0x00AA00),
                    new PdfMarkItem(page, new PdfArea(110, 280, 18, 18), PdfMark.Cross, Red),
                    new PdfMarkItem(page, new PdfArea(140, 280, 18, 18), PdfMark.Dot, 0x2060E0),
                    new PdfMarkupItem(page, PdfMarkup.Highlight, [new PdfArea(80, 149, 70, 14)], 0xFFD23F),
                    new PdfMarkupItem(page, PdfMarkup.Underline, [new PdfArea(155, 149, 40, 14)], Red),
                    new PdfMarkupItem(page, PdfMarkup.StrikeOut, [new PdfArea(200, 149, 40, 14)], Red),
                    new PdfNoteItem(page, new PdfArea(300, 280, 24, 24), "A comment", 0xFFD23F),
                    new PdfLineItem(page, 80, 330, 200, 330, false, 2, 0x2060E0),
                    new PdfLineItem(page, 80, 350, 200, 370, true, 2, Red),
                    new PdfShapeItem(page, new PdfArea(220, 320, 60, 50), false, 2, 0x00AA00),
                    new PdfShapeItem(page, new PdfArea(300, 320, 60, 50), true, 2, 0x00AA00),
                    new PdfCoverItem(page, new PdfArea(80, 124, 40, 16)),
                    new PdfInkItem(page, [[(80, 400), (100, 390), (120, 410), (140, 390), (160, 400)]], 2.5, 0x7B5CFF),
                    new PdfRedactItem(page, found.First(f => f.Page == page).Area),
                ],
            }, output);
            Keep(output, $"marks{page}", page);
        }
    }
}
