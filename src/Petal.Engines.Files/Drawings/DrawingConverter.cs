using ACadSharp.IO;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Files.Drawings;

/// <summary>
/// CAD drawings: DXF and DWG (read through ACadSharp, DWG from R14 on) to PDF and SVG (vector) and PNG, and a DWG to
/// DXF, which free CAD programs open.
/// </summary>
public sealed class DrawingConverter : IConverter
{
    public string Name => "Drawings";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        from.Family == FormatFamily.Drawing && (to == F.Pdf || to == F.Svg || to == F.Png || to == F.Dxf) && ConversionMatrix.CanConvert(from, to);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (string.Equals(Path.GetFullPath(job.InputPath), Path.GetFullPath(job.OutputPath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output path must differ from the input path.");
            progress?.Report(0.05);
            var doc = Drawing.Load(job.InputPath, job.From == F.Dwg);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.4);
            var folder = Path.GetDirectoryName(Path.GetFullPath(job.OutputPath))!;
            Directory.CreateDirectory(folder);
            var temp = job.OutputPath + ".petal-tmp";
            try
            {
                if (job.To == F.Dxf)
                {
                    TablesAsBlocks(doc);
                    using (var output = File.Create(temp))
                        DxfWriter.Write(output, doc, false, null, null);
                }
                else
                {
                    var drawing = Drawing.Read(doc, job.InputPath);
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(0.6);
                    DrawingPicture.Write(drawing, temp, job.To == F.Pdf ? DrawingPicture.Kind.Pdf : job.To == F.Svg ? DrawingPicture.Kind.Svg : DrawingPicture.Kind.Png);
                }
                File.Move(temp, job.OutputPath);
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);

    /// <summary>
    /// Tables as what they look like: ACadSharp writes no ACAD_TABLE to a DXF, and a table's lines and text are a
    /// block of its own, so each becomes an insert of that block (it reads as a drawing of the table, not a table to edit).
    /// </summary>
    private static void TablesAsBlocks(ACadSharp.CadDocument doc)
    {
        foreach (var owner in doc.BlockRecords.ToList())
        {
            foreach (var table in owner.Entities.OfType<ACadSharp.Entities.TableEntity>().ToList())
            {
                owner.Entities.Remove(table);
                if (table.Block is null)
                    continue;
                owner.Entities.Add(new ACadSharp.Entities.Insert(table.Block)
                {
                    InsertPoint = table.InsertPoint, Rotation = table.Rotation, Normal = table.Normal,
                    XScale = table.XScale, YScale = table.YScale, ZScale = table.ZScale,
                    Layer = table.Layer, Color = table.Color, LineType = table.LineType, LineWeight = table.LineWeight,
                });
            }
        }
    }
}
