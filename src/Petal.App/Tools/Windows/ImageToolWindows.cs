using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Imaging;

namespace Petal.App.Tools.Windows;

// ---------------------------------------------------------------------------------------------
// Crop Image — one image interactively, or a selection to a ratio / exact size.
// ---------------------------------------------------------------------------------------------

public sealed class CropImageWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly CropOverlay _crop = new();
    private readonly TextBox _w = new() { Width = 76 };
    private readonly TextBox _h = new() { Width = 76 };
    private readonly Segmented<double> _ratio;
    private bool _syncing;

    public CropImageWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 420)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        bool batch = files.Count > 1;

        if (!batch)
        {
            var panel = new Border { Height = 320, Child = _crop };
            Body.Children.Add(panel);
        }
        else
        {
            Body.Children.Add(Text($"{files.Count} images", "Petal.Section"));
            Body.Children.Add(Text("Each image is cropped around its centre to the ratio or size you choose.", "Petal.Caption"));
        }

        _ratio = Segments([(0.0, "Free"), (1.0, "1:1"), (16.0 / 9, "16:9"), (9.0 / 16, "9:16"), (4.0 / 3, "4:3"), (3.0 / 4, "3:4")], batch ? 1.0 : 0.0);
        _ratio.Changed += r =>
        {
            _crop.AspectRatio = r > 0 ? r : null;
            if (batch) Sync();
        };
        Body.Children.Add(Row("Aspect ratio", _ratio, 100, 14));

        var size = new StackPanel { Orientation = Orientation.Horizontal };
        size.Children.Add(Text("W ", "Petal.Label"));
        size.Children.Add(_w);
        size.Children.Add(Text("   H ", "Petal.Label"));
        size.Children.Add(_h);
        size.Children.Add(Text("  px", "Petal.Label"));
        Body.Children.Add(Row(batch ? "Exact size" : "Size", size, 100, 10));
        if (batch)
            Body.Children.Add(Text("Leave the size empty to keep each image's own resolution.", "Petal.Caption").WithMarginT(4));

        foreach (var box in new[] { _w, _h })
        {
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySize(); };
            box.LostKeyboardFocus += (_, _) => ApplySize();
        }
        _crop.Changed += r =>
        {
            _syncing = true;
            _w.Text = r.Width.ToString(CultureInfo.CurrentCulture);
            _h.Text = r.Height.ToString(CultureInfo.CurrentCulture);
            _syncing = false;
        };

        ResetButton.Visibility = batch ? Visibility.Collapsed : Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            _ratio.Value = 0;
            _crop.Reset();
        };
        PrimaryButton.Content = "Apply";
        PrimaryButton.Click += (_, _) => Apply();
        FooterNote = batch ? $"{files.Count} files" : Path.GetFileName(OutputNaming.ForTool(files[0], tool));

        if (!batch)
        {
            Loaded += async (_, _) =>
            {
                try
                {
                    var info = await ImageInspector.ReadAsync(files[0]);
                    _crop.Source = await Parts.ImageLoader.LoadAsync(files[0], 1600);
                    _crop.SourceSize = new Size(info.DisplayWidth, info.DisplayHeight);
                    _crop.Reset();
                    _crop.Focus();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Opening image to crop");
                    FooterNote = "Couldn't open this image.";
                }
            };
        }
        Sync();
    }

    private void Sync()
    {
        if (_files.Count > 1)
            PrimaryButton.IsEnabled = _ratio.Value > 0 || (int.TryParse(_w.Text, out _) && int.TryParse(_h.Text, out _));
    }

    private void ApplySize()
    {
        if (_syncing) return;
        if (int.TryParse(_w.Text, out var w) && int.TryParse(_h.Text, out var h))
            _crop.SetSize(w, h);
        Sync();
    }

    private void Apply()
    {
        if (_files.Count == 1)
        {
            var r = _crop.Crop;
            var rect = new PixelRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            var file = _files[0];
            var output = OutputNaming.ForTool(file, _tool);
            Close();
            _ = _jobs.RunAsync("Cropping", "Cropped", [new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
            {
                await ImageCropper.CropAsync(file, output, rect, p, ct);
                return (IReadOnlyList<string>)[output];
            })], _at);
            return;
        }

        CropSpec spec = int.TryParse(_w.Text, out var w) && int.TryParse(_h.Text, out var h) && w > 0 && h > 0
            ? new CropSpec.ExactSize(w, h)
            : new CropSpec.AspectRatio(_ratio.Value, 1);
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var output = OutputNaming.ForTool(file, _tool);
            await ImageCropper.CropAsync(file, output, spec, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Cropping", "Cropped", work, _at);
    }
}

