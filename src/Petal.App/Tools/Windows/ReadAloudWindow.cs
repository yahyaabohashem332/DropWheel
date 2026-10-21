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
using Petal.App.Util;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Media;
using Petal.Recipes;

namespace Petal.App.Tools.Windows;

/// <summary>
/// Read Aloud: a document becomes an MP3, spoken by a Windows voice or a natural Piper voice
/// (Arabic included), all on this PC. The voice that suits the document's language is picked.
/// </summary>
public sealed class ReadAloudWindow : ToolWindow
{
    private static readonly double[] Rates = [0.75, 1, 1.25, 1.5];
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly IReadOnlyList<SpeechVoice> _voices = Speech.AllVoices();
    private readonly TextBlock _summary;
    private readonly ComboBox _voice;
    private readonly Segmented<double> _rate;
    private readonly ModelPanel _download;
    private readonly Border _noVoice = new();
    private readonly Button _listen = new() { Content = L.T("Listen"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(110, 12, 0, 0) };
    private readonly MediaPlayer _player = new();
    private CancellationTokenSource? _preview;
    private string? _text;
    private string _language = "en";

    public ReadAloudWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 440)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(Text(Path.GetFileName(file), "Petal.Section"));
        _summary = Text(L.T("Reading the document…"), "Petal.Caption");
        _summary.Margin = new Thickness(0, 4, 0, 0);
        Body.Children.Add(_summary);

        var options = _voices.Select(v => (v.Id, Label(v))).ToList();
        _voice = Choice(options, options.Count > 0 ? options[0].Id : "");
        _voice.MaxDropDownHeight = 320;
        _voice.SelectionChanged += (_, _) => Sync();
        Body.Children.Add(Row("Voice", _voice, 110, 14));

        _rate = Segments(Rates.Select(r => (r, r.ToString("0.##", CultureInfo.InvariantCulture) + "×")), 1.0);
        Body.Children.Add(Row("Speed", _rate, 110, 12));

        _download = new ModelPanel(Speech.Kareem.Models);
        _download.Ready += Sync;
        Body.Children.Add(_download);

