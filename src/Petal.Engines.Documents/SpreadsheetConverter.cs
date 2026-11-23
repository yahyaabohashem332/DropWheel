using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Converters;
using Petal.Engines.Documents.Office;
using Petal.Engines.Documents.Sheets;
using Petal.Engines.Documents.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents;

/// <summary>
/// XLSX, XLS (read only), ODS, CSV and TSV to each other, and to PDF. Workbooks print through
/// Excel (or LibreOffice) when installed, else as paginated tables, one sheet after another.
/// A workbook with several sheets becomes a folder of CSV/TSV files, one per sheet.
/// </summary>
public sealed class SpreadsheetConverter(DocumentEngineOptions? options = null) : IConverter
{
    private readonly DocumentEngineOptions _options = options ?? DocumentEngineOptions.Default;

    public string Name => "Spreadsheets";

    private static bool IsSheet(FileFormat f) => f == F.Xlsx || f == F.Xls || f == F.Ods || f == F.Csv || f == F.Tsv;

    public bool CanConvert(FileFormat from, FileFormat to) =>
        IsSheet(from) && ConversionMatrix.CanConvert(from, to) && (to == F.Xlsx || to == F.Ods || to == F.Csv || to == F.Tsv || to == F.Pdf);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            SafeOutput.EnsureNotInput(job.InputPath, job.OutputPath);
            progress?.Report(0.02);
            var ct = cancellationToken;

            if (job.To == F.Pdf && HighFidelity.AppFor(job.From) is { } app)
            {
                using var scratch = SafeOutput.CreateScratch();
                var pdf = scratch.File("workbook.pdf");
                if (await HighFidelity.TryPdfAsync(job.InputPath, app, pdf, _options, ct).ConfigureAwait(false))
                {
                    SafeOutput.Write(job.OutputPath, temp => File.Copy(pdf, temp));
                    progress?.Report(1);
                    return JobResult.Single(job.OutputPath);
                }
            }

            var workbook = Read(job.InputPath, job.From);
            ct.ThrowIfCancellationRequested();
            progress?.Report(0.4);
            var result = Write(workbook, job, ScaledProgress.Of(progress, 0.4, 1), ct);
            progress?.Report(1);
            return result;
        }, cancellationToken);

    internal static WorkbookModel Read(string path, FileFormat format) => format.Id switch
    {
        "xlsx" => ExcelSheets.ReadXlsx(path),
        "xls" => ExcelSheets.ReadXls(path),
        "ods" => OdsSheets.Read(path),
        "csv" => DelimitedText.Read(path, ','),
        "tsv" => DelimitedText.Read(path, '\t'),
        _ => throw new NotSupportedException($"Cannot read {format.Label} spreadsheets."),
    };

    internal static JobResult Write(WorkbookModel workbook, ConversionJob job, IProgress<double>? progress, CancellationToken ct)
    {
        workbook.Title ??= F.BaseName(job.InputPath);
        switch (job.To.Id)
        {
            case "xlsx":
                SafeOutput.Write(job.OutputPath, temp => ExcelSheets.WriteXlsx(workbook, temp));
                return JobResult.Single(job.OutputPath);
            case "ods":
                SafeOutput.Write(job.OutputPath, temp => OdsSheets.Write(workbook, temp));
                return JobResult.Single(job.OutputPath);
            case "pdf":
                SafeOutput.Write(job.OutputPath, temp => SheetPdfWriter.Write(workbook, temp, progress, ct));
                return JobResult.Single(job.OutputPath);
            case "csv" or "tsv":
            {
                var delimiter = job.To == F.Tsv ? '\t' : ',';
                var sheets = workbook.Sheets.Where(s => !s.IsEmpty).ToList();
                if (sheets.Count <= 1)
                {
                    var sheet = sheets.FirstOrDefault() ?? new SheetModel("Sheet1");
                    SafeOutput.Write(job.OutputPath, temp => DelimitedText.Write(sheet, temp, delimiter));
                    return JobResult.Single(job.OutputPath);
                }
                // Plain text holds one sheet; several become a folder, one file per sheet.
                var stem = F.BaseName(job.InputPath);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var names = sheets.Select(s =>
                {
                    var name = SafeOutput.SafeFileName($"{stem} {s.Name}");
                    var candidate = name;
                    for (var n = 2; !used.Add(candidate); n++)
                        candidate = $"{name} {n}";
                    return candidate + "." + job.To.WriteExtension;
                }).ToList();
                var paths = SafeOutput.WriteFolder(job.InputPath, job.OutputPath, names, (i, path) => DelimitedText.Write(sheets[i], path, delimiter));
                return new JobResult(paths);
            }
            default:
                throw new NotSupportedException($"Cannot write {job.To.Label} spreadsheets.");
        }
    }
}