// ---------------------------------------------------------------------------------------------
// Redact Photo — boxes drawn by hand or found by local face / text detection.
// ---------------------------------------------------------------------------------------------

public sealed class RedactPhotoWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly RedactionCanvas _canvas = new();
    private readonly Segmented<RedactionStyle> _style;
    private readonly ColorWell _color = new(Colors.Black);
    private readonly Button _faces = new() { Content = "Faces" };
    private readonly Button _text = new() { Content = "Text", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new();

    public RedactPhotoWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 420)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        Body.Children.Add(PreviewPanel(_canvas, 320));

        _style = Segments([(RedactionStyle.Solid, "Solid"), (RedactionStyle.Blur, "Blur")], RedactionStyle.Solid);
        _style.Changed += _ => SyncFill();
        _color.Changed += _ => SyncFill();
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _style } };
        _color.Margin = new Thickness(12, 0, 0, 0);
        _color.VerticalAlignment = VerticalAlignment.Center;
        styleRow.Children.Add(_color);
        Body.Children.Add(Row("Style", styleRow, 80, 14));

        var detect = new StackPanel { Orientation = Orientation.Horizontal, Children = { _faces, _text } };
        _faces.Click += async (_, _) => await DetectAsync(faces: true);
        _text.Click += async (_, _) => await DetectAsync(faces: false);
        Body.Children.Add(Row("Detect", detect, 80, 10));
        _status.Style = (Style)FindResource("Petal.Caption");
        _status.Margin = new Thickness(80, 6, 0, 0);
        Body.Children.Add(_status);
        Body.Children.Add(Text("Drag on the photo to cover an area. Drag a box to move it, its corner to resize it; Delete removes it.", "Petal.Caption").WithMarginT(8));

        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => _canvas.Clear();
        PrimaryButton.Content = "Save Redacted Copy";
        PrimaryButton.Click += (_, _) => Apply();
        PrimaryButton.IsEnabled = false;
        _canvas.Changed += () => PrimaryButton.IsEnabled = _canvas.Boxes.Count > 0;
        FooterNote = Path.GetFileName(OutputNaming.ForTool(file, tool));
        SyncFill();

        Loaded += async (_, _) =>
        {
            try
            {
                var info = await ImageInspector.ReadAsync(file);
                _canvas.Source = await Parts.ImageLoader.LoadAsync(file, 1600);
                _canvas.SourceSize = new Size(info.DisplayWidth, info.DisplayHeight);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Opening image to redact");
                FooterNote = "Couldn't open this image.";
            }
        };
    }

    private void SyncFill()
    {
        _canvas.BoxFill = _style.Value == RedactionStyle.Blur
            ? new SolidColorBrush(Color.FromArgb(0xD0, 0x9A, 0x9A, 0xA2))
            : new SolidColorBrush(_color.Color);
        _color.Visibility = _style.Value == RedactionStyle.Solid ? Visibility.Visible : Visibility.Collapsed;
        _canvas.Refresh();
    }

    private async Task DetectAsync(bool faces)
    {
        _faces.IsEnabled = _text.IsEnabled = false;
        _status.Text = faces ? "Looking for faces…" : "Looking for text…";
        try
        {
            IReadOnlyList<PixelRect> rects = faces
                ? await ImageRedactor.DetectFacesAsync(_file)
                : (await ImageRedactor.DetectTextAsync(_file)).Select(t => t.Bounds).ToList();
            foreach (var r in rects)
            {
                // A little margin so nothing peeks out at the edges.
                double pad = Math.Max(r.Width, r.Height) * (faces ? 0.12 : 0.06);
                _canvas.Add(new Rect(Math.Max(0, r.X - pad), Math.Max(0, r.Y - pad), r.Width + pad * 2, r.Height + pad * 2));
            }
            _status.Text = rects.Count switch
            {
                0 => faces ? "No faces found." : "No text found.",
                1 => faces ? "Covered 1 face." : "Covered 1 line of text.",
                _ => faces ? $"Covered {rects.Count} faces." : $"Covered {rects.Count} lines of text.",
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Detecting areas to redact");
            _status.Text = "Detection isn't available on this PC.";
        }
        finally
        {
            _faces.IsEnabled = _text.IsEnabled = true;
        }
    }

    private void Apply()
    {
        var c = _color.Color;
        uint argb = 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        var regions = _canvas.Boxes.Select(b => new RedactionRegion(
            new PixelRect((int)b.Rect.X, (int)b.Rect.Y, (int)Math.Max(1, b.Rect.Width), (int)Math.Max(1, b.Rect.Height)), _style.Value, argb)).ToList();
        var output = OutputNaming.ForTool(_file, _tool);
        Close();
        _ = _jobs.RunAsync("Redacting", "Redacted", [new WorkItem(_file, Path.GetFileName(_file), async (p, ct) =>
        {
            await ImageRedactor.RedactAsync(_file, output, regions, p, ct);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }
}

/// <summary>Image and PDF-from-images hooks into the shared windows.</summary>
public static class ImageToolSetup
{
    public static void Register()
    {
        CompressWindow.Backends[FormatFamily.Image] = CompressImage;
        CompressWindow.Backends[FormatFamily.Vector] = CompressImage;
        ToolLauncher.RegisterWindow(ToolId.CropImage, (t, f, j, a) => new CropImageWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.RedactPhoto, (t, f, j, a) => new RedactPhotoWindow(t, f[0], j, a));
        ToolLauncher.RegisterDirect(ToolId.ReadCode, (tool, files, jobs, at) => ReadCodeAsync(files[0], at));
        ToolLauncher.RegisterDirect(ToolId.CreatePdf, (tool, files, jobs, at) =>
        {
            // Named after the first image, in selection order: "trip 1.jpg" + … → "trip 1.pdf".
            var output = OutputNaming.ForTool(files[0], tool);
            return jobs.RunAsync("Creating PDF", "PDF created", [new WorkItem(files[0], $"{files.Count} images", async (p, ct) =>
            {
                await PdfCreator.CreateAsync(files, output, new PdfCreateOptions(), p, ct);
                return (IReadOnlyList<string>)[output];
            })], at);
        });
    }

    /// <summary>Reads QR codes and barcodes locally and copies what they say.</summary>
    public static async Task ReadCodeAsync(string file, ScreenPoint at)
    {
        var card = Hud.HudCard.ShowAt(at, "Reading code", Path.GetFileName(file));
        try
        {
            var results = await BarcodeScanner.ScanAsync(file);
            if (results.Count == 0)
            {
                card.Fail("No code found", "Try a sharper or closer photo of the QR code.");
                return;
            }
            var text = string.Join(Environment.NewLine, results.Select(r => r.Text));
            Clipboard.SetText(text);
            var first = results[0].Text;
            bool link = Uri.TryCreate(first, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            card.Succeed(results.Count == 1 ? "Copied to clipboard" : $"{results.Count} codes copied",
                link ? $"{first} · click to open" : first,
                link ? () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(first) { UseShellExecute = true }) : null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading a code");
            card.Fail("Couldn't read this image", ex.Message);
        }
    }

    private static async Task<string> CompressImage(string input, CompressRequest request, IProgress<double> progress, CancellationToken ct)
    {
        var output = OutputNaming.ForTool(input, ToolRegistry.Get(ToolId.Compress));
        var options = new ImageCompressOptions
        {
            Preset = request.Strong ? CompressionPreset.Strong : CompressionPreset.Balanced,
            TargetBytes = request.TargetBytes,
            ReductionPercent = request.ReductionPercent,
            Resize = request.MaxSize is { } max ? ImageResize.LongEdge(max) : ImageResize.Original,
        };
        var result = await ImageCompressor.CompressAsync(input, output, options, progress, ct);
        return result.OutputPath;
    }
}
