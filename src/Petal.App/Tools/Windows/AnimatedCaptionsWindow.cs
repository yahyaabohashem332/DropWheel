using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Models;
using Petal.Core.Tools;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Animated Captions: the words of a video burned into it, a few at a time, the word being said lit up,
/// as on short videos. In the language spoken, in English, or in Arabic (see <see cref="SpeechText"/> for
/// how each is had). A frame of the video shows how they will look.
/// </summary>
public sealed class AnimatedCaptionsWindow : ToolWindow
{
    private static readonly ModelInfo[] Models = [ModelCatalog.WhisperFast, ModelCatalog.WhisperBalanced, ModelCatalog.WhisperAccurate];

    /// <summary>The colours for the word being said.</summary>
    internal static readonly (string Hex, string Name)[] Colours = [("#FFD60A", "Yellow"), ("#32D74B", "Green"), ("#FF375F", "Pink"), ("#9B7BFF", "Violet"), ("#64D2FF", "Blue")];

    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Segmented<int> _accuracy;
    private readonly ModelPanel _model;
    private readonly ComboBox _language;
    private readonly LanguageChoice _into;
    // What the words in another language need besides the speech model (translation models).
    private readonly ModelPanel _helpers = new(Petal.Recipes.Translation.Translator.Models);
    private readonly Segmented<CaptionStyle> _style;
    private readonly Segmented<CaptionPlace> _place;
    private readonly Segmented<double> _size;
    private readonly WrapPanel _colours = new();
    private readonly Tools.Parts.CustomColourChip _ownColour;
    private string _highlight = Colours[0].Hex;
    private readonly Grid _preview = new() { Height = 200, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Image _frame = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _sample = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold, Margin = new Thickness(16) };

