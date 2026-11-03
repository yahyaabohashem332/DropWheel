using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
using Petal.Recipes.Translation;
using Path = System.IO.Path;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Make Shorts: a long video's best moments as short clips ready to post. The video is listened to on this PC (once:
/// it opens again at once), its moments found and ranked, then shown to be checked (played, retitled, ticked) before
/// they are made: tall, framed on the speaker, the words burned in as they are said, a headline over the first seconds,
/// the pauses cut and the sound at the level the apps play at. See <see cref="ShortsMaker"/>.
/// </summary>
public sealed class MakeShortsWindow : MediaToolWindow
{
    private static readonly ModelInfo[] Models = [ModelCatalog.WhisperFast, ModelCatalog.WhisperBalanced, ModelCatalog.WhisperAccurate];

    /// <summary>How many more moments can be shown beyond those asked for.</summary>
    private const int Spare = 4;

    // ---------- the choices ----------
    private readonly StackPanel _settings = new();
    private readonly Grid _preview = new() { Height = 176, ClipToBounds = true };
    private readonly Image _frame = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Segmented<int> _count;
    private readonly Segmented<ShortLength> _length;
    private readonly Segmented<ShortShape> _shape;
    private readonly Segmented<ShortFraming> _framing;
    private readonly Segmented<string> _captions;
    private readonly WrapPanel _colours = new();
    private readonly Tools.Parts.CustomColourChip _ownColour;
    private string _highlight = AnimatedCaptionsWindow.Colours[0].Hex;
    private readonly LanguageChoice _into;
    private readonly CheckBox _headline = new() { IsChecked = true, Margin = new Thickness(0, 0, 18, 0) };
    private readonly CheckBox _pauses = new() { IsChecked = true };
    private readonly Segmented<int> _accuracy;
    private readonly ModelPanel _model;
    private readonly ModelPanel _helpers = new(Translator.Models);
    private readonly ComboBox _language;

    // ---------- listening ----------
    private readonly StackPanel _working = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _stage = new();
    private readonly TextBlock _detail = new();
    private readonly Bar _bar = new();

    // ---------- the moments found ----------
    private readonly StackPanel _review = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _found = new();
    private readonly Grid _player = new() { Height = 200, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _list = new();
    private readonly Button _more = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly List<Card> _cards = [];

    private ShortsSource? _source;
    private (string Model, string Language) _heardWith;
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _titling;
    private Card? _playing;
    private bool _reviewing;

    /// <summary>A thin bar in the theme's colours, filled as the listening goes on.</summary>
    private sealed class Bar : Border
    {
        private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Width = 0 };
        private double _value;

        public Bar()
        {
            Height = 6;
            CornerRadius = new CornerRadius(3);
            Margin = new Thickness(0, 12, 0, 0);
            SetResourceReference(BackgroundProperty, "Petal.Control");
            _fill.SetResourceReference(BackgroundProperty, "Petal.Accent");
            Child = _fill;
            SizeChanged += (_, _) => Draw();
        }

        public double Value
        {
            get => _value;
            set
            {
                _value = Math.Clamp(value, 0, 1);
                Draw();
            }
        }

        private void Draw() => _fill.Width = Math.Max(0, ActualWidth * _value);
    }

    /// <summary>A moment shown for checking: ticked to be made, its title, its picture.</summary>
    private sealed class Card
    {
        public required Moment Moment { get; init; }
        public required Border Root { get; init; }
        public required CheckBox Include { get; init; }
        public required TextBox Title { get; init; }
        public required Image Picture { get; init; }
        public required Border PlayMark { get; init; }
        public bool Edited { get; set; }
        public bool Writing { get; set; }
    }

