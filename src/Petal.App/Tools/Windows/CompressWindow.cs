using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Tools.Parts;
using Petal.App.Util;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Media;

namespace Petal.App.Tools.Windows;

public enum CompressMode
{
    Preset,
    TargetSize,
    Percentage,
}

/// <summary>What the user chose in the Compress window, independent of file type.</summary>
public sealed record CompressRequest(CompressMode Mode, bool Strong, long? TargetBytes, int? ReductionPercent, int? MaxSize);

/// <summary>Runs a compression for one file type; returns the output path.</summary>
public delegate Task<string> CompressBackend(string input, CompressRequest request, IProgress<double> progress, CancellationToken ct);

/// <summary>
/// Compress for every kind of file: a preset (Balanced / Strong), a target file size, or a percentage,
/// plus an optional size limit. The finished card reports how much was saved.
/// </summary>
public sealed class CompressWindow : ToolWindow
{
    /// <summary>Backends per family; images and PDFs register theirs from their engines.</summary>
    public static readonly Dictionary<FormatFamily, CompressBackend> Backends = new()
    {
        [FormatFamily.Audio] = MediaBackend,
        [FormatFamily.Video] = MediaBackend,
    };

    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Segmented<CompressMode> _mode;
    private readonly Segmented<bool> _preset;
    private readonly TextBlock _presetCaption = new();
    private readonly TextBox _target = new() { Width = 90, HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly LabeledSlider _percent;
    private readonly ComboBox _size;
    private readonly StackPanel _presetPanel = new();
    private readonly Grid _targetRow;
    private readonly Grid _percentRow;
    private readonly long _original;
    private readonly FormatFamily _family;

    public CompressWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 400)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        _family = Formats.FromPath(files[0])?.Family ?? FormatFamily.Video;
        _original = files.Sum(f => new FileInfo(f).Length);

        Body.Children.Add(Text(FileLabel(files), "Petal.Caption"));
        Body.Children.Add(Text($"Original size: {Sizes.Format(_original)}", "Petal.Body").WithMarginT(2));

        _mode = Segments([(CompressMode.Preset, "Preset"), (CompressMode.TargetSize, "Target size"), (CompressMode.Percentage, "Percentage")], CompressMode.Preset);
        _mode.Changed += _ => Sync();
        Body.Children.Add(_mode.WithMarginT(14));

        _preset = Segments([(false, "Balanced"), (true, "Strong")], false);
        _preset.Changed += _ => Sync();
        _presetCaption.Style = (Style)FindResource("Petal.Caption");
        _presetCaption.Margin = new Thickness(0, 6, 0, 0);
        _presetPanel.Children.Add(_preset);
        _presetPanel.Children.Add(_presetCaption);
        _presetPanel.Margin = new Thickness(0, 12, 0, 0);
        Body.Children.Add(_presetPanel);

        var targetBox = new StackPanel { Orientation = Orientation.Horizontal };
        _target.Text = (Math.Max(0.1, _original / 1_000_000.0 / Math.Max(1, files.Count) / 2)).ToString("0.#", CultureInfo.CurrentCulture);
        targetBox.Children.Add(_target);
        targetBox.Children.Add(Text("  MB per file", "Petal.Label"));
        _targetRow = Row("Target size", targetBox, 110, 12);
        Body.Children.Add(_targetRow);

        _percent = Slider(10, 90, 50, v => $"{v:0}% smaller", 5);
        _percentRow = Row("Reduce by", _percent, 110, 12);
        Body.Children.Add(_percentRow);

        var sizeOptions = _family switch
        {
            FormatFamily.Video => new[] { (0, "Keep original resolution"), (2160, "4K (2160p)"), (1080, "Full HD (1080p)"), (720, "HD (720p)"), (480, "SD (480p)") },
            FormatFamily.Image or FormatFamily.Vector => [(0, "Keep original dimensions"), (3840, "Up to 3840 px"), (2560, "Up to 2560 px"), (1920, "Up to 1920 px"), (1280, "Up to 1280 px")],
            _ => [],
        };
        _size = Choice(sizeOptions.Length > 0 ? sizeOptions : [(0, "Keep original")], 0);
        if (sizeOptions.Length > 0)
            Body.Children.Add(Row(_family == FormatFamily.Video ? "Resolution" : "Image size", _size, 110, 12));

        PrimaryButton.Content = "Compress";
        PrimaryButton.Click += (_, _) => Apply();
        PrimaryButton.IsEnabled = Backends.ContainsKey(_family);
        FooterNote = files.Count == 1 ? Path.GetFileName(OutputFor(files[0])) : $"{files.Count} files";
        Sync();
    }

    private string OutputFor(string file)
    {
        var ext = Formats.FromPath(file)?.Family == FormatFamily.Audio ? MediaCompressor.CompressedExtension(file) : null;
        return OutputNaming.ForTool(file, _tool, extensionOverride: ext == OutputNaming.ExtensionOf(file) ? null : ext);
    }

    private void Sync()
    {
        _presetPanel.Visibility = _mode.Value == CompressMode.Preset ? Visibility.Visible : Visibility.Collapsed;
        _targetRow.Visibility = _mode.Value == CompressMode.TargetSize ? Visibility.Visible : Visibility.Collapsed;
        _percentRow.Visibility = _mode.Value == CompressMode.Percentage ? Visibility.Visible : Visibility.Collapsed;
        _presetCaption.Text = _preset.Value ? "Much smaller files; some quality loss may be visible." : "Smaller files with little visible quality loss.";
    }

    private CompressRequest Request()
    {
        long? target = null;
        if (_mode.Value == CompressMode.TargetSize && double.TryParse(_target.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var mb) && mb > 0)
            target = (long)(mb * 1_000_000);
        int? percent = _mode.Value == CompressMode.Percentage ? (int)_percent.Value : null;
        int max = ValueOf<int>(_size);
        return new CompressRequest(_mode.Value, _preset.Value, target, percent, max > 0 ? max : null);
    }

    private void Apply()
    {
        var request = Request();
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var family = Formats.FromPath(file)?.Family ?? _family;
            var backend = Backends.GetValueOrDefault(family) ?? throw new NotSupportedException("This file type can't be compressed yet.");
            return (IReadOnlyList<string>)[await backend(file, request, p, ct)];
        })).ToList();
        long before = _original;
        _ = _jobs.RunAsync("Compressing", "Compressed", work, _at, outputs =>
        {
            long after = outputs.Where(System.IO.File.Exists).Sum(f => new FileInfo(f).Length);
            if (after <= 0) return null;
            double saved = 1 - (double)after / before;
            return saved > 0
                ? $"{Sizes.Format(before)} → {Sizes.Format(after)} ({saved:P0} smaller)"
                : "already as small as it gets";
        });
    }

    private static async Task<string> MediaBackend(string input, CompressRequest request, IProgress<double> progress, CancellationToken ct)
    {
        var tool = ToolRegistry.Get(ToolId.Compress);
        var ext = MediaCompressor.CompressedExtension(input);
        var output = OutputNaming.ForTool(input, tool, extensionOverride: ext == OutputNaming.ExtensionOf(input) ? null : ext);
        var options = new CompressOptions(
            request.Strong ? CompressPreset.Strong : CompressPreset.Balanced,
            request.TargetBytes,
            request.ReductionPercent,
            request.MaxSize) { Settings = Petal.App.Settings.SettingsStore.Current.Conversion };
        var result = await MediaCompressor.CompressAsync(input, output, options, progress, ct);
        return result.OutputPath;
    }
}
