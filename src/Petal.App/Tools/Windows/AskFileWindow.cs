using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;
using Petal.Core.Formats;
using Petal.Core.Models;
using Petal.Core.Tools;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Pdf;
using Petal.Recipes.Questions;
using Path = System.IO.Path;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Ask a File: questions about a document or a recording, answered from its own words, each answer with where it comes
/// from (the page, the slide, the rows, the minute). Clicking where shows the page with the words marked, plays the
/// recording from there, or shows the passage. Without the answer model the parts of the file that speak to the question
/// are shown. See <see cref="AskFile"/>.
/// </summary>
public sealed class AskFileWindow : ToolWindow
{
    private static readonly ModelInfo[] Speech = [ModelCatalog.WhisperFast, ModelCatalog.WhisperBalanced, ModelCatalog.WhisperAccurate];

    /// <summary>How wide a page is shown (the card's width).</summary>
    private const double PageWidth = 556;

    private static readonly Brush Mark = Frozen(Color.FromArgb(0x5C, 0xFF, 0xC1, 0x07));

    private readonly string _file;
    private readonly bool _recording;
    private readonly bool _video;

    // ---------- reading ----------
    private readonly ModelPanel _finding;
    private readonly StackPanel _reading = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _stage = new();
    private readonly TextBlock _detail = new();
    private readonly Border _track = new() { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Width = 0 };
    private double _share;

