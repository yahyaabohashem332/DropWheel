using System.IO;
using System.Windows;
using System.Windows.Controls;
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
using Petal.Recipes.Translation;
using Path = System.IO.Path;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Dub: a video's speech in the other language (Arabic into English, English into Arabic), each line in the voice of the
/// person who said it, the music taken out and every other sound kept. Prepared first (voices separated, heard, translated; kept for
/// next time), then the translations are shown to be checked and changed before the voices are made. See <see cref="Dubbing"/>.
/// </summary>
public sealed class DubWindow : MediaToolWindow
{
    private static readonly ModelInfo[] Models = [ModelCatalog.WhisperFast, ModelCatalog.WhisperBalanced, ModelCatalog.WhisperAccurate];

    /// <summary>The level that hears with the most accurate model and translates by meaning with the AI model.</summary>
    private const int Finest = 3;

    // ---------- the choices ----------
    private readonly StackPanel _settings = new();
    private readonly Image _frame = new() { Stretch = Stretch.UniformToFill };
    private readonly LanguageChoice _into;
    private readonly LanguageChoice _spoken;
    // The original sound has its music: left out unless asked for.
    private readonly CheckBox _original = new() { IsChecked = false, Margin = new Thickness(0, 0, 18, 0) };
    private readonly CheckBox _subtitles = new() { IsChecked = true };
    private readonly Segmented<int> _accuracy;
    private readonly ModelPanel _model;
    private readonly ModelPanel _voices = new(Dubbing.Models);
    private readonly ModelPanel _helpers = new(Translator.Models);
    // The finest level's translator, and what it is.
    private readonly ModelPanel _writer = new(DubTranslation.Models);
    private readonly StackPanel _finest = new() { Visibility = Visibility.Collapsed };

    // ---------- preparing ----------
    private readonly StackPanel _working = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _stage = new();
    private readonly TextBlock _detail = new();
    private readonly Border _track = new() { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Width = 0 };
    private double _share;

