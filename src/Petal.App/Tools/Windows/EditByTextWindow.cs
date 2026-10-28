using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;
using Petal.Core.Jobs;
using Petal.Core.Models;
using Petal.Core.Tools;
using Petal.Engines.Media;
using Petal.Recipes;
using Path = System.IO.Path;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Edit by Text: a recording cut by editing what was said. It is heard on this PC (once: Make Shorts' hearing is kept and
/// shared) and shown as its words, with the sounds between words that weren't words and the pauses. Words taken out of the
/// text are cut out of the picture and the sound; a clean-up takes out the "um"s, the words said twice and the sounds, and
/// long pauses are shortened. The edit plays as it will be before it is saved, and is kept as it is left, so the same
/// recording opens again as it was. See <see cref="TextCut"/> and <see cref="TextEditing"/>.
/// </summary>
public sealed class EditByTextWindow : MediaToolWindow
{
    private static readonly ModelInfo[] Models = [ModelCatalog.WhisperFast, ModelCatalog.WhisperBalanced, ModelCatalog.WhisperAccurate];

    /// <summary>"Shorten pauses" shortens the pauses longer than this.</summary>
    private const double LongPause = 0.8;

    /// <summary>A paragraph ends with a sentence once it has this many words, or at a pause this long.</summary>
    private const int ParagraphWords = 40;
    private const double ParagraphPause = 1.5;

    private static readonly Brush FoundBrush = Frozen(Color.FromArgb(0x66, 0xF5, 0xB7, 0x00));

    // ---------- the choices ----------
    private readonly StackPanel _settings = new();
    private readonly Border _coverBox;
    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill };
    private readonly Segmented<int> _accuracy;
    private readonly ComboBox _language;
    private readonly ModelPanel _model;

    // ---------- listening ----------
    private readonly StackPanel _working = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _stage = new();
    private readonly TextBlock _detail = new();
    private readonly Border _track = new() { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Width = 0 };
    private double _share;

    // ---------- the words ----------
    private readonly StackPanel _editor = new() { Visibility = Visibility.Collapsed };
    private readonly Border _pictureBox;
    private readonly Grid _picture = new() { Height = 160, ClipToBounds = true };
    private readonly Image _still = new() { Stretch = Stretch.Uniform };
    private readonly RichTextBox _text = new();
    private readonly Button _play;
    private readonly Button _take = new() { Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _fillers = new() { Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _pauses = new() { Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly TextBox _find = new() { Width = 190, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _findHint;
    private readonly TextBlock _foundCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button _takeFound = new() { Margin = new Thickness(10, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly MenuItem _takeItem = new();

    private ShortsSource? _source;
    private SoundLevels? _loudness;
    private (string Model, string Language) _heardWith;
    private CancellationTokenSource? _work;
    private bool _editing;
    private bool _snapshot;

    private IReadOnlyList<EditToken> _tokens = [];
    private Run[] _runs = [];
    private string[] _plain = [];
    private double _length;
    private HashSet<int> _removed = [];
    private readonly Stack<HashSet<int>> _history = new();
    private readonly Stack<HashSet<int>> _future = new();
    private List<(double Start, double End)> _kept = [];

    // Find: the words found, where each match starts, which one is shown.
    private HashSet<int> _found = [];
    private readonly List<(int First, int Last)> _matches = [];
    private int _match = -1;

    // Playing the edit: the word lit, the stretch kept that is playing, ticks left to wait for the player to get there.
    private int _lit = -1;
    private int _range = -1;
    private int _settling;
    private int _stillTicket;

    public EditByTextWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at, 600)
    {
        // The choices.
        var cover = new Grid { Height = 150, ClipToBounds = true };
        cover.Children.Add(new Border { Background = Brushes.Black });
        cover.Children.Add(_cover);
        _coverBox = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = cover, Margin = new Thickness(0, 0, 0, 10) };
        _settings.Children.Add(_coverBox);
        _settings.Children.Add(Text("What was said is written down, each word with its time. Take words out of the text and they are cut out of the picture and the sound.", "Petal.Caption").WithMarginT(2));
        int preferred = Array.FindLastIndex(Models, ModelStore.IsAvailable) is >= 0 and var found ? found : 1;
        _accuracy = Segments([(0, "Fast"), (1, "Balanced"), (2, "Accurate")], preferred);
        _settings.Children.Add(Row("Accuracy", _accuracy, 120, 12));
        var languages = new List<(string, string)> { ("auto", "Detect automatically") };
        languages.AddRange(Transcriber.Languages.Select(code => (code, NativeName(code))));
        _language = Choice(languages, "auto");
        _settings.Children.Add(Row("Spoken in", _language, 120, 8));
        _model = new ModelPanel(Models[preferred]);
        _settings.Children.Add(_model);
        Body.Children.Add(_settings);

        // Listening.
        _stage.Style = (Style)FindResource("Petal.Section");
        _detail.Style = (Style)FindResource("Petal.Caption");
        _detail.Margin = new Thickness(0, 6, 0, 0);
        _detail.TextWrapping = TextWrapping.Wrap;
        _track.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _fill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        _track.Child = _fill;
        _track.SizeChanged += (_, _) => Fill(_share);
        _working.Children.Add(_stage);
        _working.Children.Add(_track);
        _working.Children.Add(_detail);
        _working.Children.Add(Text("It all happens on this PC; nothing is uploaded. A recording heard once opens again at once.", "Petal.Caption").WithMarginT(14));
        Body.Children.Add(_working);

        // The words: the picture, the tools, the text.
        _picture.Children.Add(new Border { Background = Brushes.Black });
        _picture.Children.Add(_still);
        AttachPlayer(_picture);
        _pictureBox = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _picture, Margin = new Thickness(0, 0, 0, 10) };
        _editor.Children.Add(_pictureBox);

        _play = IconButton("play", "Play the edit (Space)", 16);
        _play.Click += (_, _) => PlayOrStop();
        _take.Click += (_, _) => TakeOrPutBack(forward: true);
        _take.ToolTip = L.T("Take the words selected out, or put them back (Delete)");
        _fillers.Click += (_, _) => Change(set => set.UnionWith(TextCut.Fillers(_tokens)));
        _fillers.ToolTip = L.T("Hesitations such as “um” and “uh”, a word said twice in a row, and the sounds between words that weren't words");
        _pauses.Click += (_, _) => Change(set => set.UnionWith(TextCut.LongPauses(_tokens, LongPause)));
        _pauses.ToolTip = L.F("Pauses longer than {0} s are shortened to {1} s", LongPause, TextCut.ShortPause);
        _editor.Children.Add(new WrapPanel { Children = { _play, _take, _fillers, _pauses } });

        _undo = IconButton("undo", "Undo (Ctrl+Z)", 15);
        _redo = IconButton("redo", "Redo (Ctrl+Y)", 15);
        _undo.Click += (_, _) => Undo();
        _redo.Click += (_, _) => Redo();
        var history = new StackPanel { Orientation = Orientation.Horizontal, Children = { _undo, _redo } };
        DockPanel.SetDock(history, Dock.Right);
        _findHint = Text("Find words (Ctrl+F)", "Petal.Caption");
        _findHint.IsHitTestVisible = false;
        _findHint.VerticalAlignment = VerticalAlignment.Center;
        _findHint.Margin = new Thickness(9, 0, 9, 0);
        _foundCount.Style = (Style)FindResource("Petal.Caption");
        _takeFound.Content = L.T("Take them out");
        _takeFound.Click += (_, _) => Change(set => set.UnionWith(_found));
        var findRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        findRow.Children.Add(history);
        findRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new Grid { Children = { _find, _findHint } }, _foundCount, _takeFound } });
        _editor.Children.Add(findRow);

        _text.IsReadOnly = true;
        _text.IsReadOnlyCaretVisible = true;
        _text.IsUndoEnabled = false;
        _text.IsInactiveSelectionHighlightEnabled = true;
        _text.AcceptsReturn = false;
        _text.AcceptsTab = false;
        _text.Background = Brushes.Transparent;
        _text.BorderThickness = new Thickness(0);
        _text.Height = 250;
        _text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _text.SetResourceReference(Control.ForegroundProperty, "Petal.Text");
        _text.SetResourceReference(TextBoxBase.CaretBrushProperty, "Petal.Text");
        _text.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Petal.Accent");
        _text.SelectionOpacity = 0.35;
        _text.SelectionChanged += (_, _) => Moved();
        _text.ContextMenu = Menu();
        var page = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 6, 2, 6), Margin = new Thickness(0, 10, 0, 0), Child = _text };
        page.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _editor.Children.Add(page);
        _editor.Children.Add(Text("Select words and press Delete to take them out, and again to put them back. Space plays the edit from the cursor.", "Petal.Caption").WithMarginT(8));
        Body.Children.Add(_editor);

        _accuracy.Changed += i => { _model.Model = Models[i]; Sync(); };
        _model.Ready += Sync;
        _language.SelectionChanged += (_, _) => Sync();
        _find.TextChanged += (_, _) => Find();
        _find.PreviewKeyDown += (_, e) =>
        {
            // Enter goes to the next match (Shift+Enter the one before), not to Save.
            if (e.Key == Key.Enter)
            {
                Next(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
            }
        };

        PrimaryButton.Click += async (_, _) =>
        {
            if (_editing)
                Save();
            else
                await ListenAsync();
        };
        ResetButton.Click += (_, _) =>
        {
            // While listening it cancels; once the words are shown it goes back to the choices.
            _work?.Cancel();
            ShowSettings();
        };
        Closed += (_, _) => _work?.Cancel();
        ShowSettings();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string NativeName(string code)
    {
        try
        {
            var name = System.Globalization.CultureInfo.GetCultureInfo(code).NativeName;
            return name.Length > 0 ? char.ToUpper(name[0], System.Globalization.CultureInfo.GetCultureInfo(code)) + name[1..] : code;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>Languages written right to left.</summary>
    private static bool RightToLeft(string language) => language is "ar" or "fa" or "ur" or "he" or "ps" or "sd" or "yi" or "ug";

    /// <summary>A length as a clock: "4:05", "1:02:03".</summary>
    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private static async Task<BitmapImage> LoadAsync(byte[] picture)
    {
        await Task.Yield();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(picture);
        image.EndInit();
        image.Freeze();
        return image;
    }

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        if (!info.HasVideo)
        {
            // A recording without a picture: more room for the words (the player still plays its sound).
            _coverBox.Visibility = Visibility.Collapsed;
            _picture.Height = 0;
            _pictureBox.Margin = new Thickness(0);
            _text.Height = 360;
        }
        else
        {
            try
            {
                _cover.Source = await LoadAsync(await MediaPreview.ExtractFrameAsync(File, info.Duration / 3, 640));
            }
            catch (Exception ex)
            {
                Log.Info($"No frame for Edit by Text: {ex.Message}");
            }
            if (_editing)
                _ = StillAsync(_snapshot ? 1 : _tokens[TokenAt(_text.Selection.Start)].Start);
        }
        Sync();
        // Heard before (here or by Make Shorts): straight to the words.
        if (_snapshot || _editing || _work is not null)
            return;
        for (int i = Models.Length - 1; i >= 0; i--)
        {
            if (ModelStore.Find(Models[i]) is { } path && ShortsMaker.IsKnown(File, path, "auto"))
            {
                _accuracy.Value = i;
                _model.Model = Models[i];
                await ListenAsync();
                return;
            }
        }
    }

    private void Fill(double share)
    {
        _share = Math.Clamp(share, 0, 1);
        _fill.Width = Math.Max(0, _track.ActualWidth * _share);
    }

    private void Sync()
    {
        if (_editing || _accuracy is null)
            return;
        bool working = _work is not null;
        PrimaryButton.IsEnabled = !working && _model.IsReady && Info is { HasAudio: true };
        if (!working)
        {
            var model = ModelStore.Find(_model.Model);
            FooterNote = Info is { HasAudio: false } ? L.T("This file has no sound to hear.")
                : model is not null && TextEditing.IsKnown(File, model, ValueOf<string>(_language)) ? L.T("Heard before: the words are shown at once.")
                : Info is { } info ? L.F("{0} long", Clock(info.DurationSeconds)) : "";
        }
    }

    // ---------- listening ----------

    private void ShowSettings()
    {
        StopPlayback();
        _editing = false;
        _settings.Visibility = Visibility.Visible;
        _working.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Collapsed;
        ResetButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = L.T("Show the Words");
        Sync();
    }

    private async Task ListenAsync()
    {
        var model = ModelStore.Find(_model.Model);
        if (model is null || Info is not { } info)
            return;
        var language = ValueOf<string>(_language);
        var cancel = _work = new CancellationTokenSource();
        _settings.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = L.T("Listening…");
        ResetButton.Content = L.T("Cancel");
        ResetButton.Visibility = Visibility.Visible;
        FooterNote = "";
        try
        {
            if (_source is null || _heardWith != (model, language))
            {
                _stage.Text = info.HasVideo ? L.T("Listening to the video…") : L.T("Listening to the recording…");
                Fill(0);
                _detail.Text = L.F("{0} long", Clock(info.DurationSeconds));
                var progress = new Progress<double>(p =>
                {
                    if (cancel.IsCancellationRequested)
                        return;
                    Fill(p);
                    _detail.Text = L.F("{0} of {1} heard", Clock(p * info.DurationSeconds), Clock(info.DurationSeconds));
                });
                var file = File;
                (_source, _loudness) = await Task.Run(() => TextEditing.ListenAsync(file, model, language, progress, cancel.Token), cancel.Token);
                _heardWith = (model, language);
            }
            if (cancel.IsCancellationRequested)
                return;
            var source = _source;
            var tokens = TextCut.Tokens(source.Segments, _loudness ?? source.Levels, info.DurationSeconds);
            // Each sound between words heard again on its own, so a clean-up never takes out a word Whisper timed far out.
            if (!TextEditing.SoundsChecked(File, model, language, tokens))
            {
                _stage.Text = L.T("Checking the sounds between words…");
                _detail.Text = "";
                var file = File;
                tokens = await Task.Run(() => TextEditing.CheckSoundsAsync(file, model, language, source.Language, tokens, cancel.Token), cancel.Token);
            }
            else
                tokens = await TextEditing.CheckSoundsAsync(File, model, language, source.Language, tokens, cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            if (!tokens.Any(t => t.Kind == EditTokenKind.Word))
            {
                ShowSettings();
                FooterNote = L.T("No speech was found in this file.");
                return;
            }
            ShowEditor(tokens, TextEditing.Recall(File, model, language, tokens.Count) ?? [], source.Language);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Edit by Text");
            ShowSettings();
            FooterNote = L.T(ex.Message);
        }
        finally
        {
            if (_work == cancel)
                _work = null;
            if (!_editing && IsLoaded)
                Sync();
        }
    }

    // ---------- the words ----------

    private void ShowEditor(IReadOnlyList<EditToken> tokens, HashSet<int> removed, string language)
    {
        _editing = true;
        _tokens = tokens;
        _plain = tokens.Select(t => t.Kind == EditTokenKind.Word ? TextCut.Plain(t.Text) : "").ToArray();
        _length = Math.Max(Info?.DurationSeconds ?? 0, tokens.Count > 0 ? tokens[^1].End : 0);
        _removed = removed;
        _history.Clear();
        _future.Clear();
        _found = [];
        _matches.Clear();
        _lit = -1;
        _settings.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Visible;
        ResetButton.Content = L.T("Start over");
        ResetButton.Visibility = Visibility.Visible;
        PrimaryButton.Content = L.T("Save");
        _text.Document = Write(tokens, language);
        _find.Text = "";
        Find();
        Changed(null);
        _text.CaretPosition = _text.Document.ContentStart;
        _text.Focus();
        if (Info is { HasVideo: true })
            _ = StillAsync(_snapshot ? 1 : tokens.FirstOrDefault(t => t.Kind == EditTokenKind.Word)?.Start ?? 0);
    }

    /// <summary>The words as a document: one run for each piece (its word, a sound, a pause), in paragraphs as it was said.</summary>
    private FlowDocument Write(IReadOnlyList<EditToken> tokens, string language)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(6, 4, 6, 4),
            FontFamily = UiFonts.Text,
            FontSize = 15,
            LineHeight = 26,
            FlowDirection = RightToLeft(language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Petal.Text");
        _runs = new Run[tokens.Count];
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
        int words = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            // The space after a piece is its own, so a word and its space go out together.
            var run = new Run(Shown(token) + " ") { Tag = i };
            if (token.Kind != EditTokenKind.Word)
                run.FontSize = 13;
            _runs[i] = run;
            paragraph.Inlines.Add(run);
            Paint(i);
            if (token.Kind == EditTokenKind.Word)
                words++;
            bool sentence = token.Kind == EditTokenKind.Word && token.Text.Length > 0 && ".?!؟…".Contains(token.Text[^1]);
            if (i < tokens.Count - 1 && words > 0 && (sentence && words >= ParagraphWords || token.Kind == EditTokenKind.Pause && token.Length >= ParagraphPause))
            {
                document.Blocks.Add(paragraph);
                paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
                words = 0;
            }
        }
        document.Blocks.Add(paragraph);
        return document;
    }

    /// <summary>A piece as it reads: the word, "[sound]", or a pause as its length.</summary>
    private static string Shown(EditToken token) => token.Kind switch
    {
        EditTokenKind.Sound => L.T("[sound]"),
        EditTokenKind.Pause => "[" + L.F("{0} s", Math.Round(token.Length, 1)) + "]",
        _ => token.Text,
    };

    /// <summary>A piece's look: struck through when taken out, lit while it plays, marked when found.</summary>
    private void Paint(int i)
    {
        var run = _runs[i];
        bool removed = _removed.Contains(i);
        run.TextDecorations = removed ? TextDecorations.Strikethrough : null;
        run.SetResourceReference(TextElement.ForegroundProperty,
            removed ? "Petal.TextDisabled" : _tokens[i].Kind == EditTokenKind.Word ? "Petal.Text" : "Petal.TextSecondary");
        if (i == _lit)
            run.SetResourceReference(TextElement.BackgroundProperty, "Petal.AccentSoft");
        else if (_found.Contains(i))
            run.Background = FoundBrush;
        else
            run.ClearValue(TextElement.BackgroundProperty);
    }

    private ContextMenu Menu()
    {
        var menu = new ContextMenu();
        _takeItem.Click += (_, _) => TakeOrPutBack(forward: true);
        var play = new MenuItem { Header = L.T("Play from here") };
        play.Click += (_, _) =>
        {
            StopPlayback();
            PlayOrStop();
        };
        var all = new MenuItem { Header = L.T("Put everything back") };
        all.Click += (_, _) => Change(set => set.Clear());
        var copy = new MenuItem { Header = L.T("Copy the edited text") };
        copy.Click += (_, _) => CopyEdited();
        menu.Items.Add(_takeItem);
        menu.Items.Add(play);
        menu.Items.Add(new Separator());
        menu.Items.Add(all);
        menu.Items.Add(copy);
        menu.Opened += (_, _) => all.IsEnabled = _removed.Count > 0;
        return menu;
    }

    /// <summary>The words left, as text.</summary>
    private void CopyEdited()
    {
        var words = Enumerable.Range(0, _tokens.Count).Where(i => _tokens[i].Kind == EditTokenKind.Word && !_removed.Contains(i)).Select(i => _tokens[i].Text);
        try
        {
            System.Windows.Clipboard.SetText(string.Join(' ', words));
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Info($"Couldn't copy the edited text: {ex.Message}");
        }
    }

    // ---------- where the cursor is ----------

    /// <summary>The piece at a place in the text: the one the cursor is in, or the one it is just before.</summary>
    private int TokenAt(TextPointer p)
    {
        if (_runs.Length == 0)
            return 0;
        int lo = 0, hi = _runs.Length - 1, at = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_runs[mid].ContentStart.CompareTo(p) <= 0)
            {
                at = mid;
                lo = mid + 1;
            }
            else
                hi = mid - 1;
        }
        // At the end of a piece (after its space) the cursor is before the next one.
        if (at + 1 < _runs.Length && _runs[at].ContentEnd.CompareTo(p) <= 0)
            at++;
        return at;
    }

    /// <summary>The pieces selected (any part of one counts), or the one at the cursor.</summary>
    private (int First, int Last)? Selection()
    {
        if (_runs.Length == 0)
            return null;
        var selection = _text.Selection;
        if (selection.IsEmpty)
        {
            int at = TokenAt(selection.Start);
            return (at, at);
        }
        // The first piece ending after the selection starts, the last starting before it ends.
        int lo = 0, hi = _runs.Length - 1, first = _runs.Length;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_runs[mid].ContentEnd.CompareTo(selection.Start) > 0)
            {
                first = mid;
                hi = mid - 1;
            }
            else
                lo = mid + 1;
        }
        lo = 0;
        hi = _runs.Length - 1;
        int last = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_runs[mid].ContentStart.CompareTo(selection.End) < 0)
            {
                last = mid;
                lo = mid + 1;
            }
            else
                hi = mid - 1;
        }
        return first <= last ? (first, last) : null;
    }

    /// <summary>The cursor or the selection moved: the button says what Delete would do; the picture follows the cursor.</summary>
    private void Moved()
    {
        if (!_editing || _runs.Length == 0)
            return;
        UpdateTake();
        if (!_text.Selection.IsEmpty)
            return;
        double at = _tokens[TokenAt(_text.Selection.Start)].Start;
        // Clicking a word while the edit plays plays it from there.
        if (IsPlaying)
            PlayFrom(at);
        else
            _ = StillAsync(at);
    }

    private void UpdateTake()
    {
        bool putBack = Selection() is { } s && Enumerable.Range(s.First, s.Last - s.First + 1).All(_removed.Contains);
        _take.Content = putBack ? L.T("Put back") : L.T("Take out");
        _takeItem.Header = _take.Content;
    }

    /// <summary>The picture at a moment, shown while the edit isn't playing (the latest asked for wins).</summary>
    private async Task StillAsync(double seconds)
    {
        if (Info is not { HasVideo: true })
            return;
        int ticket = ++_stillTicket;
        await Task.Delay(120);
        if (ticket != _stillTicket || IsPlaying)
            return;
        try
        {
            var picture = await MediaPreview.ExtractFrameAsync(File, TimeSpan.FromSeconds(Math.Clamp(seconds + 0.05, 0, Math.Max(0, Duration - 0.1))), 640);
            if (ticket == _stillTicket)
                _still.Source = await LoadAsync(picture);
        }
        catch (Exception ex)
        {
            Log.Info($"No picture for Edit by Text: {ex.Message}");
        }
    }

    // ---------- taking out ----------

    /// <summary>
    /// Takes the pieces selected out, or puts them back when they are all out. With nothing selected it is the word at the
    /// cursor (Delete) or before it (Backspace), and the cursor moves past it, so pressing again goes on to the next.
    /// </summary>
    private void TakeOrPutBack(bool forward)
    {
        if (Selection() is not { } range)
            return;
        var (first, last) = range;
        bool empty = _text.Selection.IsEmpty;
        if (empty && !forward && first > 0 && _text.Selection.Start.CompareTo(_runs[first].ContentStart) <= 0)
            first = last = first - 1;
        var pieces = Enumerable.Range(first, last - first + 1).ToList();
        bool putBack = pieces.All(_removed.Contains);
        Change(set =>
        {
            if (putBack)
                set.ExceptWith(pieces);
            else
                set.UnionWith(pieces);
        });
        if (empty)
            _text.CaretPosition = forward ? (last + 1 < _runs.Length ? _runs[last + 1].ContentStart : _runs[last].ContentEnd) : _runs[first].ContentStart;
    }

    private void Change(Action<HashSet<int>> change)
    {
        var before = new HashSet<int>(_removed);
        change(_removed);
        if (_removed.SetEquals(before))
            return;
        _history.Push(before);
        _future.Clear();
        Changed(before);
    }

    private void Undo()
    {
        if (_history.Count == 0)
            return;
        var before = _removed;
        _future.Push(before);
        _removed = _history.Pop();
        Changed(before);
    }

    private void Redo()
    {
        if (_future.Count == 0)
            return;
        var before = _removed;
        _history.Push(before);
        _removed = _future.Pop();
        Changed(before);
    }

    /// <summary>What was taken out changed: the pieces' look, the stretches kept, the counts, and what is kept for next time.</summary>
    private void Changed(IReadOnlySet<int>? before)
    {
        if (before is not null)
            foreach (var i in before.Union(_removed).Where(i => before.Contains(i) != _removed.Contains(i)).ToList())
                Paint(i);
        if (IsPlaying)
            StopPlayback();
        _kept = TextCut.Keep(_tokens, _removed, _length);
        double after = _kept.Sum(k => k.End - k.Start);
        int fillers = TextCut.Fillers(_tokens).Count(i => !_removed.Contains(i));
        int pauses = TextCut.LongPauses(_tokens, LongPause).Count(i => !_removed.Contains(i));
        _fillers.Content = fillers > 0 ? L.P(fillers, "Take out {0} filler", "Take out {0} fillers") : L.T("No fillers left");
        _fillers.IsEnabled = fillers > 0;
        _pauses.Content = pauses > 0 ? L.P(pauses, "Shorten {0} long pause", "Shorten {0} long pauses") : L.T("No long pauses left");
        _pauses.IsEnabled = pauses > 0;
        _undo.IsEnabled = _history.Count > 0;
        _redo.IsEnabled = _future.Count > 0;
        PrimaryButton.IsEnabled = _removed.Count > 0 && _kept.Count > 0;
        FooterNote = _removed.Count == 0 ? L.F("{0} long", Clock(_length))
            : _kept.Count == 0 ? L.T("Nothing would be left of this recording.")
            : L.F("{0} → {1}, {2} shorter", Clock(_length), Clock(after), Clock(_length - after));
        UpdateTake();
        if (!_snapshot && _heardWith.Model is not null)
            TextEditing.Remember(File, _heardWith.Model, _heardWith.Language, _tokens.Count, _removed);
    }

    // ---------- finding ----------

    /// <summary>The words matching what is typed in the find box, in order (a pause or a sound between them doesn't count).</summary>
    private void Find()
    {
        _findHint.Visibility = _find.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var old = _found;
        _found = [];
        _matches.Clear();
        _match = -1;
        var wanted = _find.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(TextCut.Plain).Where(w => w.Length > 0).ToArray();
        if (wanted.Length > 0)
        {
            var words = Enumerable.Range(0, _tokens.Count).Where(i => _plain[i].Length > 0).ToList();
            for (int w = 0; w + wanted.Length <= words.Count; w++)
            {
                bool match = true;
                for (int k = 0; k < wanted.Length && match; k++)
                    match = _plain[words[w + k]] == wanted[k];
                if (!match)
                    continue;
                _matches.Add((words[w], words[w + wanted.Length - 1]));
                for (int k = 0; k < wanted.Length; k++)
                    _found.Add(words[w + k]);
            }
        }
        foreach (var i in old.Union(_found).ToList())
            if (i < _runs.Length)
                Paint(i);
        _foundCount.Text = wanted.Length == 0 ? "" : _matches.Count == 0 ? L.T("Not found") : L.P(_matches.Count, "{0} match", "{0} matches");
        _takeFound.Visibility = _matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the next match (or the one before), selected in the text.</summary>
    private void Next(int step)
    {
        if (_matches.Count == 0)
            return;
        _match = _match < 0 ? (step > 0 ? 0 : _matches.Count - 1) : ((_match + step) % _matches.Count + _matches.Count) % _matches.Count;
        var (first, last) = _matches[_match];
        _text.Selection.Select(_runs[first].ContentStart, _runs[last].ContentEnd);
        Reveal(first);
        _foundCount.Text = L.F("{0} of {1}", _match + 1, _matches.Count);
    }

    /// <summary>Scrolls the text so a piece shows, a third of the way down when it has to move.</summary>
    private void Reveal(int i)
    {
        var rect = _runs[i].ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty)
            return;
        if (rect.Top < 0 || rect.Bottom > _text.ViewportHeight)
            _text.ScrollToVerticalOffset(Math.Max(0, _text.VerticalOffset + rect.Top - _text.ViewportHeight / 3));
    }

    // ---------- playing the edit ----------

    private void PlayOrStop()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }
        if (_kept.Count == 0 || _runs.Length == 0)
            return;
        PlayFrom(_tokens[TokenAt(_text.Selection.Start)].Start);
    }

    /// <summary>Plays the edit from a moment of the original: from the next stretch kept when that moment was cut out.</summary>
    private void PlayFrom(double seconds)
    {
        int k = _kept.FindIndex(r => r.End > seconds + 0.05);
        if (k < 0)
            (k, seconds) = (0, 0);
        _range = k;
        _settling = 25;
        StartPlayback(Math.Max(seconds, _kept[k].Start), Duration);
        if (PlaybackUnavailable)
            FooterNote = L.T("Windows can't play this recording here; it is cut all the same.");
    }

    protected override void PlaybackPosition(double seconds)
    {
        if (_range < 0 || _range >= _kept.Count)
            return;
        var (start, end) = _kept[_range];
        if (_settling > 0)
        {
            // Until the player has got to the stretch it was sent to (a second at most).
            if (seconds < start - 0.3 || seconds > end + 0.3)
            {
                _settling--;
                return;
            }
            _settling = 0;
        }
        // On to the next stretch kept, a little early: the player is asked every 40 ms.
        if (seconds >= end - 0.03)
        {
            if (++_range >= _kept.Count)
            {
                StopPlayback();
                return;
            }
            _settling = 25;
            StartPlayback(_kept[_range].Start, Duration);
            return;
        }
        Light(TokenAtTime(seconds));
    }

    protected override void PlaybackStateChanged(bool playing)
    {
        _play.Content = ThemedIcon(playing ? "pause" : "play", 16);
        if (playing)
            return;
        int stopped = _lit;
        Light(-1);
        _range = -1;
        // The cursor is left where the edit stopped; the picture follows it.
        if (stopped >= 0 && _runs.Length > stopped)
            _text.CaretPosition = _runs[stopped].ContentStart;
        if (PlaybackUnavailable)
            FooterNote = L.T("Windows can't play this recording here; it is cut all the same.");
    }

    /// <summary>The piece being said at a moment of the original.</summary>
    private int TokenAtTime(double seconds)
    {
        int lo = 0, hi = _tokens.Count - 1, at = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_tokens[mid].Start <= seconds)
            {
                at = mid;
                lo = mid + 1;
            }
            else
                hi = mid - 1;
        }
        return at;
    }

    private void Light(int i)
    {
        if (i == _lit)
            return;
        int old = _lit;
        _lit = i;
        if (old >= 0 && old < _runs.Length)
            Paint(old);
        if (i >= 0 && i < _runs.Length)
        {
            Paint(i);
            Reveal(i);
        }
    }

    protected override void OnKeys(object sender, KeyEventArgs e)
    {
        if (!_editing || e.Handled)
            return;
        var keys = Keyboard.Modifiers;
        if (keys == ModifierKeys.Control && e.Key == Key.F)
        {
            _find.Focus();
            _find.SelectAll();
            e.Handled = true;
            return;
        }
        // Typing in the find box is typing.
        if (_find.IsKeyboardFocusWithin)
            return;
        switch (e.Key)
        {
            case Key.Delete when keys == ModifierKeys.None:
                TakeOrPutBack(forward: true);
                break;
            case Key.Back when keys == ModifierKeys.None:
                TakeOrPutBack(forward: false);
                break;
            case Key.Space when keys == ModifierKeys.None:
                PlayOrStop();
                break;
            case Key.Z when keys == ModifierKeys.Control:
                Undo();
                break;
            case Key.Z when keys == (ModifierKeys.Control | ModifierKeys.Shift):
            case Key.Y when keys == ModifierKeys.Control:
                Redo();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---------- saving ----------

    private void Save()
    {
        if (_removed.Count == 0 || _kept.Count == 0)
            return;
        StopPlayback();
        var kept = _kept.ToList();
        var output = OutputNaming.ForTool(File, Tool);
        var options = new TextCutOptions { Settings = Quality };
        var file = File;
        Run("Cutting by the words", "Edit saved", async (p, ct) => (IReadOnlyList<string>)[await TextCut.RenderAsync(file, output, kept, options, p, ct)]);
    }

    // ---------- for the design snapshots ----------

    /// <summary>The words shown as if heard, some taken out, for the design snapshots.</summary>
    internal void ShowForSnapshot(IReadOnlyList<EditToken> tokens, IEnumerable<int> removed, string language)
    {
        _snapshot = true;
        ShowEditor(tokens, removed.ToHashSet(), language);
    }

    /// <summary>The listening stage, as if part of the way through, for the design snapshots.</summary>
    internal void ShowListeningForSnapshot(double share, double length)
    {
        _snapshot = true;
        _work = new CancellationTokenSource();
        _settings.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Visible;
        _stage.Text = L.T("Listening to the video…");
        Fill(share);
        _detail.Text = L.F("{0} of {1} heard", Clock(share * length), Clock(length));
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = L.T("Listening…");
        ResetButton.Content = L.T("Cancel");
        ResetButton.Visibility = Visibility.Visible;
        FooterNote = "";
    }
}
