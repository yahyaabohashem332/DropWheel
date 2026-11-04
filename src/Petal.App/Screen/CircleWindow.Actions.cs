using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Localization;
using Petal.Core.Models;
using Petal.Core.Text;
using Petal.Engines.Documents.Ocr;
using Petal.Engines.Imaging;
using Petal.Recipes;

namespace Petal.App.Screen;

internal sealed partial class CircleWindow
{
    private MediaPlayer? _player;
    private CancellationTokenSource? _speaking;

    /// <summary>Runs a petal of the circle's wheel on what was circled.</summary>
    private async Task RunAsync(string id)
    {
        if (_content is not { } c || !IsLoaded)
            return;
        _work?.Cancel();
        var work = _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        try
        {
            switch (id)
            {
                case "copy": Copy(c.Prose, "Text copied"); break;
                case "translate": await TranslateAsync(c, work.Token); break;
                case "explain": await ExplainAsync(c, work.Token); break;
                case "ask": Ask(c); break;
                case "summary": await SummarizeAsync(c, work.Token); break;
                case "search": SearchWeb(c); break;
                case "read": await ReadAloudAsync(c.Prose, work.Token); break;
                case "files": Files(c); break;
                case "table": CopyTable(c); break;
                case "excel": await SaveExcelAsync(c, work.Token); break;
                case "link": OpenFind(c, FoundKind.Link, v => v, "Link opened"); break;
                case "email": OpenFind(c, FoundKind.Email, v => "mailto:" + v, "Writing an e-mail"); break;
                case "call": Call(c); break;
                case "event": AddEvent(c); break;
                case "time": ShowTimes(c); break;
                case "convert": ShowConversions(c); break;
                case "money": await ShowMoneyAsync(c, work.Token); break;
                case "answer": CopyAnswer(c); break;
                case "colour": ShowColour(c); break;
                case "code": UseCode(c); break;
                case "describe": await DescribeAsync(c, work.Token); break;
                case "picture": CopyPicture(c); break;
                case "save": SavePicture(c); break;
                case "cutout": await CutOutAsync(c, work.Token); break;
                case "pin": Pin(c); break;
                case "colours": await ShowColoursAsync(c, work.Token); break;
                case "tools": OpenTools(c); break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, $"Circle Anything: {id}");
            if (_card is { } card)
            {
                card.Done(L.T("It didn't work. The activity log has the details."));
            }
            else if (IsLoaded)
            {
                ShowCard("alert", "Something went wrong").Done(L.T("It didn't work. The activity log has the details."));
            }
        }
    }

    // ---------- words ----------

    private void Copy(string text, string done)
    {
        if (Words.SelectedText.TrySet(text))
            Leave(done, Preview(text));
        else
            ShowCard("alert", "The clipboard is busy").Done(L.T("Another program is holding the clipboard. Try again."));
    }