    // ---------- the lines ----------
    private readonly StackPanel _review = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _count = new();
    private readonly Grid _player = new() { Height = 190, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _list = new();
    private readonly List<(DubLine Line, TextBox Translation)> _rows = [];

    private DubSource? _source;
    private CancellationTokenSource? _work;
    private bool _reviewing;

    public DubWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool, files, jobs, at, 560)
    {
        var picture = new Grid { Height = 150, ClipToBounds = true };
        picture.Children.Add(new Border { Background = Brushes.Black });
        picture.Children.Add(_frame);
        _settings.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = picture, Margin = new Thickness(0, 0, 0, 10) });
        _settings.Children.Add(Text("Each line is said again in the other language, in the voice of the person who said it. The music is taken out; every other sound stays.", "Petal.Caption").WithMarginT(2));
        string home = Dubbing.Languages.Contains(LanguageChoice.Interface) && LanguageChoice.Interface != "en" ? LanguageChoice.Interface : "ar";
        _into = new LanguageChoice(L.Code == "en" ? "es" : Dubbing.Opposite("ar", home) == LanguageChoice.Interface ? "en" : home, only: Dubbing.Languages);
        _settings.Children.Add(Row("Dub into", _into, 130, 12));
        _spoken = new LanguageChoice("auto", [("auto", "Detect automatically")], Dubbing.Languages);
        _settings.Children.Add(Row("Spoken in", _spoken, 130, 8));
        _original.Content = L.T("Keep the original sound as a second track");
        _subtitles.Content = L.T("Subtitles in the new language");
        _settings.Children.Add(Row("", new WrapPanel { Children = { _original, _subtitles } }, 130, 10));
        int preferred = ModelStore.IsAvailable(Models[2]) && DubTranslation.Ready ? Finest
            : Array.FindLastIndex(Models, ModelStore.IsAvailable) is >= 0 and var found ? found : 2;
        _accuracy = Segments([(0, "Fast"), (1, "Balanced"), (2, "Accurate"), (Finest, L.T("Best", "dub"))], preferred);
        _settings.Children.Add(Row("Accuracy", _accuracy, 130, 12));
        _finest.Children.Add(Text("Translated by meaning, not word for word, by a large AI model on this PC that knows religious language, poetry and dialect. It takes a while: a few minutes for each minute of speech.", "Petal.Caption").WithMarginT(6));
        _finest.Children.Add(_writer);
        _settings.Children.Add(_finest);
        _model = new ModelPanel(Models[Math.Min(preferred, 2)]);
        _settings.Children.Add(_model);
        _settings.Children.Add(_voices);
        _settings.Children.Add(_helpers);
        Body.Children.Add(_settings);

        _stage.Style = (Style)FindResource("Petal.Section");
        _detail.Style = (Style)FindResource("Petal.Caption");
        _detail.Margin = new Thickness(0, 6, 0, 0);
        _detail.TextWrapping = TextWrapping.Wrap;
        _track.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _fill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        _track.Child = _fill;
        _track.SizeChanged += (_, _) => _fill.Width = _track.ActualWidth * _share;
        _working.Children.Add(_stage);
        _working.Children.Add(_track);
        _working.Children.Add(_detail);
        _working.Children.Add(Text("It all happens on this PC; nothing is uploaded. A video prepared once opens again at once.", "Petal.Caption").WithMarginT(14));
        Body.Children.Add(_working);

        _count.Style = (Style)FindResource("Petal.Section");
        _review.Children.Add(_count);
        _review.Children.Add(Text("Check the translations and change any that need it; play a line to hear how it was said.", "Petal.Caption").WithMarginT(4));
        _player.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(10) });
        AttachPlayer(_player);
        _review.Children.Add(_player);
        _review.Children.Add(new ScrollViewer { MaxHeight = 440, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list });
        Body.Children.Add(_review);

        _accuracy.Changed += i => { _model.Model = Models[Math.Min(i, 2)]; Sync(); };
        _model.Ready += Sync;
        _writer.Ready += Sync;
        _voices.Ready += Sync;
        _helpers.Ready += Sync;
        // The language spoken is never the one dubbed into: choosing one turns the other round.
        _into.Changed += into =>
        {
            if (_spoken.Value == into)
                _spoken.Value = Dubbing.Opposite(into, home);
            Sync();
        };
        _spoken.SelectionChanged += (_, _) =>
        {
            if (_spoken.Value == _into.Value)
                _into.Value = Dubbing.Opposite(_spoken.Value, home);
            Sync();
        };
        PrimaryButton.Click += async (_, _) =>
        {
            if (_reviewing)
                Dub();
            else
                await PrepareAsync();
        };
        ResetButton.Click += (_, _) =>
        {
            _work?.Cancel();
            ShowSettings();
        };
        Closed += (_, _) => _work?.Cancel();
        ShowSettings();
    }

    private static string NativeName(string code)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(code);
        return char.ToUpper(culture.NativeName[0], culture) + culture.NativeName[1..];
    }

    protected override async Task OnMediaReadyAsync(MediaInfo info)
    {
        try
        {
            var picture = await MediaPreview.ExtractFrameAsync(File, info.Duration / 3, 640);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(picture);
            image.EndInit();
            image.Freeze();
            _frame.Source = image;
        }
        catch (Exception ex)
        {
            Log.Info($"No frame for Dub: {ex.Message}");
        }
        Sync();
    }

    /// <summary>The translation models the direction needs.</summary>
    /// <summary>The translation models this dub needs; what is spoken may not be known yet, then as if English.</summary>
    private IReadOnlyList<ModelInfo> Translation
    {
        get
        {
            string from = _spoken.Value != "auto" ? _spoken.Value : _into.Value == "en" ? "ar" : "en";
            return Petal.Recipes.Cloud.CloudAi.CanTranslate ? [] : Translator.ModelsFor(from, _into.Value);
        }
    }

    private void Sync()
    {
        if (_reviewing || _into is null)
            return;
        _helpers.Models = Translation;
        _finest.Visibility = _accuracy.Value == Finest ? Visibility.Visible : Visibility.Collapsed;
        bool working = _work is not null;
        PrimaryButton.IsEnabled = !working && _model.IsReady && _voices.IsReady && Translation.All(ModelStore.IsAvailable) && Info is { HasVideo: true, HasAudio: true }
            && (_accuracy.Value != Finest || _writer.IsReady || Petal.Recipes.Cloud.CloudAi.CanWrite);
        if (!working)
            FooterNote = Info is { HasAudio: false } ? L.T("This video has no sound to dub.")
                : Dubbing.IsKnown(File) ? L.T("Prepared before: it opens again at once.")
                : Info is { } info && info.DurationSeconds > 30 * 60 ? L.T("DropWheel dubs up to 30 minutes at a time. Trim the video first.")
                : "";
        if (Info is { } i && i.DurationSeconds > 30 * 60)
            PrimaryButton.IsEnabled = false;
    }

    private void ShowSettings()
    {
        StopPlayback();
        _reviewing = false;
        _settings.Visibility = Visibility.Visible;
        _working.Visibility = Visibility.Collapsed;
        _review.Visibility = Visibility.Collapsed;
        ResetButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = L.T("Prepare the Dub");
        Sync();
    }

    private void Progress(double share, string stage)
    {
        _share = Math.Clamp(share, 0, 1);
        _fill.Width = _track.ActualWidth * _share;
        _stage.Text = L.T(stage);
    }

    private async Task PrepareAsync()
    {
        var model = ModelStore.Find(_model.Model);
        if (model is null)
            return;
        var cancel = _work = new CancellationTokenSource();
        _settings.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = L.T("Preparing…");
        ResetButton.Content = L.T("Cancel");
        ResetButton.Visibility = Visibility.Visible;
        FooterNote = "";
        _detail.Text = Info is { } info ? L.F("{0} long", Clock(info.DurationSeconds)) : "";
        Progress(0, "Separating the voices…");
        string? problem = null;
        try
        {
            var (file, spoken, into) = (File, _spoken.Value, _into.Value);
            bool natural = _accuracy.Value == Finest;
            var (separated, heard) = Dubbing.Steps(natural);
            var progress = new Progress<double>(p =>
            {
                if (!cancel.IsCancellationRequested)
                    Progress(p, p < separated ? "Separating the voices…" : p < heard ? "Listening to what is said…" : natural ? "Translating by meaning…" : "Translating…");
            });
            _source = await Task.Run(() => Dubbing.PrepareAsync(file, model, spoken, into, progress, cancel.Token, natural), cancel.Token);
            if (_source.Lines.Count == 0)
                problem = "No speech was found in this file.";
            else
                ShowReview(_source);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Dub");
            problem = ex.Message;
        }
        finally
        {
            if (_work == cancel)
                _work = null;
        }
        // Back to the settings (stopped, or nothing to dub), saying why; unless another preparing has started meanwhile.
        if (!_reviewing && IsLoaded && _work is null)
        {
            ShowSettings();
            if (problem is not null)
                FooterNote = L.T(problem);
        }
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ---------- the lines ----------

    private void ShowReview(DubSource source)
    {
        _reviewing = true;
        // The voice model loads while the lines are read (if preparing hasn't already loaded it).
        Dubbing.Voices.WarmUp();
        _settings.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Collapsed;
        _review.Visibility = Visibility.Visible;
        ResetButton.Content = L.T("Start over");
        ResetButton.Visibility = Visibility.Visible;
        _count.Text = L.P(source.Lines.Count, "{0} line to dub", "{0} lines to dub");
        _list.Children.Clear();
        _rows.Clear();
        var into = source.Into == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var from = source.Language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (var line in source.Lines)
        {
            var play = IconButton("play", "Play");
            play.VerticalAlignment = VerticalAlignment.Top;
            play.Margin = new Thickness(0, 0, 8, 0);
            var span = line;
            play.Click += (_, _) => Play(span);
            var when = Text($"{Clock(line.Start)} – {Clock(line.End)}", "Petal.Caption");
            when.FlowDirection = FlowDirection.LeftToRight;
            when.HorizontalAlignment = HorizontalAlignment.Left;
            var original = Text(line.Original, "Petal.Caption");
            original.TextWrapping = TextWrapping.Wrap;
            original.FlowDirection = from;
            original.Margin = new Thickness(0, 2, 0, 4);
            var translation = new TextBox { Text = line.Translation, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, FlowDirection = into, MaxLength = 400 };
            var words = new StackPanel { Children = { when, original, translation } };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(play);
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 0, 0, 8), Child = row };
            card.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
            _list.Children.Add(card);
            _rows.Add((line, translation));
        }
        PrimaryButton.Content = L.T("Dub");
        PrimaryButton.IsEnabled = true;
        FooterNote = Path.GetFileName(Output(source));
    }

    /// <summary>Named for the language it is dubbed into: "talk Arabic.mp4".</summary>
    private string Output(DubSource source) =>
        OutputNaming.ForTool(File, Tool, suffixOverride: System.Globalization.CultureInfo.GetCultureInfo(source.Into).EnglishName, extensionOverride: "mp4");

    private void Play(DubLine line)
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }
        _player.Visibility = Visibility.Visible;
        StartPlayback(line.Start, line.End + 0.2);
        if (PlaybackUnavailable)
            _player.Visibility = Visibility.Collapsed;
    }

    protected override void PlaybackStateChanged(bool playing)
    {
        if (!playing)
        {
            _player.Visibility = Visibility.Collapsed;
            if (PlaybackUnavailable)
                FooterNote = L.T("Windows can't play this video here; the dub is made all the same.");
        }
    }

    // ---------- dubbing ----------

    private void Dub()
    {
        if (_source is not { } source)
            return;
        var lines = _rows.Select(r => r.Line with { Translation = r.Translation.Text.Trim() }).ToList();
        var options = new DubOptions(_original.IsChecked == true, _subtitles.IsChecked == true);
        var checker = ModelStore.Find(_model.Model);
        var output = Output(source);
        var file = File;
        Close();
        _ = Jobs.RunAsync("Dubbing", "Dubbed", [new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            await Dubbing.DubAsync(source, lines, output, options, checker, p, ct);
            return (IReadOnlyList<string>)[output];
        })], At);
    }

    // ---------- for the design snapshots ----------

    /// <summary>The lines shown as if prepared, for the design snapshots.</summary>
    internal void ShowForSnapshot(DubSource source)
    {
        _source = source;
        ShowReview(source);
    }

    /// <summary>The preparing stage, part of the way through, for the design snapshots.</summary>
    /// <summary>The finest level chosen, for the design snapshots.</summary>
    internal void FinestForSnapshot() => _accuracy.Value = Finest;

    internal void ShowPreparingForSnapshot(double share)
    {
        _work = new CancellationTokenSource();
        _settings.Visibility = Visibility.Collapsed;
        _working.Visibility = Visibility.Visible;
        Progress(share, share < 0.45 ? "Separating the voices…" : share < 0.85 ? "Listening to what is said…" : "Translating…");
        _detail.Text = L.F("{0} long", "12:00");
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = L.T("Preparing…");
        ResetButton.Content = L.T("Cancel");
        ResetButton.Visibility = Visibility.Visible;
        FooterNote = "";
    }
}
