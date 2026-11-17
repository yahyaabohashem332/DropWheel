using Petal.Core.Formats;
using Petal.Engines.Documents.Converters;
using Petal.Engines.Documents.Sheets;

namespace Petal.Engines.Documents;

/// <summary>A sheet as a viewer shows it: its name, and each row's cells as they are displayed.</summary>
/// <param name="Cut">True when the sheet has more rows than were read.</param>
public sealed record PreviewSheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows, int Columns, bool Cut);

/// <summary>
/// What a viewer needs from documents: a Word, PowerPoint or OpenDocument file laid out as the program that made it
/// lays it out (by Office or LibreOffice, invisibly), and a spreadsheet's cells as they are shown.
/// </summary>
public static class Viewing
{
    /// <summary>True for a file Office or LibreOffice can lay out as pages (not a spreadsheet: that is shown as a table).</summary>
    public static bool CanLayOut(string path) =>
        Formats.FromPath(path) is { } format && format.Family is not FormatFamily.Spreadsheet && HighFidelity.AppFor(format) is not null;

    /// <summary>True when Office or LibreOffice is here to lay the file out.</summary>
    public static bool CanLayOutHere(string path) =>
        Formats.FromPath(path) is { } format && CanLayOut(path) && HighFidelity.Available(format, DocumentEngineOptions.Default);

    /// <summary>The file laid out as a PDF at <paramref name="pdf"/>; false when nothing here could do it.</summary>
    public static async Task<bool> LayOutAsync(string path, string pdf, CancellationToken cancellationToken)
    {
        if (Formats.FromPath(path) is not { } format || HighFidelity.AppFor(format) is not { } app)
            return false;
        return await HighFidelity.TryPdfAsync(path, app, pdf, DocumentEngineOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True for the spreadsheets a viewer shows as a table: Excel, OpenDocument, CSV and TSV.</summary>
    public static bool IsSheet(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xls" or ".ods" or ".csv" or ".tsv";

    /// <summary>
    /// A spreadsheet's sheets, each row's cells as displayed, at most <paramref name="mostRows"/> rows a sheet. A file
    /// another program holds open is read from a copy.
    /// </summary>
    public static IReadOnlyList<PreviewSheet> ReadSheets(string path, int mostRows = 100_000)
    {
        WorkbookModel book;
        try
        {
            book = Read(path);
        }
        catch (IOException)
        {
            // Excel keeps the file it has open from being opened again: a copy is read instead.
            var copy = Path.Combine(Path.GetTempPath(), "DropWheel-sheet-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            try
            {
                using (var from = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var to = File.Create(copy))
                    from.CopyTo(to);
                book = Read(copy);
            }
            finally
            {
                File.Delete(copy);
            }
        }
        return book.Sheets.Select(sheet =>
        {
            var rows = sheet.Rows.Take(mostRows).Select(r => (IReadOnlyList<string>)r.Select(c => c.DisplayText).ToList()).ToList();
            int columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            return new PreviewSheet(sheet.Name, rows, columns, sheet.Rows.Count > mostRows);
        }).ToList();
    }

    private static WorkbookModel Read(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xlsx" or ".xlsm" => ExcelSheets.ReadXlsx(path),
        ".xls" => ExcelSheets.ReadXls(path),
        ".ods" => OdsSheets.Read(path),
        ".tsv" => DelimitedText.Read(path, '\t'),
        _ => DelimitedText.Read(path, ','),
    };
}