    // ---------- asking ----------
    private readonly StackPanel _asking = new() { Visibility = Visibility.Collapsed };
    private readonly Grid _player = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };
    private readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private double _playEnd;
    private readonly StackPanel _talk = new();
    private readonly ScrollViewer _scroll = new() { MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _empty;
    private readonly StackPanel _answeringBox = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly ModelPanel _answering = new(AskFile.AnsweringModels);
    private readonly TextBlock _about = Text("", "Petal.Caption");
    private readonly TextBlock _answeringNote;
    private readonly TextBox _question = new() { MinHeight = 34, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 6, 8, 6) };
    private readonly TextBlock _hint;

    private FileKnowledge? _knowledge;
    private CancellationTokenSource? _work;
    private bool _busy;

    public AskFileWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 620)
    {
        _file = file;
        var format = Formats.FromPath(file);
        _recording = format is not null && AskFile.IsRecording(format);
        _video = format?.Family == FormatFamily.Video;

        var name = Text(Path.GetFileName(file), "Petal.Section");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        Body.Children.Add(name);
        Body.Children.Add(_about);

        // What finding needs: the passage model, and for a recording Whisper.
        var whisper = Speech[Array.FindLastIndex(Speech, ModelStore.IsAvailable) is >= 0 and var best ? best : 1];
        _finding = new ModelPanel(_recording ? [.. AskFile.FindingModels, whisper] : AskFile.FindingModels);
        _finding.Ready += () => _ = ReadAsync();
        Body.Children.Add(_finding);

        _stage.Style = (Style)FindResource("Petal.Section");
        _stage.Margin = new Thickness(0, 14, 0, 0);
        _detail.Style = (Style)FindResource("Petal.Caption");
        _detail.Margin = new Thickness(0, 6, 0, 0);
        _track.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _fill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        _track.Child = _fill;
        _track.SizeChanged += (_, _) => Fill(_share);
        _reading.Children.Add(_stage);
        _reading.Children.Add(_track);
        _reading.Children.Add(_detail);
        _reading.Children.Add(Text("Read once and kept, so asking again later is quick.", "Petal.Caption").WithMarginT(12));
        Body.Children.Add(_reading);

        // The recording's player (its picture when it has one), above the answers.
        _player.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(8) });
        _player.Children.Add(_media);
        _player.Height = _video ? 220 : 0;
        if (_recording)
            _media.Source = new Uri(file);
        _media.MediaFailed += (_, e) => Log.Info($"Ask a File can't play {file}: {e.ErrorException?.Message}");
        _clock.Tick += (_, _) =>
        {
            if (_media.Position.TotalSeconds >= _playEnd)
                Stop();
        };
        _asking.Children.Add(_player);
        _empty = Text("Ask your first question below. For example: what does it say about prices? Where are the dates? What was decided?", "Petal.Caption");
        _empty.Margin = new Thickness(0, 12, 0, 12);
        _talk.Children.Add(_empty);
        _scroll.Content = _talk;
        _scroll.Margin = new Thickness(0, 12, 0, 0);
        _asking.Children.Add(_scroll);
        _answeringNote = Text("Written answers come from an AI model on this PC. Until it is here you see the parts of the file that speak to your question.", "Petal.Caption").WithMarginT(8);
        _answeringBox.Children.Add(_answeringNote);
        _answeringBox.Children.Add(_answering);
        _answering.Ready += SyncAnswering;
        _answering.Switched += SyncAnswering;
        _asking.Children.Add(_answeringBox);
        _hint = Text("Ask a question about this file", "Petal.Caption");
        _hint.IsHitTestVisible = false;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.Margin = new Thickness(10, 0, 10, 0);
        _question.TextChanged += (_, _) =>
        {
            _hint.Visibility = _question.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _question.FlowDirection = AskFile.LanguageOf([_question.Text]) == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            SyncAsk();
        };
        _asking.Children.Add(new Grid { Margin = new Thickness(0, 12, 0, 0), Children = { _question, _hint } });
        Body.Children.Add(_asking);

        PrimaryButton.Content = L.T("Ask");
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += async (_, _) => await AskAsync();
        ResetButton.Content = L.T("Open the File");
        ResetButton.Click += (_, _) => Open(null);
        Loaded += (_, _) =>
        {
            if (_finding.IsReady && !_snapshot)
                _ = ReadAsync();
        };
        Closed += (_, _) =>
        {
            _work?.Cancel();
            _clock.Stop();
            _media.Close();
        };
        SyncAnswering();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Fill(double share)
    {
        _share = Math.Clamp(share, 0, 1);
        _fill.Width = Math.Max(0, _track.ActualWidth * _share);
    }

    private void SyncAnswering()
    {
        _answeringNote.Visibility = AskFile.CanAnswer ? Visibility.Collapsed : Visibility.Visible;
        _about.Text = Petal.Recipes.Cloud.CloudAi.CanWrite
            ? L.F("Ask anything about it: the answer comes from its own words, with where it says so. Your question and those words go to {0}, on your own key.", Petal.Recipes.Cloud.CloudAi.Name(Petal.Recipes.Cloud.CloudAi.Writing!.Provider))
            : L.T("Ask anything about it: the answer comes from its own words, with where it says so. All on this PC.");
    }

    private void SyncAsk() => PrimaryButton.IsEnabled = _knowledge is not null && !_busy && _question.Text.Trim().Length > 1;

    // ---------- reading ----------

    private async Task ReadAsync()
    {
        if (_work is not null || _knowledge is not null)
            return;
        var cancel = _work = new CancellationTokenSource();
        _finding.Visibility = Visibility.Collapsed;
        _reading.Visibility = Visibility.Visible;
        _stage.Text = _recording ? L.T("Listening to the recording…") : L.T("Reading the file…");
        _detail.Text = "";
        Fill(0);
        try
        {
            var whisper = _recording ? Speech.Reverse().Select(ModelStore.Find).FirstOrDefault(p => p is not null) : null;
            var progress = new Progress<double>(p =>
            {
                if (cancel.IsCancellationRequested)
                    return;
                Fill(p);
                _detail.Text = L.F("{0}% done", (int)Math.Round(p * 100));
            });
            var file = _file;
            _knowledge = await Task.Run(() => AskFile.ReadAsync(file, whisper, progress, cancel.Token), cancel.Token);
            _reading.Visibility = Visibility.Collapsed;
            _asking.Visibility = Visibility.Visible;
            ResetButton.Visibility = Visibility.Visible;
            FooterNote = Describe(_knowledge);
            _question.Focus();
            SyncAsk();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ask a File");
            _stage.Text = L.T("This file couldn't be read.");
            _detail.Text = L.T(ex.Message);
        }
        finally
        {
            if (_work == cancel)
                _work = null;
        }
    }

    /// <summary>What was read: "70 pages", "12 slides", "3 sheets", "18 parts", "42:10 long".</summary>
    private static string Describe(FileKnowledge knowledge)
    {
        var places = knowledge.Passages.Select(p => p.Place).ToList();
        return places[0].Kind switch
        {
            PlaceKind.Page => L.P(places.Max(p => p.Number), "{0} page", "{0} pages"),
            PlaceKind.Slide => L.P(places.Max(p => p.Number), "{0} slide", "{0} slides"),
            PlaceKind.Rows => L.P(places.Select(p => p.Name).Distinct().Count(), "{0} sheet", "{0} sheets"),
            PlaceKind.Time => L.F("{0} long", AskFile.Clock(places.Max(p => p.End))),
            _ => L.P(places.Select(p => p.Number).Distinct().Count(), "{0} part", "{0} parts"),
        };
    }

    // ---------- asking ----------

    /// <summary>A question asked, with its answer and where it comes from.</summary>
    private sealed class Card
    {
        public required StackPanel Root { get; init; }
        public required TextBlock Answer { get; init; }
        public required WrapPanel Places { get; init; }
        public required Border Preview { get; init; }
        public required string Question { get; init; }
        public string Said { get; set; } = "";
        public Passage? Shown { get; set; }
    }

    private async Task AskAsync()
    {
        var question = _question.Text.Trim();
        if (_knowledge is not { } knowledge || _busy || question.Length < 2)
            return;
        _busy = true;
        SyncAsk();
        _question.Text = "";
        var card = NewCard(question);
        var cancel = _work = new CancellationTokenSource();
        try
        {
            var found = await Task.Run(() => AskFile.Find(knowledge, question), cancel.Token);
            if (!AskFile.CanAnswer)
            {
                // Without the answer model: the parts that speak to it.
                card.Answer.Text = L.T("The parts of the file that speak to your question:");
                card.Answer.Style = (Style)FindResource("Petal.Caption");
                ShowPlaces(card, found.Take(3).ToList(), question);
                return;
            }
            card.Answer.Text = L.T("Writing the answer…");
            card.Answer.Style = (Style)FindResource("Petal.Caption");
            var answer = await Task.Run(() => AskFile.AnswerAsync(question, found, text => Dispatcher.BeginInvoke(() =>
            {
                if (text.Length == 0)
                    return;
                card.Answer.Style = (Style)FindResource("Petal.Body");
                SetText(card.Answer, text);
            }), cancel.Token), cancel.Token);
            if (answer.NotFound)
            {
                card.Answer.Style = (Style)FindResource("Petal.Body");
                card.Answer.Text = L.T("The file doesn't seem to say. The closest parts:");
                ShowPlaces(card, found.Take(3).ToList(), question);
            }
            else
            {
                card.Answer.Style = (Style)FindResource("Petal.Body");
                SetText(card.Answer, answer.Text);
                card.Said = answer.Text;
                ShowPlaces(card, answer.Sources, answer.Text);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ask a File");
            card.Answer.Style = (Style)FindResource("Petal.Caption");
            card.Answer.Text = L.T(ex.Message);
        }
        finally
        {
            if (_work == cancel)
                _work = null;
            _busy = false;
            SyncAsk();
            if (IsLoaded)
                _question.Focus();
        }
    }

    private static void SetText(TextBlock block, string text)
    {
        block.Text = text;
        block.FlowDirection = AskFile.LanguageOf([text]) == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        block.TextAlignment = TextAlignment.Left;
    }

    private Card NewCard(string question)
    {
        _empty.Visibility = Visibility.Collapsed;
        var asked = Text(question, "Petal.Body");
        asked.FontWeight = FontWeights.SemiBold;
        SetText(asked, question);
        var answer = Text("Finding…", "Petal.Caption");
        answer.Margin = new Thickness(0, 6, 0, 0);
        var places = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var preview = new Border { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        var root = new StackPanel { Children = { asked, answer, places, preview } };
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 0, 0, 10), Child = root };
        box.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _talk.Children.Add(box);
        _scroll.UpdateLayout();
        _scroll.ScrollToEnd();
        return new Card { Root = root, Answer = answer, Places = places, Preview = preview, Question = question };
    }

    /// <summary>The places an answer comes from, as buttons; the first is shown.</summary>
    private void ShowPlaces(Card card, IReadOnlyList<Passage> passages, string said)
    {
        card.Said = said;
        card.Places.Children.Clear();
        foreach (var passage in passages.DistinctBy(p => (p.Place.Kind, p.Place.Number, p.Place.Name)))
        {
            var button = new Button { Content = Where(passage.Place), Margin = new Thickness(0, 6, 8, 0), Padding = new Thickness(10, 3, 10, 3), Tag = passage, Cursor = Cursors.Hand };
            button.SetResourceReference(BackgroundProperty, "Petal.AccentSoft");
            button.Click += async (_, _) => await ShowAsync(card, passage);
            System.Windows.Automation.AutomationProperties.SetName(button, L.F("Show {0}", Where(passage.Place)));
            card.Places.Children.Add(button);
        }
        if (passages.Count > 0)
            _ = ShowAsync(card, passages[0]);
    }

    /// <summary>Where a passage is, as the reader sees it.</summary>
    internal static string Where(PlacedText place) => place.Kind switch
    {
        PlaceKind.Page => L.F("Page {0}", place.Number),
        PlaceKind.Slide => L.F("Slide {0}", place.Number),
        PlaceKind.Rows => L.F("{0}, rows {1}–{2}", place.Name, place.Number, place.Last),
        PlaceKind.Time => AskFile.Clock(place.Start),
        _ => place.Name.Length > 0 ? place.Name : L.F("Part {0}", place.Number),
    };

    /// <summary>
    /// A place shown under its answer: the page with the words marked (when the file has pages), the recording played from
    /// there, and the sentence it says so in. Shown again, it is put away.
    /// </summary>
    private async Task ShowAsync(Card card, Passage passage)
    {
        if (card.Shown == passage && card.Preview.Visibility == Visibility.Visible)
        {
            card.Preview.Visibility = Visibility.Collapsed;
            Stop();
            return;
        }
        card.Shown = passage;
        foreach (Button b in card.Places.Children)
            b.FontWeight = ReferenceEquals(b.Tag, passage) ? FontWeights.SemiBold : FontWeights.Normal;
        var quote = AskFile.Quote(passage, card.Said.Length > 0 ? card.Said : card.Question);
        var content = new StackPanel();
        var marked = AskFile.LanguageOf([quote]) == "ar" ? "«" + quote + "»" : "“" + quote + "”";
        var said = Text(marked, "Petal.Body");
        SetText(said, marked);
        said.FontStyle = FontStyles.Italic;
        content.Children.Add(said);
        card.Preview.Child = content;
        card.Preview.Visibility = Visibility.Visible;

        if (passage.Place.Kind == PlaceKind.Time)
        {
            Play(passage.Place.Start, passage.Place.End);
            var stop = new Button { Content = L.T("Stop"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
            stop.Click += (_, _) => Stop();
            var again = new Button { Content = L.F("Play from {0}", AskFile.Clock(passage.Place.Start)), Margin = new Thickness(0, 8, 8, 0), Padding = new Thickness(10, 3, 10, 3) };
            again.Click += (_, _) => Play(passage.Place.Start, passage.Place.End);
            content.Children.Add(new WrapPanel { Children = { again, stop } });
            return;
        }
        if ((_knowledge?.PagesPdf ?? _snapshotPdf) is not { } pdf || passage.Place.Kind is not (PlaceKind.Page or PlaceKind.Slide))
            return;
        try
        {
            int number = passage.Place.Number;
            var (picture, marks) = await Task.Run(() => (PdfInfo.RenderPagePng(pdf, number - 1, (int)(PageWidth * 1.5)), DocumentPlaces.Marks(pdf, number, quote)));
            if (card.Shown != passage)
                return;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(picture);
            image.EndInit();
            image.Freeze();
            double width = PageWidth, height = width * image.PixelHeight / Math.Max(1, image.PixelWidth);
            var page = new Grid { Width = width, Height = height };
            page.Children.Add(new Image { Source = image, Width = width, Height = height, Stretch = Stretch.Fill });
            var overlay = new Canvas { IsHitTestVisible = false };
            foreach (var (left, top, right, bottom) in marks)
            {
                var box = new Rectangle { Width = (right - left) * width + 4, Height = (bottom - top) * height + 2, Fill = Mark, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(box, left * width - 2);
                Canvas.SetTop(box, top * height - 1);
                overlay.Children.Add(box);
            }
            page.Children.Add(overlay);
            var frame = new Border { BorderThickness = new Thickness(1), Child = page, Cursor = Cursors.Hand, ToolTip = L.T("Open the File") };
            frame.SetResourceReference(Border.BorderBrushProperty, "Petal.Divider");
            frame.MouseLeftButtonUp += (_, _) => Open(number);
            var view = new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = frame, Margin = new Thickness(0, 10, 0, 0) };
            content.Children.Add(view);
            // The marked words in sight.
            if (marks.Count > 0)
                view.Loaded += (_, _) => view.ScrollToVerticalOffset(Math.Max(0, marks.Min(m => m.Top) * height - 60));
        }
        catch (Exception ex)
        {
            Log.Info($"Ask a File couldn't show page {passage.Place.Number}: {ex.Message}");
        }
    }

    private void Play(double from, double to)
    {
        if (!_recording)
            return;
        _player.Visibility = Visibility.Visible;
        _playEnd = to + 0.5;
        _media.Position = TimeSpan.FromSeconds(Math.Max(0, from - 0.3));
        _media.Play();
        _clock.Start();
    }

    private void Stop()
    {
        _clock.Stop();
        _media.Pause();
        if (_video)
            _player.Visibility = Visibility.Collapsed;
    }

    /// <summary>The file in the program Windows opens it with (a PDF reader can't be told the page, so it opens at the start).</summary>
    private void Open(int? page)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_file) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Info($"Couldn't open {_file}{(page is { } p ? $" at page {p}" : "")}: {ex.Message}");
        }
    }

    // ---------- for the design snapshots ----------

    private bool _snapshot;
    private string? _snapshotPdf;

    /// <summary>Questions shown as if asked, for the design snapshots: each with its answer and where it comes from.</summary>
    internal void ShowForSnapshot(IReadOnlyList<(string Question, string Answer, IReadOnlyList<Passage> Sources)> asked, string about, string? pdf)
    {
        _snapshot = true;
        _snapshotPdf = pdf;
        _finding.Visibility = Visibility.Collapsed;
        _asking.Visibility = Visibility.Visible;
        ResetButton.Visibility = Visibility.Visible;
        FooterNote = about;
        foreach (var (question, answer, sources) in asked)
        {
            var card = NewCard(question);
            card.Answer.Style = (Style)FindResource("Petal.Body");
            SetText(card.Answer, answer);
            ShowPlaces(card, sources, answer);
        }
        _question.Text = Localization.L.IsArabic ? "ما المدة المسموح بها؟" : "How long is the warranty?";
        PrimaryButton.IsEnabled = true;
    }

    /// <summary>The reading stage, part of the way through, for the design snapshots.</summary>
    internal void ShowReadingForSnapshot(double share)
    {
        _snapshot = true;
        _finding.Visibility = Visibility.Collapsed;
        _reading.Visibility = Visibility.Visible;
        _stage.Text = L.T("Reading the file…");
        Fill(share);
        _detail.Text = L.F("{0}% done", (int)Math.Round(share * 100));
    }
}
