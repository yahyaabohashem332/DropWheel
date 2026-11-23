using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Files.Models;

/// <summary>
/// 3D models: STL, OBJ, PLY, 3MF, GLB and glTF to each other and to a picture (PNG). What every format shares
/// (triangles, normals, colours, materials, pictures on them, parts) goes across; what one format alone has
/// (animation, lights, cameras) is left out. Units follow the formats that state one: STL in millimetres, glTF in
/// metres, 3MF in what it declares.
/// </summary>
public sealed class ModelConverter : IConverter
{
    public string Name => "3D models";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        from.Family == FormatFamily.Model && (to.Family == FormatFamily.Model || to == F.Png) && ConversionMatrix.CanConvert(from, to);

    internal static Model3D Read(string path, FileFormat format) => format.Id switch
    {
        "stl" => Stl.Read(path),
        "obj" => Obj.Read(path),
        "ply" => Ply.Read(path),
        "3mf" => ThreeMf.Read(path),
        "glb" or "gltf" => Gltf.Read(path),
        _ => throw new NotSupportedException($"DropWheel can't read {format.Label} models."),
    };

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (string.Equals(Path.GetFullPath(job.InputPath), Path.GetFullPath(job.OutputPath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output path must differ from the input path.");
            progress?.Report(0.05);
            var model = Read(job.InputPath, job.From);
            model.Name ??= F.BaseName(job.InputPath);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.5);

            var folder = Path.GetDirectoryName(Path.GetFullPath(job.OutputPath))!;
            Directory.CreateDirectory(folder);
            if (job.To == F.Obj)
            {
                // The OBJ, its .mtl and its pictures, written together and put in place together.
                var stem = Path.GetFileNameWithoutExtension(job.OutputPath);
                while (File.Exists(Path.Combine(folder, stem + ".mtl")) || File.Exists(Path.Combine(folder, stem + ".obj")))
                    stem += " 2";
                var scratch = Path.Combine(Path.GetTempPath(), "petal-obj-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                try
                {
                    var written = Obj.Write(model, scratch, stem);
                    var placed = new List<string>();
                    foreach (var file in written)
                    {
                        var target = Path.Combine(folder, Path.GetFileName(file));
                        File.Move(file, target, overwrite: false);
                        placed.Add(target);
                    }
                    progress?.Report(1);
                    return new JobResult(placed);
                }
                finally
                {
                    try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }

            var temp = job.OutputPath + ".petal-tmp";
            try
            {
                switch (job.To.Id)
                {
                    case "stl": Stl.Write(model, temp); break;
                    case "ply": Ply.Write(model, temp); break;
                    case "3mf": ThreeMf.Write(model, temp); break;
                    case "glb": Gltf.Write(model, temp, binary: true); break;
                    case "gltf": Gltf.Write(model, temp, binary: false); break;
                    case "png": File.WriteAllBytes(temp, ModelPicture.Render(model)); break;
                    default: throw new NotSupportedException($"DropWheel can't write {job.To.Label} models.");
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
}