        // When nothing installed speaks the document's language.
        _noVoice.Visibility = Visibility.Collapsed;
        _noVoice.Margin = new Thickness(0, 12, 0, 0);
        _noVoice.CornerRadius = new CornerRadius(8);
        _noVoice.Padding = new Thickness(10, 8, 10, 8);
        _noVoice.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        var settings = new Button { Content = L.T("Open Speech Settings"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        settings.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:speech") { UseShellExecute = true });
        var why = Text("No voice for this document's language is installed. Windows can add one under Settings › Time & language › Speech.", "Petal.Body");
        why.TextWrapping = TextWrapping.Wrap;
        _noVoice.Child = new StackPanel { Children = { why, settings } };
        Body.Children.Add(_noVoice);

        _listen.Click += async (_, _) => await PreviewAsync();
        Body.Children.Add(_listen);
        Body.Children.Add(Text("Spoken on this PC; the text never leaves it.", "Petal.Caption").WithMarginT(12));

        PrimaryButton.Content = L.T("Save Audio");
        PrimaryButton.Click += (_, _) => Save();
        Closed += (_, _) =>
        {
            _preview?.Cancel();
            _player.Close();
        };
        Sync();
        _ = LoadAsync();
    }

    private static string Label(SpeechVoice voice)
    {
        // Windows' voices are named "Microsoft David" and so on; the maker adds nothing.
        var label = voice.Name.Replace("Microsoft ", "") + " · " + LanguageName(voice.Language);
        if (voice.Kind == VoiceKind.Natural)
            label += " · " + L.T("natural");
        if (!voice.IsReady)
            label += " · " + L.F("{0} download", Sizes.Format(voice.MissingBytes));
        return label;
    }

    private static string LanguageName(string tag)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            return L.IsArabic ? culture.Parent.NativeName is { Length: > 0 } native && culture.TwoLetterISOLanguageName == "ar" ? native : culture.Parent.DisplayName : culture.Parent.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    private SpeechVoice? Selected => _voices.FirstOrDefault(v => v.Id == ValueOf<string>(_voice));

    private async Task LoadAsync()
    {
        try
        {
            var converters = App.Current?.Controller?.Jobs.Converters ?? EngineSetup.CreateConverters();
            var text = await Task.Run(() => DocumentText.ReadAsync(_file, converters, SettingsStore.Current.Conversion));
            _text = text;
            _language = Speech.GuessLanguage(text);
            int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var minutes = Math.Max(1, (int)Math.Round(Speech.EstimateDuration(text).TotalMinutes));
            _summary.Text = words == 0
                ? L.T("This document has no text to read.")
                : L.P(words, "{0} word", "{0} words") + " · " + L.P(minutes, "about {0} minute", "about {0} minutes");
            if (Speech.Choose(_language, _voices) is { } voice)
                _voice.SelectedItem = _voice.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == voice.Id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Reading a document's text to speak it");
            _summary.Text = L.T("Petal couldn't read the text of this document.");
        }
        Sync();
    }

    private void Sync()
    {
        var voice = Selected;
        bool natural = voice is { Kind: VoiceKind.Natural };
        if (natural)
            _download.Models = voice!.Models;
        _download.Visibility = natural && !voice!.IsReady ? Visibility.Visible : Visibility.Collapsed;
        _noVoice.Visibility = _text is not null && !_voices.Any(v => v.Speaks(_language)) ? Visibility.Visible : Visibility.Collapsed;
        bool ready = voice is { IsReady: true } && !string.IsNullOrWhiteSpace(_text);
        PrimaryButton.IsEnabled = ready;
        _listen.IsEnabled = ready || _preview is not null;
        FooterNote = Path.GetFileName(OutputNaming.ForTool(_file, _tool));
        // The label's download size disappears once the voice is here.
        if (voice is not null && _voice.SelectedItem is ComboBoxItem item)
            item.Content = Label(voice);
    }

    /// <summary>The first sentence or two, spoken right away so the voice and speed can be judged.</summary>
    private async Task PreviewAsync()
    {
        if (_preview is not null)
        {
            _preview.Cancel();
            _player.Stop();
            return;
        }
        if (Selected is not { IsReady: true } voice || _text is null)
            return;
        var sample = Speech.Chunks(_text, 260).FirstOrDefault() ?? "";
        var wav = Path.Combine(Path.GetTempPath(), "Petal", $"preview-{Guid.NewGuid():N}.wav");
        _preview = new CancellationTokenSource();
        _listen.Content = L.T("Stop");
        try
        {
            await Task.Run(() => Speech.SpeakToFileAsync(sample, voice, _rate.Value, wav, null, null, _preview.Token));
            _player.Open(new Uri(wav));
            var done = new TaskCompletionSource();
            void Ended(object? s, EventArgs e) => done.TrySetResult();
            _player.MediaEnded += Ended;
            _player.MediaFailed += (_, _) => done.TrySetResult();
            using (_preview.Token.Register(() => done.TrySetResult()))
            {
                _player.Play();
                await done.Task;
            }
            _player.MediaEnded -= Ended;
            _player.Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Previewing a voice");
            FooterNote = L.T("That voice didn't work.");
        }
        finally
        {
            _preview?.Dispose();
            _preview = null;
            _listen.Content = L.T("Listen");
            try { File.Delete(wav); } catch (IOException) { }
            Sync();
        }
    }

    private void Save()
    {
        if (Selected is not { IsReady: true } voice || _text is not { } text)
            return;
        double rate = _rate.Value;
        var output = OutputNaming.ForTool(_file, _tool);
        var title = Path.GetFileNameWithoutExtension(_file);
        Close();
        _ = _jobs.RunAsync("Reading aloud", "Audio saved", [new WorkItem(_file, Path.GetFileName(_file), async (p, ct) =>
        {
            await Speech.SpeakToFileAsync(text, voice, rate, output, title, p, ct);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }
}
