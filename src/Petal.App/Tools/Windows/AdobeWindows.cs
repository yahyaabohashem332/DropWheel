using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.App.Util;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Files.Design;
using Petal.Engines.Files.Metadata;
using Petal.Engines.Imaging;
using F = Petal.Core.Formats.Formats;

namespace Petal.App.Tools.Windows;

/// <summary>Adobe files (Photoshop, camera RAW, Illustrator, EPS, InDesign, XD, Premiere, After Effects, swatches) in the tool plumbing.</summary>
public static class AdobeToolSetup
{
    public static void Register()
    {
        ToolLauncher.RegisterWindow(ToolId.ExportLayers, (t, f, j, a) => new ExportLayersWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.FileDetails, (t, f, j, a) => new FileDetailsWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.CollectFiles, (t, f, j, a) => new CollectFilesWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.Palette, (t, f, j, a) => new PaletteWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.ExtractColours, (t, f, j, a) => new ExtractColoursWindow(t, f[0], j, a));
        ToolLauncher.RegisterDirect(ToolId.SlimPhotoshop, (tool, files, jobs, at) => jobs.RunAsync("Removing Photoshop history", "Photoshop history removed",
            files.Select(f => new WorkItem(f, Path.GetFileName(f), async (_, ct) =>
            {
                var output = OutputNaming.ForTool(f, tool);
                await PhotoshopTools.RemoveHistoryAsync(f, output, ct);
                return (IReadOnlyList<string>)[output];
            })).ToList(), at, outputs => SavedText(files, outputs)));
        ToolLauncher.RegisterDirect(ToolId.SavePreview, (tool, files, jobs, at) => jobs.RunAsync("Saving the preview", "Preview saved",
            files.Select(f => new WorkItem(f, Path.GetFileName(f), (_, ct) => Task.Run(() =>
            {
                var output = OutputNaming.ForTool(f, tool);
                SavePreview(f, output);
                return (IReadOnlyList<string>)[output];
            }, ct))).ToList(), at));
    }

    /// <summary>"4.1 MB smaller" for the slimmed files together.</summary>
    private static string? SavedText(IReadOnlyList<string> inputs, IReadOnlyList<string> outputs)
    {
        long before = inputs.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        long after = outputs.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        return before > after ? L.F("{0} smaller", Sizes.Format(before - after)) : null;
    }

    /// <summary>The picture a file carries for previews (EPS, InDesign, XD), as a PNG.</summary>
    internal static void SavePreview(string input, string output)
    {
        MagickImage? image = null;
        if (F.FromPath(input) == F.Eps)
            image = EpsFile.LoadPreview(input);
        if (image is null && DesignDocuments.CanRead(input) && DesignDocuments.Preview(input) is { } bytes)
            image = new MagickImage(bytes);
        if (image is null)
            throw new InvalidOperationException("This file has no preview picture.");
        using (image)
        {
            image.Strip();
            image.Write(output, MagickFormat.Png);
        }
    }

    // ─────────────── Shared pieces ───────────────

