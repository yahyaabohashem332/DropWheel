using System.Windows.Threading;
using ImageMagick;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>Conversions as the wheel runs them: each petal makes the file it says, named as it should be.</summary>
[Collection("History")]
public sealed class ConversionPathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-paths-").FullName;
    private readonly string _previousHistory = History.FilePath;

    public ConversionPathTests()
    {
        History.FilePath = Path.Combine(_dir, "history.json");
        History.Reload();
    }

    public void Dispose()
    {
        History.FilePath = _previousHistory;
        History.Reload();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Data(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testdata")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "testdata", name);
    }

    private static async Task OnStaThreadAsync(Func<Task> action)
    {
        var started = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            started.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var dispatcher = await started.Task;
        try { await await dispatcher.InvokeAsync(action); }
        finally { dispatcher.InvokeShutdown(); }
    }

    private async Task<IReadOnlyList<string>> ConvertAsync(string file, FileFormat target)
    {
        var before = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await OnStaThreadAsync(() =>
            new JobRunner(Petal.App.EngineSetup.CreateConverters()).ConvertAsync([file], target, new ScreenPoint(200, 200)));
        return Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
            .Where(f => !before.Contains(f) && !f.EndsWith("history.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    [Fact]
    public void A_picture_is_offered_pictures_and_pdf_and_nothing_that_needs_text()
    {
        var converters = Petal.App.EngineSetup.CreateConverters();
        foreach (var picture in F.RasterImages)
        {
            var offered = converters.AvailableTargets([picture]);
            Assert.All(offered, t => Assert.True(t.Family == FormatFamily.Image || t == F.Pdf, $"{picture} → {t}"));
            Assert.Contains(F.Pdf, offered);
        }
    }

    [Fact]
    public void The_long_and_first_page_pictures_are_pngs_and_jpgs_for_the_steps_after_them()
    {
        var converters = Petal.App.EngineSetup.CreateConverters();
        Assert.Equal(F.Png, Petal.Recipes.Recipe.After(new() { Kind = Petal.Recipes.RecipeStepKind.Convert, Format = "pdflongpng" }, F.Pdf));
        Assert.Equal(F.Jpg, Petal.Recipes.Recipe.After(new() { Kind = Petal.Recipes.RecipeStepKind.Convert, Format = "pdffirstjpg" }, F.Pdf));
        // A PDF as one long PNG, then that picture as WEBP: a chain the wheel accepts.
        var chain = new Petal.Recipes.Recipe
        {
            Steps = [new() { Kind = Petal.Recipes.RecipeStepKind.Convert, Format = "pdflongpng" }, new() { Kind = Petal.Recipes.RecipeStepKind.Convert, Format = "webp" }],
        };
        Assert.True(chain.AppliesTo([F.Pdf], converters));
        // A file on disk is never taken for one of them.
        Assert.Equal(F.Png, F.FromPath(@"C:\a\b.png"));
        Assert.Equal(F.Jpg, F.FromPath(@"C:\a\b.jpg"));
        Assert.All(new[] { F.PdfLongPng, F.PdfFirstPng, F.PdfLongJpg, F.PdfFirstJpg }, f => Assert.False(f.CanRead));
    }

    [Fact]
    public void A_long_picture_stays_within_what_jpeg_and_memory_allow_or_is_refused()
    {
        static List<Petal.Engines.Documents.Pdf.PdfPageSize> Letter(int pages) =>
            Enumerable.Repeat(new Petal.Engines.Documents.Pdf.PdfPageSize(612, 792), pages).ToList();
        static (double W, double H) Size(IReadOnlyList<Petal.Engines.Documents.Pdf.PdfPageSize> pages, int dpi) =>
            (pages.Max(p => p.Width) / 72 * dpi, pages.Sum(p => p.Height) / 72 * dpi);
        var long_ = Petal.Engines.Documents.Converters.PageImages.LongDpi;

        // A few pages: the settings' own DPI.
        Assert.Equal(300, long_(Letter(3), 300));
        // Many pages: less, within 65,000 pixels tall and 100 million pixels, and still readable.
        foreach (var count in new[] { 10, 40, 100 })
        {
            var dpi = long_(Letter(count), 300);
            var (w, h) = Size(Letter(count), dpi);
            Assert.InRange(dpi, Petal.Engines.Documents.Converters.PageImages.LongMinDpi, 300);
            Assert.True(h <= 65_000 && w * h <= 100_000_000, $"{count} pages at {dpi}: {w}x{h}");
        }
        // One poster page (A0): its pixels stay within the limit too.
        var poster = new List<Petal.Engines.Documents.Pdf.PdfPageSize> { new(2384, 3370) };
        var (pw, ph) = Size(poster, long_(poster, 300));
        Assert.True(pw * ph <= 100_000_000 && pw <= 16_000);
        // A book: no readable long picture, so a clear refusal instead of a picture of specks or a crash.
        Assert.Throws<Petal.Engines.Documents.Pdf.PdfTooLongException>(() => long_(Letter(300), 300));
    }

    [Fact]
    public async Task A_pdf_becomes_a_picture_of_each_page()
    {
        var pdf = Path.Combine(_dir, "album.pdf");
        File.Copy(Data("album.pdf"), pdf);
        var made = await ConvertAsync(pdf, F.Png);
        Assert.NotEmpty(made);
    }

    [Theory]
    [InlineData("pdflongpng", "png", true)]
    [InlineData("pdflongjpg", "jpg", true)]
    [InlineData("pdffirstpng", "png", false)]
    [InlineData("pdffirstjpg", "jpg", false)]
    public async Task A_pdf_becomes_one_long_picture_or_its_first_page(string id, string extension, bool long_)
    {
        var pdf = Path.Combine(_dir, "album.pdf");
        File.Copy(Data("album.pdf"), pdf);
        var target = F.FromId(id)!;
        var made = await ConvertAsync(pdf, target);

        var output = Assert.Single(made);
        Assert.Equal("." + extension, Path.GetExtension(output));
        Assert.True(File.Exists(output));
        using var picture = new MagickImage(output);
        Assert.Equal(extension == "png" ? MagickFormat.Png : MagickFormat.Jpeg, picture.Format);
        // As tall for its width as the pages one under another (the long picture), or as the first page alone.
        var pages = Petal.Engines.Documents.Pdf.PdfInfo.GetPageSizes(pdf);
        Assert.True(pages.Count > 1);
        var expected = long_ ? pages.Sum(p => p.Height) / pages.Max(p => p.Width) : pages[0].Height / pages[0].Width;
        Assert.Equal(expected, (double)picture.Height / picture.Width, 0.01);
    }
}
