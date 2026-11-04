using System.Diagnostics;
using System.Net;
using System.Text;
using Petal.Core.Models;
using Petal.Core.Text;
using Petal.Recipes.Cloud;
using Petal.Recipes.Translation;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>Translation between any of the languages, on this PC or with the user's own key.</summary>
public sealed class GlobalTranslationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("The meeting will start tomorrow morning and everyone is invited to the new office.", "en")]
    [InlineData("La reunión empezará mañana por la mañana y todos están invitados a la nueva oficina.", "es")]
    [InlineData("La réunion commencera demain matin et tout le monde est invité dans le nouveau bureau.", "fr")]
    [InlineData("Die Besprechung beginnt morgen früh und alle sind in das neue Büro eingeladen.", "de")]
    [InlineData("Toplantı yarın sabah başlayacak ve herkes yeni ofise davetli.", "tr")]
    [InlineData("Rapat akan dimulai besok pagi dan semua orang diundang ke kantor yang baru.", "id")]
    [InlineData("سيبدأ الاجتماع صباح غد والجميع مدعوون إلى المكتب الجديد.", "ar")]
    [InlineData("جلسه فردا صبح شروع می‌شود و همه به دفتر جدید دعوت هستند.", "fa")]
    [InlineData("اجلاس کل صبح شروع ہوگا اور سب کو نئے دفتر میں مدعو کیا گیا ہے۔", "ur")]
    [InlineData("Встреча начнётся завтра утром, и все приглашены в новый офис.", "ru")]
    [InlineData("会议将于明天早上开始。", "zh")]
    [InlineData("会議は明日の朝に始まります。", "ja")]
    [InlineData("회의는 내일 아침에 시작됩니다.", "ko")]
    [InlineData("बैठक कल सुबह शुरू होगी।", "hi")]
    public void A_text_s_language_is_told(string text, string language) => Assert.Equal(language, LanguageGuess.Of(text));

    [Fact]
    public void Nothing_to_go_on_tells_nothing()
    {
        Assert.Null(LanguageGuess.Of(""));
        Assert.Null(LanguageGuess.Of("123 456"));
    }

    [Fact]
    public void Every_pair_has_a_route_and_every_file_a_pinned_hash()
    {
        foreach (var from in Translator.Languages)
            foreach (var to in Translator.Languages)
            {
                var models = Translator.ModelsFor(from.Code, to.Code);
                if (from.Code == to.Code)
                    Assert.Empty(models);
                else
                    Assert.NotEmpty(models);
            }
        foreach (var model in TranslationCatalog.Pairs.SelectMany(p => p.Files).Concat(TranslationCatalog.Universal))
        {
            Assert.Equal(64, model.Sha256.Length);
            Assert.True(model.Size > 0);
            Assert.Contains("/resolve/", model.Url);
            Assert.DoesNotContain("/resolve/main/", model.Url);
        }
    }

    [Fact]
    public void A_pair_with_its_own_model_uses_it_and_others_go_through_english_or_the_universal_model()
    {
        Assert.Contains(Translator.ModelsFor("en", "es"), m => m.Id.StartsWith("opus-mt-en-es", StringComparison.Ordinal));
        var through = Translator.ModelsFor("es", "fr");
        Assert.True(through.Any(m => m.Id.StartsWith("opus-mt-es-en", StringComparison.Ordinal)) || through.Any(m => m.Id.StartsWith("m2m100", StringComparison.Ordinal)));
        Assert.Contains(Translator.ModelsFor("en", "tr"), m => m.Id.StartsWith("m2m100", StringComparison.Ordinal));
        Assert.Contains(Translator.ModelsFor("fa", "ur"), m => m.Id.StartsWith("m2m100", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reply_s_lines_are_read_by_their_numbers()
    {
        var lines = CloudAi.NumberedLines("1. Hola\n2) Mundo\r\nsomething else\n10. Fin");
        Assert.Equal("Hola", lines[1]);
        Assert.Equal("Mundo", lines[2]);
        Assert.Equal("Fin", lines[10]);
        Assert.Equal(3, lines.Count);
    }

    /// <summary>Real models, downloaded and checked against their pins, into the folder PETAL_TRANSLATE names.</summary>
    [Fact]
    public async Task Real_translation_between_languages()
    {
        if (Environment.GetEnvironmentVariable("PETAL_TRANSLATE") is not { Length: > 0 } folder)
        {
            output.WriteLine("Skipped: set PETAL_TRANSLATE to a folder for the models.");
            return;
        }
        var before = ModelStore.Folder;
        ModelStore.Folder = folder;
        try
        {
            var cases = new (string From, string To, string Text)[]
            {
                ("en", "es", "The meeting will start tomorrow morning."),
                ("es", "en", "La reunión empezará mañana por la mañana."),
                ("en", "fr", "The meeting will start tomorrow morning."),
                ("fr", "en", "La réunion commencera demain matin."),
                ("en", "id", "The meeting will start tomorrow morning."),
                ("id", "en", "Rapat akan dimulai besok pagi."),
                ("tr", "en", "Toplantı yarın sabah başlayacak."),
                ("ar", "es", "سيبدأ الاجتماع صباح غد."),
                ("en", "tr", "The meeting will start tomorrow morning."),
                ("en", "fa", "The meeting will start tomorrow morning."),
                ("en", "ur", "The meeting will start tomorrow morning."),
                ("fa", "en", "جلسه فردا صبح شروع می‌شود."),
                ("ur", "en", "اجلاس کل صبح شروع ہوگا۔"),
                ("ar", "tr", "سيبدأ الاجتماع صباح غد."),
                ("fa", "ur", "جلسه فردا صبح شروع می‌شود."),
            };
            foreach (var (from, to, text) in cases)
            {
                foreach (var model in Translator.ModelsFor(from, to))
                    await ModelStore.DownloadAsync(model);
                var clock = Stopwatch.StartNew();
                var translated = Translator.Line(text, from, to);
                output.WriteLine($"{from}→{to} {clock.Elapsed.TotalSeconds:0.0}s: {translated}");
                Assert.False(string.IsNullOrWhiteSpace(translated));
                Assert.NotEqual(text, translated);
            }
        }
        finally
        {
            ModelStore.Folder = before;
        }
    }
}

/// <summary>The three providers' requests and replies, against a stand-in server: no real key or money is used.</summary>
[Collection("Models")]
public sealed class CloudAiTests : IDisposable
{
    private readonly HttpListener _server = new();
    private readonly string _base;
    private readonly List<(string Url, string Key, string Body)> _seen = [];
    private Func<HttpListenerRequest, (int Status, string Type, string Body)> _reply = _ => (200, "application/json", "{}");
    private readonly string? _settings = Environment.GetEnvironmentVariable("PETAL_SETTINGS_DIR");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "petal-cloud-" + Guid.NewGuid().ToString("N"));

    public CloudAiTests()
    {
        int port = Random.Shared.Next(41000, 49000);
        _base = $"http://127.0.0.1:{port}";
        _server.Prefixes.Add(_base + "/");
        _server.Start();
        _ = Task.Run(async () =>
        {
            while (_server.IsListening)
            {
                HttpListenerContext context;
                try { context = await _server.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var key = context.Request.Headers["x-goog-api-key"] ?? context.Request.Headers["x-api-key"] ?? context.Request.Headers["Authorization"] ?? "";
                lock (_seen)
                    _seen.Add((context.Request.Url!.PathAndQuery, key, body));
                var (status, type, text) = _reply(context.Request);
                context.Response.StatusCode = status;
                context.Response.ContentType = type;
                var bytes = Encoding.UTF8.GetBytes(text);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });
        Environment.SetEnvironmentVariable("PETAL_SETTINGS_DIR", _folder);
        CloudAi.TestServer = _base;
    }

    public void Dispose()
    {
        CloudAi.TestServer = null;
        Environment.SetEnvironmentVariable("PETAL_SETTINGS_DIR", _settings);
        _server.Stop();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    [Fact]
    public void A_key_is_kept_encrypted_and_can_be_removed()
    {
        Assert.False(CloudKeys.Has(CloudProvider.Gemini));
        CloudKeys.Set(CloudProvider.Gemini, " secret-key-123 ");
        Assert.Equal("secret-key-123", CloudKeys.Get(CloudProvider.Gemini));
        var file = Directory.GetFiles(Path.Combine(_folder, "keys")).Single();
        Assert.DoesNotContain("secret-key-123", Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        CloudKeys.Remove(CloudProvider.Gemini);
        Assert.False(CloudKeys.Has(CloudProvider.Gemini));
    }

    /// <summary>Best on a key needs no translation model here: a line the company's model missed the first time is asked of it alone.</summary>
    [Fact]
    public async Task Best_on_a_key_asks_again_for_a_line_it_missed_with_no_model_here()
    {
        CloudKeys.Set(CloudProvider.Gemini, "g-key");
        var (folder, writing) = (ModelStore.Folder, CloudAi.Writing);
        ModelStore.Folder = _folder;
        CloudAi.Writing = new CloudChoice(CloudProvider.Gemini, "gemini-3.8-flash");
        // The brief, then a batch with nothing in it, then the line alone.
        _reply = _ =>
        {
            int asked;
            lock (_seen)
                asked = _seen.Count;
            var words = asked == 3 ? "Buenos d\u00edas a todos." : asked == 1 ? "A greeting." : "";
            return (200, "text/event-stream", "data: {\"event_type\":\"step.delta\",\"delta\":{\"type\":\"text\",\"text\":\"" + words + "\"}}\n\ndata: {\"event_type\":\"interaction.completed\"}\n\n");
        };
        try
        {
            Assert.False(Translator.ReadyFor("en", "es") && !CloudAi.CanTranslate);
            var translated = await DubTranslation.TranslateAsync([new Petal.Engines.Media.DubLine(0, 2, "Good morning everyone.", "")], "en", "es");
            Assert.Equal(["Buenos d\u00edas a todos."], translated);
            Assert.Equal(3, _seen.Count);
        }
        finally
        {
            ModelStore.Folder = folder;
            CloudAi.Writing = writing;
        }
    }

    [Fact]
    public async Task Gemini_gets_its_request_and_its_words_come_as_they_are_written()
    {
        CloudKeys.Set(CloudProvider.Gemini, "g-key");
        _reply = _ => (200, "text/event-stream",
            "event: step.delta\ndata: {\"event_type\":\"step.delta\",\"delta\":{\"type\":\"text\",\"text\":\"Hola \"}}\n\n" +
            "data: {\"event_type\":\"step.delta\",\"delta\":{\"type\":\"thought_summary\",\"text\":\"no\"}}\n\n" +
            "data: {\"event_type\":\"step.delta\",\"delta\":{\"type\":\"text\",\"text\":\"mundo\"}}\n\n" +
            "data: {\"event_type\":\"interaction.completed\"}\n\n");
        var shown = new List<string>();
        var answer = await CloudAi.WriteAsync(new CloudChoice(CloudProvider.Gemini, "gemini-3.8-flash"), "Be brief.", "Hello world", shown.Add);
        Assert.Equal("Hola mundo", answer);
        Assert.Equal(["Hola ", "Hola mundo"], shown);
        var (url, key, body) = _seen.Single();
        Assert.StartsWith("/v1beta/interactions", url);
        Assert.Equal("g-key", key);
        Assert.Contains("\"system_instruction\":\"Be brief.\"", body);
        Assert.Contains("\"store\":false", body);
    }

    [Fact]
    public async Task ChatGpt_gets_its_request_and_its_words_come_as_they_are_written()
    {
        CloudKeys.Set(CloudProvider.ChatGpt, "o-key");
        _reply = _ => (200, "text/event-stream",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hola \"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"mundo\"}}]}\n\ndata: [DONE]\n\n");
        var answer = await CloudAi.WriteAsync(new CloudChoice(CloudProvider.ChatGpt, "gpt-5"), "Be brief.", "Hello world");
        Assert.Equal("Hola mundo", answer);
        var (url, key, body) = _seen.Single();
        Assert.Equal("/v1/chat/completions", url);
        Assert.Equal("Bearer o-key", key);
        Assert.Contains("\"max_completion_tokens\":", body);
        Assert.DoesNotContain("temperature", body);
    }

    [Fact]
    public async Task Claude_gets_its_request_with_no_temperature_and_its_words_come_as_they_are_written()
    {
        CloudKeys.Set(CloudProvider.Claude, "c-key");
        _reply = _ => (200, "text/event-stream",
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"x\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hola \"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"mundo\"}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        var answer = await CloudAi.WriteAsync(new CloudChoice(CloudProvider.Claude, "claude-opus-5-5"), "Be brief.", "Hello world");
        Assert.Equal("Hola mundo", answer);
        var (url, key, body) = _seen.Single();
        Assert.Equal("/v1/messages", url);
        Assert.Equal("Bearer c-key", key);
        Assert.DoesNotContain("temperature", body);
        Assert.Contains("claude-opus-5-5", body);
    }

    [Theory]
    [InlineData(CloudProvider.Gemini)]
    [InlineData(CloudProvider.ChatGpt)]
    [InlineData(CloudProvider.Claude)]
    public async Task A_refused_key_says_so_in_plain_words(CloudProvider provider)
    {
        CloudKeys.Set(provider, "bad");
        _reply = _ => (401, "application/json", "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}");
        var error = await Assert.ThrowsAsync<CloudException>(() => CloudAi.WriteAsync(new CloudChoice(provider, "m"), "i", "t"));
        Assert.Contains("API key was refused", error.Message);
    }

    [Fact]
    public async Task The_lines_come_back_as_lines_and_one_that_is_missing_is_asked_for_alone()
    {
        CloudKeys.Set(CloudProvider.ChatGpt, "o-key");
        CloudAi.Translating = new CloudChoice(CloudProvider.ChatGpt, "gpt-5");
        int asked = 0;
        _reply = _ =>
        {
            string reply = Interlocked.Increment(ref asked) == 1 ? "1. Uno\\n3. Tres" : "1. Dos";
            return (200, "text/event-stream", "data: {\"choices\":[{\"delta\":{\"content\":\"" + reply + "\"}}]}\n\ndata: [DONE]\n\n");
        };
        try
        {
            var lines = await CloudAi.TranslateAsync(["One", "Two", "", "12", "Three"], "es", null, default);
            Assert.Equal(["Uno", "Dos", "", "12", "Tres"], lines);
            Assert.Equal(2, asked);
        }
        finally
        {
            CloudAi.Translating = null;
        }
    }

    [Fact]
    public async Task The_models_on_offer_are_text_models()
    {
        CloudKeys.Set(CloudProvider.Gemini, "g");
        _reply = _ => (200, "application/json", "{\"models\":[{\"name\":\"models/gemini-3.8-flash\"},{\"name\":\"models/gemini-embedding-2\"},{\"name\":\"models/gemini-3.8-flash-tts\"},{\"name\":\"models/gemma-4\"}]}");
        Assert.Equal(["gemini-3.8-flash"], await CloudAi.ModelsAsync(CloudProvider.Gemini));
    }
}

/// <summary>Which models a translation asks for: what is here first, and one model that does for every pair.</summary>
[Collection("Models")]
public sealed class TranslationChoiceTests : IDisposable
{
    private readonly string _was = ModelStore.Folder;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "petal-route-" + Guid.NewGuid().ToString("N"));

    public TranslationChoiceTests()
    {
        Directory.CreateDirectory(_folder);
        ModelStore.Folder = _folder;
    }

    public void Dispose()
    {
        ModelStore.Folder = _was;
        Directory.Delete(_folder, true);
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

    /// <summary>The real model alone, in a folder with nothing else: PETAL_TRANSLATE names where its files are.</summary>
    [Fact]
    public async Task The_one_model_alone_really_translates_any_pair()
    {
        if (Environment.GetEnvironmentVariable("PETAL_TRANSLATE") is not { Length: > 0 } source)
            return;
        foreach (var model in TranslationCatalog.Universal)
            File.Copy(Path.Combine(source, model.FileName), Path.Combine(_folder, model.FileName));
        foreach (var (from, into, text, script) in new[]
        {
            ("en", "ar", "The meeting will start tomorrow morning.", @"\p{IsArabic}"),
            ("ar", "tr", "سيبدأ الاجتماع صباح غد.", "[a-zA-Z]"),
            ("tr", "en", "Toplantı yarın sabah başlayacak.", "[a-zA-Z]"),
            ("en", "tr", "The meeting will start tomorrow morning.", "[a-zA-Z]"),
            ("tr", "ar", "Toplantı yarın sabah başlayacak.", @"\p{IsArabic}"),
            ("fr", "es", "La réunion commencera demain matin.", "[a-zA-Z]"),
            ("id", "fa", "Rapat akan dimulai besok pagi.", @"\p{IsArabic}"),
            ("ur", "fr", "اجلاس کل صبح شروع ہوگا۔", "[a-zA-Z]"),
        })
        {
            Assert.Equal(Ids(TranslationCatalog.Universal), Ids(Translator.ModelsFor(from, into)));
            var said = await Task.Run(() => Translator.Line(text, from, into));
            if (Environment.GetEnvironmentVariable("PETAL_TRANSLATE_LOG") is { Length: > 0 } log)
                File.AppendAllLines(log, [$"{from} -> {into}: {text} => {said}"]);
            Assert.Matches(script, said);
            Assert.NotEqual(text, said);
        }
    }

    private static string[] Ids(IEnumerable<ModelInfo> models) => models.Select(m => m.Id).ToArray();

    [Fact]
    public void Every_pair_has_a_way_to_go()
    {
        foreach (var from in Translator.Languages)
            foreach (var into in Translator.Languages)
                if (from.Code != into.Code)
                    Assert.NotEmpty(Translator.ModelsFor(from.Code, into.Code));
    }

    [Fact]
    public void With_the_one_model_here_every_pair_is_ready()
    {
        Have(TranslationCatalog.Universal);
        foreach (var from in Translator.Languages)
            foreach (var into in Translator.Languages)
                Assert.True(Translator.ReadyFor(from.Code, into.Code), $"{from.Code} into {into.Code}");
    }

    [Fact]
    public void A_pairs_own_model_is_used_when_it_is_here()
    {
        Have(TranslationCatalog.Universal);
        Have(Translator.Models);
        Assert.Equal(Ids(Translator.Models), Ids(Translator.ModelsFor("en", "ar")));
    }

    [Fact]
    public void Nothing_here_asks_for_the_small_download_of_that_pair()
    {
        Assert.Equal(Ids(Translator.Models), Ids(Translator.ModelsFor("en", "ar")));
    }

    [Fact]
    public void Captions_offer_the_pair_when_the_language_is_known_and_the_one_model_when_it_is_not()
    {
        Assert.Equal(Ids(TranslationCatalog.Universal), Ids(Petal.App.Words.LiveCaptions.ModelsToOffer("auto", "ar")));
        Assert.Equal(Ids(TranslationCatalog.Universal), Ids(Petal.App.Words.LiveCaptions.ModelsToOffer("", "tr")));
        Assert.Equal(Ids(Translator.ModelsFor("tr", "en")), Ids(Petal.App.Words.LiveCaptions.ModelsToOffer("tr", "en")));
        Assert.Empty(Petal.App.Words.LiveCaptions.ModelsToOffer("tr", "tr"));
    }

    [Fact]
    public void The_interfaces_language_is_never_remembered_as_a_choice()
    {
        var own = Petal.App.Tools.Parts.LanguageChoice.Interface;
        Assert.Equal("", Petal.App.Tools.Parts.LanguageChoice.Remembered(own));
        var other = own == "tr" ? "fr" : "tr";
        Assert.Equal(other, Petal.App.Tools.Parts.LanguageChoice.Remembered(other));
    }

    [Theory]
    [InlineData("ar", "en", "en", "ar")]   // Into the interface's language.
    [InlineData("ar", "ar", "en", "en")]   // Already in it, and nobody chose: another.
    [InlineData("ar", "ar", null, "ar")]   // Chosen on purpose: said, not changed.
    [InlineData("tr", "fr", null, "tr")]
    public void A_dub_goes_where_it_should(string into, string spoken, string? otherwise, string target) =>
        Assert.Equal(target, Petal.Recipes.Dubbing.TargetFor(into, spoken, otherwise));
}
