using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
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
    private readonly Segmented<StudyActivities> _activities;
    private readonly Segmented<StudyScenes> _scenes;
    private readonly WrapPanel _purposes = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly List<(StudyPurpose Purpose, Button Button)> _purposeButtons = [];
    private readonly TextBlock _purposeNote = Text("", "Petal.Caption");
    private Grid? _amountRow;
    private readonly CheckBox _design = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _designNote = Text("", "Petal.Caption");
    private StudyPurpose _purpose = StudyPurpose.Study;

    /// <summary>What the file can be for, as the window offers it: its name, its picture and what it gives.</summary>
    internal static readonly (StudyPurpose Purpose, string Label, string Icon, string About)[] Purposes =
    [
        (StudyPurpose.Study, "Study", "book", "Explanations, diagrams, things to try by hand, cards, questions, a test and your progress."),
        (StudyPurpose.Teach, "Teach a lesson", "users", "A lesson to teach from: explanations to say aloud, an example for the board and questions for the class."),
        (StudyPurpose.Explain, "Explain to someone", "speech", "The idea made plain for someone who isn't a specialist, with things to try by hand. No tests."),
        (StudyPurpose.Present, "Present", "presentation", "Points that carry for an audience, with an example they will remember. No tests."),
        (StudyPurpose.Brief, "Quick briefing", "summary", "The essentials only, quickly. No tests."),
        (StudyPurpose.Children, "For children", "heart", "Simply and warmly, for children of about 8 to 12, with a few easy questions."),
        (StudyPurpose.Explore, "Explore and play", "hand", "Things to mix, move and change by hand above all, to see how it works. No tests."),
    ];
    private readonly TextBox _request = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 150, MaxLength = StudyRequest.Longest,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 7, 80, 7),
    };
    private readonly TextBlock _requestHint = Text("For example: explain it simply for beginners, in English, with diagrams and activities to drag and match.", "Petal.Caption");
    private readonly TextBlock _requestNote = Text("", "Petal.Caption");
    private readonly TextBlock _understood = Text("", "Petal.Caption");
    private readonly TextBlock _mismatch = Text("", "Petal.Caption");
    private readonly TextBlock _counter = Text("", "Petal.Caption");
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly List<(Button Chip, string Words)> _suggestions = [];
    private readonly Button _send;
    private readonly Button _keepAs;
    private readonly DockPanel _naming = new() { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBox _name = new() { MaxLength = StudyTemplates.LongestName, Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBlock _templatesLine = Text("", "Petal.Caption");
    private readonly TextBlock _templatesNote = Text("", "Petal.Caption");
    private readonly List<(Button Chip, StudyTemplate Template)> _templateChips = [];
    private StudyTemplate? _renaming;
    private readonly Button _try = new() { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 5) };
    private readonly TextBlock _tryNote = Text("Makes the first chosen part and opens it, to see how the study file will look before the whole of it. What it makes is kept.", "Petal.Caption");
    private DocumentReading? _reading;
    private StudyPlan? _plan;
    private bool _busy;
    private bool _syncing;
    private bool _intoFromRequest;
    private string? _asked;

    /// <summary>
    /// Requests to start from: what each says (in the interface's language, to change as wanted) and the settings it goes
    /// with. The person's own words are what the model follows.
    /// </summary>
    internal static readonly (string Label, string Icon, string Words, StudyAim Aim, StudyActivities? Activities, StudyAmount? Amount)[] Suggestions =
    [
        ("Visual and interactive", "sparkles", "Make it a visual, interactive explanation: draw processes as diagrams, ideas as maps and comparisons as tables, and turn what is to be learnt into activities to drag, match, sort and put in order, as suits each part.", StudyAim.Visual, StudyActivities.Many, null),
        ("Simply, for beginners", "", "Explain it very simply, for someone meeting the subject for the first time: short sentences, everyday words, and an example from daily life for each idea.", StudyAim.Beginners, null, null),
        ("For children", "", "Explain it for children of 9 to 12: friendly and simple, with comparisons to things they know and fun activities instead of long text.", StudyAim.Children, StudyActivities.Many, null),
        ("Exam revision", "", "Prepare it for an exam: stress the definitions, rules, formulas and common mistakes, with more questions at exam level.", StudyAim.Exam, null, StudyAmount.More),
        ("Quick summary", "", "Keep it short: the essentials of each part in a few lines, its key terms and a few questions to check yourself.", StudyAim.Summary, StudyActivities.Some, StudyAmount.Fewer),
        ("Solving problems", "", "Focus on how to solve problems: worked examples step by step, and the steps of each method to put in order.", StudyAim.Problems, null, null),
        ("Learn the terms", "", "Focus on learning the terms: each with a clear definition, more cards, and terms to match with their meanings and to fill in.", StudyAim.Terms, StudyActivities.Some, StudyAmount.More),
        ("A lesson to teach", "", "Prepare it as a lesson for a teacher to give: the goals, the explanation in the order to teach it, class activities and a short quiz at the end.", StudyAim.Teaching, StudyActivities.Many, null),
    ];

    /// <summary>A language to ask for: its suggestion's label and what it puts in the request, which <see cref="StudyRequest.Language"/> reads back.</summary>
    internal static readonly IReadOnlyDictionary<string, (string Label, string Words)> InLanguage = new Dictionary<string, (string, string)>
    {
        ["en"] = ("In English", "Write it in English."),
        ["ar"] = ("In Arabic", "Write it in Arabic."),
        ["tr"] = ("In Turkish", "Write it in Turkish."),
        ["fa"] = ("In Persian", "Write it in Persian."),
        ["ur"] = ("In Urdu", "Write it in Urdu."),
        ["id"] = ("In Indonesian", "Write it in Indonesian."),
        ["fr"] = ("In French", "Write it in French."),
        ["es"] = ("In Spanish", "Write it in Spanish."),
    };

    /// <summary>Requests remembered at most.</summary>
    private const int Remembered = 8;

    public StudyFileWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : this(tool, file, jobs, at, null, null)
    {
    }

    private static bool IsRecording(string file) => Formats.FromPath(file) is { } format && AskFile.IsRecording(format);

    /// <param name="reading">For snapshots: the file as already read.</param>
    /// <param name="making">For snapshots: the window as it is while the file is being explained.</param>
    internal StudyFileWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at, DocumentReading? reading, StudyProgress? making) : base(tool.Name, IsRecording(file) ? 560 : 860)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        _recording = IsRecording(file);
        _amount = Segments([(StudyAmount.Fewer, "Fewer"), (StudyAmount.Normal, "Normal"), (StudyAmount.More, "More")], StudyAmount.Normal);
        _activities = Segments([(StudyActivities.None, "None"), (StudyActivities.Some, "A few"), (StudyActivities.Many, "Many")], StudyActivities.Some);
        _scenes = Segments([(StudyScenes.None, "None"), (StudyScenes.Some, "A few"), (StudyScenes.Many, "Many")], StudyScenes.Some);
        _send = IconButton("send", "Make the study file with this request (Ctrl+Enter)", 15);
        _keepAs = IconButton("star", "Keep this request as a template", 15);

        Body.Children.Add(Text(Path.GetFileName(file), "Petal.Section"));
        var about = Text("One page that opens in any browser, on a computer or a phone, with no internet: the file explained part by part, with its key ideas, diagrams, worked examples, terms, activities, cards and questions, each saying where in the file it comes from. Send it to students as it is.", "Petal.Caption");
        about.TextWrapping = TextWrapping.Wrap;
        about.Margin = new Thickness(0, 6, 0, 0);
        _setup.Children.Add(about);
        var left = new StackPanel();
        left.Children.Add(PurposeBox());
        left.Children.Add(RequestBox());
        left.Children.Add(Row("Write in", _into, 150, 12));
        _amountRow = Row("Cards and questions", _amount, 150, 8);
        left.Children.Add(_amountRow);
        left.Children.Add(Row("Diagrams and activities", _activities, 150, 8));
        left.Children.Add(Row("Things to try by hand", _scenes, 150, 8));
        _design.Content = new TextBlock { Text = L.T("The model designs a scene of its own for each part's main idea"), TextWrapping = TextWrapping.Wrap };
        _design.Checked += (_, _) => RequestChanged();
        _design.Unchecked += (_, _) => RequestChanged();
        left.Children.Add(_design);
        _designNote.TextWrapping = TextWrapping.Wrap;
        _designNote.Margin = new Thickness(26, 2, 0, 0);
        left.Children.Add(_designNote);
        _where.Margin = new Thickness(0, 10, 0, 0);
        _where.TextWrapping = TextWrapping.Wrap;
        left.Children.Add(_where);
        _try.Style = (Style)Application.Current.FindResource("Petal.SubtleButton");
        _try.BorderThickness = new Thickness(1);
        _try.SetResourceReference(BorderBrushProperty, "Petal.ControlBorder");
        _try.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Border { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Child = ThemedIcon("eye", 13, "Petal.Accent") }, new TextBlock { Text = L.T("Try one part first"), VerticalAlignment = VerticalAlignment.Center } } };
        left.Children.Add(_try);
        _tryNote.TextWrapping = TextWrapping.Wrap;
        _tryNote.Margin = new Thickness(0, 3, 0, 0);
        left.Children.Add(_tryNote);

        if (_recording)
        {
            var whisper = Speech[Array.FindLastIndex(Speech, ModelStore.IsAvailable) is >= 0 and var best ? best : 1];
            _hearing = new ModelPanel(whisper);
            _hearing.Ready += Sync;
            left.Children.Add(_hearing);
            _setup.Children.Add(left);
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
            _partsPanel.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 330, Margin = new Thickness(0, 6, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            _count.Margin = new Thickness(0, 6, 0, 0);
            _count.TextWrapping = TextWrapping.Wrap;
            _partsPanel.Children.Add(_count);
            // What to make on one side, which parts on the other: the window stays short enough for a small screen.
            var columns = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.Children.Add(left);
            Grid.SetColumn(_partsPanel, 2);
            columns.Children.Add(_partsPanel);
            _setup.Children.Add(columns);
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
        _into.Changed += _ =>
        {
            // Chosen by hand: it no longer follows the request.
            _intoFromRequest = false;
            RequestChanged();
        };
        _amount.Changed += _ => RequestChanged();
        _activities.Changed += _ => RequestChanged();
        _scenes.Changed += _ => RequestChanged();
        _request.TextChanged += (_, _) => RequestChanged();
        _request.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
            {
                e.Handled = true;
                if (PrimaryButton.IsEnabled)
                    await MakeAsync();
            }
        };
        _send.Click += async (_, _) =>
        {
            if (PrimaryButton.IsEnabled)
                await MakeAsync();
        };
        PrimaryButton.Content = L.T("Make Study File");
        PrimaryButton.Click += async (_, _) => await MakeAsync();
        _try.Click += async (_, _) => await MakeAsync(sample: true);
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

    private StudyFile.Options Options => new(Into, _amount.Value, _plan?.Language ?? Into, _request.Text.Trim(), _activities.Value, _purpose, _scenes.Value, _design.IsChecked == true && StudyFile.CanDesign);

    // ───────────── what it is for ─────────────

    /// <summary>What the file is for, one to choose, and what the chosen one gives.</summary>
    private StackPanel PurposeBox()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(Text("What is it for?", "Petal.Label"));
        foreach (var (purpose, label, icon, about) in Purposes)
        {
            var chip = Chip(label, icon);
            chip.ToolTip = L.T(about);
            var p = purpose;
            chip.Click += (_, _) => Purpose = p;
            AutomationProperties.SetName(chip, L.T(label));
            _purposeButtons.Add((purpose, chip));
            _purposes.Children.Add(chip);
        }
        panel.Children.Add(_purposes);
        _purposeNote.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_purposeNote);
        ShowPurpose();
        return panel;
    }

    /// <summary>What the file is for: the choice lit, what it gives said, and the cards and questions only where there are some.</summary>
    internal StudyPurpose Purpose
    {
        get => _purpose;
        set
        {
            if (_purpose == value)
                return;
            _purpose = value;
            ShowPurpose();
            RequestChanged();
        }
    }

    private void ShowPurpose()
    {
        foreach (var (purpose, button) in _purposeButtons)
        {
            bool on = purpose == _purpose;
            button.SetResourceReference(BorderBrushProperty, on ? "Petal.Accent" : "Petal.ControlBorder");
            button.BorderThickness = new Thickness(on ? 2 : 1);
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            button.Padding = on ? new Thickness(9, 2, 9, 3) : new Thickness(10, 3, 10, 4);
            AutomationProperties.SetItemStatus(button, on ? L.T("Chosen") : "");
        }
        _purposeNote.Text = L.T(Purposes.First(p => p.Purpose == _purpose).About);
        if (_amountRow is not null)
            _amountRow.Visibility = StudyPurposes.Asks(_purpose) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────── the request ─────────────

    /// <summary>The box for what the study file should be, the suggestions to start from, and what the request comes to.</summary>
    private StackPanel RequestBox()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(Text("Your request", "Petal.Label"));
        var about = Text("Say how you want it, or start from a suggestion and change it: it shapes every part.", "Petal.Caption");
        about.TextWrapping = TextWrapping.Wrap;
        about.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(about);
        _requestHint.TextWrapping = TextWrapping.Wrap;
        _requestHint.IsHitTestVisible = false;
        _requestHint.VerticalAlignment = VerticalAlignment.Top;
        _requestHint.Margin = new Thickness(11, 8, 48, 0);
        _send.HorizontalAlignment = HorizontalAlignment.Right;
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        _send.Margin = new Thickness(0, 0, 6, 6);
        _send.SetResourceReference(BackgroundProperty, "Petal.Accent");
        _send.Content = ThemedIcon("send", 15, "Petal.OnAccent");
        _counter.HorizontalAlignment = HorizontalAlignment.Right;
        _counter.VerticalAlignment = VerticalAlignment.Bottom;
        _counter.Margin = new Thickness(0, 0, 80, 10);
        _counter.IsHitTestVisible = false;
        _keepAs.HorizontalAlignment = HorizontalAlignment.Right;
        _keepAs.VerticalAlignment = VerticalAlignment.Bottom;
        _keepAs.Margin = new Thickness(0, 0, 42, 6);
        _keepAs.Click += (_, _) => StartNaming(null);
        panel.Children.Add(new Grid { Margin = new Thickness(0, 6, 0, 0), Children = { _request, _requestHint, _counter, _keepAs, _send } });
        panel.Children.Add(NamingRow());
        panel.Children.Add(_chips);
        _templatesLine.TextWrapping = TextWrapping.Wrap;
        _templatesLine.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(_templatesLine);
        _templatesNote.TextWrapping = TextWrapping.Wrap;
        _templatesNote.Visibility = Visibility.Collapsed;
        panel.Children.Add(_templatesNote);
        _understood.TextWrapping = TextWrapping.Wrap;
        _understood.Margin = new Thickness(0, 2, 0, 0);
        _understood.Visibility = Visibility.Collapsed;
        panel.Children.Add(_understood);
        _mismatch.TextWrapping = TextWrapping.Wrap;
        _mismatch.Margin = new Thickness(0, 4, 0, 0);
        _mismatch.Visibility = Visibility.Collapsed;
        _mismatch.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        panel.Children.Add(_mismatch);
        _requestNote.TextWrapping = TextWrapping.Wrap;
        _requestNote.Margin = new Thickness(0, 2, 0, 0);
        _requestNote.Visibility = Visibility.Collapsed;
        panel.Children.Add(_requestNote);
        FillSuggestions();
        return panel;
    }

    /// <summary>
    /// The suggestions: the requests to start from, the languages likely wanted (the interface's and English, unless the
    /// file is in it already), and the requests made lately.
    /// </summary>
    private void FillSuggestions()
    {
        _chips.Children.Clear();
        _suggestions.Clear();
        _templateChips.Clear();
        foreach (var template in SettingsStore.Current.StudyTemplates)
            AddTemplate(template);
        foreach (var (label, icon, words, _, activities, amount) in Suggestions)
            AddChip(label, icon, L.T(words), () =>
            {
                if (activities is { } a)
                    _activities.Value = a;
                if (amount is { } m)
                    _amount.Value = m;
            });
        var from = _recording ? null : _plan?.Language;
        foreach (var code in new[] { LanguageChoice.Interface, "en" }.Distinct().Where(c => c != from))
            AddChip(InLanguage[code].Label, "languages", L.T(InLanguage[code].Words), null);
        var recent = SettingsStore.Current.StudyRequests.Where(r => r.Trim().Length > 0).Take(Remembered).ToList();
        if (recent.Count > 0)
        {
            var menu = new ContextMenu();
            foreach (var request in recent)
            {
                var flat = Regex.Replace(request, @"\s+", " ").Trim();
                var item = new MenuItem { Header = flat.Length > 90 ? flat[..88] + "…" : flat, ToolTip = request };
                item.Click += (_, _) => Put(request);
                menu.Items.Add(item);
            }
            var button = Chip("Recent", "history");
            button.ContextMenu = menu;
            button.Click += (_, _) =>
            {
                menu.PlacementTarget = button;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            button.ToolTip = L.T("Requests you made lately");
            _chips.Children.Add(button);
        }
        ShowTemplatesLine();
        ShowChips();
    }

    private Button Chip(string label, string icon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon.Length > 0)
            content.Children.Add(new Border { Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Child = ThemedIcon(icon, 12, "Petal.Accent") });
        content.Children.Add(new TextBlock { Text = L.T(label), VerticalAlignment = VerticalAlignment.Center });
        var chip = new Button
        {
            Content = content, Style = (Style)Application.Current.FindResource("Petal.SubtleButton"), Padding = new Thickness(10, 3, 10, 4),
            Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1), FontSize = 12.5,
        };
        chip.SetResourceReference(BorderBrushProperty, "Petal.ControlBorder");
        return chip;
    }

    /// <summary>A suggestion: pressed, its words go into the request (or come out again) and its settings are chosen.</summary>
    private void AddChip(string label, string icon, string words, Action? settings)
    {
        var chip = Chip(label, icon);
        chip.ToolTip = words;
        chip.Click += (_, _) =>
        {
            var text = _request.Text;
            if (text.Contains(words, StringComparison.Ordinal))
                Put(Regex.Replace(text.Replace(words, ""), @"\n{2,}", "\n").Trim());
            else
            {
                Put(text.TrimEnd().Length > 0 ? text.TrimEnd() + "\n" + words : words);
                settings?.Invoke();
            }
        };
        _suggestions.Add((chip, words));
        _chips.Children.Add(chip);
    }

    /// <summary>Words in the request, the caret after them, ready to change or send.</summary>
    private void Put(string text)
    {
        _request.Text = text;
        _request.CaretIndex = _request.Text.Length;
        _request.Focus();
    }

    /// <summary>The suggestions in the request lit.</summary>
    private void ShowChips()
    {
        foreach (var (chip, words) in _suggestions)
        {
            bool on = _request.Text.Contains(words, StringComparison.Ordinal);
            chip.SetResourceReference(BackgroundProperty, on ? "Petal.AccentSoft" : "Petal.Control");
            chip.SetResourceReference(BorderBrushProperty, on ? "Petal.Accent" : "Petal.ControlBorder");
        }
        // A template is the whole request: lit while the request is it; always edged in the accent, to tell it from a suggestion.
        foreach (var (chip, template) in _templateChips)
        {
            chip.SetResourceReference(BackgroundProperty, StudyTemplates.Same(_request.Text, template.Request) ? "Petal.AccentSoft" : "Petal.Control");
            chip.SetResourceReference(BorderBrushProperty, "Petal.Accent");
        }
        _keepAs.IsEnabled = _request.Text.Trim().Length > 0 && !SettingsStore.Current.StudyTemplates.Any(t => StudyTemplates.Same(t.Request, _request.Text));
    }

    // ───────────── templates ─────────────

    /// <summary>A template's button: pressed, the request becomes it, with its settings; a right-click renames or deletes it.</summary>
    private void AddTemplate(StudyTemplate template)
    {
        var chip = Chip("", "star");
        ((StackPanel)chip.Content).Children.OfType<TextBlock>().First().Text = template.Name;
        chip.ToolTip = template.Request;
        chip.Click += (_, _) =>
        {
            Put(template.Request);
            if (template.Activities is { } a)
                _activities.Value = a;
            if (template.Amount is { } m)
                _amount.Value = m;
        };
        var rename = new MenuItem { Header = L.T("Rename") };
        rename.Click += (_, _) => StartNaming(template);
        var delete = new MenuItem { Header = L.T("Delete") };
        delete.Click += (_, _) =>
        {
            SettingsStore.Current.StudyTemplates.RemoveAll(t => t == template);
            SettingsStore.Save();
            FillSuggestions();
        };
        chip.ContextMenu = new ContextMenu { Items = { rename, delete } };
        _templateChips.Add((chip, template));
        _chips.Children.Add(chip);
    }

    /// <summary>The row where a template is named: under the request, Enter keeps it, Esc lets it go.</summary>
    private DockPanel NamingRow()
    {
        var keep = new Button { Content = L.T("Save"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 3, 12, 4), Style = (Style)Application.Current.FindResource("Petal.PrimaryButton") };
        var cancel = new Button { Content = L.T("Cancel"), Margin = new Thickness(6, 0, 0, 0), Style = (Style)Application.Current.FindResource("Petal.SubtleButton") };
        keep.Click += (_, _) => KeepTemplate();
        cancel.Click += (_, _) => _naming.Visibility = Visibility.Collapsed;
        _name.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                KeepTemplate();
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                _naming.Visibility = Visibility.Collapsed;
            }
        };
        var label = Text("Template name", "Petal.Caption");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(keep, Dock.Right);
        _naming.Children.Add(label);
        _naming.Children.Add(cancel);
        _naming.Children.Add(keep);
        _naming.Children.Add(_name);
        return _naming;
    }

    /// <summary>Asks for a name: for the request as it is now, or a new name for a template kept.</summary>
    private void StartNaming(StudyTemplate? renaming)
    {
        _renaming = renaming;
        _name.Text = renaming?.Name ?? StudyTemplates.NameFor(_request.Text.Trim());
        _naming.Visibility = Visibility.Visible;
        _name.Focus();
        _name.SelectAll();
    }

    private void KeepTemplate()
    {
        var name = string.Join(' ', _name.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var list = SettingsStore.Current.StudyTemplates;
        if (_renaming is { } old)
        {
            int i = list.IndexOf(old);
            if (i >= 0 && name.Length > 0)
                list[i] = old with { Name = name };
        }
        else
        {
            var request = _request.Text.Trim();
            if (request.Length == 0)
                return;
            if (list.Count >= StudyTemplates.Most)
            {
                Tell(L.F("You can keep up to {0} templates. Delete one first (right-click it).", StudyTemplates.Most));
                return;
            }
            list.RemoveAll(t => StudyTemplates.Same(t.Request, request));
            list.Insert(0, new StudyTemplate(name.Length > 0 ? name : StudyTemplates.NameFor(request), request, _activities.Value, _amount.Value));
        }
        SettingsStore.Save();
        _naming.Visibility = Visibility.Collapsed;
        _renaming = null;
        FillSuggestions();
    }

    /// <summary>The line under the suggestions: templates to bring in from a file, or to send to others.</summary>
    private void ShowTemplatesLine()
    {
        _templatesLine.Inlines.Clear();
        var import = new Hyperlink(new Run(L.T("Import templates…")));
        import.Click += (_, _) => ImportTemplates();
        _templatesLine.Inlines.Add(import);
        if (SettingsStore.Current.StudyTemplates.Count > 0)
        {
            var export = new Hyperlink(new Run(L.T("Send your templates…")));
            export.Click += (_, _) => ExportTemplates();
            _templatesLine.Inlines.Add(new Run("  ·  "));
            _templatesLine.Inlines.Add(export);
        }
    }

    /// <summary>What became of keeping, sending or bringing in templates, under the suggestions.</summary>
    private void Tell(string words)
    {
        _templatesNote.Text = words;
        _templatesNote.Visibility = words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExportTemplates()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = L.T("Study file templates") + StudyTemplates.Extension,
            DefaultExt = StudyTemplates.Extension,
            Filter = L.T("DropWheel study templates") + $"|*{StudyTemplates.Extension}",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, StudyTemplates.Write(SettingsStore.Current.StudyTemplates), new System.Text.UTF8Encoding(false));
            Tell(L.P(SettingsStore.Current.StudyTemplates.Count, "{0} template saved. Send the file; it opens with Import templates.", "{0} templates saved. Send the file; it opens with Import templates."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell(L.T("That didn't work.") + " " + ex.Message);
        }
    }

    private void ImportTemplates()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = L.T("DropWheel study templates") + $"|*{StudyTemplates.Extension}" };
        if (dialog.ShowDialog(this) != true)
            return;
        string text;
        try
        {
            // A templates file is small: anything far larger isn't one.
            if (new FileInfo(dialog.FileName).Length > 256 * 1024)
                throw new InvalidDataException();
            text = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Tell(L.T("That isn't a file of study file templates."));
            return;
        }
        if (StudyTemplates.Read(text) is not { } brought)
        {
            Tell(L.T("That isn't a file of study file templates."));
            return;
        }
        var (all, added) = StudyTemplates.Merge(SettingsStore.Current.StudyTemplates, brought);
        SettingsStore.Current.StudyTemplates = all;
        SettingsStore.Save();
        FillSuggestions();
        Tell(added > 0 ? L.P(added, "{0} template added.", "{0} templates added.") : L.T("Nothing new: you have these templates already."));
    }

    /// <summary>
    /// The request changed: its direction, its suggestions, and the language it asks for, which the language choice
    /// follows (until it is chosen by hand). What it comes to is said under it.
    /// </summary>
    private void RequestChanged()
    {
        var text = _request.Text;
        _requestHint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _request.FlowDirection = text.Trim().Length == 0 ? L.Direction : Petal.Core.Text.KeyboardLayout.LooksArabic(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _counter.Text = text.Length > StudyRequest.Longest - 100 ? text.Length + "/" + StudyRequest.Longest : "";
        ShowChips();
        var asked = StudyRequest.Language(text);
        // Only when the language asked for changes: a language chosen by hand afterwards stays.
        if (asked != _asked)
        {
            _asked = asked;
            if (asked is not null)
            {
                _into.Value = !_recording && asked == _plan?.Language ? SameLanguage : asked;
                _intoFromRequest = true;
            }
            else if (_intoFromRequest)
            {
                _into.Value = SameLanguage;
                _intoFromRequest = false;
            }
        }
        _requestNote.Text = text.Trim().Length > 0 ? L.T("Your request is followed in every part; the facts still come only from the file.") : "";
        _requestNote.Visibility = _requestNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowUnderstood();
        Sync();
    }

    /// <summary>
    /// What the request comes to, said back before anything is made: the language it will be written in, what the request
    /// aims at, how many diagrams, activities, cards and questions; and where the request and the settings disagree, with
    /// the way to put it right.
    /// </summary>
    private void ShowUnderstood()
    {
        var text = _request.Text;
        _understood.Inlines.Clear();
        _mismatch.Inlines.Clear();
        if (text.Trim().Length == 0)
        {
            _understood.Visibility = _mismatch.Visibility = Visibility.Collapsed;
            return;
        }
        var reading = StudyRequest.Read(text);
        var said = new List<string>
        {
            _into.Value != SameLanguage || _plan is not null && !_recording
                ? InLanguage.TryGetValue(Into, out var named) ? L.T(named.Label) : LanguageChoice.NameOf(Into)
                : L.T("Written in the file's language"),
        };
        foreach (var aim in reading.Aims)
            said.Add(L.T(Suggestions.First(s => s.Aim == aim).Label));
        said.Add(L.F("Diagrams and activities: {0}", L.T(_activities.Value switch { StudyActivities.None => "None", StudyActivities.Some => "A few", _ => "Many" })));
        said.Add(L.F("Cards and questions: {0}", L.T(_amount.Value switch { StudyAmount.Fewer => "Fewer", StudyAmount.Normal => "Normal", _ => "More" })));
        _understood.Inlines.Add(new Run(L.T("How I read it:") + " ") { FontWeight = FontWeights.SemiBold });
        _understood.Inlines.Add(new Run(string.Join(" · ", said)));
        _understood.Visibility = Visibility.Visible;

        if (reading.Languages.Count > 1)
            Mismatch(L.T("It names more than one language to write in. Keep one, or choose it under “Write in”."), null, null);
        if (reading.AsksActivities && _activities.Value == StudyActivities.None)
            Mismatch(L.T("It asks for diagrams or activities, but “Diagrams and activities” is set to None."), L.T("Make a few"), () => _activities.Value = StudyActivities.Some);
        if (reading.Aims.Contains(StudyAim.Exam) && _amount.Value == StudyAmount.Fewer)
            Mismatch(L.T("It is for an exam, but “Cards and questions” is set to Fewer."), L.T("Make more"), () => _amount.Value = StudyAmount.More);
        _mismatch.Visibility = _mismatch.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A disagreement between the request and the settings, with a link that puts it right.</summary>
    private void Mismatch(string words, string? fix, Action? apply)
    {
        if (_mismatch.Inlines.Count > 0)
            _mismatch.Inlines.Add(new LineBreak());
        _mismatch.Inlines.Add(new Run(words));
        if (fix is null || apply is null)
            return;
        var link = new Hyperlink(new Run(fix));
        link.Click += (_, _) => apply();
        _mismatch.Inlines.Add(new Run(" "));
        _mismatch.Inlines.Add(link);
    }

    /// <summary>For snapshots: a request as if written, and how many diagrams and activities are chosen.</summary>
    internal void Ask(string request, StudyActivities? activities = null)
    {
        if (activities is { } a)
            _activities.Value = a;
        _request.Text = request;
    }

    /// <summary>A request remembered first among the latest.</summary>
    private static void Remember(string request)
    {
        if (request.Trim().Length == 0)
            return;
        var list = SettingsStore.Current.StudyRequests;
        list.RemoveAll(r => r.Trim() == request.Trim());
        list.Insert(0, request.Trim());
        if (list.Count > Remembered)
            list.RemoveRange(Remembered, list.Count - Remembered);
        SettingsStore.Save();
    }

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
            // Scenes of the model's own need a model in the cloud: one on this PC can't write them well.
            _design.IsEnabled = StudyFile.CanDesign;
            _designNote.Text = StudyFile.CanDesign
                ? L.T("Like a museum exhibit made for the idea: dust that gathers into a star, boxes pushed with sliders. One request a part, on your API key; each scene runs closed off in the page.")
                : L.T("Needs your own API key (Use My API Key below): a model on this PC can't write these scenes well.");
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
            _send.IsEnabled = _try.IsEnabled = PrimaryButton.IsEnabled;
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
        // The file's language is known now: a suggestion to write in it is no help.
        FillSuggestions();
        if (_intoFromRequest && _asked == _plan.Language)
            _into.Value = SameLanguage;
        ShowUnderstood();
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

    /// <summary>
    /// Makes the study file from the chosen parts, or a <paramref name="sample"/> of one of them: opened at once, the window
    /// staying open to change the request, and how long the rest would take said from how long the sample took.
    /// </summary>
    private async Task MakeAsync(bool sample = false)
    {
        if (_busy)
            return;
        _busy = true;
        Sync();
        Remember(_request.Text);
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
            if (sample && StudyFile.SamplePart(plan, chosen) is int one)
            {
                var part = new HashSet<int> { one };
                int fresh = StudyFile.Passages(plan, part) - StudyFile.Made(plan, part, options);
                var clock = Stopwatch.StartNew();
                var tried = await Task.Run(() => StudyFile.MakeAsync(plan, part, options, title, Path.GetFileName(_file), (a, b) => Place(into, a, b), progress, _closing.Token, sample: true), _closing.Token);
                var each = fresh > 0 ? clock.Elapsed / fresh : (TimeSpan?)null;
                var output = StudyFile.SamplePath(_file);
                await Task.Run(() => StudyFileWriter.Write(output, tried, Words(into)), _closing.Token);
                Open(output);
                _status.Inlines.Clear();
                _status.Text = Tried(StudyFile.Passages(plan, chosen) - StudyFile.Made(plan, chosen, options), each);
                _keep.Visibility = Visibility.Collapsed;
                return;
            }
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

    /// <summary>What a sample says once it is open: how long the rest of the chosen parts would take, as the sample went.</summary>
    internal static string Tried(int left, TimeSpan? each)
    {
        var open = L.T("The sample is open in your browser. Change the request and try again, or make the whole file: the part already made isn't made again.");
        if (left <= 0)
            return open;
        if (each is not { } per)
            return open + " " + L.P(left, "{0} passage left to explain.", "{0} passages left to explain.");
        var minutes = (int)Math.Round(per.TotalMinutes * left);
        return open + " " + (minutes >= 90 ? L.P((int)Math.Round(minutes / 60.0), "The rest takes about {0} hour on this PC.", "The rest takes about {0} hours on this PC.")
            : minutes >= 2 ? L.P(minutes, "The rest takes about {0} minute.", "The rest takes about {0} minutes.")
            : L.T("The rest takes about a minute."));
    }

    /// <summary>Opens a page in the browser; with none, it is still where it was saved.</summary>
    private static void Open(string page)
    {
        try
        {
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
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
            ["sample"] = W("A sample: one part, to see how the study file will look. The whole file has every part chosen."),
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
            ["seeIt"] = W("See it"),
            ["tryIt"] = W("Try it"),
            ["activities"] = W("Activities"),
            ["kindOrder"] = W("Put in order"),
            ["kindMatch"] = W("Match"),
            ["kindSort"] = W("Sort into groups"),
            ["kindBlanks"] = W("Fill in the blanks"),
            ["kindTrueFalse"] = W("True or false"),
            ["kindMapFill"] = W("Complete the map"),
            ["mapHint"] = W("Drag each name to the branch it belongs to, or press a name and then its branch."),
            ["stepOf"] = W("Step {0} of {1}"),
            ["checkIt"] = W("Check"),
            ["showAnswer"] = W("Show the answer"),
            ["again"] = W("Start again"),
            ["allRight"] = W("All right!"),
            ["rightOf"] = W("{0} of {1} right"),
            ["dragHint"] = W("Drag each piece to its place, or press a piece and then its place."),
            ["orderHint"] = W("Drag the pieces into the right order, or press one and then the place it goes."),
            ["bank"] = W("Pieces to place"),
            ["placeHere"] = W("Place here"),
            ["picked"] = W("Picked: {0}. Now press where it goes."),
            ["placed"] = W("Placed: {0}"),
            ["true"] = W("True"),
            ["false"] = W("False"),
            ["backToStart"] = W("Then it begins again"),
            ["activitiesSolved"] = W("Activities solved"),
            ["practiceChapter"] = W("Practise this chapter"),
            ["activityOf"] = W("Activity {0} of {1}"),
            ["solvedScore"] = W("{0} solved out of {1}"),
            ["noActivities"] = W("There are no activities here."),
            ["skipIt"] = W("Skip"),
            ["handsOn"] = W("Try it by hand"),
            ["kindLab"] = W("Lab: mix and see"),
            ["kindSimulate"] = W("Simulation"),
            ["kindSystem"] = W("What leads to what"),
            ["kindScenario"] = W("What would you do?"),
            ["kindEstimate"] = W("Guess the number"),
            ["labHint"] = W("Press the things on the shelf, or drag them, to put them in the flask and see what happens."),
            ["labShelf"] = W("Things to mix"),
            ["flask"] = W("The flask"),
            ["pourOut"] = W("Empty the flask"),
            ["hintMe"] = W("A hint"),
            ["labNothing"] = W("The file tells of nothing happening with these. Empty the flask and try others."),
            ["labMore"] = W("Add something else to it."),
            ["labFound"] = W("{0} of {1} found"),
            ["labAll"] = W("You found everything the file tells of!"),
            ["tryThing"] = W("Try {0}."),
            ["takeOut"] = W("Take out {0}"),
            ["whyItHappens"] = W("Why:"),
            ["play"] = W("Play"),
            ["pause"] = W("Pause"),
            ["along"] = W("The curve as this changes:"),
            ["simHint"] = W("Move the sliders and watch what follows."),
            ["moreOf"] = W("More {0}"),
            ["lessOf"] = W("Less {0}"),
            ["raisesLink"] = W("More {0} means more {1}"),
            ["lowersLink"] = W("More {0} means less {1}"),
            ["sysHint"] = W("Press ▲ or ▼ on a part to make it more or less, and watch what follows from it."),
            ["loopUp"] = W("…and back to where it began: a loop that feeds itself."),
            ["loopDown"] = W("…and back to where it began: a loop that holds itself in check."),
            ["bothWays"] = W("{0} is pushed both ways."),
            ["situationOf"] = W("Situation {0} of {1}"),
            ["goodChoice"] = W("A good choice"),
            ["badChoice"] = W("Not a good choice"),
            ["partly"] = W("Partly right"),
            ["otherChoices"] = W("See the other choices"),
            ["goodChoices"] = W("Good choices: {0} of {1}"),
            ["yourGuess"] = W("Your guess"),
            ["reveal"] = W("Show the answer"),
            ["spotOn"] = W("Spot on!"),
            ["nearly"] = W("Close!"),
            ["farOff"] = W("Quite far off."),
            ["theAnswer"] = W("The answer: {0}"),
            ["nearScore"] = W("Close on {0} of {1}"),
            ["inFormula"] = W("Worked out as:"),
            ["forExam"] = W("For the exam"),
            ["signalWords"] = W("If the question says…"),
            ["traps"] = W("Don't mix them up"),
            ["writeLikeThis"] = W("Write it like this in the exam"),
            ["showOriginal"] = W("Show the original beside it"),
            ["hideOriginal"] = W("Hide the original"),
            ["mistakes2"] = W("My mistakes"),
            ["noMistakes"] = W("No mistakes to go over. Answer the questions in the parts, or take a test."),
            ["youSaid"] = W("You answered: {0}"),
            ["designedKind"] = W("Made for this idea"),
            ["sceneFailed"] = W("This scene couldn't run in this browser; its idea is above."),
        };
        string Count(string key, int n) => key switch
        {
            "parts" => L.P(n, "{0} part", "{0} parts", language),
            "terms" => L.P(n, "{0} term", "{0} terms", language),
            "cards" => L.P(n, "{0} card", "{0} cards", language),
            "activities" => L.P(n, "{0} activity", "{0} activities", language),
            "scenes" => L.P(n, "{0} thing to try by hand", "{0} things to try by hand", language),
            _ => L.P(n, "{0} question", "{0} questions", language),
        };
        return new StudyFileWords(words, Count);
    }
}
