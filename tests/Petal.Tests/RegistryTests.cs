using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

public class RegistryTests
{
    [Fact]
    public void Conversion_matrix_has_290_paths()
    {
        Assert.Equal(290, ConversionMatrix.TotalPaths);
    }

    [Fact]
    public void No_format_converts_to_itself_or_to_a_read_only_format()
    {
        foreach (var (from, to) in ConversionMatrix.AllPaths())
        {
            Assert.NotEqual(from, to);
            Assert.True(to.CanWrite, $"{from} → {to} targets a read-only format");
        }
    }

    [Fact]
    public void Jpg_wheel_order_starts_at_png()
    {
        var labels = ConversionMatrix.TargetsFor(F.Jpg).Select(f => f.Label);
        Assert.Equal(["PNG", "WEBP", "HEIC", "TIFF", "AVIF", "BMP", "ICO", "PDF"], labels);
    }

    [Theory]
    [InlineData("photo.JPEG", "jpg")]
    [InlineData("backup.tar.gz", "tgz")]
    [InlineData("song.aif", "aiff")]
    [InlineData("readme.markdown", "md")]
    public void Detects_formats_from_extensions(string name, string expectedId)
    {
        Assert.Equal(expectedId, F.FromPath(name)?.Id);
    }

    [Fact]
    public void Mixed_image_selection_shares_targets()
    {
        var targets = ConversionMatrix.TargetsFor([F.Jpg, F.Png]).Select(f => f.Id).ToList();
        Assert.Contains("webp", targets);
        Assert.Contains("pdf", targets);
        Assert.Contains("png", targets);
        Assert.Contains("jpg", targets);
    }

    [Fact]
    public void Unrelated_selection_has_no_shared_targets()
    {
        Assert.Empty(ConversionMatrix.TargetsFor([F.Mp3, F.Zip]));
    }

    [Fact]
    public void There_are_27_tools()
    {
        Assert.Equal(27, ToolRegistry.All.Count);
        Assert.Equal(27, ToolRegistry.All.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Tools_change_with_selection_size()
    {
        Assert.Equal(8, ToolRegistry.ToolsFor([F.Jpg]).Count);
        var multi = ToolRegistry.ToolsFor([F.Jpg, F.Png]).Select(t => t.Id).ToList();
        Assert.Equal([ToolId.Compress, ToolId.Metadata, ToolId.CropImage, ToolId.CreatePdf, ToolId.CreateCollage], multi);
        Assert.Contains(ToolId.JoinVideos, ToolRegistry.ToolsFor([F.Mp4, F.Mov]).Select(t => t.Id));
        Assert.Contains(ToolId.MergePdfs, ToolRegistry.ToolsFor([F.Pdf, F.Pdf]).Select(t => t.Id));
        Assert.Empty(ToolRegistry.ToolsFor([F.Docx]));
        Assert.Empty(ToolRegistry.ToolsFor([F.Svg]));
    }
}

public class NamingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-naming-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Conversion_replaces_the_extension_and_avoids_clashes()
    {
        var input = Path.Combine(_dir, "landscape.jpg");
        File.WriteAllText(input, "");
        Assert.Equal(Path.Combine(_dir, "landscape.png"), OutputNaming.ForConversion(input, F.Png));

        File.WriteAllText(Path.Combine(_dir, "landscape.png"), "");
        Assert.Equal(Path.Combine(_dir, "landscape 2.png"), OutputNaming.ForConversion(input, F.Png));
    }

    [Fact]
    public void Tar_gz_keeps_its_double_extension()
    {
        var input = Path.Combine(_dir, "project.zip");
        Assert.Equal(Path.Combine(_dir, "project.tar.gz"), OutputNaming.ForConversion(input, F.Tgz));
        Assert.Equal("tar.gz", OutputNaming.ExtensionOf("a/b/project.tar.gz"));
    }

    [Fact]
    public void Tools_append_a_suffix()
    {
        var input = Path.Combine(_dir, "landscape.jpg");
        Assert.Equal(Path.Combine(_dir, "landscape Compressed.jpg"), OutputNaming.ForTool(input, ToolRegistry.Get(ToolId.Compress)));
        Assert.Equal(Path.Combine(_dir, "landscape Annotated.png"), OutputNaming.ForTool(input, ToolRegistry.Get(ToolId.AnnotatePhoto)));

        var video = Path.Combine(_dir, "weekend.mov");
        Assert.Equal(Path.Combine(_dir, "weekend Clips"), OutputNaming.ForTool(video, ToolRegistry.Get(ToolId.SplitVideo)));
        Assert.Equal(Path.Combine(_dir, "weekend Speed 2x.mov"), OutputNaming.ForTool(video, ToolRegistry.Get(ToolId.ChangeSpeed), "Speed 2x"));
    }
}
