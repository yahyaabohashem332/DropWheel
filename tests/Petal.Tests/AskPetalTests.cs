using Petal.Core.Formats;
using Petal.Core.Models;
using Petal.Recipes;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

[Collection("Models")]
public sealed class AskPetalTests(ITestOutputHelper output)
{
    private static FileFormat[] Many(FileFormat format, int count) => Enumerable.Repeat(format, count).ToArray();

    /// <summary>What a recipe does, compactly: "Resize:1080 Convert:webp Zip".</summary>
    private static string Steps(Recipe? recipe) => recipe is null ? "(nothing)" : string.Join(" ", recipe.Steps.Select(s => s.Kind switch
    {
        RecipeStepKind.Convert => $"Convert:{s.Format}",
        RecipeStepKind.Resize => $"Resize:{s.LongestSide}",
        RecipeStepKind.Rotate => $"Rotate:{s.Turns}",
        RecipeStepKind.Compress => $"Compress:{s.App ?? (s.Strong ? "strong" : "")}",
        RecipeStepKind.RemoveBackground => $"RemoveBackground:{s.Background}",
        RecipeStepKind.Watermark => $"Watermark:{s.Text}",
        RecipeStepKind.Rename => $"Rename:{s.Pattern}",
        _ => s.Kind.ToString(),
    }));

    public static TheoryData<string, int, string, string> Requests => new()
    {
        { "jpg", 12, "make them 1080 px webp and zip them", "Resize:1080 Convert:webp Zip" },
        { "png", 3, "put these into one pdf and make it searchable", "CombinePdf Searchable" },
        { "jpg", 5, "صغّرها إلى 800 بكسل وحوّلها إلى PNG", "Resize:800 Convert:png" },
        { "mp4", 4, "اضغطها للواتساب", "Compress:whatsapp" },
        { "pdf", 2, "add a watermark saying CONFIDENTIAL then compress", "Watermark:CONFIDENTIAL Compress:" },
        { "heic", 8, "convert to jpg, remove location, rename to Trip {n}", "Convert:jpg Clean Rename:Trip {n}" },
        { "jpg", 1, "احذف الخلفية واجعلها بيضاء", "RemoveBackground:#FFFFFF" },
        { "jpg", 6, "دمجها في ملف PDF واحد", "CombinePdf" },
        { "jpg", 3, "rotate them to the left", "Rotate:270" },
        { "png", 3, "أزل الخلفية ثم ضعها في ملف مضغوط", "RemoveBackground: Zip" },
        { "jpg", 2, "اضغطها لأقل حجم ممكن", "Compress:strong" },
        { "mov", 2, "make them small enough to email", "Compress:email" },
        { "jpg", 4, "أضف علامة مائية \"سري\" وأدرها لليمين", "Watermark:سري Rotate:90" },
        { "mp3", 2, "what's the weather tomorrow?", "(nothing)" },
        { "pdf", 1, "ما هو الطقس اليوم؟", "(nothing)" },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void Word_clues_understand_common_requests(string format, int count, string request, string expected)
    {
        var recipe = AskPetal.Understand(request, Many(F.FromId(format)!, count));
        output.WriteLine($"{request} → {Steps(recipe)}");
        Assert.Equal(expected, Steps(recipe));
    }

    [Fact]
    public void Steps_the_request_doesnt_mention_are_dropped()
    {
        var formats = Many(F.Mp4, 4);
        const string request = "اضغطها للواتساب";
        // The model added a ZIP and named the wrong app.
        var proposed = new[] { new RecipeStep { Kind = RecipeStepKind.Compress, App = "upload" }, new RecipeStep { Kind = RecipeStepKind.Zip } };
        var clues = typeof(AskPetal).GetMethod("Clues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [request, formats]) as System.Collections.IEnumerable;
        var grounded = AskPetal.Ground(proposed, clues!.Cast<AskPetal.Found>().ToList());
        Assert.Equal("Compress:whatsapp", Steps(grounded));
        // Nothing in the request backs it: nothing is kept.
        Assert.Null(AskPetal.Ground(proposed, []));
    }

    [Fact]
    public void Model_answers_are_read_safely()
    {
        Assert.Equal("Resize:1080 Convert:webp Zip", Steps(new Recipe { Steps = AskPetal.Parse("""{"steps": [{"do": "resize", "value": "1080 px"}, {"do": "convert", "value": ".WEBP"}, {"do": "zip", "value": ""}]} [end of text]""").ToList() }));
        Assert.Empty(AskPetal.Parse("not json at all"));
        Assert.Empty(AskPetal.Parse("""{"steps": [{"do": "convert", "value": "nonsense"}, {"do": "launch_rockets", "value": "now"}]}"""));
    }

    [Fact]
    public async Task Command_line_shows_the_plan_and_runs_it()
    {
        var dir = Directory.CreateTempSubdirectory("petal-ask-").FullName;
        try
        {
            var photo = Path.Combine(dir, "photo.png");
            using (var image = new ImageMagick.MagickImage(new ImageMagick.MagickColor("#3C78C8"), 1600, 1200))
                image.Write(photo);
            var plan = new StringWriter();
            Assert.Equal(0, await new Petal.Cli.Cli(plan, new StringWriter(), showProgress: false).RunAsync(["ask", "shrink to 800 px and save as webp", photo], CancellationToken.None));
            Assert.Equal(["1. Resize to 800 px", "2. Convert to WEBP"], plan.ToString().Trim().Split(Environment.NewLine));
            Assert.Single(Directory.GetFiles(dir));

            var ran = new StringWriter();
            Assert.Equal(0, await new Petal.Cli.Cli(ran, new StringWriter(), showProgress: false).RunAsync(["ask", "shrink to 800 px and save as webp", photo, "--run"], CancellationToken.None));
            var result = ran.ToString().Trim().Split(Environment.NewLine).Last().Trim();
            using var webp = new ImageMagick.MagickImage(result);
            Assert.Equal((800u, 600u), (webp.Width, webp.Height));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>With PETAL_LLM pointing at a folder with llama.cpp and Qwen (as ModelStore names them), the model runs.</summary>
    [Fact]
    public async Task The_model_turns_free_phrasing_into_steps()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_LLM");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_LLM isn't set.");
            return;
        }
        var saved = ModelStore.Folder;
        ModelStore.Folder = folder;
        try
        {
            Assert.True(AskPetal.ModelReady);
            var cases = new (FileFormat[] Formats, string Request, string Expected)[]
            {
                (Many(F.Jpg, 12), "make them 1080 px webp and zip them", "Resize:1080 Convert:webp Zip"),
                (Many(F.Png, 3), "put these into one pdf and make it searchable", "CombinePdf Searchable"),
                (Many(F.Mp4, 4), "اضغطها للواتساب", "Compress:whatsapp"),
                (Many(F.Jpg, 1), "احذف الخلفية واجعلها بيضاء", "RemoveBackground:#FFFFFF"),
                (Many(F.Mp3, 2), "what's the weather tomorrow?", "(nothing)"),
            };
            foreach (var (formats, request, expected) in cases)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var answer = await AskPetal.AskAsync(request, formats);
                output.WriteLine($"[{clock.Elapsed.TotalSeconds:0.0}s] {request} → {Steps(answer.Recipe)}");
                Assert.True(answer.UsedModel);
                Assert.Equal(expected, Steps(answer.Recipe));
            }
        }
        finally
        {
            ModelStore.Folder = saved;
        }
    }
}
