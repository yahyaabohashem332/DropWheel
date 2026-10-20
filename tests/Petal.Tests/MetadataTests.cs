using Petal.Core.Util;
using Petal.Engines.Files.Metadata;

namespace Petal.Tests;

public class MetadataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-meta-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> PhotoAsync(string name)
    {
        var path = Path.Combine(_dir, name);
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException("FFmpeg missing");
        await ProcessRunner.RunAsync(ffmpeg, ["-y", "-f", "lavfi", "-i", "testsrc2=size=320x240", "-frames:v", "1", path]);
        return path;
    }

    [Fact]
    public async Task Edits_and_removal_write_new_files()
    {
        Assert.True(ExifToolMetadata.IsAvailable, "ExifTool should be bundled under tools/exiftool");
        var photo = await PhotoAsync("café photo.jpg");

        var tagged = Path.Combine(_dir, "café photo tagged.jpg");
        await ExifToolMetadata.SaveEditsAsync(photo, tagged, [new MetadataEdit("IFD0", "Artist", "Yahya"), new MetadataEdit("IFD0", "Model", "Petal Cam")]);
        var fields = await ExifToolMetadata.ReadAsync(tagged);
        Assert.Contains(fields, f => f.Name == "Artist" && f.Value == "Yahya" && f.Editable);
        Assert.Contains(ExifToolMetadata.Summary(fields), s => s.Label == "Camera" && s.Value == "Petal Cam");

        // The original is untouched.
        Assert.DoesNotContain(await ExifToolMetadata.ReadAsync(photo), f => f.Name == "Artist");

        var clean = Path.Combine(_dir, "café photo No Metadata.jpg");
        await ExifToolMetadata.RemoveAllAsync(tagged, clean);
        Assert.DoesNotContain(await ExifToolMetadata.ReadAsync(clean), f => f.Name is "Artist" or "Model");
    }

    [Fact]
    public async Task Deleting_one_field_keeps_the_others()
    {
        var photo = await PhotoAsync("one.jpg");
        var tagged = Path.Combine(_dir, "tagged.jpg");
        await ExifToolMetadata.SaveEditsAsync(photo, tagged, [new MetadataEdit("IFD0", "Artist", "A"), new MetadataEdit("IFD0", "Copyright", "B")]);
        var trimmed = Path.Combine(_dir, "trimmed.jpg");
        await ExifToolMetadata.SaveEditsAsync(tagged, trimmed, [new MetadataEdit("IFD0", "Artist", null)]);
        var fields = await ExifToolMetadata.ReadAsync(trimmed);
        Assert.DoesNotContain(fields, f => f.Name == "Artist");
        Assert.Contains(fields, f => f.Name == "Copyright" && f.Value == "B");
    }
}
