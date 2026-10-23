using Petal.Core.Models;
using Petal.Recipes;
using Petal.Recipes.Search;

namespace Petal.Tests;

/// <summary>Asking your files: questions told from search words, passages found and scored, and answers with their sources.</summary>
public sealed class FileQuestionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-ask-files-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("when does the car insurance end?", true)]
    [InlineData("How much was the electricity bill", true)]
    [InlineData("كم كانت فاتورة الكهرباء؟", true)]
    [InlineData("متى ينتهي عقد الإيجار", true)]
    [InlineData("beach photos", false)]
    [InlineData("invoice", false)]
    public void Questions_are_told_from_search_words(string text, bool question) =>
        Assert.Equal(question, FileQuestions.LooksLikeQuestion(text));

    [Fact]
    public void A_question_keeps_the_words_that_say_what_it_is_about()
    {
        Assert.Equal(["car", "insurance", "end"], FileQuestions.Terms("When does the car insurance end?"));
        Assert.Equal(["فاتوره", "الكهرباء"], FileQuestions.Terms("كم كانت فاتورة الكهرباء؟"));
    }

    private (string Path, string Body) Document(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return (path, SearchIndex.Words(text));
    }

    [Fact]
    public async Task The_passage_that_answers_comes_first()
    {
        var insurance = Document("car insurance.txt", "Policy number 4471-B.\n\nYour car insurance covers the period from 1 March 2026 to 28 February 2027. The yearly premium is 412 dollars.");
        var recipe = Document("recipe.txt", "Mix the flour with the eggs. Bake for 40 minutes. A car could drive past while it bakes.");
        var sources = await FileQuestions.FindAsync("When does the car insurance end?", [recipe, insurance],
            (path, _) => Task.FromResult<string?>(File.ReadAllText(path)), CancellationToken.None);
        Assert.NotEmpty(sources);
        Assert.Equal(insurance.Path, sources[0].Path);
        Assert.Contains("28 February 2027", sources[0].Passage);
        Assert.Equal(1, sources[0].Number);
        // Without the model the passages themselves are the answer to read.
        if (!AskPetal.ModelReady)
            Assert.False((await FileQuestions.AnswerAsync("When does the car insurance end?", sources, CancellationToken.None)).Found);
    }

    [Fact]
    public async Task The_model_answers_from_the_passages_and_cites_them()
    {
        if (!AskPetal.ModelReady)
            return;
        var insurance = Document("car insurance.txt", "Policy number 4471-B.\n\nYour car insurance covers the period from 1 March 2026 to 28 February 2027. The yearly premium is 412 dollars.");
        var sources = await FileQuestions.FindAsync("When does my car insurance end?", [insurance], (path, _) => Task.FromResult<string?>(File.ReadAllText(path)), CancellationToken.None);
        var answer = await FileQuestions.AnswerAsync("When does my car insurance end?", sources, CancellationToken.None);
        Assert.True(answer.Found, answer.Text);
        Assert.True(answer.Text.Contains("2027") && FileQuestions.Cited(answer.Text).Contains(1), answer.Text);
        Assert.Contains(1, FileQuestions.Cited(answer.Text));
        // In Arabic too.
        var lease = Document("عقد الإيجار.txt", "عقد إيجار الشقة. يبدأ العقد في 1 تموز 2026 وينتهي في 30 حزيران 2027، والأجرة الشهرية 350 ديناراً.");
        var arabicSources = await FileQuestions.FindAsync("متى ينتهي عقد الإيجار؟", [lease], (path, _) => Task.FromResult<string?>(File.ReadAllText(path)), CancellationToken.None);
        var arabic = await FileQuestions.AnswerAsync("متى ينتهي عقد الإيجار؟", arabicSources, CancellationToken.None);
        Assert.True(arabic.Found && (arabic.Text.Contains("2027") || arabic.Text.Contains("٢٠٢٧")), arabic.Text);
        // Asked what the files don't say, it says so instead of making something up.
        var unknown = await FileQuestions.AnswerAsync("What colour is the car?", sources, CancellationToken.None);
        Assert.DoesNotContain("red", unknown.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_index_finds_the_files_that_mention_a_question()
    {
        var index = new SearchIndex(Path.Combine(_dir, "search.db"));
        try
        {
            var a = Document("lease.txt", "The lease of the flat ends on 30 June 2027.");
            var b = Document("notes.txt", "Buy milk and bread.");
            index.Update([new FileInfo(a.Path), new FileInfo(b.Path)], [_dir]);
            foreach (var (id, path, _) in index.Waiting(10))
                index.Store(id, path, File.ReadAllText(path), null);
            var found = index.Mentions(FileQuestions.Terms("When does the lease end?"), 5);
            Assert.Equal([a.Path], found.Select(f => f.Path));
        }
        finally
        {
            index.Dispose();
        }
    }

    [Theory]
    [InlineData(" Record the screen. ", "Record the screen")]
    [InlineData("Please compress these files!", "compress these files")]
    [InlineData("سجل الشاشة من فضلك.", "سجل الشاشة")]
    public void Spoken_commands_become_search_words(string heard, string words) =>
        Assert.Equal(words, Petal.App.Words.VoiceCommand.Words(heard));

    [Fact]
    public void Descriptions_lose_the_models_habits()
    {
        Assert.Equal("A window.", PictureDescriber.Clean(" In this image we can see a window. <end_of_utterance>"));
        Assert.Equal("A plane over the sea.", PictureDescriber.Clean("a plane over the sea"));
    }

    [Fact]
    public async Task A_picture_is_described_when_the_model_is_here()
    {
        // Only where the picture model was put aside for testing; a test never downloads it.
        if (Environment.GetEnvironmentVariable("PETAL_TEST_MODELS") is not { Length: > 0 } models)
            return;
        var keep = ModelStore.Folder;
        ModelStore.Folder = models;
        try
        {
            if (!PictureDescriber.ModelReady)
                return;
            var picture = Path.Combine(_dir, "red.png");
            using (var bitmap = new SkiaSharp.SKBitmap(320, 240))
            {
                using (var canvas = new SkiaSharp.SKCanvas(bitmap))
                {
                    canvas.Clear(SkiaSharp.SKColors.SkyBlue);
                    using var sun = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Gold, IsAntialias = true };
                    canvas.DrawCircle(240, 70, 40, sun);
                    using var grass = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.ForestGreen };
                    canvas.DrawRect(0, 170, 320, 70, grass);
                }
                using var stream = File.Create(picture);
                bitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
            }
            var text = await PictureDescriber.DescribeAsync(picture, detailed: false);
            Assert.True(text.Length > 8, text);
            Assert.EndsWith(".", text);
        }
        finally
        {
            ModelStore.Folder = keep;
        }
    }
}
