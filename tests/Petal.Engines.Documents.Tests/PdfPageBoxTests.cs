using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Petal.Engines.Documents.Pdf;

namespace Petal.Engines.Documents.Tests;

/// <summary>
/// PDFsharp's PdfPage.CropBox property adds an empty [0 0 0 0] crop box to a page that has none.
/// Acrobat then shows the page as a 3 × 3 point dot and reports the file as damaged, while PDFium
/// (Edge, Chrome) and PdfPig ignore the box, which hid the problem. These tests read the boxes
/// back from the saved file.
/// </summary>
public sealed class PdfPageBoxTests
{
    /// <summary>Two A4 pages without a crop box (as Word, iText and most tools write them) and one with.</summary>
    private static string Sample(string dir, string name = "sample.pdf", Action<PdfPage, int>? tweak = null)
    {
        var path = Path.Combine(dir, name);
        using var document = new PdfDocument();
        for (int i = 0; i < 3; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using (var gfx = XGraphics.FromPdfPage(page))
                gfx.DrawRectangle(XBrushes.SteelBlue, 100, 100, 200, 100);
            if (i == 2)
                page.Elements.SetRectangle("/CropBox", new PdfRectangle(new XPoint(50, 60), new XPoint(545, 782)));
            tweak?.Invoke(page, i);
        }
        document.Save(path);
        return path;
    }

    /// <summary>Each page's boxes as written in the file: null when the page has none.</summary>
    internal static List<(PdfRectangle? Crop, PdfRectangle Media)> Boxes(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.Pages.Cast<PdfPage>()
            .Select(p => (p.Elements.ContainsKey("/CropBox") ? p.Elements.GetRectangle("/CropBox") : (PdfRectangle?)null, p.Elements.GetRectangle("/MediaBox")))
            .ToList();
    }

    internal static void AssertNoEmptyBoxes(string path)
    {
        foreach (var (crop, media) in Boxes(path))
        {
            Assert.True(media.Width > 1 && media.Height > 1);
            if (crop is { } c)
                Assert.True(c.Width > 1 && c.Height > 1, $"empty crop box [{c.X1} {c.Y1} {c.X2} {c.Y2}]");
        }
    }

    [Fact]
    public async Task Numbering_keeps_pages_without_a_crop_box_whole()
    {
        var dir = Fixtures.Instance.NewOutputDir("boxes-number");
        var output = Path.Combine(dir, "numbered.pdf");
        await PdfStamper.NumberPagesAsync(Sample(dir), output, new PageNumberOptions());

        AssertNoEmptyBoxes(output);
        var boxes = Boxes(output);
        Assert.Null(boxes[0].Crop);
        Assert.Null(boxes[1].Crop);
        Assert.Equal(495, boxes[2].Crop!.Width, 1);
        Assert.All(PdfStamper.Pages(output).Take(2), p => Assert.Equal((595, 842), ((int)p.Width, (int)p.Height)));
    }

    [Fact]
    public async Task Watermarks_signatures_and_same_width_merges_keep_pages_whole()
    {
        var dir = Fixtures.Instance.NewOutputDir("boxes-other");
        var input = Sample(dir);

        var stamped = Path.Combine(dir, "stamped.pdf");
        await PdfStamper.StampAsync(input, stamped, _ => null);
        AssertNoEmptyBoxes(stamped);
        Assert.Null(Boxes(stamped)[0].Crop);

        var organized = Path.Combine(dir, "organized.pdf");
        await PdfPageTools.OrganizeAsync(input, organized, [new PdfPageOperation(0), new PdfPageOperation(2)], new PdfOrganizeOptions(UniformWidth: true));
        AssertNoEmptyBoxes(organized);

        var merged = Path.Combine(dir, "merged.pdf");
        await PdfPageTools.MergeAsync([input, stamped], merged);
        AssertNoEmptyBoxes(merged);
    }

    [Fact]
    public async Task Pages_damaged_by_earlier_versions_are_repaired()
    {
        var dir = Fixtures.Instance.NewOutputDir("boxes-repair");
        // What Petal 1.0 and 1.1 wrote: an empty crop box on every numbered page.
        var damaged = Sample(dir, "damaged.pdf", (page, i) => page.Elements.SetRectangle("/CropBox", new PdfRectangle()));
        Assert.Equal(0, Boxes(damaged)[0].Crop!.Width);

        var output = Path.Combine(dir, "numbered again.pdf");
        await PdfStamper.NumberPagesAsync(damaged, output, new PageNumberOptions());
        AssertNoEmptyBoxes(output);
        Assert.All(PdfStamper.Pages(output), p => Assert.Equal((595, 842), ((int)p.Width, (int)p.Height)));

        var compressed = Path.Combine(dir, "merged again.pdf");
        await PdfPageTools.MergeAsync([damaged], compressed);
        AssertNoEmptyBoxes(compressed);
    }

    [Fact]
    public async Task A_pdf_locked_against_changes_is_reported_as_such_not_as_needing_a_password()
    {
        var dir = Fixtures.Instance.NewOutputDir("boxes-locked");
        var input = Sample(dir);
        var locked = Path.Combine(dir, "locked.pdf");
        using (var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            document.SecurityHandler.SetEncryptionToV4UsingAES(true);
            document.SecuritySettings.OwnerPassword = "owner-only";
            document.SecuritySettings.PermitModifyDocument = false;
            document.Save(locked);
        }

        Assert.False(PdfSecurityTools.NeedsPassword(locked));
        var error = await Assert.ThrowsAsync<PdfOpenException>(() => PdfStamper.NumberPagesAsync(locked, Path.Combine(dir, "out.pdf"), new PageNumberOptions()));
        Assert.True(error.LockedForChanges);
        Assert.False(error.NeedsPassword);
        Assert.Contains("locked against changes", error.Message);
    }
}
