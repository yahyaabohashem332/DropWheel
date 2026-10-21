using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Models;
using Petal.Core.Tools;
using Petal.Engines.Imaging;
using Petal.Engines.Media;

namespace Petal.App.Tools.Windows;

// ---------------------------------------------------------------------------------------------
// Remove Background — BiRefNet on this PC: transparent, white or any colour behind the subject.
// ---------------------------------------------------------------------------------------------

public sealed class RemoveBackgroundWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly ModelPanel _model = new(ModelCatalog.Background);
    private readonly Segmented<int> _fill;
    private readonly ColorWell _color = new(Color.FromRgb(0x2E, 0x6B, 0xF0));
    private readonly CheckBox _crop = new() { Content = L.T("Crop to the subject"), Margin = new Thickness(0, 12, 0, 0) };
    private readonly Image _preview = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform, Margin = new Thickness(12) };

    public RemoveBackgroundWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 420)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(PreviewPanel(_preview, 200));
        if (files.Count > 1)
            Body.Children.Add(Text(L.P(files.Count, "{0} image", "{0} images") + " · " + L.T("the first one is shown"), "Petal.Caption").WithMarginT(6));
        Body.Children.Add(_model);

        _fill = Segments([(0, "Transparent"), (1, "White"), (2, "Colour")], 0);
        _fill.Changed += _ => Sync();
        _color.Margin = new Thickness(8, 0, 0, 0);
        Body.Children.Add(Row("Background", new StackPanel { Orientation = Orientation.Horizontal, Children = { _fill, _color } }, 100, 14));
        Body.Children.Add(_crop);
        Body.Children.Add(Text("Runs on this PC, on the processor. Older computers take up to half a minute per picture.", "Petal.Caption").WithMarginT(10));

        _model.Ready += Sync;
        PrimaryButton.Content = L.T("Remove Background");
        PrimaryButton.Click += (_, _) => Apply();
        Loaded += async (_, _) =>
        {
            try { _preview.Source = await Parts.ImageLoader.LoadAsync(files[0], 900); }
            catch (Exception ex) { Log.Error(ex, "Remove background preview"); }
        };
        Sync();
    }

    /// <summary>Transparent results need a format that keeps transparency.</summary>
    private string Output(string file)
    {
        var format = Formats.FromPath(file);
        bool keepsTransparency = format == Formats.Png || format == Formats.Webp;
        return _fill.Value == 0 && !keepsTransparency
            ? OutputNaming.ForTool(file, _tool, extensionOverride: "png")
            : OutputNaming.ForTool(file, _tool);
    }

    private void Sync()
    {
        _color.Visibility = _fill.Value == 2 ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = _model.IsReady;
        FooterNote = _files.Count == 1 ? Path.GetFileName(Output(_files[0])) : L.P(_files.Count, "{0} file", "{0} files");
    }

    private void Apply()
    {
        var options = new RemoveBackgroundOptions
        {
            Fill = _fill.Value switch
            {
                1 => ImageMagick.MagickColors.White,
                2 => new ImageMagick.MagickColor(_color.Color.R, _color.Color.G, _color.Color.B),
                _ => null,
            },
            CropToSubject = _crop.IsChecked == true,
        };
        var outputs = _files.ToDictionary(f => f, Output);
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            await BackgroundRemover.RemoveAsync(file, outputs[file], options, p, ct);
            return (IReadOnlyList<string>)[outputs[file]];
        })).ToList();
        _ = _jobs.RunAsync("Removing background", "Background removed", work, _at);
    }
}

// ---------------------------------------------------------------------------------------------
// Transcribe — Whisper on this PC: subtitles, text, or subtitles put into the video.
// ---------------------------------------------------------------------------------------------

public enum SubtitlesInVideo
{
    None,
    Track,
    Burned,
}

public sealed class TranscribeWindow : ToolWindow
{
    private static readonly ModelInfo[] Models = [ModelCatalog.WhisperFast, ModelCatalog.WhisperBalanced, ModelCatalog.WhisperAccurate];
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly bool _video;
    private readonly Segmented<int> _accuracy;
    private readonly ModelPanel _model;
    private readonly ComboBox _language;
    private readonly Segmented<string> _into;
    private readonly ModelPanel _translator = new(Petal.Recipes.Translation.Translator.Models);
    private readonly Segmented<string> _saveAs;
    private readonly Segmented<SubtitlesInVideo>? _inVideo;

