using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.Core.Jobs;
using Petal.Core.Models;
using Petal.Core.Tools;
using Petal.Engines.Imaging;

namespace Petal.App.Tools.Windows;

/// <summary>
/// A photo for a passport, a visa or a residence permit: choose the country, and the person is cut from the background and
/// placed so the head is the size asked for. It comes out at its exact size, and in rows on a sheet to print.
/// </summary>
public sealed class IdPhotoWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly ModelPanel _model = new(ModelCatalog.Background);
    private readonly Image _picture = new() { Stretch = Stretch.Uniform, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(8) };
    private readonly ComboBox _country;
    private readonly Segmented<uint> _background;
    private readonly Segmented<string> _paper;
    private readonly LabeledSlider _zoom;
    private readonly LabeledSlider _across;
    private readonly LabeledSlider _down;
    private readonly LabeledSlider _width;
    private readonly LabeledSlider _height;
    private readonly StackPanel _custom = new();
    private readonly TextBlock _facts = Text("", "Petal.Caption");
    private readonly TextBlock _status = Text("", "Petal.Caption");
    private readonly CancellationTokenSource _closing = new();
    private IdPhotoSource? _source;
    private bool _busy;

    /// <summary>The background the country asks for, chosen by the country.</summary>
    private const uint AsAsked = 0xFFFFFFFF;

    public IdPhotoWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 520)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        var remembered = SettingsStore.Current.IdPhotoSpec;
        var specs = IdPhotoCatalog.All
            .Select(s => (Spec: s, Label: Label(s)))
            .OrderBy(s => s.Label, StringComparer.Create(System.Globalization.CultureInfo.CurrentUICulture, false))
            .ToList();
        _country = new ComboBox { MinWidth = 160, IsTextSearchEnabled = true };
        foreach (var (spec, label) in specs)
            _country.Items.Add(new ComboBoxItem { Content = label, Tag = spec });
        _country.SelectedItem = _country.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((IdPhotoSpec)i.Tag).Id == remembered)
            ?? _country.Items.OfType<ComboBoxItem>().First(i => ((IdPhotoSpec)i.Tag).Id == IdPhotoCatalog.Default.Id);

        _background = Segments<uint>([(AsAsked, "As required"), (0xFFFFFF, "White"), (0xEDEDED, "Light grey"), (0xBFD8F2, "Light blue")], AsAsked);
        _paper = Segments(IdPhotoPaper.All.Select(p => (p.Id, p.Name)), SettingsStore.Current.IdPhotoPaper is { Length: > 0 } paper ? paper : IdPhotoPaper.Print4By6.Id);
        _zoom = Slider(80, 120, 100, v => L.F("{0}%", (int)v));
        _across = Slider(-15, 15, 0, v => L.F("{0}%", (int)v));
        _down = Slider(-15, 15, 0, v => L.F("{0}%", (int)v));
        _width = Slider(20, 60, 35, v => L.F("{0} mm", (int)v));
        _height = Slider(25, 80, 45, v => L.F("{0} mm", (int)v));
        // Left means left on the picture, whichever way the interface reads.
        _across.FlowDirection = FlowDirection.LeftToRight;
        _custom.Children.Add(Row("Width", _width, 110, 8));
        _custom.Children.Add(Row("Height", _height, 110, 4));

        Body.Children.Add(PreviewPanel(_picture, 300));
        Body.Children.Add(Row("Country", _country, 110, 12));
        Body.Children.Add(_custom);
        Body.Children.Add(Row("Background", _background, 110, 8));
        Body.Children.Add(Row("Sheet", _paper, 110, 8));
        Body.Children.Add(_model);
        Body.Children.Add(Row("Head size", _zoom, 110, 8));
        Body.Children.Add(Row("Left and right", _across, 110, 4));
        Body.Children.Add(Row("Up and down", _down, 110, 4));
        _facts.Margin = new Thickness(0, 10, 0, 0);
        _facts.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_facts);
        _status.Margin = new Thickness(0, 6, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_status);
        Body.Children.Add(Text("Rules change, and some offices ask for more: check the size with the office that asked for the photo.", "Petal.Caption").WithMarginT(6));

        _country.SelectionChanged += (_, _) => Remember();
        _background.Changed += _ => Draw();
        _width.Changed += _ => Draw();
        _height.Changed += _ => Draw();
        _paper.Changed += _ => Remember();
        _zoom.Changed += _ => Draw();
        _across.Changed += _ => Draw();
        _down.Changed += _ => Draw();
        _model.Ready += async () => await ReadAsync();
        PrimaryButton.Content = L.T("Save Copy");
        PrimaryButton.Click += (_, _) => Save();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _source?.Dispose();
        };
        Loaded += async (_, _) => await ReadAsync();
        Sync();
    }

    private static string Label(IdPhotoSpec spec) =>
        spec.Document.Length == 0 ? L.T(spec.Country) : L.T(spec.Country) + " · " + L.T(spec.Document);

    private IdPhotoSpec Spec => (IdPhotoSpec)((ComboBoxItem)_country.SelectedItem).Tag is { Id: "custom" } ? IdPhotoCatalog.Sized(_width.Value, _height.Value) : (IdPhotoSpec)((ComboBoxItem)_country.SelectedItem).Tag;

    private IdPhotoPaper Paper => IdPhotoPaper.All.First(p => p.Id == _paper.Value);

    private IdPhotoAdjust Adjust => new(_zoom.Value / 100, _across.Value / 100, _down.Value / 100);

    private uint Background => _background.Value == AsAsked ? Spec.Background : _background.Value;

    private void Remember()
    {
        var settings = SettingsStore.Current;
        settings.IdPhotoSpec = Spec.Id;
        settings.IdPhotoPaper = _paper.Value;
        SettingsStore.Save();
        Draw();
    }

    private async Task ReadAsync()
    {
        _busy = true;
        Sync();
        _status.Text = L.T("Finding the person in the photo…");
        try
        {
            var source = await IdPhoto.PrepareAsync(_file, _closing.Token);
            _source?.Dispose();
            _source = source;
            _status.Text = "";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Reading a photo for an ID photo");
            _status.Text = L.T("Couldn't open this image.");
        }
        _busy = false;
        Draw();
        Sync();
    }

    private void Draw()
    {
        Sync();
        if (_source is not { } source || _busy)
            return;
        var spec = Spec;
        var crop = IdPhoto.Layout(source.Head, spec, Adjust);
        using var print = IdPhoto.Render(source, crop, spec, Background);
        print.Format = MagickFormat.Bmp;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(print.ToByteArray());
        bitmap.EndInit();
        bitmap.Freeze();
        _picture.Source = bitmap;
        var (across, down, _) = IdPhoto.Fit(spec, Paper);
        string size = spec.WidthMm.ToString("0.#"), tall = spec.HeightMm.ToString("0.#"), head = IdPhoto.HeadMmIn(source.Head, crop, spec).ToString("0.#");
        _facts.Text = (spec.HeadStated
                ? L.F("{0} × {1} mm, head {2} mm of the {3}–{4} mm asked for.", size, tall, head, spec.HeadMinMm.ToString("0.#"), spec.HeadMaxMm.ToString("0.#"))
                : L.F("{0} × {1} mm, head {2} mm. This office states no head size: this is the usual one.", size, tall, head))
            + " " + L.P(across * down, "{0} on a sheet.", "{0} on a sheet.");
        _status.Text = string.Join(" ", IdPhoto.Check(source, crop, spec).Select(Words));
    }

    private static string Words(IdPhotoProblem problem) => problem switch
    {
        IdPhotoProblem.NoFace => L.T("No face was found: move the person into place with the sliders."),
        IdPhotoProblem.SeveralFaces => L.T("There is more than one face here: the largest was taken."),
        IdPhotoProblem.LowResolution => L.T("This photo is small for printing at this size and may come out soft."),
        IdPhotoProblem.TooClose => L.T("The photo ends too close to the person: use one that shows the shoulders and space around them."),
        _ => "",
    };

    private void Sync()
    {
        _custom.Visibility = _country.SelectedItem is ComboBoxItem { Tag: IdPhotoSpec { Id: "custom" } } ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = !_busy && _source is { Mask: not null };
        _model.Wanted = !BackgroundRemover.IsReady;
        if (_source is null)
            FooterNote = "";
        else
            FooterNote = Path.GetFileName(OutputFor(_file, _tool));
    }

    /// <summary>"me.jpg" becomes "me ID Photo.jpg"; the sheet is beside it, with the paper in its name.</summary>
    internal static string OutputFor(string file, ToolInfo tool) => OutputNaming.ForTool(file, tool);

    internal static string SheetFor(string print, IdPhotoPaper paper) =>
        OutputNaming.Unique(Path.GetDirectoryName(print)!, Path.GetFileNameWithoutExtension(print) + " " + paper.Id, "jpg");

    private void Save()
    {
        if (_source is not { } source)
            return;
        var spec = Spec;
        var paper = Paper;
        var crop = IdPhoto.Layout(source.Head, spec, Adjust);
        var print = IdPhoto.Render(source, crop, spec, Background);
        var sheet = IdPhoto.Sheet(print, spec, paper);
        var output = OutputFor(_file, _tool);
        var sheetFile = SheetFor(output, paper);
        Close();
        _ = _jobs.RunAsync("Saving", "ID photo saved", [new WorkItem(_file, Path.GetFileName(_file), async (_, ct) =>
        {
            using (print)
            using (sheet)
            {
                await IdPhoto.SaveAsync(print, output, ct);
                await IdPhoto.SaveAsync(sheet, sheetFile, ct);
            }
            return (IReadOnlyList<string>)[output, sheetFile];
        })], _at);
    }
}