    public AnimatedCaptionsWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 480)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        _preview.Children.Add(new Border { Background = Brushes.Black });
        _preview.Children.Add(_frame);
        _preview.Children.Add(_sample);
        Body.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _preview });

        _style = Segments([(CaptionStyle.WordByWord, "Word by word"), (CaptionStyle.OneWord, "One word"), (CaptionStyle.Lines, "Lines")], CaptionStyle.WordByWord);
        _place = Segments([(CaptionPlace.Bottom, "Bottom"), (CaptionPlace.Middle, "Middle"), (CaptionPlace.Top, "Top")], CaptionPlace.Bottom);
        _size = Segments([(0.045, "Small"), (0.06, "Medium"), (0.08, "Large")], 0.06);
        Body.Children.Add(Row("Style", _style, 110, 0));
        Body.Children.Add(Row("Place", _place, 110, 8));
        Body.Children.Add(Row("Size", _size, 110, 8));
        foreach (var (hex, name) in Colours)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Cursor = Cursors.Hand,
                ToolTip = L.T(name),
                Tag = hex,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _highlight = hex;
                ShowSample();
            };
            _colours.Children.Add(swatch);
        }
        _ownColour = new Tools.Parts.CustomColourChip(34, () => Tools.Parts.ColourMath.TryParseHex(_highlight, out var c) ? c : Colors.White);
        _ownColour.Changing += c =>
        {
            _highlight = Tools.Parts.ColourMath.Hex(c);
            ShowSample();
        };
        _colours.Children.Add(_ownColour);
        Body.Children.Add(Row("Lit word", _colours, 110, 8));

        int preferred = Array.FindLastIndex(Models, ModelStore.IsAvailable) is >= 0 and var found ? found : 1;
        _accuracy = Segments([(0, "Fast"), (1, "Balanced"), (2, "Accurate")], preferred);
        Body.Children.Add(Row("Accuracy", _accuracy, 110, 14));
        _model = new ModelPanel(Models[preferred]);
        Body.Children.Add(_model);
        _accuracy.Changed += i => { _model.Model = Models[i]; Sync(); };
        _model.Ready += Sync;
        var languages = new List<(string, string)> { ("auto", "Detect automatically") };
        languages.AddRange(Transcriber.Languages.Select(code => (code, NativeName(code))));
        _language = Choice(languages, "auto");
        _language.SelectionChanged += (_, _) => Sync();
        Body.Children.Add(Row("Spoken in", _language, 110, 10));
        _into = LanguageChoice.ForWords();
        Body.Children.Add(Row("Words in", _into, 110, 10));
        Body.Children.Add(_helpers);
        _into.Changed += _ => Sync();
        _helpers.Ready += Sync;
        foreach (var changed in new Action<Action>[] { a => _style.Changed += _ => a(), a => _place.Changed += _ => a(), a => _size.Changed += _ => a(), a => _into.Changed += _ => a() })
            changed(ShowSample);

        PrimaryButton.Content = L.T("Add Captions");
        PrimaryButton.Click += (_, _) => Apply();
        Loaded += async (_, _) => await LoadFrameAsync();
        Sync();
        ShowSample();
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

    private async Task LoadFrameAsync()
    {
        try
        {
            var info = await MediaProbe.ProbeAsync(_files[0]);
            var jpeg = await MediaPreview.ExtractFrameAsync(_files[0], info.Duration / 3, 640);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(jpeg);
            image.EndInit();
            image.Freeze();
            _frame.Source = image;
        }
        catch (Exception ex)
        {
            Log.Info($"No frame for the captions preview: {ex.Message}");
        }
    }

    /// <summary>A sample line over the frame, in the chosen style, place, size and colour.</summary>
    private void ShowSample()
    {
        bool arabic = _into.Value == "ar" || _into.Value == "spoken" && L.IsArabic;
        string[] words = arabic ? ["هكذا", "تظهر", "كلماتك", "في", "الفيديو"] : ["This", "is", "how", "your", "words", "look"];
        double size = _preview.Height * _size.Value * 1.35;
        var lit = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_highlight));
        _sample.Inlines.Clear();
        _sample.FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _sample.FontSize = _style.Value == CaptionStyle.OneWord ? size * 1.5 : size;
        _sample.VerticalAlignment = _place.Value switch { CaptionPlace.Top => VerticalAlignment.Top, CaptionPlace.Middle => VerticalAlignment.Center, _ => VerticalAlignment.Bottom };
        _sample.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 1, Color = Colors.Black };
        var shown = _style.Value == CaptionStyle.OneWord ? words[2..3] : words[..Math.Min(5, words.Length)];
        for (int i = 0; i < shown.Length; i++)
        {
            bool isLit = _style.Value == CaptionStyle.WordByWord && i == 2 || _style.Value == CaptionStyle.OneWord;
            _sample.Inlines.Add(new System.Windows.Documents.Run(shown[i]) { Foreground = isLit && _style.Value == CaptionStyle.WordByWord ? lit : Brushes.White });
            if (i < shown.Length - 1)
                _sample.Inlines.Add(new System.Windows.Documents.Run(" "));
        }
        bool own = !_colours.Children.OfType<Border>().Any(b => (string)b.Tag == _highlight);
        _ownColour.Chosen = own;
        _ownColour.Colour = own && Tools.Parts.ColourMath.TryParseHex(_highlight, out var mine) ? mine : null;
        foreach (var swatch in _colours.Children.OfType<Border>())
        {
            bool on = (string)swatch.Tag == _highlight;
            swatch.BorderThickness = new Thickness(on ? 3 : 0);
            swatch.SetResourceReference(Border.BorderBrushProperty, "Petal.Text");
        }
    }

    /// <summary>The language the words are wanted in ("spoken", "en", "ar"), as if chosen (for snapshots).</summary>
    internal string WordsIn
    {
        set => _into.Value = value;
    }

    private void Sync()
    {
        if (_into is null)
            return;
        var needed = SpeechLanguages.Needed(_model.Model, ValueOf<string>(_language), _into.Value);
        if (needed.Count > 0)
            _helpers.Models = needed;
        bool ready = needed.All(ModelStore.IsAvailable);
        _helpers.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.IsEnabled = _model.IsReady && ready;
        FooterNote = _files.Count == 1 ? Path.GetFileName(_files[0]) : L.P(_files.Count, "{0} video", "{0} videos");
    }

    private void Apply()
    {
        var modelPath = ModelStore.Find(_model.Model);
        if (modelPath is null)
            return;
        var speech = new SpeechTextOptions(modelPath, ValueOf<string>(_language), SpeechLanguages.Into(_into.Value), SpeechLanguages.TranslatingModel());
        var options = new AnimatedCaptionOptions(_style.Value, _place.Value, _size.Value, _highlight) { Settings = SettingsStore.Current.Conversion };
        var tool = _tool;
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var transcript = await SpeechText.TranscribeAsync(file, speech, new ScaledProgress(p, 0, 0.6), ct);
            if (transcript.IsEmpty)
                throw new InvalidOperationException(L.T("No speech was found in this file."));
            var output = OutputNaming.ForTool(file, tool);
            await AnimatedCaptions.BurnAsync(file, transcript.Segments, output, options, new ScaledProgress(p, 0.6, 1), ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Adding captions", "Captions added", work, _at);
    }
}