    internal static TextBlock Data(string text, string style = "Petal.Body", bool leftToRight = false)
    {
        var block = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource(style), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text.Length > 40 ? text : null };
        if (leftToRight)
        {
            block.FlowDirection = FlowDirection.LeftToRight;
            // Left is the start of the line, mirrored in Arabic.
            block.HorizontalAlignment = HorizontalAlignment.Left;
        }
        return block;
    }

    /// <summary>A line with a green tick or a red cross, a name and a smaller note under it.</summary>
    internal static FrameworkElement StatusLine(bool ok, string title, string? note, bool titleIsPath = false)
    {
        var icon = ToolWindow.ThemedIcon(ok ? "circleCheck" : "circleX", 14, ok ? "Petal.Success" : "Petal.Danger");
        icon.Margin = new Thickness(0, 3, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var words = new StackPanel { Children = { Data(title, "Petal.Body", titleIsPath) } };
        if (note is { Length: > 0 })
            words.Children.Add(Data(note, "Petal.Caption"));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(words);
        return row;
    }

    /// <summary>A colour chip with its name and hex value; clicking it copies the hex value.</summary>
    internal static FrameworkElement Chip(Swatch swatch, Action<string> copied, double size = 60)
    {
        var (r, g, b) = swatch.Rgb;
        var colour = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = $"{swatch.Name} · {swatch.Hex}" + (swatch.Spot ? " · " + L.T("spot colour") : ""),
        };
        colour.MouseLeftButtonUp += (_, _) =>
        {
            Clipboard.SetText(swatch.Hex);
            copied(swatch.Hex);
        };
        var panel = new StackPanel { Width = size + 16, Margin = new Thickness(0, 0, 8, 10), Children = { colour } };
        // Extracted colours are named by their value: show it once.
        if (swatch.Name != swatch.Hex)
        {
            bool arabic = swatch.Name.Any(c => c is >= '\u0600' and <= '\u06FF');
            var name = Data(swatch.Name, "Petal.Caption", leftToRight: !arabic);
            name.MaxWidth = size + 14;
            panel.Children.Add(name);
        }
        panel.Children.Add(Data(swatch.Hex, "Petal.Caption", leftToRight: true));
        return panel;
    }

    internal static Image Picture(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return new Image { Source = bitmap, Stretch = Stretch.Uniform, FlowDirection = FlowDirection.LeftToRight };
    }

    internal static void SaveSwatches(IReadOnlyList<Swatch> swatches, string format, string output, string title)
    {
        switch (format)
        {
            case "ase": Swatches.WriteAse(swatches, output); break;
            case "aco": Swatches.WriteAco(swatches, output); break;
            case "css": File.WriteAllText(output, Swatches.ToCss(swatches), new System.Text.UTF8Encoding(false)); break;
            case "json": File.WriteAllText(output, Swatches.ToJson(swatches), new System.Text.UTF8Encoding(false)); break;
            case "gpl": File.WriteAllText(output, Swatches.ToGpl(swatches, title), new System.Text.UTF8Encoding(false)); break;
            default: Swatches.WriteSheet(swatches, output); break;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Export Layers — every layer of a Photoshop document as its own PNG, named after it.
// ---------------------------------------------------------------------------------------------

public sealed class ExportLayersWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Segmented<bool> _canvas;
    private readonly CheckBox _hidden = new() { Content = L.T("Include hidden layers"), Margin = new Thickness(0, 12, 0, 0) };

    public ExportLayersWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 440)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(Text(FileLabel(files), "Petal.Section"));
        if (files.Count == 1)
        {
            try
            {
                var psd = PsdFile.Read(files[0]);
                int withPixels = psd.Layers.Count(l => l.FrameIndex >= 0), hidden = psd.Layers.Count(l => l.FrameIndex >= 0 && l.Hidden);
                var summary = L.P(withPixels, "{0} layer", "{0} layers") + (hidden > 0 ? " · " + L.P(hidden, "{0} hidden", "{0} hidden") : "");
                Body.Children.Add(Text(summary, "Petal.Caption").WithMarginT(4));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
            {
                Body.Children.Add(Text("This isn't a Photoshop file.", "Petal.Caption").WithMarginT(4));
            }
        }
        _canvas = Segments([(false, "Cropped to the layer"), (true, "On the full canvas")], false);
        Body.Children.Add(Row("Each layer", _canvas, 100, 14));
        Body.Children.Add(_hidden);
        Body.Children.Add(Text("Saved as PNG files named after the layers, numbered from the top of the Layers panel, in a folder beside the file.", "Petal.Caption").WithMarginT(12));
        PrimaryButton.Content = L.T("Export Layers");
        PrimaryButton.Click += (_, _) => Apply();
        FooterNote = files.Count == 1 ? Path.GetFileName(OutputNaming.ForTool(files[0], tool)) : L.P(files.Count, "{0} file", "{0} files");
    }

    private void Apply()
    {
        var options = new LayerExportOptions(_canvas.Value, _hidden.IsChecked == true);
        var tool = _tool;
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var folder = OutputNaming.ForTool(file, tool);
            await PhotoshopTools.ExportLayersAsync(file, folder, options, p, ct);
            return (IReadOnlyList<string>)[folder];
        })).ToList();
        _ = _jobs.RunAsync("Exporting layers", "Layers exported", work, _at);
    }
}

