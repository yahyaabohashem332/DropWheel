using Petal.App.Jobs;
using Petal.App.Overlay;
using Petal.Core.Actions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Recipes;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>Tests that measure how long something takes: run on their own, after the others, so no other test's work slows them.</summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public sealed class TimingCollection;

/// <summary>A preview on the wheel comes quickly.</summary>
[Collection("Timing")]
public sealed class WheelPreviewTimingTests
{
    [Fact]
    public async Task A_video_preview_is_worked_out_from_a_few_seconds()
    {
        // The first one may wait for the video encoders to be found; after that it is quick.
        await PetalPreviews.SizeAsync([WheelPowerTests.Data("weekend.mov")], F.Mp4, new ConversionSettings(), WheelPowerTests.Converters.Find, CancellationToken.None);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var preview = await PetalPreviews.SizeAsync([WheelPowerTests.Data("weekend.mov")], F.Webm, new ConversionSettings(), WheelPowerTests.Converters.Find, CancellationToken.None);
        Assert.NotNull(preview);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), clock.Elapsed.ToString());
        Assert.Contains("%", preview.Text);
    }
}

/// <summary>Steps chained on the wheel, and what a petal will make before the drop.</summary>
public sealed class WheelPowerTests
{
    internal static readonly ConverterRegistry Converters = Petal.App.EngineSetup.CreateConverters();

    /// <summary>A sample file of the repository's testdata folder.</summary>
    internal static string Data(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testdata")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "testdata", name);
    }

    private static WheelItem Tool(ToolId id) => WheelItem.From(ToolRegistry.Get(id));

    private static WheelItem Action(FileActionId id) => WheelItem.From(FileActionRegistry.All.First(a => a.Id == id));

    [Fact]
    public void Formats_favourites_recipes_and_some_tools_are_steps()
    {
        Assert.Equal(RecipeStepKind.Convert, Assert.Single(Chains.StepsFor(WheelItem.From(F.Webp))!).Kind);
        var favourite = Assert.Single(Chains.StepsFor(WheelItem.From(new Favourite { Format = "jpg", LongestSide = 1600, Quality = 80 }))!);
        Assert.Equal(("jpg", 1600, 80), (favourite.Format, favourite.LongestSide, favourite.Quality));
        Assert.Equal(RecipeStepKind.Compress, Chains.StepsFor(Tool(ToolId.Compress))![0].Kind);
        Assert.Equal(RecipeStepKind.RemoveBackground, Chains.StepsFor(Tool(ToolId.RemoveBackground))![0].Kind);
        Assert.Equal(RecipeStepKind.Zip, Chains.StepsFor(Action(FileActionId.Zip))![0].Kind);
        Assert.Equal(RecipeStepKind.Clean, Chains.StepsFor(Action(FileActionId.Clean))![0].Kind);
        // A tool with a window, or sharing, can only end a chain.
        Assert.Null(Chains.StepsFor(Tool(ToolId.CropImage)));
        Assert.Null(Chains.StepsFor(Action(FileActionId.Share)));
        var recipe = new Recipe { Name = "Shop", Steps = [new() { Kind = RecipeStepKind.Resize, LongestSide = 1080 }, new() { Kind = RecipeStepKind.Convert, Format = "webp" }] };
        Assert.Equal(2, Chains.StepsFor(WheelItem.From(recipe))!.Count);
    }

    [Fact]
    public void A_step_must_fit_what_the_steps_before_make()
    {
        string[] photos = [Data("landscape.jpg"), Data("figures.png")];
        Assert.Null(Chains.Check([], WheelItem.From(F.Webp), photos, Converters));
        Assert.Null(Chains.Check([WheelItem.From(F.Webp)], Tool(ToolId.Compress), photos, Converters));
        Assert.Null(Chains.Check([WheelItem.From(F.Webp), Tool(ToolId.Compress)], Action(FileActionId.Zip), photos, Converters));
        // Once zipped, nothing changes pictures any more.
        Assert.NotNull(Chains.Check([Action(FileActionId.Zip)], Tool(ToolId.RemoveBackground), photos, Converters));
        // A tool with a window can't be a step in the middle.
        Assert.NotNull(Chains.Check([], Tool(ToolId.CropImage), photos, Converters));
        // Folders aren't steps.
        Assert.NotNull(Chains.Check([], WheelItem.From(F.Webp), [Path.GetDirectoryName(Data("landscape.jpg"))!], Converters));
    }

