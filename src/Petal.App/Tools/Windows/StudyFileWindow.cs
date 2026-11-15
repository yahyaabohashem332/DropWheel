using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
/// An interactive study file from a book, course material, lecture notes or a recorded lecture: one page that opens in any
/// browser, offline, to be sent to students. The file is read with its own chapters (the parts can be chosen: a whole
/// book takes a while on this PC), then explained part by part; what is made is kept, so making it again carries on.
/// </summary>
public sealed class StudyFileWindow : ToolWindow
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
    private readonly ModelPanel _writer = new(StudyFile.Models);
    private readonly ModelPanel? _hearing;
    private readonly TextBlock _where = Text("", "Petal.Caption");
    private readonly TextBlock _partsNote = Text("", "Petal.Caption");
    private readonly TextBlock _count = Text("", "Petal.Caption");
    private readonly StackPanel _list = new();
    private readonly StackPanel _partsPanel = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _setup = new();
    private readonly TextBlock _status = Text("", "Petal.Caption");
    private readonly TextBlock _keep = Text("", "Petal.Caption");
    private readonly CancellationTokenSource _closing = new();
    private readonly Dictionary<int, CheckBox> _boxes = [];
    private DocumentReading? _reading;
    private StudyPlan? _plan;
    private bool _busy;
    private bool _syncing;

    public StudyFileWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : this(tool, file, jobs, at, null, null)
    {
    }

    /// <param name="reading">For snapshots: the file as already read.</param>
    /// <param name="making">For snapshots: the window as it is while the file is being explained.</param>
    internal StudyFileWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at, DocumentReading? reading, StudyProgress? making) : base(tool.Name, 560)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        _recording = Formats.FromPath(file) is { } format && AskFile.IsRecording(format);
        _amount = Segments([(StudyAmount.Fewer, "Fewer"), (StudyAmount.Normal, "Normal"), (StudyAmount.More, "More")], StudyAmount.Normal);

        Body.Children.Add(Text(Path.GetFileName(file), "Petal.Section"));
        var about = Text("One page that opens in any browser, on a computer or a phone, with no internet: the file explained part by part, with its key ideas, worked examples, terms, cards and questions, each saying where in the file it comes from. Send it to students as it is.", "Petal.Caption");
        about.TextWrapping = TextWrapping.Wrap;
        about.Margin = new Thickness(0, 6, 0, 0);
        _setup.Children.Add(about);
        _setup.Children.Add(Row("Write in", _into, 120, 12));
        _setup.Children.Add(Row("Cards and questions", _amount, 120, 8));
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
        else
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var subtle = (Style)Application.Current.FindResource("Petal.SubtleButton");
            var all = new Button { Content = L.T("All"), Style = subtle, Margin = new Thickness(6, 0, 0, 0) };
            var none = new Button { Content = L.T("None"), Style = subtle, Margin = new Thickness(6, 0, 0, 0) };
            all.Click += (_, _) => Choose(true);
            none.Click += (_, _) => Choose(false);
            DockPanel.SetDock(none, Dock.Right);
            DockPanel.SetDock(all, Dock.Right);
            head.Children.Add(none);
            head.Children.Add(all);
            var heading = Text("Parts of the file", "Petal.Label");
            heading.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(heading);
            _partsPanel.Children.Add(head);
            _partsNote.TextWrapping = TextWrapping.Wrap;
            _partsPanel.Children.Add(_partsNote);
            _partsPanel.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 240, Margin = new Thickness(0, 6, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            _count.Margin = new Thickness(0, 6, 0, 0);
            _count.TextWrapping = TextWrapping.Wrap;
            _partsPanel.Children.Add(_count);
            _setup.Children.Add(_partsPanel);
        }
        _setup.Children.Add(_writer);
        Body.Children.Add(_setup);
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_status);
        _keep.Margin = new Thickness(0, 4, 0, 0);
        _keep.TextWrapping = TextWrapping.Wrap;
        _keep.Visibility = Visibility.Collapsed;
        Body.Children.Add(_keep);

        _writer.Ready += Sync;
        _writer.Switched += () =>
        {
            // A model in the cloud reads more at once: the passages are cut again.
            Replan();
            Sync();
        };
        _into.Changed += _ => Sync();
        _amount.Changed += _ => Sync();
        PrimaryButton.Content = L.T("Make Study File");
        PrimaryButton.Click += async (_, _) => await MakeAsync();
        Closed += (_, _) => _closing.Cancel();

        if (making is { } shown)
        {
            _reading = reading;
            Replan();
            _busy = true;
            Making(shown);
        }
        else if (reading is not null)
        {
            _reading = reading;
            Replan();
        }
        else if (!_recording)
            _ = ReadAsync();
        Sync();
    }

    internal static string StudyFileFor(string file, ToolInfo tool) => OutputNaming.ForTool(file, tool);

    private string Into => _into.Value == SameLanguage ? _plan?.Language ?? "en" : _into.Value;

    private StudyFile.Options Options => new(Into, _amount.Value, _plan?.Language ?? Into);

    private HashSet<int> Chosen => _plan is null ? [] : _plan.Parts.Where(p => !_boxes.TryGetValue(p.Number, out var box) || box.IsChecked == true).Select(p => p.Number).ToHashSet();

    private void Sync()
    {
        if (_syncing)
            return;
        _syncing = true;
        try
        {
            _where.Text = CloudAi.CanWrite ? L.F("The text goes to {0}, on your own API key.", CloudAi.Name(CloudAi.Writing!.Provider))
                : L.T("It runs on this PC: the file doesn't leave it.");
            if (_plan is { } plan && !_recording)
            {
                var chosen = Chosen;
                int passages = StudyFile.Passages(plan, chosen);
                int made = passages > 0 ? StudyFile.Made(plan, chosen, Options) : 0;
                var count = L.F("{0} of {1} parts chosen", chosen.Count, plan.Parts.Count) + " · " + L.P(passages, "{0} passage to explain", "{0} passages to explain");
                if (made > 0)
                    count += " · " + L.F("{0} already made", made);
                // A book takes a while on this PC: some chapters now, the rest another time.
                if (!CloudAi.CanWrite && passages - made > 40)
                    count += "\n" + L.T("A long file takes a while on this PC. You can choose some parts now and make the rest later: what is made is kept.");
                _count.Text = count;
            }
            PrimaryButton.IsEnabled = !_busy && _writer.IsReady && _hearing?.IsReady != false && (_recording || _plan is not null && Chosen.Count > 0);
            FooterNote = Path.GetFileName(StudyFileFor(_file, _tool));
        }
        finally
        {
            _syncing = false;
        }
    }

    // ───────────── reading ─────────────

    private async Task ReadAsync()
    {
        _partsNote.Text = L.T("Reading the file…");
        var progress = new Progress<double>(f => _partsNote.Text = L.T("Reading the file…") + " " + (int)(Math.Clamp(f, 0, 1) * 100) + "%");
        try
        {
            _reading = await Task.Run(() => StudyFile.ReadDocumentAsync(_file, progress, _closing.Token), _closing.Token);
            Replan();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Reading a file for a study file");
            _partsNote.Text = L.T("That didn't work.") + " " + L.T(ex.Message);
        }
        Sync();
    }

    /// <summary>The parts again, for the writer chosen now; the choices made stay.</summary>
    private void Replan()
    {
        if (_reading is null)
            return;
        var unchosen = _boxes.Where(b => b.Value.IsChecked != true).Select(b => _plan?.Parts.FirstOrDefault(p => p.Number == b.Key)?.Title).Where(t => t is not null).ToHashSet();
        _plan = StudyFile.Plan(_reading, CloudAi.CanWrite);
        _partsNote.Text = _plan.Structured ? L.T("The file's own chapters and sections. Untick what you don't need; contents, an index, summaries and review questions start unticked.")
            : L.T("No chapters were found in this file, so it is explained a few pages at a time.");
        Fill(unchosen!);
    }

    /// <summary>The parts as boxes to tick: a chapter's heading ticks all of its parts.</summary>
    private void Fill(HashSet<string?> unchosen)
    {
        _list.Children.Clear();
        _boxes.Clear();
        if (_plan is null)
            return;
        var plan = _plan;
        string? open = null;
        CheckBox? chapterBox = null;
        foreach (var part in plan.Parts)
        {
            if (part.Chapter.Length > 0 && part.Chapter != open)
            {
                open = part.Chapter;
                var members = plan.Parts.Where(p => p.Chapter == part.Chapter).Select(p => p.Number).ToList();
                chapterBox = new CheckBox { Content = Label(part.Chapter, ""), Margin = new Thickness(0, 6, 0, 2), FontWeight = FontWeights.SemiBold, IsThreeState = false };
                chapterBox.Click += (s, _) =>
                {
                    foreach (var n in members)
                        _boxes[n].IsChecked = ((CheckBox)s!).IsChecked == true;
                    Sync();
                };
                _list.Children.Add(chapterBox);
            }
            else if (part.Chapter.Length == 0)
                open = null;
            var intro = part.Chapter.Length > 0 && part.Title == part.Chapter;
            var title = part.Title.Length > 0 ? intro ? L.T("Introduction") : part.Title : Place(L.Code, part.First, part.Last);
            var box = new CheckBox
            {
                Content = Label(title, part.Title.Length > 0 ? Place(L.Code, part.First, part.Last) : ""),
                IsChecked = part.Suggested && !unchosen.Contains(part.Title.Length > 0 ? part.Title : null),
                Margin = new Thickness(part.Chapter.Length > 0 ? 22 : 0, 2, 0, 2),
            };
            box.Click += (_, _) =>
            {
                SyncChapters();
                Sync();
            };
            _boxes[part.Number] = box;
            _list.Children.Add(box);
        }
        SyncChapters();
    }

    /// <summary>A chapter's box is ticked when all of its parts are.</summary>
    private void SyncChapters()
    {
        if (_plan is null)
            return;
        foreach (var box in _list.Children.OfType<CheckBox>().Where(b => !_boxes.ContainsValue(b)))
        {
            var chapter = ((box.Content as DockPanel)?.Tag as string) ?? "";
            box.IsChecked = _plan.Parts.Where(p => p.Chapter == chapter).All(p => _boxes[p.Number].IsChecked == true);
        }
    }

    private void Choose(bool all)
    {
        foreach (var box in _boxes.Values)
            box.IsChecked = all;
        SyncChapters();
        Sync();
    }

    /// <summary>A part's line: its heading as the file has it (never translated), and where it is.</summary>
    private static DockPanel Label(string title, string where)
    {
        var panel = new DockPanel { LastChildFill = true, Tag = title };
        if (where.Length > 0)
        {
            var place = Words(where, "Petal.Caption");
            place.Margin = new Thickness(10, 0, 0, 0);
            place.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(place, Dock.Right);
            panel.Children.Add(place);
        }
        var words = Words(title, "Petal.Body");
        words.TextTrimming = TextTrimming.CharacterEllipsis;
        words.ToolTip = title;
        panel.Children.Add(words);
        return panel;
    }

    /// <summary>
    /// Words of the file shown as they are: not looked up in the interface's language, and in their own direction: a
    /// heading in Latin letters in an Arabic window keeps its number at its start ("1.2 The cell membrane"), at the
    /// window's side all the same.
    /// </summary>
    private static TextBlock Words(string text, string style)
    {
        var words = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource(style) };
        if (Direction(text) is { } own && own != L.Direction)
        {
            words.FlowDirection = own;
            words.TextAlignment = TextAlignment.Right;
        }
        return words;
    }

    /// <summary>The direction a text reads in, by its letters; null when it has none to tell.</summary>
    internal static FlowDirection? Direction(string text)
    {
        int rtl = text.Count(c => c is >= (char)0x0590 and <= (char)0x08FF), latin = text.Count(c => char.IsLetter(c) && c < (char)0x0590);
        return rtl + latin == 0 ? null : rtl >= latin ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    // ───────────── making ─────────────

    /// <summary>
    /// Where a place is, in the study file's language: "Page 3", "Slides 4–6", "12:30–14:05", or a section's heading. In
    /// a language written right to left a range keeps its order ("3–4", not "4–3"): marks either side of its dash hold it.
    /// </summary>
    internal static string Place(string language, PlacedText first, PlacedText last)
    {
        var place = Named(language, first, last);
        return language is "ar" or "fa" or "ur" ? Regex.Replace(place, @"(?<=\d)–(?=\d)", Mark + "–" + Mark) : place;
    }

    /// <summary>The left-to-right mark, which makes what is beside it read left to right.</summary>
    private static readonly string Mark = ((char)0x200E).ToString();

    private static string Named(string language, PlacedText first, PlacedText last) => first.Kind switch
    {
        PlaceKind.Page when first.Number == last.Number => string.Format(L.In(language, "Page {0}"), first.Number),
        PlaceKind.Page => string.Format(L.In(language, "Pages {0}–{1}"), first.Number, last.Number),
        PlaceKind.Slide when first.Number == last.Number => string.Format(L.In(language, "Slide {0}"), first.Number),
        PlaceKind.Slide => string.Format(L.In(language, "Slides {0}–{1}"), first.Number, last.Number),
        PlaceKind.Time => first.Start == last.Start ? AskFile.Clock(first.Start) : AskFile.Clock(first.Start) + "–" + AskFile.Clock(last.End),
        PlaceKind.Rows => first.Name,
        _ => first.Name is { Length: > 0 } name ? last.Name is { Length: > 0 } other && other != name ? name + " – " + other : name : "",
    };

    private async Task MakeAsync()
    {
        _busy = true;
        Sync();
        _keep.Text = L.T("You can close this window: what is made is kept, and making it again carries on from there.");
        _keep.Visibility = Visibility.Visible;
        try
        {
            if (_recording)
            {
                string? whisper = ModelStore.Find(Speech[Array.FindLastIndex(Speech, ModelStore.IsAvailable)]);
                _status.Text = L.T("Listening…");
                var listening = new Progress<double>(f => _status.Text = L.T("Listening…") + " " + (int)(Math.Clamp(f, 0, 1) * 100) + "%");
                _plan = await StudyFile.ReadAsync(_file, whisper, CloudAi.CanWrite, listening, _closing.Token);
            }
            var plan = _plan ?? throw new InvalidOperationException("There are no words to read in this file.");
            var chosen = Chosen;
            var options = Options;
            var into = options.Into;
            var title = plan.Title is { Length: > 0 } own ? own : Path.GetFileNameWithoutExtension(_file);
            _status.Text = L.T("Explaining the file…");
            var progress = new Progress<StudyProgress>(Making);
            var book = await Task.Run(() => StudyFile.MakeAsync(plan, chosen, options, title, Path.GetFileName(_file), (a, b) => Place(into, a, b), progress, _closing.Token), _closing.Token);
            Save(book, into);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Making a study file");
            _status.Text = L.T("That didn't work.") + " " + L.T(ex.Message);
        }
        finally
        {
            _busy = false;
            _setup.IsEnabled = true;
            Sync();
        }
    }

    /// <summary>How far it is: the passage being explained of all, and once that can be told, about how long the rest will take.</summary>
    private void Making(StudyProgress p)
    {
        _setup.IsEnabled = false;
        var text = p.Total == 0 ? L.T("Explaining the file…")
            : p.Done >= p.Total ? L.T("Summing up the chapters…")
            : L.F("Explaining passage {0} of {1}", p.Done + 1, p.Total) + " · " + (int)(100.0 * p.Done / Math.Max(1, p.Total)) + "%";
        if (p.Left is { } left && p.Done < p.Total)
            text += " · " + (left.TotalMinutes >= 1.5 ? L.P((int)Math.Round(left.TotalMinutes), "about {0} minute left", "about {0} minutes left") : L.T("about a minute left"));
        // The part's heading on its own line, in its own direction.
        _status.Inlines.Clear();
        _status.Inlines.Add(new Run(text));
        if (p.Part.Length > 0 && p.Done < p.Total)
        {
            _status.Inlines.Add(new LineBreak());
            _status.Inlines.Add(new Run(p.Part) { FlowDirection = Direction(p.Part) ?? L.Direction });
        }
        _keep.Text = L.T("You can close this window: what is made is kept, and making it again carries on from there.");
        _keep.Visibility = Visibility.Visible;
    }

    private void Save(StudyBook book, string into)
    {
        var output = StudyFileFor(_file, _tool);
        var words = Words(into);
        Close();
        _ = _jobs.RunAsync("Saving", "Study file saved", [new WorkItem(_file, Path.GetFileName(_file), async (_, ct) =>
        {
            await Task.Run(() => StudyFileWriter.Write(output, book, words), ct);
            try
            {
                Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No browser: the study file is still next to the file.
            }
            return (IReadOnlyList<string>)[output];
        })], _at);
    }

    /// <summary>The words of the study file's page, in the language it is written in.</summary>
    internal static StudyFileWords Words(string language)
    {
        string W(string english) => L.In(language, english);
        var words = new Dictionary<string, string>
        {
            ["home"] = W("Overview"),
            ["terms"] = W("Terms"),
            ["cards"] = W("Cards"),
            ["test"] = W("Test yourself"),
            ["progress"] = W("Progress"),
            ["contents"] = W("Table of contents"),
            ["search"] = W("Search this file"),
            ["noResults"] = W("Nothing found."),
            ["smaller"] = W("Smaller text"),
            ["larger"] = W("Larger text"),
            ["theme"] = W("Light or dark"),
            ["skip"] = W("Skip to the text"),
            ["keyIdeas"] = W("Key ideas"),
            ["explained"] = W("Explained"),
            ["example"] = W("Worked example"),
            ["termsHere"] = W("Terms in this part"),
            ["check"] = W("Check yourself"),
            ["partCards"] = W("Cards to remember"),
            ["markStudied"] = W("Mark as studied"),
            ["studied"] = W("Studied"),
            ["previous"] = W("Previous"),
            ["next"] = W("Next"),
            ["intro"] = W("Introduction"),
            ["answer"] = W("The answer"),
            ["right"] = W("Right!"),
            ["wrong"] = W("Not quite."),
            ["fromOriginal"] = W("In the original:"),
            ["turn"] = W("Turn the card over"),
            ["notYet"] = W("Not yet"),
            ["knewIt"] = W("I knew it"),
            ["shuffle"] = W("Shuffle the cards"),
            ["startOver"] = W("Start over"),
            ["from"] = W("Choose from"),
            ["wholeFile"] = W("The whole file"),
            ["known"] = W("Known: {0}/{1}"),
            ["cardsHint"] = W("Press the card or Space to turn it over, then say whether you knew it."),
            ["allKnown"] = W("You know every card here. Start over to go through them again."),
            ["howMany"] = W("How many"),
            ["all"] = W("All"),
            ["start"] = W("Start the test"),
            ["questionOf"] = W("Question {0} of {1}"),
            ["score"] = W("{0} right out of {1}"),
            ["best"] = W("Best so far: {0}%"),
            ["mistakes"] = W("Look at these again"),
            ["tryAgain"] = W("Try again"),
            ["finish"] = W("See the result"),
            ["readPart"] = W("read the part"),
            ["partsStudied"] = W("Parts studied"),
            ["questionsRight"] = W("Questions answered right"),
            ["cardsKnown"] = W("Cards you know"),
            ["reset"] = W("Start my progress again"),
            ["resetSure"] = W("Forget everything done in this file on this device?"),
            ["kept"] = W("Your progress is kept in this browser, on this device only."),
            ["print"] = W("Print"),
            ["withAnswers"] = W("With the answers"),
            ["madeFrom"] = W("Made from {0}"),
            ["aiNote"] = W("Written by an AI model ({0}) from the file's own words. It can still be wrong: check what matters against the original, where each part says it comes from."),
            ["howTo"] = W("How to study with it"),
            ["step1"] = W("Read a part: its key ideas, its explanation and its example."),
            ["step2"] = W("Check yourself with its questions, then mark it as studied."),
            ["step3"] = W("Go over the cards, and test yourself on a chapter or the whole file."),
            ["begin"] = W("Begin with the first part"),
            ["resume"] = W("Carry on: {0}"),
            ["goals"] = W("What you will learn"),
            ["chapterTest"] = W("Test yourself on this chapter"),
            ["letters"] = W("A B C D"),
            ["notExplained"] = W("This passage couldn't be explained; here it is as the file has it."),
            ["findTerm"] = W("Find a term"),
            ["close"] = W("Close"),
            ["redo"] = W("Answer again"),
            ["noQuestions"] = W("There are no questions here."),
            ["chapter"] = W("Chapter"),
            ["needsScript"] = W("Turn on JavaScript in this browser for the cards, the test and your progress."),
        };
        string Count(string key, int n) => key switch
        {
            "parts" => L.P(n, "{0} part", "{0} parts", language),
            "terms" => L.P(n, "{0} term", "{0} terms", language),
            "cards" => L.P(n, "{0} card", "{0} cards", language),
            _ => L.P(n, "{0} question", "{0} questions", language),
        };
        return new StudyFileWords(words, Count);
    }
}
