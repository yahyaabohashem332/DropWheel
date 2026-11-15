using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Petal.Core.Models;
using Petal.Recipes.Translation;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>
/// The small translation models that translate live captions on a PC without a graphics card: which ones a pair of
/// languages gets, when they are used, and (with PETAL_SMALL_ROUTES naming a folder that holds them) what they write.
/// </summary>
[Collection("Models")]
public sealed class SmallTranslationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _was = ModelStore.Folder;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "petal-small-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ModelStore.Folder = _was;
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    private void Empty()
    {
        Directory.CreateDirectory(_folder);
        ModelStore.Folder = _folder;
    }

    /// <summary>Files of the right size, which is all a model's being here depends on; empty, so nothing is kept.</summary>
    private void Have(IEnumerable<ModelInfo> models)
    {
        foreach (var model in models)
        {
            using var file = File.Create(Path.Combine(_folder, model.FileName));
            file.SetLength(model.Size);
        }
    }

    private static string[] Ids(IEnumerable<ModelInfo> models) => models.Select(m => m.Id).ToArray();

    [Fact]
    public void A_pair_gets_its_own_model_two_through_english_or_the_one_for_every_pair()
    {
        Assert.Equal(["opus-mt-en-ar-encoder", "opus-mt-en-ar-decoder-merged", "opus-mt-en-ar-source", "opus-mt-en-ar-vocab"], Ids(SmallTranslation.ModelsFor("en", "ar")));
        Assert.All(Ids(SmallTranslation.ModelsFor("en", "tr")), id => Assert.StartsWith("opus-mt-tc-big-en-tr", id));
        Assert.All(Ids(SmallTranslation.ModelsFor("es", "fr")), id => Assert.StartsWith("opus-mt-es-fr", id));
        // No model of their own: through English, the first half first.
        var through = Ids(SmallTranslation.ModelsFor("tr", "ar"));
        Assert.Equal(8, through.Length);
        Assert.StartsWith("opus-mt-tc-big-tr-en", through[0]);
        Assert.StartsWith("opus-mt-en-ar", through[4]);
        // Persian and Urdu, which Opus-MT does poorly, whatever the other language.
        foreach (var (from, to) in new[] { ("fa", "ar"), ("en", "fa"), ("ur", "en"), ("tr", "ur"), ("fa", "ur") })
            Assert.All(Ids(SmallTranslation.ModelsFor(from, to)), id => Assert.StartsWith("m2m100", id));
        Assert.Empty(SmallTranslation.ModelsFor("ar", "ar"));
        Assert.False(SmallTranslation.Knows("de", "ar"));
        Assert.False(SmallTranslation.Knows("auto", "ar"));
        foreach (var from in Translator.Languages)
            foreach (var to in Translator.Languages)
                Assert.Equal(from.Code != to.Code, SmallTranslation.Knows(from.Code, to.Code));
    }

    [Fact]
    public void A_pair_is_ready_once_its_models_are_here_or_the_one_for_every_pair_is()
    {
        Empty();
        Assert.False(SmallTranslation.ReadyFor("tr", "ar"));
        Have(SmallTranslation.ModelsFor("tr", "en"));
        Assert.True(SmallTranslation.ReadyFor("tr", "en"));
        // Half the way isn't the way.
        Assert.False(SmallTranslation.ReadyFor("tr", "ar"));
        Have(SmallTranslation.ModelsFor("en", "ar"));
        Assert.True(SmallTranslation.ReadyFor("tr", "ar"));
        Assert.False(SmallTranslation.ReadyFor("ar", "tr"));
        // The model for every pair translates what has nothing better here yet.
        Have(TranslationCatalog.Universal);
        Assert.True(SmallTranslation.ReadyFor("ar", "tr"));
        Assert.True(SmallTranslation.ReadyFor("fa", "ur"));
        Assert.False(SmallTranslation.ReadyFor("ar", "ar"));
    }

    [Fact]
    public void Small_models_are_wanted_where_there_is_no_graphics_card()
    {
        Assert.True(LiveTranslator.SmallWanted("en", "ar", card: false));
        Assert.False(LiveTranslator.SmallWanted("en", "ar", card: true));
        // A language they don't know is the large model's.
        Assert.False(LiveTranslator.SmallWanted("de", "ar", card: false));
    }

    [Fact]
    public async Task What_is_missing_is_asked_for_once_for_each_pair()
    {
        Empty();
        var asked = new List<string>();
        var translator = new LiveTranslator { Card = false };
        translator.Missing += (from, to) => asked.Add(from + ">" + to);
        Assert.Null(await translator.SentenceAsync("Good morning.", "en", "ar", [], "", default));
        Assert.Null(await translator.SentenceAsync("See you tomorrow.", "en", "ar", [], "", default));
        Assert.Null(await translator.SoFarAsync("See you", "en", "ar", [], "", default));
        Assert.Null(await translator.SentenceAsync("Günaydın.", "tr", "ar", [], "", default));
        Assert.Equal(["en>ar", "tr>ar"], asked);
    }

    [Fact]
    public void Captions_offer_a_pair_s_small_models_without_a_card_and_the_one_model_with()
    {
        var live = Ids(GemmaTranslation.LiveModels);
        Assert.Equal(live, Ids(Petal.App.Words.LiveCaptions.ModelsToOffer("tr", "ar", card: true)));
        Assert.Equal(live, Ids(Petal.App.Words.LiveCaptions.ModelsToOffer("auto", "ar", card: true)));
        Assert.Equal(Ids(SmallTranslation.ModelsFor("tr", "ar")), Ids(Petal.App.Words.LiveCaptions.ModelsToOffer("tr", "ar", card: false)));
        // The language isn't known until it is heard: nothing to offer before.
        Assert.Empty(Petal.App.Words.LiveCaptions.ModelsToOffer("auto", "ar", card: false));
        Assert.Empty(Petal.App.Words.LiveCaptions.ModelsToOffer("ar", "ar", card: false));
    }

    [Fact]
    public void Every_file_is_pinned_and_every_decoder_carries_on()
    {
        foreach (var model in TranslationCatalog.All)
        {
            Assert.Equal(64, model.Sha256.Length);
            Assert.True(model.Size > 0);
            Assert.Matches("/resolve/[0-9a-f]{40}/", model.Url);
            Assert.NotEqual(model.FileName, model.Replaces);
            Assert.DoesNotContain(model, ModelCatalog.All);
        }
        Assert.Equal(TranslationCatalog.All.Count, TranslationCatalog.All.Select(m => m.FileName).Distinct().Count());
        foreach (var pair in TranslationCatalog.Pairs)
        {
            Assert.Equal(4, pair.Files.Count);
            Assert.Contains("decoder-merged", pair.Files[1].FileName);
            Assert.EndsWith(".spm", pair.Files[2].FileName);
            Assert.EndsWith("vocab.json", pair.Files[3].FileName);
        }
        Assert.Contains("decoder-merged", TranslationCatalog.Universal[1].FileName);
        // Dub's models for English and Arabic stay as they were: the captions' decoder is another file beside them.
        Assert.DoesNotContain("merged", ModelCatalog.OpusEnArDecoder.FileName);
        Assert.DoesNotContain(TranslationCatalog.All, m => m.Replaces == ModelCatalog.OpusEnArDecoder.FileName || m.Replaces == ModelCatalog.OpusArEnDecoder.FileName);
    }

    [Fact]
    public void The_small_models_move_with_the_models_folder()
    {
        Empty();
        var file = TranslationCatalog.Pairs.First(p => p.From == "es" && p.To == "fr").Files[1];
        File.WriteAllText(Path.Combine(_folder, file.FileName), "x");
        Assert.Contains(file.FileName, ModelStore.ItemsIn(_folder));
    }

    private sealed class Serves(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
    }

    [Fact]
    public async Task A_download_takes_the_place_of_the_file_it_replaces()
    {
        Empty();
        var content = "the new decoder"u8.ToArray();
        var old = Path.Combine(_folder, "old-decoder.onnx");
        File.WriteAllText(old, "the old decoder");
        var model = new ModelInfo("test-decoder", "Test", "new-decoder.onnx", "https://example.invalid/new", Convert.ToHexString(SHA256.HashData(content)), content.Length, "MIT", Replaces: "old-decoder.onnx");
        using var client = new HttpClient(new Serves(content));
        var path = await ModelStore.DownloadAsync(model, client: client);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.False(File.Exists(old));
        // One that doesn't arrive whole takes nothing away.
        File.WriteAllText(old, "the old decoder");
        var wrong = model with { FileName = "other-decoder.onnx", Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStore.DownloadAsync(wrong, client: client));
        Assert.True(File.Exists(old));
    }

    // ---------- with the models (PETAL_SMALL_ROUTES names their folder) ----------

    private static string? Models => Environment.GetEnvironmentVariable("PETAL_SMALL_ROUTES") is { Length: > 0 } folder ? folder : null;

    private bool Real()
    {
        if (Models is not { } folder)
        {
            output.WriteLine("Skipped: PETAL_SMALL_ROUTES isn't set.");
            return false;
        }
        ModelStore.Folder = folder;
        return true;
    }

    [Theory]
    [InlineData("en", "ar", "The meeting will start tomorrow morning in the new office.", "الاجتماع")]
    [InlineData("ar", "en", "سيبدأ الاجتماع صباح غد في المكتب الجديد.", "meeting")]
    [InlineData("en", "tr", "The meeting will start tomorrow morning in the new office.", "toplantı")]
    [InlineData("tr", "en", "Toplantı yarın sabah yeni ofiste başlayacak.", "meeting")]
    [InlineData("tr", "ar", "Toplantı yarın sabah yeni ofiste başlayacak.", "الاجتماع")]
    [InlineData("es", "fr", "La reunión empezará mañana por la mañana.", "réunion")]
    [InlineData("fa", "en", "جلسه فردا صبح شروع می‌شود.", "tomorrow")]
    [InlineData("en", "ur", "The meeting will start tomorrow morning.", "صبح")]
    public void A_sentence_comes_out_in_the_other_language(string from, string to, string text, string expected)
    {
        if (!Real() || !SmallTranslation.ReadyFor(from, to))
            return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var translated = SmallTranslation.Sentence(text, from, to);
        output.WriteLine($"{from}→{to} {clock.Elapsed.TotalSeconds:0.00}s: {translated}");
        Assert.Contains(expected, translated, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The second line: a sentence heard a few words more at a time. Whatever the listener was shown as settled is
    /// still there, word for word, in every translation after.
    /// </summary>
    [Theory]
    [InlineData("en", "ar", "The city council decided last night that the old bridge over the river will be closed for repairs until the end of next month.")]
    [InlineData("tr", "ar", "Belediye meclisi dün gece nehrin üzerindeki eski köprünün gelecek ayın sonuna kadar onarım için kapatılmasına karar verdi.")]
    [InlineData("ar", "en", "قرر مجلس المدينة مساء أمس إغلاق الجسر القديم فوق النهر لإصلاحه حتى نهاية الشهر المقبل.")]
    [InlineData("en", "fa", "The city council decided last night that the old bridge over the river will be closed for repairs until the end of next month.")]
    public void Settled_words_stay_as_a_sentence_grows(string from, string to, string sentence)
    {
        if (!Real() || !SmallTranslation.ReadyFor(from, to))
            return;
        var soFar = new SmallSoFar();
        var words = sentence.Split(' ');
        var shown = new List<string>();
        int steady = 0, carried = 0;
        for (int count = 3; ; count = Math.Min(words.Length, count + 3))
        {
            string begun = string.Join(' ', shown.Take(steady));
            var now = LiveInterpreter.Unfinished(soFar.Translate(string.Join(' ', words.Take(count)), from, to, begun, default)).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            output.WriteLine($"{count,2}: {string.Join(' ', now.Take(steady))} | {string.Join(' ', now.Skip(steady))}");
            Assert.NotEmpty(now);
            if (begun.Length > 0)
            {
                carried++;
                Assert.StartsWith(begun, string.Join(' ', now), StringComparison.Ordinal);
            }
            // As the interpreter settles them: what two translations in a row agree on, but never the newest word.
            int same = 0;
            while (same < now.Count - 1 && same < shown.Count && now[same] == shown[same])
                same++;
            steady = Math.Min(Math.Max(same, Math.Min(steady, now.Count - 1)), now.Count);
            shown = now;
            if (count == words.Length)
                break;
        }
        Assert.True(carried >= 2, "The translation never had settled words to carry on from.");
        Assert.True(shown.Count >= words.Length / 3, "The whole sentence should be translated by its end.");
    }

    /// <summary>
    /// The first line: a finished sentence keeps the words the second line showed for it when they are as likely as a
    /// fresh translation, so it changes only where it must; words the model finds clearly worse give way.
    /// </summary>
    [Theory]
    [InlineData("en", "tr", "The city council decided last night that the old bridge over the river will be closed for repairs.")]
    [InlineData("ar", "en", "قرر مجلس المدينة مساء أمس إغلاق الجسر القديم فوق النهر لإصلاحه.")]
    public void A_finished_sentence_keeps_the_words_shown_for_it_where_they_are_as_good(string from, string to, string sentence)
    {
        if (!Real() || !SmallTranslation.ReadyFor(from, to))
            return;
        var soFar = new SmallSoFar();
        // The second line had it up to its last few words when it ended, without a full stop.
        var words = sentence.Split(' ');
        var shown = LiveInterpreter.Unfinished(soFar.Translate(string.Join(' ', words.SkipLast(3)), from, to, "", default));
        var final = soFar.Final(sentence, from, to, shown, default, margin: 1e9);
        output.WriteLine($"shown: {shown}\nfinal: {final}\nfresh: {SmallTranslation.Sentence(sentence, from, to)}");
        Assert.StartsWith(shown, final, StringComparison.Ordinal);
        // Weighed, a fresh translation that the model finds far likelier takes its place.
        var nonsense = soFar.Final(sentence, from, to, shown, default, margin: -1e9);
        Assert.Equal(SmallTranslation.Sentence(sentence, from, to), nonsense);
    }

    [Fact]
    public void A_translation_is_likelier_than_the_same_words_jumbled()
    {
        if (!Real() || !SmallTranslation.ReadyFor("en", "ar"))
            return;
        var pair = TranslationCatalog.Pairs.First(p => p.From == "en" && p.To == "ar");
        var model = new PairModel(pair.Files, pair.Start, pair.Mark, pair.End);
        const string text = "Today we are going to bake bread at home.";
        var right = model.Translate(text, 1, null, default).Pieces;
        var jumbled = right.Reverse().ToList();
        double a = model.Likelihood(text, right), b = model.Likelihood(text, jumbled);
        output.WriteLine($"{a:0.000} {b:0.000}");
        Assert.InRange(a, -3, 0);
        Assert.True(a > b + 1);
    }

    [Fact]
    public void Following_a_few_candidates_carries_on_too()
    {
        if (!Real() || !SmallTranslation.ReadyFor("en", "ar"))
            return;
        var pair = TranslationCatalog.Pairs.First(p => p.From == "en" && p.To == "ar");
        var model = new PairModel(pair.Files, pair.Start, pair.Mark, pair.End);
        const string text = "Welcome back to the channel, everyone. Today we are going to bake bread at home.";
        var likeliest = model.Translate(text, 1, null, default);
        var careful = model.Translate(text, 4, null, default);
        output.WriteLine(likeliest.Text + "\n" + careful.Text);
        Assert.Contains("الخبز", likeliest.Text);
        Assert.Contains("الخبز", careful.Text);
        // The start of an answer, given back, is where the next begins.
        var start = model.Start(likeliest.Pieces, 2);
        Assert.InRange(start.Count, 1, likeliest.Pieces.Count - 1);
        var carried = model.Translate(text, 4, start, default);
        Assert.StartsWith(model.Words(start), carried.Text, StringComparison.Ordinal);
        Assert.Equal(start.ToList(), carried.Pieces.Take(start.Count).ToList());
        // An answer is no longer than it is let be.
        Assert.True(model.Translate(text, 1, null, default, most: 5).Pieces.Count <= 5);
    }
}
