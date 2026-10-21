using System.IO;
using System.Text;
using System.Windows;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Media;
using Petal.Recipes.Translation;

namespace Petal.App.Tools.Windows;

// ---------------------------------------------------------------------------------------------
// Remove Noise — DeepFilterNet on this PC: hum, hiss and room noise out of speech.
// ---------------------------------------------------------------------------------------------

public sealed class RemoveNoiseWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly ModelPanel _model = new(VoiceCleaner.Models);
    private readonly Segmented<double> _strength;

    public RemoveNoiseWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 420)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(Text(FileLabel(files), "Petal.Section"));
        Body.Children.Add(_model);
        _strength = Segments([(12.0, "Gentle"), (24.0, "Strong"), (100.0, "Full")], 100.0);
        Body.Children.Add(Row("Amount", _strength, 100, 14));
        Body.Children.Add(Text("Gentle leaves a little of the room's sound, so the voice stays natural. Full takes out all the noise it can.", "Petal.Caption").WithMargin(100, 4, 0, 0));
        Body.Children.Add(Text("Made for speech: interviews, lectures, calls and videos. In a video only the sound changes.", "Petal.Caption").WithMarginT(12));
        _model.Ready += Sync;
        PrimaryButton.Content = L.T("Remove Noise");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    private void Sync()
    {
        PrimaryButton.IsEnabled = _model.IsReady;
        FooterNote = _files.Count == 1 ? Path.GetFileName(OutputNaming.ForTool(_files[0], _tool)) : L.P(_files.Count, "{0} file", "{0} files");
    }

    private void Apply()
    {
        var options = new CleanVoiceOptions(_strength.Value) { Settings = SettingsStore.Current.Conversion };
        var tool = _tool;
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var output = OutputNaming.ForTool(file, tool);
            await VoiceCleaner.CleanAsync(file, output, options, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Removing noise", "Noise removed", work, _at);
    }
}

// ---------------------------------------------------------------------------------------------
// Separate Vocals — Demucs on this PC: the voice apart from the music.
// ---------------------------------------------------------------------------------------------

public sealed class SeparateVocalsWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly ModelPanel _model = new(StemSplitter.Models);
    private readonly Segmented<StemSet> _stems;

    public SeparateVocalsWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 420)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(Text(FileLabel(files), "Petal.Section"));
        Body.Children.Add(_model);
        _stems = Segments([(StemSet.VocalsAndMusic, "Vocals and music"), (StemSet.FourStems, "Four parts")], StemSet.VocalsAndMusic);
        Body.Children.Add(Row("Split into", _stems, 100, 14));
        Body.Children.Add(Text("Four parts: vocals, drums, bass and the other instruments, each in its own file.", "Petal.Caption").WithMargin(100, 4, 0, 0));
        Body.Children.Add(Text("Runs on this PC. A song of a few minutes takes a few minutes.", "Petal.Caption").WithMarginT(12));
        _model.Ready += Sync;
        PrimaryButton.Content = L.T("Separate");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    private void Sync()
    {
        PrimaryButton.IsEnabled = _model.IsReady;
        FooterNote = _files.Count == 1 ? Path.GetFileName(OutputNaming.ForTool(_files[0], _tool)) : L.P(_files.Count, "{0} file", "{0} files");
    }

    private void Apply()
    {
        var options = new SplitStemsOptions(_stems.Value) { Settings = SettingsStore.Current.Conversion };
        var tool = _tool;
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var folder = OutputNaming.ForTool(file, tool);
            // Songs keep their format; the sound of a video becomes M4A.
            var extension = Formats.FromPath(file)?.Family == FormatFamily.Audio ? OutputNaming.ExtensionOf(file) : "m4a";
            return await StemSplitter.SplitAsync(file, folder, extension, options, p, ct);
        })).ToList();
        _ = _jobs.RunAsync("Separating", "Separated", work, _at);
    }
}

// ---------------------------------------------------------------------------------------------
// Translate to Arabic — subtitles through Opus-MT on this PC.
// ---------------------------------------------------------------------------------------------

public sealed class TranslateSubtitlesWindow : ToolWindow
{
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly ModelPanel _model = new(Translator.Models);

    public TranslateSubtitlesWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 420)
    {
        _files = files;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(Text(FileLabel(files), "Petal.Section"));
        Body.Children.Add(Text("English subtitles become Arabic, cue by cue, with their timing unchanged. Saved beside the original with “.ar” in the name, so players offer them as Arabic.", "Petal.Body").WithMarginT(8));
        Body.Children.Add(_model);
        Body.Children.Add(Text("For subtitles in another language, use Transcribe on the video with Arabic subtitles.", "Petal.Caption").WithMarginT(12));
        _model.Ready += Sync;
        PrimaryButton.Content = L.T("Translate");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    /// <summary>"talk.srt" becomes "talk.ar.srt".</summary>
    internal static string OutputFor(string file) =>
        OutputNaming.Unique(OutputNaming.DirectoryFor(file), Path.GetFileNameWithoutExtension(file) + ".ar", OutputNaming.ExtensionOf(file));

    private void Sync()
    {
        PrimaryButton.IsEnabled = _model.IsReady;
        FooterNote = _files.Count == 1 ? Path.GetFileName(OutputFor(_files[0])) : L.P(_files.Count, "{0} file", "{0} files");
    }

    private void Apply()
    {
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var text = await File.ReadAllTextAsync(file, ct);
            var vtt = OutputNaming.ExtensionOf(file).Equals("vtt", StringComparison.OrdinalIgnoreCase);
            var arabic = await Task.Run(() => Translator.TranslateSubtitles(text, vtt, p, ct), ct);
            var output = OutputFor(file);
            await File.WriteAllTextAsync(output, arabic, new UTF8Encoding(true), ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Translating", "Translated", work, _at);
    }
}