    [Fact]
    public void The_wheel_after_a_step_is_for_the_files_as_they_will_be()
    {
        string[] photos = [@"C:\a\one.png", @"C:\a\two.jpg"];
        Assert.Equal([@"C:\a\one.webp", @"C:\a\two.webp"], Chains.View(photos, [WheelItem.From(F.Webp)]));
        Assert.Equal([@"C:\a\one.webp", @"C:\a\two.webp"], Chains.View(photos, [WheelItem.From(F.Webp), Tool(ToolId.Compress)]));
        Assert.Equal([@"C:\a\one.zip"], Chains.View(photos, [WheelItem.From(F.Webp), Action(FileActionId.Zip)]));
        Assert.Equal([@"C:\a\one.pdf"], Chains.View(photos, [Tool(ToolId.CreatePdf)]));
        Assert.Equal(photos, Chains.View(photos, []));
    }

    [Fact]
    public async Task A_chain_of_petals_runs_as_one_recipe()
    {
        var folder = Directory.CreateTempSubdirectory("petal-chain-").FullName;
        try
        {
            var photo = Path.Combine(folder, "landscape.jpg");
            File.Copy(Data("landscape.jpg"), photo);
            var steps = Chains.Steps([WheelItem.From(F.Png), Action(FileActionId.Zip)]);
            var made = await new RecipeRunner(Converters, new ConversionSettings()).RunAsync(new Recipe { Name = "PNG → ZIP", Steps = steps }, [photo], folder);
            var zip = Assert.Single(made);
            Assert.EndsWith(".zip", zip);
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            Assert.Equal("landscape.png", Assert.Single(archive.Entries).Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Sizes_read_as_smaller_or_larger()
    {
        var smaller = PetalPreviews.Describe(1_000_000, 220_000, exact: true);
        Assert.Contains("78%", smaller);
        Assert.DoesNotContain("About", smaller);
        Assert.Contains("About", PetalPreviews.Describe(1_000_000, 1_120_000, exact: false));
        Assert.Contains("12%", PetalPreviews.Describe(1_000_000, 1_120_000, exact: false));
        Assert.Contains("same", PetalPreviews.Describe(1_000_000, 1_003_000, exact: true));
    }

    [Fact]
    public async Task A_picture_preview_is_the_real_size_of_the_result()
    {
        var photo = Data("landscape.jpg");
        var settings = new ConversionSettings();
        var preview = await PetalPreviews.SizeAsync([photo], F.Webp, settings, Converters.Find, CancellationToken.None);
        Assert.NotNull(preview);
        // Convert it for real and compare with what the preview said.
        var folder = Directory.CreateTempSubdirectory("petal-preview-").FullName;
        try
        {
            var output = Path.Combine(folder, "x.webp");
            await Converters.Find(F.Jpg, F.Webp)!.ConvertAsync(new ConversionJob(photo, F.Jpg, F.Webp, output, settings), null, CancellationToken.None);
            Assert.StartsWith(Petal.App.Util.Sizes.Format(new FileInfo(output).Length), preview.Text);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        // Nothing for a petal that isn't a conversion of these files.
        Assert.Null(await PetalPreviews.SizeAsync([Data("album.pdf")], F.Mp3, settings, Converters.Find, CancellationToken.None));
    }

    [Fact]
    public void A_crowded_tools_wheel_keeps_eleven_petals_and_gathers_the_rest_under_More()
    {
        var video = ToolRegistry.ToolsFor([F.Mp4]).Select(WheelItem.From).ToList();
        Assert.True(video.Count > Petal.App.PetalController.MostToolPetals);
        var content = Petal.App.PetalController.Overflow(new WheelContent(video, ""));
        Assert.Equal(Petal.App.PetalController.MostToolPetals, content.Items.Count);
        var more = content.Items[^1];
        Assert.True(more.IsGroup);
        Assert.Equal(video.Skip(11).Select(i => i.Label), more.Children!.Select(i => i.Label));
        // The usual tools stay on the ring.
        Assert.Contains(content.Items, i => i.Label == "TRIM");
        // Twelve or fewer are left alone.
        var pdf = ToolRegistry.ToolsFor([F.Pdf]).Select(WheelItem.From).ToList();
        Assert.Same(pdf, Petal.App.PetalController.Overflow(new WheelContent(pdf, "")).Items);
    }
}
