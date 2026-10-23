using System.Diagnostics;
using System.IO;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Documents.Ocr;
using Petal.Engines.Imaging;
using Petal.Recipes;

namespace Petal.App.Tools.Windows;

/// <summary>Tools for documents at work: a table in a picture into Excel, and two versions of a file compared.</summary>
public static class WorkToolSetup
{
    public static void Register()
    {
        ToolLauncher.RegisterDirect(ToolId.TableFromImage, (tool, files, jobs, at) => jobs.RunAsync("Reading the table", "Table saved",
            files.Select(f => new WorkItem(f, Path.GetFileName(f), async (progress, ct) =>
            {
                progress.Report(0.1);
                var output = OutputNaming.ForTool(f, tool);
                await SaveTableAsync(f, output, ct);
                return (IReadOnlyList<string>)[output];
            })).ToList(), at));
        ToolLauncher.RegisterDirect(ToolId.CompareFiles, (tool, files, jobs, at) => CompareAsync(tool, files, jobs, at));
        ToolLauncher.RegisterWindow(ToolId.DescribePicture, (t, f, j, a) => new DescribePictureWindow(t, f[0]));
    }

    /// <summary>The table in a picture saved as an Excel sheet.</summary>
    internal static async Task SaveTableAsync(string picture, string output, CancellationToken ct)
    {
        // Through the picture reader every tool uses, so HEIC, TIFF and the rest open too.
        var bitmap = await ImagePreview.DecodeBgraAsync(picture, 4000, ct);
        var table = await PictureTables.ReadAsync(bitmap.Pixels, bitmap.Width, bitmap.Height, ct);
        if (table.IsEmpty)
            throw new InvalidOperationException(L.T("No table was found in this picture."));
        PictureTables.SaveXlsx(table, output, L.T("Table"));
    }

    /// <summary>The words of a comparison report in the interface's language.</summary>
    internal static ComparisonText ReportText() => new(
        L.T("Comparison"), L.T("Before"), L.T("After"),
        L.T("The two files have the same text."), L.T("The pages look the same."), L.T("The text"), L.T("The pages"),
        n => L.P(n, "{0} word added", "{0} words added"),
        n => L.P(n, "{0} word removed", "{0} words removed"),
        n => L.P(n, "{0} unchanged paragraph", "{0} unchanged paragraphs"),
        n => L.F("Page {0}", n),
        L.T("Only in the first file"), L.T("Only in the second file"),
        L.F("Only the first {0} pages are drawn; the text is compared in full.", FileComparison.MostPages),
        L.IsRightToLeft, L.Code);

    /// <summary>The older file is "before": by when each was last changed, the first selected when that can't tell.</summary>
    private static async Task CompareAsync(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at)
    {
        var ordered = files.OrderBy(File.GetLastWriteTimeUtc).ToList();
        string before = ordered[0], after = ordered[1];
        // Beside the newer file, or in the folder Settings › Results names.
        var output = Petal.Core.Jobs.OutputNaming.Unique(Petal.Core.Jobs.OutputNaming.DirectoryFor(after),
            $"{Path.GetFileNameWithoutExtension(before)} vs {Path.GetFileNameWithoutExtension(after)}", "html");
        ComparisonResult? result = null;
        await jobs.RunAsync("Comparing", "Compared", [new WorkItem(after, $"{Path.GetFileName(before)} · {Path.GetFileName(after)}", async (progress, ct) =>
        {
            result = await FileComparison.CompareAsync(before, after, output, jobs.Converters, SettingsStore.Current.Conversion, ReportText(), progress, ct);
            try
            {
                Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No browser: the report is still next to the files.
            }
            return (IReadOnlyList<string>)[output];
        })], at, _ => result switch
        {
            null => null,
            { Same: true } => L.T("No differences"),
            { HasText: true } => L.P(result.AddedWords, "{0} word added", "{0} words added") + " · " + L.P(result.RemovedWords, "{0} word removed", "{0} words removed"),
            _ => L.T("Differences are boxed"),
        });
    }
}
