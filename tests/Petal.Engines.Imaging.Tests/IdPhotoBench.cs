using ImageMagick;
using Xunit;
using Xunit.Abstractions;

namespace Petal.Engines.Imaging.Tests;

/// <summary>
/// For looking at, with real portraits: PETAL_FACES is a folder of pictures (searched below it), PETAL_FACES_OUT where the
/// results go. Each picture with a face comes out at each size asked for, with where the head was found drawn on a copy.
/// </summary>
public sealed class IdPhotoBench(ITestOutputHelper output)
{
    [Fact]
    public async Task Portraits_for_looking_at()
    {
        if (Environment.GetEnvironmentVariable("PETAL_FACES") is not { Length: > 0 } folders)
            return;
        var outFolder = Environment.GetEnvironmentVariable("PETAL_FACES_OUT");
        int found = 0;
        foreach (var folder in folders.Split(';'))
        {
            var files = new List<string>();
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp"));
            }
            catch (IOException)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (new FileInfo(file).Length < 20_000)
                    continue;
                IReadOnlyList<PixelRect> faces;
                try { faces = await ImageRedactor.DetectFacesAsync(file); }
                catch (Exception) { continue; }
                if (faces.Count == 0)
                    continue;
                found++;
                output.WriteLine($"{faces.Count} face(s): {file}");
                if (outFolder is null || found > 12)
                    continue;
                Directory.CreateDirectory(outFolder);
                using var source = await IdPhoto.PrepareAsync(file);
                foreach (var spec in new[] { IdPhotoCatalog.Default, IdPhotoCatalog.All.First(s => s.Id == "tr-passport"), IdPhotoCatalog.All.First(s => s.Id == "us") })
                {
                    using var print = IdPhoto.Render(source, IdPhoto.Layout(source.Head, spec), spec, spec.Background);
                    print.Write(Path.Combine(outFolder, $"{found}-{spec.Id}.jpg"));
                }
                output.WriteLine($"  head: crown {source.Head.Crown:0} chin {source.Head.Chin:0}, mask {source.Mask is not null}");
            }
        }
        output.WriteLine($"{found} pictures with faces");
    }
}
