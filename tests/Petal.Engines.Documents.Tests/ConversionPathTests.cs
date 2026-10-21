using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Converters;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Tests;

/// <summary>Office automation is not parallelised: every test that may start Word, Excel or PowerPoint lives here.</summary>
[CollectionDefinition("Office", DisableParallelization = true)]
public sealed class OfficeCollection;

[Collection("Office")]
public sealed class ConversionPathTests(ITestOutputHelper output)
{
    private static readonly FileFormat[] Sources =
        [F.Pdf, F.Docx, F.Odt, F.Rtf, F.Md, F.Html, F.Txt, F.Epub, F.Pptx, F.Ppt, F.Xlsx, F.Xls, F.Ods, F.Csv, F.Tsv];

    public static IEnumerable<object[]> Paths() =>
        ConversionMatrix.AllPaths()
            .Where(p => Sources.Contains(p.From))
            .OrderBy(p => p.From.Id).ThenBy(p => p.To.Id)
            .Select(p => new object[] { p.From.Id, p.To.Id });

    [Fact]
    public void Every_document_path_in_the_matrix_has_a_converter()
    {
        // Slides depend on PowerPoint/LibreOffice; every other path must be covered without them.
        var options = new DocumentEngineOptions { UseMicrosoftOffice = false, UseLibreOffice = false };
        var missing = ConversionMatrix.AllPaths()
            .Where(p => Sources.Contains(p.From) && p.From != F.Pptx && p.From != F.Ppt)
            .Where(p => Fixtures.ConverterFor(p.From, p.To, options) is null)
            .Select(p => $"{p.From} → {p.To}")
            .ToList();
        Assert.Empty(missing);
        Assert.Equal(97, ConversionMatrix.AllPaths().Count(p => Sources.Contains(p.From)));
    }

    [Fact]
    public void Slides_cannot_convert_without_an_engine()
    {
        var converter = new SlidesConverter(new DocumentEngineOptions { UseMicrosoftOffice = false, UseLibreOffice = false });
        Assert.False(converter.CanConvert(F.Pptx, F.Pdf));
        Assert.False(converter.CanConvert(F.Ppt, F.Png));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Converts(string fromId, string toId)
    {
        var from = F.FromId(fromId)!;
        var to = F.FromId(toId)!;
        var converter = Fixtures.ConverterFor(from, to);
        if (converter is null)
        {
            output.WriteLine($"Skipped {from} → {to}: no converter can run on this machine (needs PowerPoint or LibreOffice).");
            return;
        }
        var fixture = Fixtures.Instance.For(from);
        if (fixture is null)
        {
            output.WriteLine($"Skipped {from} → {to}: no {from} sample can be made without Microsoft Office.");
            return;
        }

        var dir = Fixtures.Instance.NewOutputDir($"{fromId}-{toId}");
        var input = Fixtures.Instance.CopyTo(fixture, dir);
        var before = Fixtures.OfficeProcessIds();
        var started = DateTime.Now;
        var result = await Fixtures.ConvertAsync(converter, input, from, to);
        output.WriteLine($"{from} → {to} with {converter.Name} in {(DateTime.Now - started).TotalSeconds:0.0}s: {string.Join(", ", result.OutputPaths.Select(Path.GetFileName))}");

        Assert.NotEmpty(result.OutputPaths);
        Assert.True(File.Exists(input), "The input must stay in place.");
        var files = result.OutputPaths.Where(File.Exists).ToList();
        Assert.NotEmpty(files);
        var text = string.Join("\n", files.Where(f => Path.GetExtension(f).TrimStart('.').Equals(to.WriteExtension, StringComparison.OrdinalIgnoreCase))
            .Select(f => OutputVerifier.TextOf(f, to)));
        if (to != F.Png && to != F.Jpg)
            OutputVerifier.ContainsText(text, Fixtures.Token);

        // Strict checks, beyond what lenient readers accept: Office schemas, PDF structure.
        foreach (var file in files)
            AssertStrictlyValid(file, to);

        // No temporary files left behind.
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir, "*.petal-tmp", SearchOption.AllDirectories));
        Assert.Empty(Fixtures.LingeringOfficeProcesses(before));
    }

    /// <summary>
    /// What Word, Excel, PowerPoint and Acrobat insist on: Office files that follow the schema (or
    /// Office offers to "repair" them) and PDFs whose every reference resolves, with no empty page.
    /// </summary>
    internal static void AssertStrictlyValid(string file, FileFormat format)
    {
        if (format == F.Pdf)
        {
            PdfStructureTests.AssertReferencesResolve(file);
            PdfPageBoxTests.AssertNoEmptyBoxes(file);
            return;
        }
        if (format != F.Docx && format != F.Xlsx && format != F.Pptx)
            return;
        using DocumentFormat.OpenXml.Packaging.OpenXmlPackage package = format.Id switch
        {
            "docx" => DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(file, false),
            "xlsx" => DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(file, false),
            _ => DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(file, false),
        };
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019)
            .Validate(package).Take(5).Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, $"{Path.GetFileName(file)} breaks the Office schema:\n" + string.Join("\n", errors));
    }
}
