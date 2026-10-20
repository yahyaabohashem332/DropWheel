using Petal.Core.Jobs;
using Petal.Engines.Documents.Office;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Tests;

[Collection("Office")]
public sealed class OfficeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Office_conversions_leave_no_processes_behind()
    {
        var before = Fixtures.OfficeProcessIds();
        var ran = 0;
        foreach (var (app, fixture, from) in new[] { (OfficeApp.Word, "docx", F.Docx), (OfficeApp.Excel, "xlsx", F.Xlsx), (OfficeApp.PowerPoint, "pptx", F.Pptx) })
        {
            if (!OfficeAutomation.IsInstalled(app))
            {
                output.WriteLine($"{app} is not installed; skipped.");
                continue;
            }
            var dir = Fixtures.Instance.NewOutputDir("office-" + app);
            var input = Fixtures.Instance.CopyTo(Fixtures.Instance.Get(fixture), dir);
            var converter = Fixtures.ConverterFor(from, F.Pdf)!;
            var result = await Fixtures.ConvertAsync(converter, input, from, F.Pdf);
            Assert.True(new FileInfo(result.Primary).Length > 1000);
            ran++;
        }
        Assert.Empty(Fixtures.LingeringOfficeProcesses(before));
        output.WriteLine($"{ran} Office applications exercised.");
    }

    [Fact]
    public async Task A_hung_or_cancelled_office_job_is_stopped_and_cleaned_up()
    {
        if (!OfficeAutomation.IsInstalled(OfficeApp.Word))
        {
            output.WriteLine("Word is not installed; skipped.");
            return;
        }
        var before = Fixtures.OfficeProcessIds();
        var dir = Fixtures.Instance.NewOutputDir("office-cancel");
        var input = Fixtures.Instance.CopyTo(Fixtures.Instance.Get("docx"), dir);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfficeAutomation.ExportPdfAsync(OfficeApp.Word, input, Path.Combine(dir, "out.pdf"), TimeSpan.FromMinutes(1), cts.Token));
        Assert.Empty(Fixtures.LingeringOfficeProcesses(before));

        // A timeout behaves the same way and reports itself as a timeout.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            OfficeAutomation.ExportPdfAsync(OfficeApp.Word, input, Path.Combine(dir, "out2.pdf"), TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.Empty(Fixtures.LingeringOfficeProcesses(before));
    }

    [Fact]
    public async Task High_fidelity_can_be_turned_off()
    {
        var before = Fixtures.OfficeProcessIds();
        var dir = Fixtures.Instance.NewOutputDir("no-office");
        var input = Fixtures.Instance.CopyTo(Fixtures.Instance.Get("docx"), dir);
        var converter = new DocumentConverter(new DocumentEngineOptions { UseMicrosoftOffice = false, UseLibreOffice = false });
        var result = await Fixtures.ConvertAsync(converter, input, F.Docx, F.Pdf);
        var text = OutputVerifier.TextOf(result.Primary, F.Pdf);
        OutputVerifier.ContainsText(text, "Petal Word Report");
        OutputVerifier.ContainsText(text, "Green banana");
        // The built-in renderer was used: PDFsharp/MigraDoc, not Word.
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(result.Primary);
        Assert.DoesNotContain("Word", pdf.Information.Creator ?? "");
        Assert.Empty(Fixtures.OfficeProcessIds().Except(before));
    }
}
