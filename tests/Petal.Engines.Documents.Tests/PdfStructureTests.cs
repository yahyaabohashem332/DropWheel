using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Petal.Engines.Documents.Pdf;

namespace Petal.Engines.Documents.Tests;

/// <summary>
/// Outputs must be structurally sound, not just readable by lenient viewers: every object a file
/// refers to must be in it. PDFsharp writes a reference to an empty bookmark tree without writing
/// the tree, which qpdf and Acrobat's repair report as damage; PDFium and PdfPig shrug it off.
/// </summary>
public sealed partial class PdfStructureTests
{
    private static string Plain(string dir, string name, int pages)
    {
        var path = Path.Combine(dir, name);
        using var document = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.Teal, 50 + 20 * i, 50, 100, 100);
        }
        document.Save(path);
        return path;
    }

    /// <summary>
    /// Every "N G R" in the file has an "N G obj". Files with object streams (Word, Excel and
    /// PowerPoint exports) keep objects compressed where text can't see them, so they are skipped;
    /// PDFsharp, which writes everything Petal makes itself, never uses them.
    /// </summary>
    internal static void AssertReferencesResolve(string path)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        if (text.Contains("/ObjStm", StringComparison.Ordinal))
            return;
        var defined = Defined().Matches(text).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToHashSet();
        var missing = Reference().Matches(text)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .Where(r => !defined.Contains(r))
            .Distinct()
            .ToList();
        Assert.True(missing.Count == 0, $"{Path.GetFileName(path)} refers to objects it doesn't contain: {string.Join(", ", missing.Select(r => $"{r.Item1} {r.Item2} R"))}");
    }

    [Fact]
    public async Task Merging_files_without_bookmarks_leaves_no_dangling_objects()
    {
        var dir = Fixtures.Instance.NewOutputDir("structure-merge");
        var a = Plain(dir, "a.pdf", 2);
        var b = Plain(dir, "b.pdf", 1);

        var merged = Path.Combine(dir, "merged.pdf");
        await PdfPageTools.MergeAsync([a, b], merged);
        AssertReferencesResolve(merged);

        var withFileBookmarks = Path.Combine(dir, "merged with bookmarks.pdf");
        await PdfPageTools.MergeAsync([a, b], withFileBookmarks, new PdfMergeOptions(BookmarkEachFile: true));
        AssertReferencesResolve(withFileBookmarks);
        using var pig = UglyToad.PdfPig.PdfDocument.Open(withFileBookmarks);
        Assert.True(pig.TryGetBookmarks(out var bookmarks));
        Assert.Equal(["a", "b"], bookmarks.Roots.Select(r => r.Title));
    }

    [Fact]
    public async Task Organizing_splitting_and_unlocking_leave_no_dangling_objects()
    {
        var dir = Fixtures.Instance.NewOutputDir("structure-organize");
        var input = Plain(dir, "in.pdf", 3);

        var organized = Path.Combine(dir, "organized.pdf");
        await PdfPageTools.OrganizeAsync(input, organized, [new PdfPageOperation(2, 90), new PdfPageOperation(0)], new PdfOrganizeOptions(UniformWidth: true));
        AssertReferencesResolve(organized);

        foreach (var page in await PdfPageTools.SplitAsync(input, Path.Combine(dir, "split")))
            AssertReferencesResolve(page);

        var locked = Path.Combine(dir, "locked.pdf");
        await PdfSecurityTools.ProtectAsync(input, locked, new PdfProtectOptions("1234"));
        var unlocked = Path.Combine(dir, "unlocked.pdf");
        await PdfSecurityTools.UnlockAsync(locked, unlocked, "1234");
        AssertReferencesResolve(unlocked);

        var numbered = Path.Combine(dir, "numbered.pdf");
        await PdfStamper.NumberPagesAsync(input, numbered, new PageNumberOptions());
        AssertReferencesResolve(numbered);
    }

    [GeneratedRegex(@"(?<![\d.])(\d+) (\d+) obj\b")]
    private static partial Regex Defined();

    [GeneratedRegex(@"(?<![\d.])(\d+) (\d+) R\b")]
    private static partial Regex Reference();
}

public sealed class OfficeCleaningTests
{
    [Fact]
    public async Task Office_files_lose_author_company_and_custom_properties_and_stay_valid()
    {
        var dir = Fixtures.Instance.NewOutputDir("office-clean");
        var input = Path.Combine(dir, "report.docx");
        File.Copy(Fixtures.Instance.Get("docx"), input);
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(input, true))
        {
            doc.PackageProperties.Creator = "Yahya";
            doc.PackageProperties.LastModifiedBy = "Yahya";
            var app = doc.ExtendedFilePropertiesPart ?? doc.AddExtendedFilePropertiesPart();
            app.Properties ??= new DocumentFormat.OpenXml.ExtendedProperties.Properties();
            app.Properties.Company = new DocumentFormat.OpenXml.ExtendedProperties.Company("Secret Ltd");
            app.Properties.Save();
            var custom = doc.CustomFilePropertiesPart ?? doc.AddCustomFilePropertiesPart();
            custom.Properties = new DocumentFormat.OpenXml.CustomProperties.Properties();
            custom.Properties.Save();
        }

        var output = Path.Combine(dir, "report Clean.docx");
        await Petal.Engines.Documents.Office.OfficeMetadata.CleanAsync(input, output);

        using (var clean = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(output, false))
        {
            Assert.Null(clean.PackageProperties.Creator);
            Assert.Null(clean.PackageProperties.LastModifiedBy);
            Assert.Null(clean.ExtendedFilePropertiesPart?.Properties?.Company);
            Assert.Null(clean.CustomFilePropertiesPart);
            Assert.Contains(Fixtures.Token, clean.MainDocumentPart!.Document.InnerText);
        }
        ConversionPathTests.AssertStrictlyValid(output, Petal.Core.Formats.Formats.Docx);
        using var original = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(input, false);
        Assert.Equal("Yahya", original.PackageProperties.Creator);
    }
}