    public MakeShortsWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at, 520)
    {
        // The choices.
        _preview.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(10) });
        _preview.Children.Add(_frame);
        _preview.Children.Add(_overlay);
        _settings.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _preview, Margin = new Thickness(0, 0, 0, 12) });
        _count = Segments([(3, "3"), (5, "5"), (8, "8")], 5);
        _length = Segments([(ShortLength.Quick, "Up to 30 s"), (ShortLength.Standard, "30–60 s"), (ShortLength.Long, "60–90 s")], ShortLength.Standard);
        _shape = Segments([(ShortShape.Tall, "Tall 9:16"), (ShortShape.Square, "Square"), (ShortShape.Original, "As filmed")], ShortShape.Tall);
        _framing = Segments([(ShortFraming.Speaker, "Follow the speaker"), (ShortFraming.Whole, "Whole picture")], ShortFraming.Speaker);
        _captions = Segments([("word", "Word by word"), ("one", "One word"), ("lines", "Lines"), ("none", "None")], "word");
        _settings.Children.Add(Row("Clips", _count, 120, 0));
        _settings.Children.Add(Row("Length", _length, 120, 8));
        _settings.Children.Add(Row("Shape", _shape, 120, 8));
        _settings.Children.Add(Row("Framing", _framing, 120, 8));
        _settings.Children.Add(Row("Captions", _captions, 120, 8));
        foreach (var (hex, name) in AnimatedCaptionsWindow.Colours)
        {
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Cursor = Cursors.Hand,
                ToolTip = L.T(name),
                Tag = hex,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _highlight = hex;
                DrawPreview();
            };
            _colours.Children.Add(swatch);
        }
        _ownColour = new Tools.Parts.CustomColourChip(32, () => Tools.Parts.ColourMath.TryParseHex(_highlight, out var c) ? c : Colors.White);
        _ownColour.Changing += c =>
        {
            _highlight = Tools.Parts.ColourMath.Hex(c);
            DrawPreview();
        };
        _colours.Children.Add(_ownColour);
        _settings.Children.Add(Row("Lit word", _colours, 120, 8));
        _into = LanguageChoice.ForWords();
        _settings.Children.Add(Row("Words in", _into, 120, 8));
        _headline.Content = L.T("Headline at the start");
        _pauses.Content = L.T("Cut the pauses");
        _settings.Children.Add(Row("", new WrapPanel { Children = { _headline, _pauses } }, 120, 10));

        int preferred = Array.FindLastIndex(Models, ModelStore.IsAvailable) is >= 0 and var found ? found : 1;
        _accuracy = Segments([(0, "Fast"), (1, "Balanced"), (2, "Accurate")], preferred);
        _settings.Children.Add(Row("Accuracy", _accuracy, 120, 14));
        var languages = new List<(string, string)> { ("auto", "Detect automatically") };
        languages.AddRange(Transcriber.Languages.Select(code => (code, NativeName(code))));
        _language = Choice(languages, "auto");
        _settings.Children.Add(Row("Spoken in", _language, 120, 8));
        _model = new ModelPanel(Models[preferred]);
        _settings.Children.Add(_model);
        _settings.Children.Add(_helpers);
        Body.Children.Add(_settings);

        // Listening.
        _stage.Style = (Style)FindResource("Petal.Section");
        _detail.Style = (Style)FindResource("Petal.Caption");
        _detail.Margin = new Thickness(0, 6, 0, 0);
        _detail.TextWrapping = TextWrapping.Wrap;
        _working.Children.Add(_stage);
        _working.Children.Add(_bar);
        _working.Children.Add(_detail);
        _working.Children.Add(Text("It all happens on this PC; nothing is uploaded. A video heard once opens again at once.", "Petal.Caption").WithMarginT(14));
        Body.Children.Add(_working);

        // The moments found.
        _found.Style = (Style)FindResource("Petal.Section");
        _review.Children.Add(_found);
        _review.Children.Add(Text("Best first. Play one to check it, change a title, untick any you don't want.", "Petal.Caption").WithMarginT(4));
        _player.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(10) });
        AttachPlayer(_player);
        _review.Children.Add(_player);
        _review.Children.Add(new ScrollViewer { MaxHeight = 430, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list });
        _more.Click += (_, _) => ShowMore();
        _review.Children.Add(_more);
        Body.Children.Add(_review);

        foreach (var changed in new Action<Action>[]
                 {
                     a => _shape.Changed += _ => a(), a => _framing.Changed += _ => a(), a => _captions.Changed += _ => a(), a => _into.Changed += _ => a(),
                     a => _headline.Click += (_, _) => a(),
                 })
            changed(DrawPreview);
        _accuracy.Changed += i => { _model.Model = Models[i]; Sync(); };
        _model.Ready += Sync;
        _helpers.Ready += Sync;
        _into.Changed += _ => Sync();
        _language.SelectionChanged += (_, _) => Sync();
        _shape.Changed += _ => _framing.IsEnabled = _shape.Value != ShortShape.Original;
        _preview.SizeChanged += (_, _) => DrawPreview();
        _frame.SizeChanged += (_, _) => DrawPreview();

        PrimaryButton.Click += async (_, _) =>
        {
            if (_reviewing)
                Make();
            else
                await FindAsync();
        };
        ResetButton.Click += (_, _) =>
        {
            // While listening it cancels; once the moments are shown it goes back to the choices.
            _work?.Cancel();
            ShowSettings();
        };
        Closed += (_, _) =>
        {
            _work?.Cancel();
            _titling?.Cancel();
        };
        ShowSettings();
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

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        try
        {
            var image = await LoadAsync(await MediaPreview.ExtractFrameAsync(File, info.Duration / 3, 640));
            _frame.Source = image;
            DrawPreview();
        }
        catch (Exception ex)
        {
            Log.Info($"No frame for Make Shorts: {ex.Message}");
        }
        Sync();
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

    private void Sync()
    {
        if (_reviewing || _into is null)
            return;
        var needed = SpeechLanguages.Needed(_model.Model, ValueOf<string>(_language), _into.Value);
        if (needed.Count > 0)
            _helpers.Models = needed;
        bool ready = needed.All(ModelStore.IsAvailable);
        _helpers.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        bool working = _work is not null;
        PrimaryButton.IsEnabled = !working && _model.IsReady && ready && Info is { HasVideo: true };
        if (!working)
        {
            var model = ModelStore.Find(_model.Model);
            FooterNote = model is not null && ShortsMaker.IsKnown(File, model, ValueOf<string>(_language))
                ? L.T("Heard before: the moments are found at once.")
                : Info is { } info ? L.F("{0} long", Clock(info.DurationSeconds)) : "";
        }
    }

    /// <summary>A length as a clock: "4:05", "1:02:03".</summary>
    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ---------- the preview ----------

    /// <summary>The frame with the short's window over it, a headline and a line of words in it, as the clips will look.</summary>
    private void DrawPreview()
    {
        _overlay.Children.Clear();
        bool own = !_colours.Children.OfType<Border>().Any(b => (string)b.Tag == _highlight);
        _ownColour.Chosen = own;
        _ownColour.Colour = own && Tools.Parts.ColourMath.TryParseHex(_highlight, out var mine) ? mine : null;
        foreach (var swatch in _colours.Children.OfType<Border>())
        {
            swatch.BorderThickness = new Thickness((string)swatch.Tag == _highlight ? 3 : 0);
            swatch.SetResourceReference(Border.BorderBrushProperty, "Petal.Text");
        }
        if (_frame.Source is not BitmapSource || _frame.ActualWidth <= 0)
            return;
        double w = _frame.ActualWidth, h = _frame.ActualHeight;
        double left = (_preview.ActualWidth - w) / 2, top = (_preview.ActualHeight - h) / 2;
        double shape = _shape.Value switch { ShortShape.Tall => 9 / 16.0, ShortShape.Square => 1, _ => w / h };
        double winH = h, winW = Math.Min(w, h * shape);
        double x = left + (w - winW) / 2;
        var shade = new SolidColorBrush(Color.FromArgb(0xA8, 0, 0, 0));
        if (winW < w - 1)
        {
            _overlay.Children.Add(Box(left, top, x - left, h, shade));
            _overlay.Children.Add(Box(x + winW, top, left + w - x - winW, h, shade));
            if (_framing.Value == ShortFraming.Whole)
            {
                // The whole picture, small in the middle of the window, over a darkened copy.
                _overlay.Children.Add(Box(x, top, winW, h, new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x10, 0x14))));
                var small = new Image { Source = _frame.Source, Width = winW, Stretch = Stretch.Uniform };
                Canvas.SetLeft(small, x);
                Canvas.SetTop(small, top + (h - winW * h / w) / 2);
                _overlay.Children.Add(small);
            }
            var outline = new Rectangle { Width = winW, Height = winH, Stroke = Brushes.White, StrokeThickness = 1.5, RadiusX = 3, RadiusY = 3 };
            Canvas.SetLeft(outline, x);
            Canvas.SetTop(outline, top);
            _overlay.Children.Add(outline);
        }
        bool arabic = _into.Value == "ar" || _into.Value == "spoken" && L.IsArabic;
        var lit = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_highlight));
        if (_headline.IsChecked == true)
        {
            var title = new Border
            {
                Background = lit,
                Padding = new Thickness(5, 1, 5, 2),
                Child = new TextBlock { Text = arabic ? "أهم ما قيل" : "The best part", FontWeight = FontWeights.Bold, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)) },
                FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            };
            title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(title, x + (winW - title.DesiredSize.Width) / 2);
            Canvas.SetTop(title, top + winH * 0.1);
            _overlay.Children.Add(title);
        }
        if (_captions.Value != "none")
        {
            string[] words = arabic ? ["هكذا", "تظهر", "كلماتك"] : ["This", "is", "how"];
            var line = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                FontSize = _captions.Value == "one" ? 15 : 12,
                Foreground = Brushes.White,
                FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 1, Color = Colors.Black },
            };
            var shown = _captions.Value == "one" ? words[1..2] : words;
            for (int i = 0; i < shown.Length; i++)
            {
                bool isLit = _captions.Value == "word" && i == 1;
                line.Inlines.Add(new System.Windows.Documents.Run(shown[i]) { Foreground = isLit ? lit : Brushes.White });
                if (i < shown.Length - 1)
                    line.Inlines.Add(new System.Windows.Documents.Run(" "));
            }
            line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(line, x + (winW - line.DesiredSize.Width) / 2);
            Canvas.SetTop(line, top + winH * (_shape.Value == ShortShape.Tall ? 0.66 : 0.8));
            _overlay.Children.Add(line);
        }
    }

    private static Rectangle Box(double x, double y, double w, double h, Brush fill)
    {
        var r = new Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), Fill = fill };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    // ---------- finding the moments ----------

    private void ShowSettings()
    {
        _titling?.Cancel();
        StopPlayback();
        _reviewing = false;
        _settings.Visibility = Visibility.Visible;
        _working.Visibility = Visibility.Collapsed;
        _review.Visibility = Visibility.Collapsed;
        ResetButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = L.T("Find the Best Moments");
        Sync();
    }

    private async Task FindAsync()
    {
        var model = ModelStore.Find(_model.Model);
        if (model is null || Info is not { } info)
            return;
        var language = ValueOf<string>(_language);
        var cancel = _work = new CancellationTokenSource();
        _settings.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = L.T("Finding…");
        ResetButton.Content = L.T("Cancel");
        ResetButton.Visibility = Visibility.Visible;
        FooterNote = "";
        try
        {
            if (_source is null || _heardWith != (model, language))
            {
                _stage.Text = L.T("Listening to the video…");
                _bar.Value = 0;
                _detail.Text = L.F("{0} long", Clock(info.DurationSeconds));
                var progress = new Progress<double>(p =>
                {
                    if (cancel.IsCancellationRequested)
                        return;
                    _bar.Value = p;
                    _detail.Text = L.F("{0} of {1} heard", Clock(p * info.DurationSeconds), Clock(info.DurationSeconds));
                });
                var file = File;
                _source = await Task.Run(() => ShortsMaker.ListenAsync(file, model, language, progress, cancel.Token), cancel.Token);
                _heardWith = (model, language);
            }
            _stage.Text = L.T("Finding the best moments…");
            _bar.Value = 1;
            var source = _source;
            var (length, count) = (_length.Value, _count.Value);
            var moments = source.HasSpeech ? await Task.Run(() => ShortMoments.Find(source.Segments, source.Levels, length, count, Spare), cancel.Token) : [];
            if (cancel.IsCancellationRequested)
                return;
            if (moments.Count == 0)
            {
                ShowSettings();
                FooterNote = source.HasSpeech ? L.T("There isn't enough said in this video for a short clip.") : L.T("No speech was found in this file.");
                return;
            }
            ShowReview(moments, count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Make Shorts");
            ShowSettings();
            FooterNote = L.T(ex.Message);
        }
        finally
        {
            if (_work == cancel)
                _work = null;
            if (!_reviewing && IsLoaded)
                Sync();
        }
    }

    // ---------- the moments found ----------

    private void ShowReview(IReadOnlyList<Moment> moments, int shown)
    {
        _reviewing = true;
        _settings.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Collapsed;
        _review.Visibility = Visibility.Visible;
        ResetButton.Content = L.T("Start over");
        ResetButton.Visibility = Visibility.Visible;
        _list.Children.Clear();
        _cards.Clear();
        for (int i = 0; i < moments.Count; i++)
        {
            var card = CardFor(moments[i], i + 1);
            card.Root.Visibility = i < shown ? Visibility.Visible : Visibility.Collapsed;
            card.Include.IsChecked = i < shown;
            _cards.Add(card);
            _list.Children.Add(card.Root);
        }
        _found.Text = L.P(Math.Min(shown, moments.Count), "{0} moment found", "{0} moments found");
        int more = moments.Count - Math.Min(shown, moments.Count);
        _more.Content = L.P(more, "Show {0} more", "Show {0} more");
        _more.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        Count();
        _ = PicturesAsync();
        _ = TitlesAsync();
    }

    private void ShowMore()
    {
        foreach (var card in _cards)
            card.Root.Visibility = Visibility.Visible;
        _found.Text = L.P(_cards.Count, "{0} moment found", "{0} moments found");
        _more.Visibility = Visibility.Collapsed;
    }

    private Card CardFor(Moment moment, int rank)
    {
        var include = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        include.Click += (_, _) => Count();
        var picture = new Image { Stretch = Stretch.UniformToFill };
        var playMark = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Icons.Create("play", 12, Brushes.White, 2),
        };
        var thumb = new Grid { Width = 112, Height = 63, Cursor = Cursors.Hand, ToolTip = L.T("Play") };
        thumb.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Brushes.Black, Child = picture });
        thumb.Children.Add(playMark);
        // A long title wraps rather than hides its end; Enter still makes the shorts.
        var title = new TextBox { FontWeight = FontWeights.SemiBold, Text = ShortMoments.Headline(moment), MaxLength = 90, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
        var score = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = moment.Score.ToString(L.Culture), FontWeight = FontWeights.Bold, FontSize = 12, Foreground = Brushes.White },
            Background = new SolidColorBrush(moment.Score >= 75 ? Color.FromRgb(0x1F, 0x9D, 0x55) : moment.Score >= 50 ? Color.FromRgb(0xC9, 0x8A, 0x0C) : Color.FromRgb(0x8A, 0x8F, 0x98)),
            ToolTip = L.T("How likely this moment is to hold a viewer, out of 100"),
        };
        var head = new DockPanel();
        DockPanel.SetDock(score, Dock.Right);
        head.Children.Add(score);
        head.Children.Add(title);
        var seconds = L.F("{0} s", Math.Round(moment.Length));
        var when = Text($"{Clock(moment.Start)} – {Clock(moment.End)} · {seconds}", "Petal.Caption");
        // Times read left to right in any language, at the start of the line (the right in Arabic: alignment follows the card).
        when.FlowDirection = FlowDirection.LeftToRight;
        when.HorizontalAlignment = HorizontalAlignment.Left;
        var why = Text(string.Join(" · ", moment.Reasons.Select(Reason)), "Petal.Caption");
        why.TextTrimming = TextTrimming.CharacterEllipsis;
        var words = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        words.Children.Add(head);
        words.Children.Add(when.WithMarginT(4));
        if (moment.Reasons.Count > 0)
            words.Children.Add(why.WithMarginT(2));
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(include);
        Grid.SetColumn(thumb, 1);
        row.Children.Add(thumb);
        Grid.SetColumn(words, 2);
        row.Children.Add(words);
        var root = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8), Child = row };
        root.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        System.Windows.Automation.AutomationProperties.SetName(include, L.F("Moment {0}", rank));
        var card = new Card { Moment = moment, Root = root, Include = include, Title = title, Picture = picture, PlayMark = playMark };
        title.TextChanged += (_, _) =>
        {
            if (!card.Writing)
                card.Edited = true;
        };
        thumb.MouseLeftButtonUp += (_, _) => Play(card);
        return card;
    }

    /// <summary>Why a moment was chosen, in a few words.</summary>
    internal static string Reason(MomentReason reason) => reason switch
    {
        MomentReason.Question => L.T("Opens with a question"),
        MomentReason.Hook => L.T("Strong opening"),
        MomentReason.Story => L.T("A story"),
        MomentReason.Advice => L.T("Advice"),
        MomentReason.Numbers => L.T("Facts and numbers"),
        MomentReason.Feeling => L.T("Feeling"),
        MomentReason.Laughter => L.T("Laughter"),
        MomentReason.Lively => L.T("Lively"),
        _ => L.T("A whole thought"),
    };

    private void Count()
    {
        int chosen = _cards.Count(c => c.Include.IsChecked == true && c.Root.Visibility == Visibility.Visible);
        PrimaryButton.Content = L.P(chosen, "Make {0} Short", "Make {0} Shorts");
        PrimaryButton.IsEnabled = chosen > 0;
        FooterNote = Path.GetFileName(OutputNaming.ForTool(File, Tool));
    }

    private async Task PicturesAsync()
    {
        try
        {
            // The snapshots' sample video is a few seconds long, shorter than the moments made up for it.
            var times = _cards.Select((c, i) => TimeSpan.FromSeconds(_snapshot ? 0.3 + 0.6 * i : c.Moment.Start + Math.Min(2.5, c.Moment.Length / 2))).ToList();
            var pictures = await MediaPreview.ExtractFramesAsync(File, times, 224);
            for (int i = 0; i < pictures.Count && i < _cards.Count; i++)
                _cards[i].Picture.Source = await LoadAsync(pictures[i]);
        }
        catch (Exception ex)
        {
            Log.Info($"No pictures for the moments: {ex.Message}");
        }
    }

    /// <summary>
    /// Titles written for the moments one after another, while they are being checked (a few seconds each); a title
    /// the user has already changed is left as it is.
    /// </summary>
    private async Task TitlesAsync()
    {
        _titling?.Cancel();
        if (!AskPetal.ModelReady || _source is not { } source || _snapshot)
            return;
        var cancel = _titling = new CancellationTokenSource();
        try
        {
            foreach (var card in _cards.ToList())
            {
                if (card.Edited)
                    continue;
                var title = await Task.Run(() => ShortsMaker.TitleAsync(card.Moment, source.Language, cancel.Token), cancel.Token);
                if (cancel.IsCancellationRequested)
                    return;
                if (!card.Edited && title.Length > 0)
                {
                    card.Writing = true;
                    card.Title.Text = title;
                    card.Writing = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Play(Card card)
    {
        if (IsPlaying && _playing == card)
        {
            StopPlayback();
            return;
        }
        _playing = card;
        _player.Visibility = Visibility.Visible;
        StartPlayback(card.Moment.Start, card.Moment.End);
        if (PlaybackUnavailable)
            _player.Visibility = Visibility.Collapsed;
    }

    protected override void PlaybackStateChanged(bool playing)
    {
        if (!playing)
        {
            _player.Visibility = Visibility.Collapsed;
            _playing = null;
            // Windows can't play every video DropWheel can read (AV1 in WebM, say): the clips are made all the same.
            if (PlaybackUnavailable)
                FooterNote = L.T("Windows can't play this video here; the shorts are made all the same.");
        }
    }

    // ---------- making them ----------

    private void Make()
    {
        var chosen = _cards.Where(c => c.Include.IsChecked == true && c.Root.Visibility == Visibility.Visible).ToList();
        if (chosen.Count == 0 || _source is not { } source)
            return;
        var folder = OutputNaming.ForTool(File, Tool);
        var style = _captions.Value switch { "one" => CaptionStyle.OneWord, "lines" => CaptionStyle.Lines, _ => CaptionStyle.WordByWord };
        var options = new ShortOptions(_shape.Value, _framing.Value, _captions.Value != "none", style, _highlight, _pauses.IsChecked == true) { Settings = Quality };
        var into = SpeechLanguages.Into(_into.Value);
        var translating = SpeechLanguages.TranslatingModel();
        bool headline = _headline.IsChecked == true;
        var file = File;
        var made = chosen.Select((c, i) => (c.Moment, Title: c.Title.Text.Trim(), Number: i + 1)).ToList();
        _titling?.Cancel();
        Close();
        var work = made.Select(m => new WorkItem(file, m.Title.Length > 0 ? m.Title : L.F("Moment {0}", m.Number), async (p, ct) =>
        {
            Directory.CreateDirectory(folder);
            var output = Path.Combine(folder, FileName(m.Number, m.Title) + ".mp4");
            var captions = options.Captions ? await ShortsMaker.CaptionsAsync(source, m.Moment, into, translating, ct) : null;
            // The headline in the language of the words under it.
            var title = headline ? await ShortsMaker.TitleInAsync(m.Title, source.Language, into, ct) : null;
            await ShortClip.RenderAsync(file, m.Moment, output, options, title, captions, source.Levels, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = Jobs.RunAsync("Making Shorts", "Shorts made", work, At);
    }

    /// <summary>"01 The truth about space": the clip's number, then its title as a file name allows it.</summary>
    internal static string FileName(int number, string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(title.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.', ' ');
        if (clean.Length > 60)
            clean = clean[..60].TrimEnd('.', ' ');
        return clean.Length > 0 ? $"{number:00} {clean}" : $"{number:00}";
    }

    // ---------- for the design snapshots ----------

    /// <summary>The moments shown as if found, for the design snapshots.</summary>
    internal void ShowForSnapshot(IReadOnlyList<Moment> moments, ShortsSource source)
    {
        _snapshot = true;
        _source = source;
        ShowReview(moments, moments.Count - 1);
    }

    private bool _snapshot;

    /// <summary>The listening stage, as if part of the way through, for the design snapshots.</summary>
    internal void ShowListeningForSnapshot(double share, double length)
    {
        // As while listening: the choices are put away until it ends.
        _work = new CancellationTokenSource();
        _settings.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Visible;
        _stage.Text = L.T("Listening to the video…");
        _bar.Value = share;
        _detail.Text = L.F("{0} of {1} heard", Clock(share * length), Clock(length));
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = L.T("Finding…");
        ResetButton.Content = L.T("Cancel");
        ResetButton.Visibility = Visibility.Visible;
        FooterNote = "";
    }
}
