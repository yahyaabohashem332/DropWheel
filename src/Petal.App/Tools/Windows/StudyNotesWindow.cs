using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Models;
using Petal.Core.Tools;
using Petal.Engines.Documents;
using Petal.Recipes.Cloud;
using Petal.Recipes.Questions;
using Petal.Recipes.Study;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Study notes from a lecture or a document: a summary to read, cards to turn over and a quiz to take, all from the file's
/// own words and saying where in the file each comes from; saved as notes and as a deck for Anki.
/// </summary>
public sealed class StudyNotesWindow : ToolWindow
{
    private const string SameLanguage = "same";
    private static readonly ModelInfo[] Speech = [ModelCatalog.WhisperFast, ModelCatalog.WhisperBalanced, ModelCatalog.WhisperAccurate];

    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly bool _recording;
    private readonly LanguageChoice _into = new(SameLanguage, [(SameLanguage, "The file's language")]);
    private readonly Segmented<StudyAmount> _amount;
    private readonly ModelPanel _writer = new(StudyMaker.Models);
    private readonly ModelPanel? _hearing;
    private readonly TextBlock _where = Text("", "Petal.Caption");
    private readonly TextBlock _status = Text("", "Petal.Caption");
    private readonly StackPanel _setup = new();
    private readonly StackPanel _results = new() { Visibility = Visibility.Collapsed };
    private readonly Segmented<int> _view;
    private readonly StackPanel _shown = new();
    private readonly CancellationTokenSource _closing = new();
    private StudyPack? _pack;
    private bool _busy;

