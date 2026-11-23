using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

public class RegistryTests
{
    [Fact]
    public void Conversion_matrix_has_737_paths()
    {
        // 363 until 1.20, when WMV and WMA became read only (13 paths wrote them).
        // +4 in 1.22 for PDF long/first page image outputs.
        // +27 in 1.24 for data: JSON, JSON Lines, YAML, TOML, XML and vCard, and spreadsheets to JSON.
        // +60 in 1.24 for subtitles: ASS, SSA, TTML, SAMI, MicroDVD and LRC, every one to every other and to text.
        // +65 in 1.24 for pictures: JXL with the everyday formats, TGA, DDS, JPEG 2000, QOI and PCX from PNG and
        // TIFF and to six common formats, GIMP's XCF to seven.
        // +45 in 1.24 for video and audio: TS written and read, 3GP and FLV read, M4B audiobooks.
        // +81 in 1.24 for documents: DOC, DOCM, DOTX, MHTML and RST to the document formats, PPSX, POTX and PPTM,
        // XLSM and XLTX, a document, presentation and workbook to their templates and shows, RST from three.
        // +31 for books: MOBI, AZW3 and KEPUB to EPUB and the document formats, EPUB to KEPUB, comic books to PDF,
        // EPUB and CBZ, a PDF to CBZ.
        // +38 for archives: 7Z, TAR.XZ and TAR.BZ2 written, they and CAB and ISO read, a ZIP to ISO and CAB.
        // +36 for 3D models: STL, OBJ, PLY, 3MF, GLB and glTF to each other and to a PNG picture.
        Assert.Equal(737, ConversionMatrix.TotalPaths);
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
        Assert.Equal(["PNG", "WEBP", "HEIC", "TIFF", "AVIF", "BMP", "ICO", "JXL", "PDF"], labels);
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
    public void There_are_72_tools_and_17_actions()
    {
        Assert.Equal(72, ToolRegistry.All.Count);
        Assert.Equal(72, ToolRegistry.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(Enum.GetValues<ToolId>().Length, ToolRegistry.All.Count);
        Assert.Equal(17, Petal.Core.Actions.FileActionRegistry.All.Count);
    }

    [Fact]
    public void Tools_change_with_selection_size()
    {
        Assert.Equal(18, ToolRegistry.ToolsFor([F.Jpg]).Count);
        var multi = ToolRegistry.ToolsFor([F.Jpg, F.Png]).Select(t => t.Id).ToList();
        Assert.Equal([ToolId.Compress, ToolId.Metadata, ToolId.ResizeImage, ToolId.CropImage, ToolId.Watermark, ToolId.RemoveBackground, ToolId.TranslatePicture, ToolId.TraceToSvg, ToolId.CreatePdf, ToolId.MakeSearchable, ToolId.CreateCollage, ToolId.TableFromImage, ToolId.CompareFiles], multi);
        Assert.Contains(ToolId.Transcribe, ToolRegistry.ToolsFor([F.Mp3]).Select(t => t.Id));
        Assert.Contains(ToolId.Transcribe, ToolRegistry.ToolsFor([F.Mp4, F.Mov]).Select(t => t.Id));
        Assert.Contains(ToolId.Watermark, ToolRegistry.ToolsFor([F.Mp4]).Select(t => t.Id));
        Assert.Equal(ToolId.EditPdf, ToolRegistry.ToolsFor([F.Pdf])[0].Id);
        Assert.DoesNotContain(ToolId.EditPdf, ToolRegistry.ToolsFor([F.Pdf, F.Pdf]).Select(t => t.Id));
        Assert.Contains(ToolId.SignPdf, ToolRegistry.ToolsFor([F.Pdf]).Select(t => t.Id));
        Assert.DoesNotContain(ToolId.SignPdf, ToolRegistry.ToolsFor([F.Pdf, F.Pdf]).Select(t => t.Id));
        Assert.Contains(ToolId.JoinVideos, ToolRegistry.ToolsFor([F.Mp4, F.Mov]).Select(t => t.Id));
        Assert.Contains(ToolId.MergePdfs, ToolRegistry.ToolsFor([F.Pdf, F.Pdf]).Select(t => t.Id));
        Assert.Equal([ToolId.AskFile, ToolId.StudyNotes, ToolId.StudyFile, ToolId.TranslateDocument, ToolId.ReadAloud], ToolRegistry.ToolsFor([F.Docx]).Select(t => t.Id));
        Assert.Equal([ToolId.AskFile, ToolId.StudyFile, ToolId.ReadAloud], ToolRegistry.ToolsFor([F.Txt]).Select(t => t.Id));
        Assert.Equal([ToolId.AskFile, ToolId.StudyFile, ToolId.ReadAloud], ToolRegistry.ToolsFor([F.Epub]).Select(t => t.Id));
        Assert.Equal([ToolId.AskFile, ToolId.StudyFile, ToolId.TranslateDocument], ToolRegistry.ToolsFor([F.Pptx]).Select(t => t.Id));
        Assert.Equal([ToolId.AskFile, ToolId.StudyFile], ToolRegistry.ToolsFor([F.Ppt]).Select(t => t.Id));
        Assert.Equal([ToolId.TranslateDocument], ToolRegistry.ToolsFor([F.Pptx, F.Pptx]).Select(t => t.Id));
        Assert.Contains(ToolId.StudyFile, ToolRegistry.ToolsFor([F.Pdf]).Select(t => t.Id));
        Assert.Contains(ToolId.StudyFile, ToolRegistry.ToolsFor([F.Mp4]).Select(t => t.Id));
        Assert.Contains(ToolId.StudyFile, ToolRegistry.ToolsFor([F.Mp3]).Select(t => t.Id));
        Assert.DoesNotContain(ToolId.StudyFile, ToolRegistry.ToolsFor([F.Pdf, F.Pdf]).Select(t => t.Id));
        Assert.Equal([ToolId.AskFile, ToolId.TranslateDocument], ToolRegistry.ToolsFor([F.Xlsx]).Select(t => t.Id));
        Assert.Equal([ToolId.AskFile], ToolRegistry.ToolsFor([F.Xls]).Select(t => t.Id));
        Assert.Contains(ToolId.TranslateDocument, ToolRegistry.ToolsFor([F.Pdf]).Select(t => t.Id));
        Assert.Contains(ToolId.TranslateDocument, ToolRegistry.ToolsFor([F.Pdf, F.Pdf]).Select(t => t.Id));
        Assert.Contains(ToolId.ReadAloud, ToolRegistry.ToolsFor([F.Pdf]).Select(t => t.Id));
        // Two documents of any kinds, or two pictures, can be compared; three can't.
        Assert.Equal([ToolId.CompareFiles], ToolRegistry.ToolsFor([F.Docx, F.Txt]).Select(t => t.Id));
        Assert.Equal([ToolId.CompareFiles], ToolRegistry.ToolsFor([F.Pdf, F.Docx]).Select(t => t.Id));
        Assert.Contains(ToolId.CompareFiles, ToolRegistry.ToolsFor([F.Pdf, F.Pdf]).Select(t => t.Id));
        Assert.DoesNotContain(ToolId.CompareFiles, ToolRegistry.ToolsFor([F.Pdf, F.Pdf, F.Pdf]).Select(t => t.Id));
        Assert.Empty(ToolRegistry.ToolsFor([F.Docx, F.Txt, F.Txt]));
        Assert.Empty(ToolRegistry.ToolsFor([F.Mp4, F.Pdf]));
        Assert.DoesNotContain(ToolId.CompareFiles, ToolRegistry.ToolsFor([F.Mp3, F.Mp3]).Select(t => t.Id));
        Assert.Equal([ToolId.ExtractColours], ToolRegistry.ToolsFor([F.Svg]).Select(t => t.Id));
    }

    [Fact]
    public void Adobe_files_get_their_own_tools()
    {
        IEnumerable<ToolId> For(params FileFormat[] formats) => ToolRegistry.ToolsFor(formats).Select(t => t.Id);
        Assert.Equal([ToolId.ExportLayers, ToolId.FileDetails, ToolId.ExtractColours, ToolId.SlimPhotoshop], For(F.Psd));
        Assert.Equal([ToolId.ExportLayers, ToolId.SlimPhotoshop, ToolId.CreatePdf, ToolId.CreateCollage], For(F.Psd, F.Psb));
        Assert.Equal([ToolId.FileDetails, ToolId.ExtractColours], For(F.Raw));
        Assert.Equal([ToolId.CreatePdf, ToolId.CreateCollage], For(F.Raw, F.Raw));
        Assert.Equal([ToolId.FileDetails, ToolId.CollectFiles, ToolId.ExtractColours], For(F.Ai));
        Assert.Equal([ToolId.SavePreview, ToolId.FileDetails], For(F.Eps));
        Assert.Equal([ToolId.FileDetails, ToolId.CollectFiles, ToolId.SavePreview], For(F.Indd));
        Assert.Equal([ToolId.FileDetails, ToolId.CollectFiles], For(F.Idml));
        Assert.Equal([ToolId.SavePreview, ToolId.FileDetails], For(F.Xd));
        Assert.Equal([ToolId.CollectFiles], For(F.Prproj));
        Assert.Equal([ToolId.CollectFiles], For(F.Aep));
        Assert.Equal([ToolId.SavePreview], For(F.Indd, F.Xd));
        Assert.Empty(For(F.Prproj, F.Aep));
        Assert.Equal([ToolId.Palette], For(F.Ase));
        Assert.Empty(For(F.Pages));
        Assert.Empty(For(F.Keynote));
        Assert.Empty(For(F.Woff2));
        Assert.Contains(ToolId.ExtractColours, For(F.Jpg));
        // RAW files are recognised by every camera's extension.
        foreach (var ext in new[] { "dng", "CR2", "cr3", "nef", "arw", "raf", "orf", "rw2", "pef", "srw" })
            Assert.Equal(F.Raw, F.FromPath("photo." + ext));
        Assert.Equal([F.Jpg, F.Heic, F.Tiff, F.Png, F.Webp, F.Avif, F.Pdf], ConversionMatrix.TargetsFor(F.Raw));
        Assert.Equal([F.Docx, F.Odt, F.Txt, F.Md, F.Html], ConversionMatrix.TargetsFor(F.Idml));
        Assert.Empty(ConversionMatrix.TargetsFor(F.Indd));
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