    private static string Preview(string text)
    {
        var flat = string.Join(' ', text.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 60 ? flat[..60] + "…" : flat;
    }

    /// <summary>
    /// The text translated and laid over the screen where it was, paragraph by paragraph, with the whole translation in the
    /// card to copy. Arabic goes into English and the rest into Arabic (or into DropWheel's language).
    /// </summary>
    private async Task TranslateAsync(CircleContent c, CancellationToken ct)
    {
        var card = ShowCard("languages", "Translation");
        var text = c.Prose;
        if (Words.Translation.Missing(text) is { Count: > 0 } missing)
        {
            NeedsModels(card, missing, "Translating on this PC needs a translation model, downloaded once.", () => _ = RunAsync("translate"));
            return;
        }
        card.Busy(Words.Translation.Working());
        var blocks = c.Blocks;
        // A paragraph a line: the translation comes back a paragraph a line, ready to lay over each.
        var joined = string.Join("\n", blocks.Select(b => b.Text.Replace("\r", " ").Replace("\n", " ")));
        var result = await Words.Translation.RunAsync(joined, ct);
        var lines = result.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        if (lines.Count != blocks.Count)
        {
            // The AI model joined or split lines: each paragraph on its own, then.
            lines = [];
            foreach (var block in blocks)
                lines.Add((await Words.Translation.RunAsync(block.Text, ct)).Trim());
        }
        ct.ThrowIfCancellationRequested();
        CircleTranslation.Render(_translation, blocks, lines, _pixels, _width, _height);
        _translation.Visibility = Visibility.Visible;
        _translation.BeginAnimation(OpacityProperty, Visuals.Motion.To(1, 200, Visuals.Motion.Standard, from: 0));
        card.Text = string.Join(Environment.NewLine + Environment.NewLine, lines.Where(l => l.Length > 0));
        WaysChip(card, Petal.Recipes.Translation.Translator.ModelsFor(Words.Translation.From(text), Words.Translation.Into(text)), () => _ = RunAsync("translate"));
        card.Done(Petal.Recipes.Cloud.CloudAi.CanTranslate ? L.F("Translated by {0}, on your own key.", Petal.Recipes.Cloud.CloudAi.Name(Petal.Recipes.Cloud.CloudAi.Translating!.Provider)) : L.T("Translated on this PC."));
        card.CopyChip("Copy", () => card.Text);
        Border? original = null;
        original = card.Chip("eye", "Original", () =>
        {
            bool showing = _translation.Visibility == Visibility.Visible;
            _translation.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            CircleCard.Relabel(original!, showing ? "Translation" : "Original");
        });
        card.Chip("speech", "Read Aloud", () => _ = ReadAloudAsync(card.Text, _work?.Token ?? CancellationToken.None));
    }

    private async Task ExplainAsync(CircleContent c, CancellationToken ct)
    {
        if (c.Kind == CircleKind.Picture)
        {
            await DescribeAsync(c, ct);
            return;
        }
        var card = ShowCard("sparkles", "Explanation");
        if (!AskPetal.ModelReady)
        {
            NeedsModels(card, AskPetal.Models, "Explaining on this PC needs the AI model, downloaded once.", () => _ = RunAsync("explain"));
            return;
        }
        card.Busy(Petal.App.Settings.AiSetup.Writing("Thinking on this PC…"));
        var answer = await AskPetal.WriteAsync(AskPetal.Instructions.Explain(L.EnglishName), c.Prose, s => Dispatcher.BeginInvoke(() => { if (!ct.IsCancellationRequested) card.Text = s; }), 420, ct, 0.3);
        card.Text = answer;
        WaysChip(card, AskPetal.Models, () => _ = RunAsync("explain"));
        card.Done(answer.Length == 0 ? L.T("Nothing came back. Try again.") : Petal.App.Settings.AiSetup.WorkedOut(Petal.Recipes.Cloud.CloudAi.CanWrite ? Petal.Recipes.Cloud.CloudAi.Writing : null));
        card.CopyChip("Copy", () => card.Text);
        card.Chip("ask", "Ask More", () => FollowUp(card, c, ct));
    }

    /// <summary>A question box in the card; each answer comes under it, and the next question can follow on.</summary>
    private void Ask(CircleContent c)
    {
        var card = ShowCard("ask", "Ask About It");
        FollowUp(card, c, CancellationToken.None);
    }

    private void FollowUp(CircleCard card, CircleContent c, CancellationToken _)
    {
        var history = new List<(string Question, string Answer)>();
        card.ClearChips();
        card.AskBox(c.Kind == CircleKind.Picture ? "What do you want to know about the picture?" : "What do you want to know about it?");
        card.Asked += async question =>
        {
            _work?.Cancel();
            var work = _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            try
            {
                bool picture = c.Kind == CircleKind.Picture || (c.Kind == CircleKind.Mixed && c.WordCount < 5);
                var models = picture ? PictureDescriber.Models : AskPetal.Models;
                if (!(picture ? PictureDescriber.ModelReady : AskPetal.ModelReady))
                {
                    NeedsModels(card, models, "Answering on this PC needs the AI model, downloaded once.", () => card.Done(L.T("Ready. Ask your question again.")));
                    return;
                }
                card.Text = question;
                card.Busy(picture ? Petal.App.Settings.AiSetup.Looking : Petal.App.Settings.AiSetup.Writing("Thinking on this PC…"));
                string answer;
                if (picture)
                    answer = await AskPictureAsync(c, question, work.Token);
                else
                {
                    answer = await AskPetal.WriteAsync(AskPetal.Instructions.Answer(Petal.Recipes.Translation.Translator.EnglishName(Petal.Core.Text.LanguageGuess.Of(question) ?? L.Code)), AskPetal.ScreenQuestion(c.Prose, history, question),
                        s => Dispatcher.BeginInvoke(() => { if (!work.IsCancellationRequested) card.Text = question + Environment.NewLine + Environment.NewLine + s; }), 450, work.Token, 0.3);
                }
                history.Add((question, answer));
                card.Text = question + Environment.NewLine + Environment.NewLine + answer;
                WaysChip(card, models, () => card.Done(L.T("Ready. Ask your question again.")));
                card.Done(Petal.App.Settings.AiSetup.WorkedOut(picture ? (Petal.Recipes.Cloud.CloudAi.CanSee ? Petal.Recipes.Cloud.CloudAi.Seeing : null) : Petal.Recipes.Cloud.CloudAi.CanWrite ? Petal.Recipes.Cloud.CloudAi.Writing : null));
                card.ClearChips();
                card.CopyChip("Copy Answer", () => answer);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                Log.Error(ex, "Circle Anything: asking");
                card.Done(L.T("It didn't work. The activity log has the details."));
            }
        };
    }

    /// <summary>A question about a picture: the picture model reads English, so the question goes into English and the answer comes back.</summary>
    private async Task<string> AskPictureAsync(CircleContent c, string question, CancellationToken ct)
    {
        var english = question;
        if (Petal.Core.Text.LanguageGuess.Of(question) is { } asked && asked != "en" && Petal.Recipes.Translation.Translator.ReadyFor(asked, "en"))
            english = (await Petal.Recipes.Translation.Translator.TranslateAsync([question], asked, "en", null, ct))[0];
        var path = Temporary(c.Picture);
        try
        {
            var answer = await PictureDescriber.AskAsync(path, english, ct);
            return await InReadersLanguageAsync(answer, ct);
        }
        finally
        {
            Forget(path);
        }
    }

    private async Task SummarizeAsync(CircleContent c, CancellationToken ct)
    {
        var card = ShowCard("summary", "Summary");
        if (!AskPetal.ModelReady)
        {
            NeedsModels(card, AskPetal.Models, "Summarizing on this PC needs the AI model, downloaded once.", () => _ = RunAsync("summary"));
            return;
        }
        card.Busy(Petal.App.Settings.AiSetup.Writing("Summarizing on this PC…"));
        var text = c.Prose;
        var answer = await AskPetal.WriteAsync(AskPetal.Instructions.Summary(true, KeyboardLayout.LooksArabicLanguage(text)), text, s => Dispatcher.BeginInvoke(() => { if (!ct.IsCancellationRequested) card.Text = s; }), 500, ct);
        card.Text = answer;
        WaysChip(card, AskPetal.Models, () => _ = RunAsync("summary"));
        card.Done(answer.Length == 0 ? L.T("Nothing came back. Try again.") : "");
        card.CopyChip("Copy", () => card.Text);
    }

    private void SearchWeb(CircleContent c)
    {
        var query = Preview(c.Prose).TrimEnd('…');
        if (query.Length > 300)
            query = query[..300];
        Open("https://www.bing.com/search?q=" + Uri.EscapeDataString(query));
        Leave("Searching the web", query);
    }

    private void Files(CircleContent c)
    {
        var query = Preview(c.Prose).TrimEnd('…');
        Close();
        Search.SearchWindow.ShowSingle(query);
    }

    private void CopyTable(CircleContent c)
    {
        if (c.Table is not { } table)
            return;
        if (Words.SelectedText.TrySet(PictureTables.ToTabs(table)))
            Leave("Table copied", L.P(table.Rows.Count, "{0} row", "{0} rows") + " · " + L.P(table.Columns, "{0} column", "{0} columns"));
    }

    private async Task SaveExcelAsync(CircleContent c, CancellationToken ct)
    {
        if (c.Table is not { } table)
            return;
        var folder = Settings.SavedPlaces.Tables;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var output = Petal.Core.Jobs.OutputNaming.Unique(folder, L.T("Table") + " " + stamp, "xlsx");
        await Task.Run(() => PictureTables.SaveXlsx(table, output, L.T("Table")), ct);
        Close();
        Hud.HudCard.ShowSaved(L.T("Table saved"), output);
        Open(output);
    }

    // ---------- what the text holds ----------

    private void OpenFind(CircleContent c, FoundKind kind, Func<string, string> address, string done)
    {
        if (c.Finds.FirstOrDefault(f => f.Kind == kind) is not { } found)
            return;
        Open(address(found.Value));
        Leave(done, found.Text);
    }

    private void Call(CircleContent c)
    {
        if (c.Finds.FirstOrDefault(f => f.Kind == FoundKind.Phone) is not { } phone)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("tel:" + phone.Value) { UseShellExecute = true });
            Leave("Calling", phone.Text);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No app takes calls on this PC: the number, ready to paste.
            Words.SelectedText.TrySet(phone.Value);
            Leave("Number copied", L.T("No app on this PC makes calls."));
        }
    }

    /// <summary>The date (and time) as an event file the calendar opens to add it, named after the line it was on.</summary>
    private void AddEvent(CircleContent c)
    {
        if (c.Finds.FirstOrDefault(f => f.Kind == FoundKind.When && f.HasDate) is not { When: { } when } found)
            return;
        var prose = c.Prose;
        var title = prose.Replace(found.Text, "").Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim(' ', ',', '،', '-', '·', ':')).FirstOrDefault(l => l.Length > 2) ?? L.T("Event");
        if (title.Length > 80)
            title = title[..80] + "…";
        var ics = CalendarFile.Event(title, when, found.Utc, !found.HasTime, found.HasTime ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1),
            prose.Length > 600 ? prose[..600] + "…" : prose, DateTime.Now);
        var folder = Path.Combine(Path.GetTempPath(), "Petal", "events");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"event-{Guid.NewGuid():N}.ics");
        File.WriteAllText(file, ics, new System.Text.UTF8Encoding(false));
        Open(file);
        Leave("Adding to your calendar", CircleChoices.Moment(found.Utc?.ToLocalTime() ?? when, found.HasTime));
    }

    private void ShowTimes(CircleContent c)
    {
        var card = ShowCard("history", "In Your Own Time");
        foreach (var f in c.Finds.Where(f => f.Kind == FoundKind.When && f.Utc is not null))
        {
            var local = f.Utc!.Value.ToLocalTime();
            card.Row(CircleChoices.Moment(local, true), L.F("{0} where it was written", f.Text), local.ToString("g", L.Culture));
        }
        card.Done(L.F("Your time zone: {0}", TimeZoneInfo.Local.DisplayName));
    }

    private void ShowConversions(CircleContent c)
    {
        var card = ShowCard("ruler", "Conversions");
        foreach (var f in c.Finds.Where(f => f.Kind == FoundKind.Measure))
            foreach (var answer in Calculator.ForReader(f.Amount, f.Unit))
                card.Row(answer.Text, f.Text, answer.Text);
        card.Done();
    }

    /// <summary>Amounts in the reader's currency and the dollar, at today's rates (fetched now unless fetched today).</summary>
    private async Task ShowMoneyAsync(CircleContent c, CancellationToken ct)
    {
        var card = ShowCard("coins", "Currency");
        card.Busy("Getting today's exchange rates…");
        ExchangeRates rates;
        try
        {
            rates = await ExchangeRateService.GetAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            card.Done(L.T(ex.Message));
            card.Chip("refresh", "Try Again", () => _ = RunAsync("money"));
            return;
        }
        var home = Money.HomeCurrency;
        foreach (var f in c.Finds.Where(f => f.Kind == FoundKind.Money))
        {
            var targets = new[] { CircleChoices.Target(f.Unit, home), "USD", "EUR" }.OfType<string>().Where(t => t != f.Unit).Distinct().Take(2);
            foreach (var to in targets)
                if (rates.Convert(f.Amount, f.Unit, to) is { } value)
                    card.Row("≈ " + Money.Format(value, to), Money.Format(f.Amount, f.Unit), Money.Format(value, to));
        }
        card.Done(ExchangeRateService.Credit + " · " + rates.Updated.ToLocalTime().ToString("d MMM, HH:mm", L.Culture));
        card.Chip("externalLink", "About the Rates", () => Open(ExchangeRateService.CreditLink));
    }

    private void CopyAnswer(CircleContent c)
    {
        if (c.Finds.FirstOrDefault(f => f.Kind == FoundKind.Sum) is { } sum)
            Copy(sum.Value, "Answer copied");
    }

    private void ShowColour(CircleContent c)
    {
        var card = ShowCard("dropper", "Colour");
        foreach (var f in c.Finds.Where(f => f.Kind == FoundKind.Colour).Take(4))
        {
            var colour = (Color)ColorConverter.ConvertFromString(f.Value);
            card.Swatch(colour, f.Value);
            var (h, s, l) = Hsl(colour);
            card.Row(f.Value, $"rgb({colour.R}, {colour.G}, {colour.B}) · hsl({h}, {s}%, {l}%)");
        }
        card.Done(L.T("Click a colour to copy it."));
    }

    private static (int H, int S, int L) Hsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return ((int)Math.Round((h + 360) % 360), (int)Math.Round(s * 100), (int)Math.Round(l * 100));
    }

    private void UseCode(CircleContent c)
    {
        if (c.Codes.Count == 0)
            return;
        var text = c.Codes[0].Text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            Open(text);
            Leave("Link opened", text);
        }
        else
            Copy(text, "Code copied");
    }

    // ---------- the picture ----------

    private async Task DescribeAsync(CircleContent c, CancellationToken ct)
    {
        var card = ShowCard("eye", "Description");
        if (!PictureDescriber.ModelReady)
        {
            NeedsModels(card, PictureDescriber.Models, "Describing pictures on this PC needs the picture model, downloaded once.", () => _ = RunAsync("describe"));
            return;
        }
        card.Busy(Petal.App.Settings.AiSetup.Looking);
        var path = Temporary(c.Picture);
        try
        {
            var english = await PictureDescriber.DescribeAsync(path, detailed: true, ct);
            card.Text = await InReadersLanguageAsync(english, ct);
        }
        finally
        {
            Forget(path);
        }
        WaysChip(card, PictureDescriber.Models, () => _ = RunAsync("describe"));
        card.Done(Petal.App.Settings.AiSetup.WorkedOut(Petal.Recipes.Cloud.CloudAi.CanSee ? Petal.Recipes.Cloud.CloudAi.Seeing : null));
        card.CopyChip("Copy", () => card.Text);
        card.Chip("ask", "Ask More", () => FollowUp(card, c, ct));
    }

    /// <summary>English from the picture model, in DropWheel's language when that isn't English and a translation is at hand.</summary>
    private static async Task<string> InReadersLanguageAsync(string english, CancellationToken ct)
    {
        if (L.Code == "en" || english.Length == 0 || Words.Translation.Missing(english).Count > 0)
            return english;
        try
        {
            return await Words.Translation.RunAsync(english, ct);
        }
        catch (InvalidOperationException)
        {
            return english;
        }
    }

    private void CopyPicture(CircleContent c)
    {
        if (Wheels.Screenshots.Copy(c.Picture))
            Leave("Picture copied", $"{c.Area.Width} × {c.Area.Height}");
        else
            ShowCard("alert", "The clipboard is busy").Done(L.T("Another program is holding the clipboard. Try again."));
    }

    private void SavePicture(CircleContent c)
    {
        Close();
        Wheels.Screenshots.Keep(c.Picture);
    }

    /// <summary>The subject cut out of its background, ready to paste with its see-through edges, or to keep.</summary>
    private async Task CutOutAsync(CircleContent c, CancellationToken ct)
    {
        var card = ShowCard("scissors", "Cut Out");
        if (!BackgroundRemover.IsReady)
        {
            NeedsModels(card, [ModelCatalog.Background], "Cutting out on this PC needs the cut-out model, downloaded once.", () => _ = RunAsync("cutout"));
            return;
        }
        card.Busy("Cutting out the subject on this PC…");
        var input = Temporary(c.Picture);
        var output = Path.Combine(Path.GetDirectoryName(input)!, $"cutout-{Guid.NewGuid():N}.png");
        try
        {
            await BackgroundRemover.RemoveAsync(input, output, cancellationToken: ct);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(output);
            image.EndInit();
            image.Freeze();
            var copied = Wheels.Screenshots.Copy(image);
            Close();
            var saved = Wheels.Screenshots.Save(image);
            Hud.HudCard.ShowSaved(L.T(copied ? "Cut out, saved and copied" : "Cut out and saved"), saved);
        }
        finally
        {
            Forget(input);
            Forget(output);
        }
    }

    private void Pin(CircleContent c)
    {
        var area = new Interop.NativeMethods.RECT { Left = _bounds.Left + c.Area.X, Top = _bounds.Top + c.Area.Y, Right = _bounds.Left + c.Area.X + c.Area.Width, Bottom = _bounds.Top + c.Area.Y + c.Area.Height };
        Close();
        PinnedShot.Pin(c.Picture, area, (path, at) => App.Current.Controller.OpenWheelNow([path], at));
    }

    private async Task ShowColoursAsync(CircleContent c, CancellationToken ct)
    {
        var card = ShowCard("palette", "Colours");
        card.Busy("Finding the main colours…");
        var path = Temporary(c.Picture);
        try
        {
            var swatches = await Task.Run(() => Swatches.Extract(path, 8), ct);
            foreach (var s in swatches)
                card.Swatch(Color.FromRgb(s.Rgb.R, s.Rgb.G, s.Rgb.B), s.Hex);
            card.Done(L.T("Click a colour to copy it."));
            card.CopyChip("Copy All", () => string.Join(Environment.NewLine, swatches.Select(s => s.Hex)));
        }
        finally
        {
            Forget(path);
        }
    }

    /// <summary>The picture saved as a screenshot, and DropWheel's wheel opened on it: every conversion and tool.</summary>
    private void OpenTools(CircleContent c)
    {
        var at = Middle;
        Close();
        var path = Wheels.Screenshots.Keep(c.Picture);
        App.Current.Controller.OpenWheelNow([path], at);
    }

    // ---------- reading aloud ----------

    /// <summary>Reads the text aloud on this PC, a piece at a time, with the best voice there is for its language.</summary>
    private async Task ReadAloudAsync(string text, CancellationToken ct)
    {
        StopSpeaking();
        var card = _card ?? ShowCard("speech", "Read Aloud");
        var voices = Petal.Engines.Media.Speech.AllVoices();
        var voice = Petal.Engines.Media.Speech.Choose(Petal.Engines.Media.Speech.GuessLanguage(text), voices);
        if (voice is null)
        {
            card.Done(L.T("No voice on this PC speaks this language. Add one in Windows Settings › Time & language › Speech."));
            return;
        }
        if (!voice.IsReady)
        {
            NeedsModels(card, voice.Models, "Reading aloud in this language needs a natural voice, downloaded once.", () => _ = ReadAloudAsync(text, ct));
            return;
        }
        var speaking = _speaking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Border? stop = null;
        stop = card.Chip("pause", "Stop", () => { StopSpeaking(); card.Done(); });
        card.Busy("Reading aloud…");
        try
        {
            foreach (var piece in Petal.Engines.Media.Speech.Chunks(text, 400))
            {
                speaking.Token.ThrowIfCancellationRequested();
                var wav = Path.Combine(Path.GetTempPath(), "Petal", $"circle-{Guid.NewGuid():N}.wav");
                Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
                await Task.Run(() => Petal.Engines.Media.Speech.SpeakToFileAsync(piece, voice, 1.0, wav, null, null, speaking.Token), speaking.Token);
                await PlayAsync(wav, speaking.Token);
                Forget(wav);
            }
            card.Done();
        }
        finally
        {
            if (stop is not null)
                stop.Visibility = Visibility.Collapsed;
        }
    }

    private async Task PlayAsync(string wav, CancellationToken ct)
    {
        var player = _player ??= new MediaPlayer();
        var done = new TaskCompletionSource();
        void Ended(object? s, EventArgs e) => done.TrySetResult();
        player.MediaEnded += Ended;
        player.MediaFailed += Ended;
        try
        {
            player.Open(new Uri(wav));
            player.Play();
            using (ct.Register(() => done.TrySetCanceled()))
                await done.Task;
        }
        finally
        {
            player.MediaEnded -= Ended;
            player.MediaFailed -= Ended;
            player.Stop();
            player.Close();
        }
    }

    private void StopSpeaking()
    {
        _speaking?.Cancel();
        _speaking = null;
        _player?.Stop();
    }

    // ---------- helpers ----------

    /// <summary>The card says what is missing, and a chip downloads it over the still; once it is here, <paramref name="ready"/> runs.</summary>
    /// <summary>Under a result: the other way to do this job, the model on this PC or the user's own key. Moved, the job runs again.</summary>
    private void WaysChip(CircleCard card, IReadOnlyList<ModelInfo> models, Action again) =>
        card.Chip("key", "This PC or a Key", () =>
            Words.ModelDownloadWindow.Show(L.T("Circle Anything"), L.T("Choose what does this job: a model on this PC, or your own API key."), models, this, () => Dispatcher.BeginInvoke(again)));

    private void NeedsModels(CircleCard card, IReadOnlyList<ModelInfo> models, string why, Action ready)
    {
        var missing = models.Where(m => !ModelStore.IsAvailable(m)).ToList();
        long bytes = missing.Sum(m => m.Size);
        card.Text = L.T(why);
        card.Done(L.F("{0} to download", Util.Sizes.Format(bytes)));
        card.Chip("download", "Download", () =>
            Words.ModelDownloadWindow.Show(L.T("Circle Anything"), L.T(why), missing, this, () => Dispatcher.BeginInvoke(ready)));
        if (Petal.App.Settings.AiSetup.JobOf(missing) is not null)
            card.Chip("key", "Use My API Key", () =>
                Words.ModelDownloadWindow.Show(L.T("Circle Anything"), L.T(why), missing, this, () => Dispatcher.BeginInvoke(ready), ownKey: true));
    }

    private static string Temporary(BitmapSource picture)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Petal", "circle");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"picture-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        return path;
    }

    private static void Forget(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Open(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Info($"Circle Anything: opening {address}: {ex.Message}"); }
    }
}
