using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using System.Text;

namespace Petal.Engines.Documents.Pdf;

/// <summary>A small application form with one field of each kind and some private details in its text: what the design snapshots open.</summary>
public static class PdfSampleForm
{
    public static void Write(string path)
    {
        PdfFonts.EnsureInitialized();
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        var fields = new PdfArray(document);
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var title = new XFont("Arial", 18, XFontStyleEx.Bold);
            var body = new XFont("Arial", 11);
            var arabic = new XFont("Arial", 11, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            gfx.DrawString("Visa application form", title, XBrushes.Black, 72, 50);
            gfx.DrawString("Please fill in every part of this form in capital letters.", body, XBrushes.Black, 72, 78);
            gfx.DrawString("Full name", body, XBrushes.Black, 72, 112);
            gfx.DrawString(PdfFonts.Drawable(Text.Bidi.ToVisual("الاسم بالعربية", true)), arabic, XBrushes.Black, 72, 162);
            gfx.DrawString("Address", body, XBrushes.Black, 72, 212);
            gfx.DrawString("Country", body, XBrushes.Black, 72, 292);
            gfx.DrawString("Passport number", body, XBrushes.Black, 72, 342);
            gfx.DrawString("Purpose of travel", body, XBrushes.Black, 72, 392);
            gfx.DrawString("I agree to the terms", body, XBrushes.Black, 96, 482);
            gfx.DrawString("Contact: visa.office@example.com or +90 532 123 45 67", body, XBrushes.Black, 72, 520);
            gfx.DrawString("Bank: IBAN TR33 0006 1005 1978 6457 8413 26", body, XBrushes.Black, 72, 540);
            gfx.DrawString("Office use only. Reference 2026-418.", body, XBrushes.Gray, 72, 575);
        }
        PdfDictionary Widget(string name, string type, double left, double top, double width, double height)
        {
            var space = PdfPageSpace.Of(page);
            var widget = new PdfDictionary(document);
            widget.Elements["/Type"] = new PdfName("/Annot");
            widget.Elements["/Subtype"] = new PdfName("/Widget");
            widget.Elements["/FT"] = new PdfName(type);
            widget.Elements["/T"] = new PdfString(name);
            widget.Elements["/Rect"] = new PdfArray(document, new PdfReal(left), new PdfReal(space.Height - top - height), new PdfReal(left + width), new PdfReal(space.Height - top));
            widget.Elements["/F"] = new PdfInteger(4);
            widget.Elements["/DA"] = new PdfString("/Helv 11 Tf 0 g");
            document.Internals.AddObject(widget);
            if (page.Elements.GetArray("/Annots") is not { } annots)
                page.Elements["/Annots"] = annots = new PdfArray(document);
            annots.Elements.Add(widget.Reference!);
            fields.Elements.Add(widget.Reference!);
            return widget;
        }
        Widget("name", "/Tx", 72, 118, 300, 22);
        Widget("native", "/Tx", 72, 168, 300, 22);
        Widget("address", "/Tx", 72, 218, 300, 52).Elements["/Ff"] = new PdfInteger(1 << 12);
        var country = Widget("country", "/Ch", 72, 298, 200, 22);
        country.Elements["/Ff"] = new PdfInteger(1 << 17);
        country.Elements["/Opt"] = new PdfArray(document, new PdfArray(document, new PdfString("TR"), new PdfString("Turkey")), new PdfArray(document, new PdfString("SY"), new PdfString("Syria")));
        var passport = Widget("passport", "/Tx", 72, 348, 200, 24);
        passport.Elements["/Ff"] = new PdfInteger(1 << 24);
        passport.Elements["/MaxLen"] = new PdfInteger(9);
        var purpose = Widget("purpose", "/Ch", 72, 398, 200, 58);
        purpose.Elements["/Opt"] = new PdfArray(document, new PdfString("Study"), new PdfString("Work"), new PdfString("Family"));
        var agree = Widget("agree", "/Btn", 72, 470, 16, 16);
        agree.Elements["/AS"] = new PdfName("/Off");
        var normal = new PdfDictionary(document);
        normal.Elements["/Yes"] = PdfEditorParts.Form(document, 0, 0, 16, 16, "0 0 0 RG 2 w 3 8 m 7 3 l 13 13 l S").Reference!;
        normal.Elements["/Off"] = PdfEditorParts.Form(document, 0, 0, 16, 16, Encoding.ASCII.GetBytes(" ")).Reference!;
        var appearance = new PdfDictionary(document);
        appearance.Elements["/N"] = normal;
        agree.Elements["/AP"] = appearance;
        var form = new PdfDictionary(document);
        form.Elements["/Fields"] = fields;
        form.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
        document.Internals.Catalog.Elements["/AcroForm"] = form;
        PdfSharpIO.Save(document, path);
    }
}
