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
using Petal.Core.Util;
using Petal.Engines.Imaging;
using Petal.App.Localization;

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
            Body.Children.Add(Text(L.P(files.Count, "{0} image", "{0} images"), "Petal.Section"));
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
        PrimaryButton.Content = L.T("Apply");
        PrimaryButton.Click += (_, _) => Apply();
        FooterNote = batch ? L.P(files.Count, "{0} file", "{0} files") : Path.GetFileName(OutputNaming.ForTool(files[0], tool));

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
    private readonly Button _faces = new() { Content = L.T("Faces"), Margin = new Thickness(0, 2, 8, 2) };
    private readonly Button _text = new() { Content = L.T("Text"), Margin = new Thickness(0, 2, 0, 2) };
    private readonly Button _private = new() { Content = L.T("Private details"), Margin = new Thickness(0, 2, 8, 2) };
    private readonly TextBlock _status = new();

    /// <summary>
    /// A private screenshot: the file is the uncovered picture in Petal's temporary folder, deleted when the
    /// window closes; the covered copy goes to Pictures\Screenshots and onto the clipboard.
    /// </summary>
    public bool PrivateShot { get; init; }

    public RedactPhotoWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 420)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;
        Closed += (_, _) =>
        {
            if (PrivateShot && !_saving)
                Wheels.Screenshots.ForgetTemporary(_file);
        };
        Body.Children.Add(PreviewPanel(_canvas, 320));

        _style = Segments([(RedactionStyle.Solid, "Solid"), (RedactionStyle.Blur, "Blur")], RedactionStyle.Solid);
        _style.Changed += _ => SyncFill();
        _color.Changed += _ => SyncFill();
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _style } };
        _color.Margin = new Thickness(12, 0, 0, 0);
        _color.VerticalAlignment = VerticalAlignment.Center;
        styleRow.Children.Add(_color);
        Body.Children.Add(Row("Style", styleRow, 80, 14));

        // Wraps when the three names are long in the language Petal speaks.
        var detect = new WrapPanel { Children = { _private, _faces, _text } };
        _faces.Click += async (_, _) => await DetectAsync(faces: true);
        _text.Click += async (_, _) => await DetectAsync(faces: false);
        _private.Click += async (_, _) => await DetectPrivateAsync();
        _private.ToolTip = L.T("E-mail addresses, phone numbers, card numbers, IBANs, IP addresses and passwords");
        Body.Children.Add(Row("Detect", detect, 80, 10));
        _status.Style = (Style)FindResource("Petal.Caption");
        _status.Margin = new Thickness(80, 6, 0, 0);
        Body.Children.Add(_status);
        Body.Children.Add(Text("Drag on the photo to cover an area. Drag a box to move it, its corner to resize it; Delete removes it.", "Petal.Caption").WithMarginT(8));

        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => _canvas.Clear();
        PrimaryButton.Content = L.T("Save Redacted Copy");
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
                // Private details are looked for straight away; each box can still be moved or deleted.
                await DetectPrivateAsync();
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

    private async Task DetectPrivateAsync()
    {
        _faces.IsEnabled = _text.IsEnabled = _private.IsEnabled = false;
        _status.Text = L.T("Looking for private details…");
        try
        {
            var found = await ImageRedactor.DetectPrivateAsync(_file);
            foreach (var (r, _) in found)
            {
                double pad = Math.Max(2, r.Height * 0.15);
                _canvas.Add(new Rect(Math.Max(0, r.X - pad), Math.Max(0, r.Y - pad), r.Width + pad * 2, r.Height + pad * 2));
            }
            _status.Text = found.Count == 0 ? L.T("No private details found.") : L.P(found.Count, "Covered {0} private detail.", "Covered {0} private details.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Looking for private details");
            _status.Text = L.T("Detection isn't available on this PC.");
        }
        finally
        {
            _faces.IsEnabled = _text.IsEnabled = _private.IsEnabled = true;
        }
    }

    private async Task DetectAsync(bool faces)
    {
        _faces.IsEnabled = _text.IsEnabled = false;
        _status.Text = L.T(faces ? "Looking for faces…" : "Looking for text…");
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
            _status.Text = rects.Count == 0
                ? L.T(faces ? "No faces found." : "No text found.")
                : faces ? L.P(rects.Count, "Covered {0} face.", "Covered {0} faces.") : L.P(rects.Count, "Covered {0} line of text.", "Covered {0} lines of text.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Detecting areas to redact");
            _status.Text = L.T("Detection isn't available on this PC.");
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
        if (PrivateShot)
        {
            _saving = true;
            Close();
            _ = SavePrivateAsync(regions);
            return;
        }
        var output = OutputNaming.ForTool(_file, _tool);
        Close();
        _ = _jobs.RunAsync("Redacting", "Redacted", [new WorkItem(_file, Path.GetFileName(_file), async (p, ct) =>
        {
            await ImageRedactor.RedactAsync(_file, output, regions, p, ct);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }

    private bool _saving;

    /// <summary>The covered copy into Pictures\Screenshots and onto the clipboard; the uncovered picture is deleted.</summary>
    private async Task SavePrivateAsync(IReadOnlyList<RedactionRegion> regions)
    {
        try
        {
            Directory.CreateDirectory(Wheels.Screenshots.Folder);
            var output = OutputNaming.ForTool(_file, _tool, outputDirectory: Wheels.Screenshots.Folder);
            await ImageRedactor.RedactAsync(_file, output, regions, null, CancellationToken.None);
            Wheels.Screenshots.KeepFile(output, "Private screenshot saved and copied");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ImageMagick.MagickException)
        {
            Log.Error(ex, "Saving a private screenshot");
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(L.T("The screenshot couldn't be saved"), ex.Message);
        }
        finally
        {
            Wheels.Screenshots.ForgetTemporary(_file);
        }
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
        ToolLauncher.RegisterWindow(ToolId.TraceToSvg, (t, f, j, a) => new TraceToSvgWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.IdPhoto, (t, f, j, a) => new IdPhotoWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.RedactPhoto, (t, f, j, a) => new RedactPhotoWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.EditPhoto, (t, f, j, a) => new EditPhotoWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.ResizeImage, (t, f, j, a) => new ResizeImageWindow(t, f, j, a));
        // One watermark window for photos, videos and PDFs.
        ToolLauncher.RegisterWindow(ToolId.Watermark, (t, f, j, a) => new WatermarkWindow(t, f, j, a));
        ToolLauncher.RegisterDirect(ToolId.CopyText, (tool, files, jobs, at) => CopyTextAsync(files[0], at));
        ToolLauncher.RegisterWindow(ToolId.RemoveBackground, (t, f, j, a) => new RemoveBackgroundWindow(t, f, j, a));
        ToolLauncher.RegisterDirect(ToolId.CreatePdf, (tool, files, jobs, at) =>
        {
            // Named after the first image, in selection order: "trip 1.jpg" + … → "trip 1.pdf".
            var output = OutputNaming.ForTool(files[0], tool);
            return jobs.RunAsync("Creating PDF", "PDF created", [new WorkItem(files[0], L.P(files.Count, "{0} image", "{0} images"), async (p, ct) =>
            {
                await PdfCreator.CreateAsync(files, output, new PdfCreateOptions(), p, ct);
                return (IReadOnlyList<string>)[output];
            })], at);
        });
    }

    /// <summary>
    /// Copies what a picture says: the contents of any QR code or barcode in it, otherwise its
    /// text, read with Windows' own OCR.
    /// </summary>
    public static async Task CopyTextAsync(string file, ScreenPoint at)
    {
        var card = Hud.HudCard.ShowAt(at, "Reading text", Path.GetFileName(file));
        try
        {
            // Codes first: decoding them is quick and exact.
            var codes = await BarcodeScanner.ScanAsync(file);
            if (codes.Count > 0)
            {
                CopyCodes(card, codes);
                return;
            }
            var text = await ImageRedactor.ReadTextAsync(file);
            if (text.Length == 0)
            {
                card.Fail("No text found", "Try a sharper picture, or add its language under Windows Settings › Time & language.");
                return;
            }
            Clipboard.SetText(text);
            var lines = text.Split('\n');
            var first = lines[0].Trim();
            if (first.Length > 48)
                first = first[..48] + "…";
            card.Succeed(L.T("Text copied"), L.P(lines.Length, "{0} line", "{0} lines") + " · " + first, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copying text from an image");
            card.Fail("Couldn't read this image", ex.Message);
        }
    }

    /// <summary>Copies what the codes say; a link opens on a click.</summary>
    private static void CopyCodes(Hud.HudCard card, IReadOnlyList<BarcodeResult> results)
    {
        var text = string.Join(Environment.NewLine, results.Select(r => r.Text));
        Clipboard.SetText(text);
        var first = results[0].Text;
        bool link = Uri.TryCreate(first, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        // Check for fake link warnings
        var fakeWarnings = new List<string>();
        foreach (var result in results)
        {
            if (result.LinkAnalysis?.IsSuspicious == true)
            {
                var topWarning = result.LinkAnalysis.Warnings
                    .OrderByDescending(w => GetWarningSeverity(w.Type))
                    .FirstOrDefault();
                if (topWarning != null)
                {
                    fakeWarnings.Add($"⚠ {topWarning.Message}: {topWarning.Detail}");
                }
            }
        }

        var subtitle = link ? first + " · " + L.T("click to open") : first;
        if (fakeWarnings.Count > 0)
        {
            subtitle += "\n\n" + string.Join("\n", fakeWarnings);
        }

        card.Succeed(results.Count == 1 ? L.T("Code copied") : L.P(results.Count, "{0} code copied", "{0} codes copied"),
            subtitle,
            link ? () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(first) { UseShellExecute = true }) : null);
    }

    private static int GetWarningSeverity(FakeLinkDetector.WarningType type) => type switch
    {
        FakeLinkDetector.WarningType.MismatchedDomain => 10,
        FakeLinkDetector.WarningType.BrandImpersonation => 9,
        FakeLinkDetector.WarningType.HomographAttack => 8,
        FakeLinkDetector.WarningType.UrlShortener => 7,
        FakeLinkDetector.WarningType.PunycodeDomain => 7,
        FakeLinkDetector.WarningType.IpAddress => 6,
        FakeLinkDetector.WarningType.SuspiciousTld => 5,
        FakeLinkDetector.WarningType.ExcessiveSubdomains => 4,
        FakeLinkDetector.WarningType.SuspiciousPath => 3,
        FakeLinkDetector.WarningType.MissingHttps => 2,
        _ => 1
    };

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