    public TranscribeWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 440)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        _video = files.All(f => Formats.FromPath(f)?.Family == FormatFamily.Video);
        Body.Children.Add(Text(FileLabel(files), "Petal.Section"));

        // The most accurate model already on this PC, else the balanced one.
        int preferred = Array.FindLastIndex(Models, ModelStore.IsAvailable) is >= 0 and var found ? found : 1;
        _accuracy = Segments([(0, "Fast"), (1, "Balanced"), (2, "Accurate")], preferred);
        Body.Children.Add(Row("Accuracy", _accuracy, 110, 14));
        Body.Children.Add(Text("Fast suits clear English speech. Accurate is best for Arabic, accents and noisy recordings.", "Petal.Caption").WithMargin(110, 4, 0, 0));
        _model = new ModelPanel(Models[preferred]);
        Body.Children.Add(_model);
        _accuracy.Changed += i => { _model.Model = Models[i]; Sync(); };
        _model.Ready += Sync;

        var languages = new List<(string, string)> { ("auto", "Detect automatically") };
        languages.AddRange(Transcriber.Languages.Select(code => (code, NativeName(code))));
        _language = Choice(languages, "auto");
        Body.Children.Add(Row("Language", _language, 110, 12));
        _into = Segments([("spoken", "As spoken"), ("en", "English"), ("ar", "Arabic")], "spoken");
        _into.Changed += _ => Sync();
        Body.Children.Add(Row("Subtitles in", _into, 110, 10));
        Body.Children.Add(_translator);
        _translator.Ready += Sync;

        _saveAs = Segments([("srt", "Subtitles"), ("txt", "Text"), ("both", "Both")], "srt");
        Body.Children.Add(Row("Save as", _saveAs, 110, 14));
        if (_video)
        {
            _inVideo = Segments([(SubtitlesInVideo.None, "No"), (SubtitlesInVideo.Track, "As a track"), (SubtitlesInVideo.Burned, "Burned in")], SubtitlesInVideo.None);
            Body.Children.Add(Row("In the video", _inVideo, 110, 10));
            Body.Children.Add(Text("A track can be switched on and off in the player. Burned-in subtitles are part of the picture and show everywhere.", "Petal.Caption").WithMargin(110, 4, 0, 0));
        }
        Body.Children.Add(Text("Runs on this PC. The graphics card speeds it up when it can.", "Petal.Caption").WithMarginT(12));

        PrimaryButton.Content = L.T("Transcribe");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    /// <summary>"العربية", "English", "Français"…: every language in its own words.</summary>
    private static string NativeName(string code)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).NativeName;
            return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.GetCultureInfo(code)) + name[1..] : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private void Sync()
    {
        bool arabic = _into.Value == "ar";
        _translator.Visibility = arabic && !_translator.IsReady ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = _model.IsReady && (!arabic || _translator.IsReady);
        FooterNote = _files.Count == 1 ? Path.GetFileNameWithoutExtension(_files[0]) + (_saveAs.Value == "txt" ? ".txt" : ".srt") : L.P(_files.Count, "{0} file", "{0} files");
    }

    private void Apply()
    {
        var modelPath = ModelStore.Find(_model.Model);
        if (modelPath is null)
            return;
        // Arabic subtitles: Whisper writes English (from any language), then Opus-MT turns it into Arabic.
        string into = _into.Value;
        var options = new TranscribeOptions(modelPath, ValueOf<string>(_language), into is "en" or "ar");
        string saveAs = _saveAs.Value;
        var inVideo = _inVideo?.Value ?? SubtitlesInVideo.None;
        var settings = SettingsStore.Current.Conversion;
        var tool = _tool;
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var scaled = inVideo == SubtitlesInVideo.None ? p : new ScaledProgress(p, 0, 0.8);
            var transcript = await Transcriber.TranscribeAsync(file, options, into == "ar" ? new ScaledProgress(scaled, 0, 0.7) : scaled, ct);
            if (transcript.IsEmpty)
                throw new InvalidOperationException(L.T("No speech was found in this file."));
            if (into == "ar")
            {
                var arabic = await Task.Run(() => Petal.Recipes.Translation.Translator.ToArabic(transcript.Segments.Select(s => s.Text).ToList(), new ScaledProgress(scaled, 0.7, 1), ct), ct);
                transcript = new Transcript(transcript.Segments.Select((s, i) => s with { Text = arabic[i] }).ToList(), "ar");
            }
            var directory = Path.GetDirectoryName(file)!;
            var stem = Path.GetFileNameWithoutExtension(file);
            var outputs = new List<string>();
            // Named like the video, so players load the subtitles by themselves.
            string? srt = null;
            if (saveAs is "srt" or "both")
            {
                srt = OutputNaming.Unique(directory, stem, "srt");
                await File.WriteAllTextAsync(srt, transcript.ToSrt(), new System.Text.UTF8Encoding(true), ct);
                outputs.Add(srt);
            }
            if (saveAs is "txt" or "both")
            {
                var txt = OutputNaming.Unique(directory, stem, "txt");
                await File.WriteAllTextAsync(txt, transcript.ToText(), new System.Text.UTF8Encoding(true), ct);
                outputs.Add(txt);
            }
            if (inVideo != SubtitlesInVideo.None)
            {
                var subtitles = srt ?? Path.Combine(Path.GetTempPath(), $"petal-{Guid.NewGuid():N}.srt");
                if (srt is null)
                    await File.WriteAllTextAsync(subtitles, transcript.ToSrt(), new System.Text.UTF8Encoding(false), ct);
                try
                {
                    var video = OutputNaming.ForTool(file, tool);
                    await Transcriber.AddSubtitlesAsync(file, subtitles, video, inVideo == SubtitlesInVideo.Burned, transcript.Language, settings, new ScaledProgress(p, 0.8, 1), ct);
                    outputs.Add(video);
                }
                finally
                {
                    if (srt is null)
                        File.Delete(subtitles);
                }
            }
            return (IReadOnlyList<string>)outputs;
        })).ToList();
        _ = _jobs.RunAsync("Transcribing", "Transcribed", work, _at);
    }

    /// <summary>Maps a step's 0–1 onto a part of the whole job.</summary>
    private sealed class ScaledProgress(IProgress<double> inner, double from, double to) : IProgress<double>
    {
        public void Report(double value) => inner.Report(from + (to - from) * Math.Clamp(value, 0, 1));
    }
}