    public StudyNotesWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at, StudyPack? ready = null) : base(tool.Name, 540)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        _recording = Formats.FromPath(file) is { } format && AskFile.IsRecording(format);
        _amount = Segments([(StudyAmount.Fewer, "Fewer"), (StudyAmount.Normal, "Normal"), (StudyAmount.More, "More")], StudyAmount.Normal);
        _view = Segments([(0, "Summary"), (1, "Cards"), (2, "Quiz")], 0);

        Body.Children.Add(Text(Path.GetFileName(file), "Petal.Section"));
        _setup.Children.Add(Text("A summary, cards and a quiz, from what the file itself says. Each says where in the file it comes from.", "Petal.Caption").WithMarginT(6));
        _setup.Children.Add(Row("Write in", _into, 100, 12));
        _setup.Children.Add(Row("How many", _amount, 100, 8));
        _where.Margin = new Thickness(0, 10, 0, 0);
        _where.TextWrapping = TextWrapping.Wrap;
        _setup.Children.Add(_where);
        if (_recording)
        {
            var whisper = Speech[Array.FindLastIndex(Speech, ModelStore.IsAvailable) is >= 0 and var best ? best : 1];
            _hearing = new ModelPanel(whisper);
            _hearing.Ready += Sync;
            _setup.Children.Add(_hearing);
        }
        _setup.Children.Add(_writer);
        Body.Children.Add(_setup);
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_status);
        _results.Children.Add(_view);
        _results.Children.Add(new ScrollViewer { Content = _shown, MaxHeight = 420, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Body.Children.Add(_results);

        _view.Changed += _ => Show();
        _writer.Ready += Sync;
        _writer.Switched += Sync;
        PrimaryButton.Content = L.T("Make Notes");
        PrimaryButton.Click += async (_, _) =>
        {
            if (_pack is null)
                await MakeAsync();
            else
                Save();
        };
        Closed += (_, _) => _closing.Cancel();
        if (ready is not null)
        {
            _pack = ready;
            Finish();
        }
        Sync();
    }

    private void Sync()
    {
        _where.Text = CloudAi.CanWrite ? L.F("The text goes to {0}, on your own API key.", CloudAi.Name(CloudAi.Writing!.Provider))
            : L.T("It runs on this PC: the file doesn't leave it.");
        PrimaryButton.IsEnabled = !_busy && (_pack is not null || (_writer.IsReady && _hearing?.IsReady != false));
        FooterNote = Path.GetFileName(NotesFor(_file, _tool));
    }

    internal static string NotesFor(string file, ToolInfo tool) => OutputNaming.ForTool(file, tool, extensionOverride: "md");

    internal static string DeckFor(string notes) => Path.ChangeExtension(notes, "apkg");

    private static string PlaceName(PlacedText first, PlacedText last) => first.Kind switch
    {
        PlaceKind.Page when first.Number == last.Number => L.F("Page {0}", first.Number),
        PlaceKind.Page => L.F("Pages {0}–{1}", first.Number, last.Number),
        PlaceKind.Slide when first.Number == last.Number => L.F("Slide {0}", first.Number),
        PlaceKind.Slide => L.F("Slides {0}–{1}", first.Number, last.Number),
        PlaceKind.Time => first.Start == last.Start ? AskFile.Clock(first.Start) : AskFile.Clock(first.Start) + "–" + AskFile.Clock(last.End),
        _ => first.Name is { Length: > 0 } name ? name : "",
    };

    private async Task MakeAsync()
    {
        _busy = true;
        Sync();
        try
        {
            string? whisper = _recording ? ModelStore.Find(Speech[Array.FindLastIndex(Speech, ModelStore.IsAvailable)]) : null;
            _status.Text = L.T(_recording ? "Listening…" : "Reading the file…");
            var reading = new Progress<double>(f => _status.Text = L.T(_recording ? "Listening…" : "Reading the file…") + " " + (int)(Math.Clamp(f, 0, 1) * 100) + "%");
            var places = await AskFile.WordsAsync(_file, whisper, reading, _closing.Token);
            var into = _into.Value == SameLanguage ? StudyMaker.LanguageOf(places) : _into.Value;
            var writing = new Progress<double>(f => _status.Text = L.T("Writing the notes…") + " " + (int)(Math.Clamp(f, 0, 1) * 100) + "%");
            _status.Text = L.T("Writing the notes…");
            _pack = await Task.Run(() => StudyMaker.MakeAsync(places, into, _amount.Value, PlaceName, writing, _closing.Token), _closing.Token);
            Finish();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Making study notes");
            _status.Text = L.T("That didn't work.") + " " + L.T(ex.Message);
        }
        finally
        {
            _busy = false;
            Sync();
        }
    }

    private void Finish()
    {
        _setup.Visibility = Visibility.Collapsed;
        _results.Visibility = Visibility.Visible;
        PrimaryButton.Content = L.T("Save");
        var pack = _pack!;
        _status.Text = L.P(pack.Cards.Count, "{0} card", "{0} cards") + " · " + L.P(pack.Quiz.Count, "{0} question", "{0} questions");
        Show();
    }

    private void Show()
    {
        _shown.Children.Clear();
        if (_pack is not { } pack)
            return;
        switch (_view.Value)
        {
            case 0:
                _shown.Children.Add(Words(pack.Summary));
                break;
            case 1:
                foreach (var card in pack.Cards)
                    _shown.Children.Add(CardRow(card));
                break;
            default:
                foreach (var question in pack.Quiz)
                    _shown.Children.Add(QuestionRow(question));
                break;
        }
    }

    private static TextBlock Words(string text)
    {
        var words = Text(text);
        words.TextWrapping = TextWrapping.Wrap;
        words.FlowDirection = LooksRightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return words;
    }

    private static bool LooksRightToLeft(string text) =>
        text.Count(c => c is >= (char)0x0590 and <= (char)0x08FF) * 2 > text.Count(char.IsLetter);

    /// <summary>A card: pressed, it turns over to show its answer.</summary>
    private static UIElement CardRow(StudyCard card)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var front = Words(card.Front);
        front.FontWeight = FontWeights.SemiBold;
        var back = Words(card.Back);
        back.Margin = new Thickness(0, 4, 0, 0);
        var where = Text(card.Where, "Petal.Caption");
        var hidden = new StackPanel { Visibility = Visibility.Collapsed, Children = { back, where } };
        var turn = new Button { Content = L.T("Show the answer"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        turn.Click += (_, _) =>
        {
            hidden.Visibility = Visibility.Visible;
            turn.Visibility = Visibility.Collapsed;
        };
        panel.Children.Add(front);
        panel.Children.Add(hidden);
        panel.Children.Add(turn);
        return panel;
    }

    /// <summary>A question: a choice pressed says whether it was right, and why.</summary>
    private static UIElement QuestionRow(StudyQuestion question)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var asked = Words(question.Question);
        asked.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(asked);
        var said = Text("", "Petal.Caption");
        said.TextWrapping = TextWrapping.Wrap;
        said.Margin = new Thickness(0, 6, 0, 0);
        var choices = new List<Button>();
        for (int i = 0; i < question.Options.Count; i++)
        {
            int choice = i;
            var words = Words(question.Options[i]);
            var button = new Button { Content = words, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
            button.Click += (_, _) =>
            {
                foreach (var b in choices)
                    b.IsEnabled = false;
                var verdict = choice == question.Correct ? L.T("Right.") : L.F("Not quite: the answer is {0}", question.Options[question.Correct]);
                said.Text = string.Join(" ", new[] { verdict, question.Why, question.Where.Length > 0 ? "(" + question.Where + ")" : "" }.Where(s => s.Length > 0));
            };
            choices.Add(button);
            panel.Children.Add(button);
        }
        panel.Children.Add(said);
        return panel;
    }

    private void Save()
    {
        if (_pack is not { } pack)
            return;
        var notes = NotesFor(_file, _tool);
        var deck = DeckFor(notes);
        var title = Path.GetFileNameWithoutExtension(_file);
        var markdown = StudyMaker.Markdown(title, pack, L.T("Cards"), L.T("Quiz"), L.T("Answer:"));
        Close();
        _ = _jobs.RunAsync("Saving", "Study notes saved", [new WorkItem(_file, Path.GetFileName(_file), async (_, ct) =>
        {
            await File.WriteAllTextAsync(notes, markdown, new UTF8Encoding(false), ct);
            await Task.Run(() => AnkiDeck.Write(deck, title, pack.Cards, pack.Quiz), ct);
            return (IReadOnlyList<string>)[notes, deck];
        })], _at);
    }
}
