using System.Text;
using System.Text.Json.Nodes;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Sheets;
using Petal.Engines.Documents.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Data;

/// <summary>
/// Data files to each other and to and from tables: JSON, JSON Lines, YAML, TOML and XML through one tree of
/// values; spreadsheets (XLSX, XLS, ODS, CSV, TSV) to JSON as records; data that is a list of records to CSV and
/// XLSX; contacts (vCard) to CSV, XLSX and JSON. Text is written as UTF-8 without a byte order mark.
/// </summary>
public sealed class DataConverter : IConverter
{
    public string Name => "Data";

    private static bool IsTree(FileFormat f) => f == F.Json || f == F.Jsonl || f == F.Yaml || f == F.Toml || f == F.Xml;

    private static bool IsSheet(FileFormat f) => f == F.Xlsx || f == F.Xls || f == F.Ods || f == F.Csv || f == F.Tsv;

    public bool CanConvert(FileFormat from, FileFormat to) =>
        ConversionMatrix.CanConvert(from, to) && (from.Family == FormatFamily.Data || IsSheet(from) && to == F.Json);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(job.InputPath, job.OutputPath);
            progress?.Report(0.05);
            var title = F.BaseName(job.InputPath);

            if (job.From == F.Vcf)
            {
                var contacts = VCard.Read(File.ReadAllBytes(job.InputPath));
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.5);
                var result = job.To == F.Json
                    ? WriteText(job.OutputPath, DataTree.WriteJson(VCard.ToJson(contacts)))
                    : WriteTable(VCard.ToWorkbook(contacts, title), job);
                progress?.Report(1);
                return result;
            }

            JsonNode? tree = IsSheet(job.From)
                ? DataTables.ToJson(SpreadsheetConverter.Read(job.InputPath, job.From))
                : ReadTree(job.InputPath, job.From);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.5);

            var written = job.To.Id switch
            {
                "json" => WriteText(job.OutputPath, DataTree.WriteJson(tree)),
                "jsonl" => WriteText(job.OutputPath, DataTree.WriteJsonLines(tree)),
                "yaml" => WriteText(job.OutputPath, DataTree.WriteYaml(tree)),
                "toml" => WriteText(job.OutputPath, DataTree.WriteToml(tree, out _)),
                "xml" => WriteText(job.OutputPath, DataTree.WriteXml(tree)),
                "csv" or "xlsx" => WriteTable(DataTables.ToWorkbook(Records(tree, job.From), title), job),
                _ => throw new NotSupportedException($"Cannot write {job.To.Label} from {job.From.Label}."),
            };
            progress?.Report(1);
            return written;
        }, cancellationToken);

    internal static JsonNode? ReadTree(string path, FileFormat format)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return format.Id switch
        {
            "json" => DataTree.ReadJson(text),
            "jsonl" => DataTree.ReadJsonLines(text),
            "yaml" => DataTree.ReadYaml(text),
            "toml" => DataTree.ReadToml(text),
            "xml" => DataTree.ReadXml(text),
            _ => throw new NotSupportedException($"Cannot read {format.Label} as data."),
        };
    }

    /// <summary>
    /// The records a table is made of. A YAML or JSON object that holds one list of records (such as {"items": […]})
    /// gives that list; anything that isn't records is said so.
    /// </summary>
    private static JsonNode? Records(JsonNode? tree, FileFormat from)
    {
        if (tree is JsonObject obj && obj.Count == 1 && obj.First().Value is JsonArray only && only.Any(i => i is JsonObject))
            return only;
        if (tree is JsonArray or JsonObject)
            return tree;
        throw new DataShapeException($"This {from.Label} holds a single value, not a list of records, so it can't become a table.");
    }

    private static JobResult WriteText(string path, string text)
    {
        SafeOutput.Write(path, temp => File.WriteAllText(temp, text, new UTF8Encoding(false)));
        return JobResult.Single(path);
    }

    private static JobResult WriteTable(WorkbookModel workbook, ConversionJob job) =>
        SpreadsheetConverter.Write(workbook, job, null, CancellationToken.None);
}