// ---------------------------------------------------------------------------------------------
// File Info — what a design file or project holds: size and colour, layers, pages or
// artboards, fonts (installed here or not), linked files (found or missing), swatches.
// ---------------------------------------------------------------------------------------------

public sealed class FileDetailsWindow : ToolWindow
{
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly StackPanel _content = new();
    private readonly TextBlock _status;

    public FileDetailsWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 520)
    {
        _file = file;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(AdobeToolSetup.Data(Path.GetFileName(file), "Petal.Section"));
        _status = Text("Reading the file…", "Petal.Caption");
        _status.Margin = new Thickness(0, 6, 0, 0);
        Body.Children.Add(_status);
        Body.Children.Add(new ScrollViewer { MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _content });
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        if (ProjectCollector.CanCollect(file))
        {
            ResetButton.Content = L.T("Collect Files…");
            ResetButton.Visibility = Visibility.Visible;
            ResetButton.Click += (_, _) =>
            {
                Close();
                new CollectFilesWindow(ToolRegistry.Get(ToolId.CollectFiles), _file, _jobs, _at).ShowNear(_at);
            };
        }
        Loaded += async (_, _) => await FillAsync();
    }

    private sealed record Details(string Kind, string? Application, List<(string Label, string Value)> Facts, IReadOnlyList<CollectFont> Fonts,
        IReadOnlyList<(string Name, bool Found, string? Note)> Files, string FilesTitle, IReadOnlyList<Swatch> Swatches, byte[]? Preview);

    private async Task FillAsync()
    {
        try
        {
            var details = await Task.Run(() => Read(_file));
            Show(details);
            // The right-to-left mark keeps "(IDML)" at the end of the Arabic kind, before the Latin application name.
            _status.Text = L.T(details.Kind) + (details.Application is { Length: > 0 } app ? (L.IsArabic ? "\u200F" : "") + " · " + app : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Reading file details");
            _status.Text = L.T("Petal couldn't read this file.") + " " + L.T(ex.Message);
        }
    }

    private static Details Read(string file)
    {
        var format = F.FromPath(file);
        var details = format == F.Psd || format == F.Psb ? Photoshop(file) : format == F.Raw ? Raw(file) : Design(file, format);
        // Pictures get a real preview (Photoshop's stored thumbnail is only 160 pixels); projects keep the one they carry.
        if (format?.Family == FormatFamily.Vector)
        {
            try
            {
                return details with { Preview = ImagePreview.DecodePngAsync(file, 700).GetAwaiter().GetResult() };
            }
            catch (Exception ex) when (ex is MagickException or InvalidOperationException or InvalidDataException or IOException)
            {
                // Keep whatever preview the file stored.
            }
        }
        return details;
    }

    private static Details Design(string file, FileFormat? format)
    {
        var d = DesignDocuments.Read(file);
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var files = d.Links.Select(link =>
        {
            var found = ProjectCollector.Resolve(link, folder);
            return (link, found is not null, found is not null && !found.Equals(link, StringComparison.OrdinalIgnoreCase) ? L.F("Found at {0}", found) : (string?)null);
        }).ToList();
        byte[]? preview = d.Preview;
        if (preview is null && format == F.Eps && EpsFile.LoadPreview(file) is { } eps)
        {
            using (eps)
                preview = eps.ToByteArray(MagickFormat.Png);
        }
        return new Details(d.Kind, d.Application, [.. d.Facts], d.Fonts.Select(f => new CollectFont(f, FontIndex.Find(f))).ToList(),
            files, "Linked files", [], preview) { };
    }

    private static Details Photoshop(string file)
    {
        var psd = PsdFile.Read(file);
        var mode = L.T(psd.ColorMode);
        var bits = L.F("{0}-bit", psd.Depth);
        var facts = new List<(string, string)>
        {
            ("Size", $"{psd.Width} × {psd.Height} px"),
            ("Resolution", L.F("{0} ppi", Math.Round(psd.DpiX).ToString(CultureInfo.InvariantCulture))),
            ("Colour", $"{mode}, {bits}"),
        };
        int pixels = psd.Layers.Count(l => l.Kind == PsdLayerKind.Pixels), text = psd.Layers.Count(l => l.Kind == PsdLayerKind.Text);
        int smart = psd.Layers.Count(l => l.Kind == PsdLayerKind.SmartObject), groups = psd.Layers.Count(l => l.Kind == PsdLayerKind.Group);
        var parts = new List<string> { L.P(pixels + text + smart, "{0} layer", "{0} layers") };
        if (text > 0) parts.Add(L.P(text, "{0} text layer", "{0} text layers"));
        if (smart > 0) parts.Add(L.P(smart, "{0} smart object", "{0} smart objects"));
        if (groups > 0) parts.Add(L.P(groups, "{0} group", "{0} groups"));
        facts.Add(("Layers", string.Join(" · ", parts)));
        int hidden = psd.Layers.Count(l => l.FrameIndex >= 0 && l.Hidden);
        if (hidden > 0)
            facts.Add(("Hidden layers", hidden.ToString(CultureInfo.InvariantCulture)));
        facts.Add(("Flat copy saved", L.T(psd.HasComposite ? "Yes" : "No")));
        int history = PhotoshopTools.HistoryEntries(psd);
        if (history > 0)
            facts.Add(("Photoshop history", L.P(history, "{0} entry", "{0} entries")));
        var xmp = psd.Xmp is { Length: > 0 } x ? XmpPacket.Parse(x) : null;
        var fonts = psd.Fonts.Select(f => new DesignFont(f, null, null, f, null)).Select(f => new CollectFont(f, FontIndex.Find(f))).ToList();
        var placed = psd.PlacedFiles.Select(p => (p.Name, !p.Linked || p.Path is { } path && File.Exists(path), p.Linked ? p.Path ?? L.T("linked") : L.T("embedded"))).ToList();
        return new Details(psd.IsLarge ? "Large Photoshop document (PSB)" : "Photoshop document", xmp?.CreatorTool, facts, fonts, placed, "Placed files", [], psd.Thumbnail);
    }

    private static Details Raw(string file)
    {
        var fields = ExifToolMetadata.ReadAsync(file).GetAwaiter().GetResult();
        string? Find(params string[] names) => fields.FirstOrDefault(f => names.Contains(f.Name.Replace(" ", ""), StringComparer.OrdinalIgnoreCase))?.Value;
        var facts = new List<(string, string)>();
        foreach (var (label, value) in ExifToolMetadata.Summary(fields))
            facts.Add((label, value));
        if (Find("LensModel", "LensID", "Lens") is { } lens)
            facts.Add(("Lens", lens));
        var exposure = new List<string>();
        if (Find("ExposureTime") is { } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            exposure.Add(seconds < 1 ? $"1/{Math.Round(1 / seconds)} s" : $"{seconds:0.#} s");
        if (Find("FNumber") is { } f)
            exposure.Add("f/" + f);
        if (Find("ISO") is { } iso)
            exposure.Add("ISO " + iso);
        if (Find("FocalLength") is { } focal)
            exposure.Add(focal + " mm");
        if (exposure.Count > 0)
            facts.Add(("Exposure", string.Join(" · ", exposure)));
        if (Find("ImageSize") is { } size)
            facts.Add(("Size", size.Replace('x', '×').Replace(" ", " × ")));
        return new Details("Camera RAW photo", Find("Software"), facts, [], [], "", [], null);
    }

    private void Show(Details d)
    {
        _content.Children.Clear();
        if (d.Preview is { } preview)
        {
            try
            {
                _content.Children.Add(PreviewPanel(AdobeToolSetup.Picture(preview), 200).WithMarginT(12));
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
            {
                // A preview WPF can't decode: leave it out.
            }
        }
        foreach (var (label, value) in d.Facts)
        {
            // "400 × 300 px" and "210 × 297 mm" read left to right, also in Arabic.
            bool latin = !value.Any(c => c is >= '\u0600' and <= '\u06FF');
            var text = AdobeToolSetup.Data(value, leftToRight: latin);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            _content.Children.Add(Row(label, text, 150, 8));
        }
        if (d.Fonts.Count > 0)
        {
            int missing = d.Fonts.Count(f => f.Installed is null);
            _content.Children.Add(SectionTitle("Fonts").WithMarginT(16));
            _content.Children.Add(Text(missing == 0 ? L.T("All installed on this PC.") : L.P(missing, "{0} isn't installed on this PC", "{0} aren't installed on this PC"), "Petal.Caption").WithMargin(0, 2, 0, 6));
            foreach (var font in d.Fonts)
                _content.Children.Add(AdobeToolSetup.StatusLine(font.Installed is not null, font.Font.Name, font.Installed is { } i ? Path.GetFileName(i.File) : L.T("Not installed")));
        }
        if (d.Files.Count > 0)
        {
            int missing = d.Files.Count(f => !f.Found);
            _content.Children.Add(SectionTitle(d.FilesTitle).WithMarginT(16));
            _content.Children.Add(Text(missing == 0 ? L.T("All found.") : L.P(missing, "{0} is missing", "{0} are missing"), "Petal.Caption").WithMargin(0, 2, 0, 6));
            foreach (var (name, found, note) in d.Files)
                _content.Children.Add(AdobeToolSetup.StatusLine(found, name, note ?? (found ? null : L.T("Missing")), titleIsPath: true));
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Collect Files — a project and everything it uses, gathered into one folder.
// ---------------------------------------------------------------------------------------------

public sealed class CollectFilesWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly StackPanel _list = new();
    private readonly TextBlock _summary;
    private readonly CheckBox _fonts = new() { Content = L.T("Copy the fonts too"), IsChecked = true, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _relink;
    private CollectPlan? _plan;

    public CollectFilesWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 520)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(AdobeToolSetup.Data(Path.GetFileName(file), "Petal.Section"));
        _summary = Text("Looking for the files…", "Petal.Caption");
        _summary.Margin = new Thickness(0, 6, 0, 0);
        Body.Children.Add(_summary);
        Body.Children.Add(new ScrollViewer { MaxHeight = 380, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list });
        Body.Children.Add(_fonts);
        _relink = Text("", "Petal.Caption");
        _relink.Margin = new Thickness(0, 10, 0, 0);
        _relink.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_relink);
        PrimaryButton.Content = L.T("Collect");
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += (_, _) => Apply();
        FooterNote = Path.GetFileName(OutputNaming.ForTool(file, tool));
        Loaded += async (_, _) => await PlanAsync();
    }

    private async Task PlanAsync()
    {
        try
        {
            var plan = await Task.Run(() => ProjectCollector.Plan(_file));
            Show(plan);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Planning a collection");
            _summary.Text = L.T("Petal couldn't read this file.") + " " + L.T(ex.Message);
        }
    }

    internal void Show(CollectPlan plan)
    {
        _plan = plan;
        _list.Children.Clear();
        var counts = L.P(plan.Files.Count, "{0} file", "{0} files") + " · " + Sizes.Format(plan.Size);
        if (plan.Missing > 0)
            counts += " · " + L.P(plan.Missing, "{0} is missing", "{0} are missing");
        _summary.Text = counts;
        foreach (var item in plan.Files)
            _list.Children.Add(AdobeToolSetup.StatusLine(item.Found is not null, item.Recorded,
                item.Found is null ? L.T("Missing") : !item.Found.Equals(item.Recorded, StringComparison.OrdinalIgnoreCase) ? L.F("Found at {0}", item.Found) : Sizes.Format(item.Size), titleIsPath: true));
        if (plan.Fonts.Count > 0)
        {
            _list.Children.Add(SectionTitle("Fonts").WithMarginT(12));
            foreach (var font in plan.Fonts)
                _list.Children.Add(AdobeToolSetup.StatusLine(font.Installed is not null, font.Font.Name, font.Installed is { } i ? Path.GetFileName(i.File) : L.T("Not installed")));
            _fonts.Visibility = plan.Fonts.Any(f => f.Installed is not null) ? Visibility.Visible : Visibility.Collapsed;
        }
        _relink.Text = L.T(plan.CanRelink
            ? "The copy of the project points at the collected files, so it opens anywhere."
            : "The copy of the document still points at the original files. If they move, relink them to the Links folder.");
        PrimaryButton.IsEnabled = true;
    }

    private void Apply()
    {
        if (_plan is not { } plan)
            return;
        var options = new CollectOptions(_fonts.IsChecked == true);
        var folder = OutputNaming.ForTool(_file, _tool);
        Close();
        _ = _jobs.RunAsync("Collecting files", "Files collected", [new WorkItem(_file, Path.GetFileName(_file), async (p, ct) =>
        {
            var result = await ProjectCollector.CollectAsync(plan, folder, options, p, ct);
            return (IReadOnlyList<string>)[result.Folder];
        })], _at, _ => plan.Missing > 0 ? L.P(plan.Missing, "{0} is missing", "{0} are missing") : null);
    }
}

// ---------------------------------------------------------------------------------------------
// Colour Palette — the colours of a swatch file: copy them, or save them for code and other apps.
// ---------------------------------------------------------------------------------------------

public sealed class PaletteWindow : ToolWindow
{
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly WrapPanel _chips = new();
    private readonly TextBlock _status;
    private readonly Segmented<string> _format;
    private IReadOnlyList<Swatch> _swatches = [];

    public PaletteWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 560)
    {
        _file = file;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(AdobeToolSetup.Data(Path.GetFileName(file), "Petal.Section"));
        _status = Text("Click a colour to copy its value.", "Petal.Caption");
        _status.Margin = new Thickness(0, 4, 0, 10);
        Body.Children.Add(_status);
        Body.Children.Add(new ScrollViewer { MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _chips });
        _format = Segments([("css", "CSS"), ("json", "JSON"), ("gpl", "GIMP"), ("png", "Picture")], "css");
        Body.Children.Add(Row("Save as", _format, 90, 12));
        var copyAll = new Button { Content = L.T("Copy All"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(90, 10, 0, 0) };
        copyAll.Click += (_, _) =>
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _swatches.Select(s => $"{s.Name}\t{s.Hex}")));
            _status.Text = L.P(_swatches.Count, "Copied {0} colour", "Copied {0} colours");
        };
        Body.Children.Add(copyAll);
        PrimaryButton.Content = L.T("Save");
        PrimaryButton.Click += (_, _) => Save();
        try
        {
            _swatches = Swatches.Read(file);
            foreach (var swatch in _swatches)
                _chips.Children.Add(AdobeToolSetup.Chip(swatch, hex => _status.Text = L.F("Copied {0}", hex)));
            FooterNote = L.P(_swatches.Count, "{0} colour", "{0} colours");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException)
        {
            _status.Text = L.T("This isn't a swatch file.");
            PrimaryButton.IsEnabled = false;
        }
    }

    private void Save()
    {
        var format = _format.Value;
        var swatches = _swatches;
        var output = OutputNaming.Unique(Path.GetDirectoryName(_file)!, Path.GetFileNameWithoutExtension(_file), format);
        var title = Path.GetFileNameWithoutExtension(_file);
        Close();
        _ = _jobs.RunAsync("Saving colours", "Colours saved", [new WorkItem(_file, Path.GetFileName(_file), (_, ct) => Task.Run(() =>
        {
            AdobeToolSetup.SaveSwatches(swatches, format, output, title);
            return (IReadOnlyList<string>)[output];
        }, ct))], _at);
    }
}

