using DocumentFormat.OpenXml.Packaging;
using Petal.Engines.Documents.Util;

namespace Petal.Engines.Documents.Office;

/// <summary>
/// Personal details in Word, Excel and PowerPoint files: who wrote and last saved them, their company
/// and manager, and custom properties. Content, formatting and comments are left as they are.
/// </summary>
public static class OfficeMetadata
{
    public static bool CanClean(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx" or ".docm" or ".xlsm" or ".pptm";

    /// <summary>Writes a copy without author, editor, company, manager, template and custom properties.</summary>
    public static Task CleanAsync(string input, string output, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(input, output);
            SafeOutput.Write(output, temp =>
            {
                File.Copy(input, temp, overwrite: true);
                cancellationToken.ThrowIfCancellationRequested();
                using OpenXmlPackage package = Path.GetExtension(input).ToLowerInvariant() switch
                {
                    ".docx" or ".docm" => WordprocessingDocument.Open(temp, true),
                    ".xlsx" or ".xlsm" => SpreadsheetDocument.Open(temp, true),
                    ".pptx" or ".pptm" => PresentationDocument.Open(temp, true),
                    _ => throw new NotSupportedException("Only Word, Excel and PowerPoint files can be cleaned this way."),
                };
                var core = package.PackageProperties;
                core.Creator = null;
                core.LastModifiedBy = null;
                core.LastPrinted = null;
                core.Revision = null;
                core.Identifier = null;
                core.ContentStatus = null;

                if (package.GetPartsOfType<ExtendedFilePropertiesPart>().FirstOrDefault() is { Properties: { } app } appPart)
                {
                    app.Company?.Remove();
                    app.Manager?.Remove();
                    app.Template?.Remove();
                    app.HyperlinkBase?.Remove();
                    app.TotalTime?.Remove();
                    appPart.Properties.Save();
                }
                foreach (var custom in package.GetPartsOfType<CustomFilePropertiesPart>().ToList())
                    package.DeletePart(custom);
            });
        }, cancellationToken);
}
