using System.IO.Compression;
using ImageMagick;
using Petal.Core.Jobs;
using Petal.Recipes;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

public class RecipeTests : IDisposable
{
    private static readonly ConverterRegistry Converters = Petal.App.EngineSetup.CreateConverters();
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-recipes-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Picture(string name, int width, int height, string colour = "#C86428")
    {
        var path = Path.Combine(_dir, name);
        using var image = new MagickImage(new MagickColor(colour), (uint)width, (uint)height);
        image.Write(path);
        return path;
    }

    private static RecipeRunner Runner() => new(Converters, new ConversionSettings());

    [Fact]
    public async Task Steps_run_in_order_and_results_land_beside_the_originals()
    {
        var a = Picture("a.png", 2000, 1000);
        var b = Picture("b.png", 900, 1800, "#2868C8");
        var shop = new Recipe
        {
            Name = "Shop",
            Steps = [new RecipeStep { Kind = RecipeStepKind.Resize, LongestSide = 800 }, new RecipeStep { Kind = RecipeStepKind.Convert, Format = "webp" }],
        };
        Assert.True(shop.AppliesTo([F.Png], Converters));

        var saved = await Runner().RunAsync(shop, [a, b]);

        Assert.Equal([Path.Combine(_dir, "a Shop.webp"), Path.Combine(_dir, "b Shop.webp")], saved);
        using (var first = new MagickImage(saved[0]))
        {
            Assert.Equal(MagickFormat.WebP, first.Format);
            Assert.Equal((800u, 400u), (first.Width, first.Height));
        }
        using (var second = new MagickImage(saved[1]))
            Assert.Equal((400u, 800u), (second.Width, second.Height));
        using (var original = new MagickImage(a))
            Assert.Equal(2000u, original.Width);
        Assert.Equal(4, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public async Task Renamed_files_can_be_packed_into_one_zip()
    {
        var a = Picture("IMG_0041.jpg", 64, 64);
        var b = Picture("IMG_0042.jpg", 64, 64);
        var trip = new Recipe
        {
            Name = "Trip",
            Steps = [new RecipeStep { Kind = RecipeStepKind.Rename, Pattern = "Trip {n}" }, new RecipeStep { Kind = RecipeStepKind.Zip }],
        };
        var output = Path.Combine(_dir, "out");

        var saved = await Runner().RunAsync(trip, [a, b], output);

        Assert.Equal([Path.Combine(output, "Trip.zip")], saved);
        using var zip = ZipFile.OpenRead(saved[0]);
        Assert.Equal(["Trip 1.jpg", "Trip 2.jpg"], zip.Entries.Select(e => e.FullName).Order());
        Assert.True(File.Exists(a) && File.Exists(b), "Renaming works on copies.");
    }

    [Fact]
    public async Task Pictures_and_pdfs_combine_into_one_pdf_in_order()
    {
        var cover = Picture("1 cover.jpg", 600, 800);
        var pdf = Path.Combine(_dir, "2 pages.pdf");
        await PdfOf(Picture("p1.png", 300, 400), Picture("p2.png", 300, 400), pdf);
        var back = Picture("3 back.jpg", 600, 800, "#28C864");
        var report = new Recipe { Name = "Report", Steps = [new RecipeStep { Kind = RecipeStepKind.CombinePdf }] };

        var saved = await Runner().RunAsync(report, [cover, pdf, back]);

        Assert.Equal([Path.Combine(_dir, "Report.pdf")], saved);
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(saved[0], PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(4, document.PageCount);
    }

    private static async Task PdfOf(string first, string second, string output) =>
        await Petal.Engines.Imaging.PdfCreator.CreateAsync([first, second], output, new Petal.Engines.Imaging.PdfCreateOptions(), null, CancellationToken.None);

    [Fact]
    public void A_recipe_fits_only_files_every_step_can_take()
    {
        var resizeThenPdf = new Recipe { Steps = [new RecipeStep { Kind = RecipeStepKind.Resize, LongestSide = 1000 }, new RecipeStep { Kind = RecipeStepKind.Convert, Format = "pdf" }] };
        var pdfThenResize = new Recipe { Steps = [new RecipeStep { Kind = RecipeStepKind.Convert, Format = "pdf" }, new RecipeStep { Kind = RecipeStepKind.Resize, LongestSide = 1000 }] };
        var clean = new Recipe { Steps = [new RecipeStep { Kind = RecipeStepKind.Clean }, new RecipeStep { Kind = RecipeStepKind.Zip }] };

        Assert.True(resizeThenPdf.AppliesTo([F.Jpg, F.Png], Converters));
        Assert.False(pdfThenResize.AppliesTo([F.Jpg], Converters));
        Assert.False(resizeThenPdf.AppliesTo([F.Mp3], Converters));
        Assert.True(clean.AppliesTo([F.Jpg, F.Mp4, F.Docx], Converters));
        Assert.False(clean.AppliesTo([F.Zip], Converters));
        Assert.False(new Recipe().AppliesTo([F.Jpg], Converters));
    }

    [Fact]
    public async Task A_failed_run_leaves_nothing_behind()
    {
        var good = Picture("good.png", 400, 300);
        var broken = Path.Combine(_dir, "broken.png");
        File.WriteAllText(broken, "not a picture");
        var recipe = new Recipe { Name = "Small", Steps = [new RecipeStep { Kind = RecipeStepKind.Resize, LongestSide = 100 }] };

        await Assert.ThrowsAnyAsync<Exception>(() => Runner().RunAsync(recipe, [good, broken]));

        Assert.Equal(["broken.png", "good.png"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Command_line_runs_a_recipe_by_name()
    {
        var photo = Picture("photo.png", 1200, 900);
        var recipe = new Recipe { Name = "Web", Steps = [new RecipeStep { Kind = RecipeStepKind.Convert, Format = "jpg", LongestSide = 600 }] };
        var output = new StringWriter();
        var cli = new Petal.Cli.Cli(output, new StringWriter(), showProgress: false);

        Assert.Equal(0, await cli.RunRecipeAsync(recipe, [photo, "--out", Path.Combine(_dir, "web")], CancellationToken.None));

        var result = output.ToString().Trim();
        Assert.Equal(Path.Combine(_dir, "web", "photo Web.jpg"), result);
        using var image = new MagickImage(result);
        Assert.Equal((600u, 450u), (image.Width, image.Height));
    }
}