// ---------------------------------------------------------------------------------------------
// Extract Colours — the main colours of a picture, as swatches for Photoshop, Illustrator and code.
// ---------------------------------------------------------------------------------------------

public sealed class ExtractColoursWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Image _picture = new() { Stretch = Stretch.Uniform, FlowDirection = FlowDirection.LeftToRight };
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _status;
    private readonly LabeledSlider _count;
    private readonly Segmented<string> _format;
    private IReadOnlyList<Swatch> _swatches = [];
    private CancellationTokenSource? _run;

    public ExtractColoursWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 520)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(PreviewPanel(_picture, 220));
        _count = Slider(3, 12, 6, v => L.P((int)v, "{0} colour", "{0} colours"));
        Body.Children.Add(Row("Colours", _count, 90, 12));
        Body.Children.Add(_chips);
        _status = Text("Finding the colours…", "Petal.Caption");
        Body.Children.Add(_status);
        _format = Segments([("ase", "ASE"), ("aco", "ACO"), ("css", "CSS"), ("png", "Picture")], "ase");
        Body.Children.Add(Row("Save as", _format, 90, 12));
        Body.Children.Add(Text("ASE opens in Photoshop, Illustrator and InDesign (Swatches panel › Import); ACO is Photoshop's own.", "Petal.Caption").WithMargin(90, 4, 0, 0));
        _count.Changed += value => { _ = ExtractAsync(); };
        _format.Changed += _ => Sync();
        PrimaryButton.Content = L.T("Save Colours");
        PrimaryButton.Click += (_, _) => Save();
        Loaded += async (_, _) =>
        {
            try
            {
                _picture.Source = AdobeToolSetup.Picture(await ImagePreview.DecodePngAsync(file, 900)).Source;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error(ex, "Previewing a picture for its colours");
            }
            await ExtractAsync();
        };
        Sync();
    }

    private async Task ExtractAsync()
    {
        _run?.Cancel();
        var run = _run = new CancellationTokenSource();
        int count = (int)_count.Value;
        try
        {
            var swatches = await Task.Run(() => Swatches.Extract(_file, count), run.Token);
            if (run != _run)
                return;
            _swatches = swatches;
            _chips.Children.Clear();
            foreach (var swatch in swatches)
                _chips.Children.Add(AdobeToolSetup.Chip(swatch, hex => _status.Text = L.F("Copied {0}", hex), 52));
            _status.Text = L.T("Click a colour to copy its value.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Extracting colours");
            _status.Text = L.T("That didn't work.") + " " + L.T(ex.Message);
        }
        Sync();
    }

    private void Sync()
    {
        PrimaryButton.IsEnabled = _swatches.Count > 0;
        FooterNote = Path.GetFileName(Output());
    }

    private string Output() => OutputNaming.ForTool(_file, _tool, extensionOverride: _format.Value);

    private void Save()
    {
        var swatches = _swatches;
        var format = _format.Value;
        var output = Output();
        var title = Path.GetFileNameWithoutExtension(_file);
        Close();
        _ = _jobs.RunAsync("Saving colours", "Colours saved", [new WorkItem(_file, Path.GetFileName(_file), (_, ct) => Task.Run(() =>
        {
            AdobeToolSetup.SaveSwatches(swatches, format, output, title);
            return (IReadOnlyList<string>)[output];
        }, ct))], _at);
    }
}
