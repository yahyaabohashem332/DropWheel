using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.App.Tools.Parts;
using Petal.Engines.Imaging;

namespace Petal.App.Tools.Windows;

// ---------------------------------------------------------------------------------------------
// Create QR Code — text, a link or a Wi-Fi network, saved as PNG or SVG or copied.
// ---------------------------------------------------------------------------------------------

public sealed class QrWindow : ToolWindow
{
    private static QrWindow? _open;
    private readonly Segmented<bool> _wifi;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _network = new();
    private readonly TextBox _wifiPassword = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly CheckBox _hidden = new() { Content = L.T("Hidden network"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _textPanel = new(), _wifiPanel = new();
    private readonly ColorWell _dark = new(Color.FromRgb(0x14, 0x14, 0x18));
    private readonly ColorWell _light = new(Colors.White);
    private readonly Segmented<QrRedundancy> _strength;
    private readonly Image _code = new() { FlowDirection = FlowDirection.LeftToRight, Width = 200, Height = 200 };
    private readonly TextBlock _problem = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DispatcherTimer _redraw;
    private readonly TextBlock _hint;
    private bool[,]? _modules;

    /// <param name="text">Text or a link to start from, e.g. a link copied to the clipboard.</param>
    public static void ShowSingle(string? text = null)
    {
        if (_open is not null)
        {
            if (text is not null)
                _open._text.Text = text;
            _open.Activate();
            return;
        }
        _open = new QrWindow();
        if (text is not null)
            _open._text.Text = text;
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    private QrWindow() : base("Create QR Code", 420)
    {
        RenderOptions.SetBitmapScalingMode(_code, BitmapScalingMode.NearestNeighbor);
        var hint = Text("Type text or a link, and the code appears here.", "Petal.Caption");
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.VerticalAlignment = VerticalAlignment.Center;
        _hint = hint;
        var stage = new Grid { Height = 230 };
        stage.Children.Add(hint);
        stage.Children.Add(_code);
        var panel = new Border { CornerRadius = new CornerRadius(12), Child = stage };
        panel.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        Body.Children.Add(panel);
        _problem.Style = (Style)FindResource("Petal.Caption");
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        Body.Children.Add(_problem);

        _wifi = Segments([(false, "Text or link"), (true, "Wi-Fi")], false);
        _wifi.Changed += _ => Sync();
        Body.Children.Add(Row("Contents", _wifi, 100, 12));

        _textPanel.Children.Add(Row("Text", _text, 100, 10));
        Body.Children.Add(_textPanel);
        _wifiPanel.Children.Add(Row("Network", _network, 100, 10));
        _wifiPanel.Children.Add(Row("Password", _wifiPassword, 100, 6));
        _hidden.Margin = new Thickness(100, 8, 0, 0);
        _wifiPanel.Children.Add(_hidden);
        _wifiPanel.Children.Add(Text("Phones that scan it join the network without typing the password.", "Petal.Caption").WithMargin(100, 4, 0, 0));
        Body.Children.Add(_wifiPanel);

        var colours = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dark, _light } };
        _light.Margin = new Thickness(8, 0, 0, 0);
        Body.Children.Add(Row("Colours", colours, 100, 12));
        _strength = Segments([(QrRedundancy.Medium, "Standard"), (QrRedundancy.Quartile, "Strong"), (QrRedundancy.High, "Strongest")], QrRedundancy.Medium);
        Body.Children.Add(Row("Strength", _strength, 100, 10));
        Body.Children.Add(Text("Stronger codes still read when scratched or partly covered, but are denser.", "Petal.Caption").WithMargin(100, 4, 0, 0));

        foreach (var box in new[] { _text, _network, _wifiPassword })
            box.TextChanged += (_, _) => Sync();
        _hidden.Checked += (_, _) => Sync();
        _hidden.Unchecked += (_, _) => Sync();
        _dark.Changed += _ => Sync();
        _light.Changed += _ => Sync();
        _strength.Changed += _ => Sync();

        ResetButton.Content = L.T("Copy Image");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => Copy();
        PrimaryButton.Content = L.T("Save…");
        PrimaryButton.Click += (_, _) => Save();
        FooterNote = "PNG · SVG";
        _redraw = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Encode(), Dispatcher);
        Loaded += (_, _) => _text.Focus();
        Sync();
    }

    private string Payload() => _wifi.Value
        ? (_network.Text.Length > 0 ? QrGenerator.WifiPayload(_network.Text, _wifiPassword.Text, _hidden.IsChecked == true) : "")
        : _text.Text;

    private void Sync()
    {
        _textPanel.Visibility = _wifi.Value ? Visibility.Collapsed : Visibility.Visible;
        _wifiPanel.Visibility = _wifi.Value ? Visibility.Visible : Visibility.Collapsed;
        _redraw.Stop();
        _redraw.Start();
    }

    private void Encode()
    {
        _redraw.Stop();
        var payload = Payload();
        _modules = null;
        _problem.Text = "";
        if (payload.Length > 0)
        {
            try
            {
                _modules = QrGenerator.Encode(payload, _strength.Value);
            }
            catch (ArgumentException)
            {
                _problem.Text = L.T("This is too much text for a QR code.");
            }
        }
        _code.Source = _modules is null ? null : Render(_modules, 4);
        _code.Opacity = _modules is null ? 0 : 1;
        _hint.Visibility = _modules is null ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = ResetButton.IsEnabled = _modules is not null;
    }

    /// <summary>The code with a quiet zone, <paramref name="scale"/> pixels per module.</summary>
    private BitmapSource Render(bool[,] modules, int scale)
    {
        int n = modules.GetLength(0), size = (n + 8) * scale;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(_light.Color), null, new Rect(0, 0, size, size));
            var dark = new SolidColorBrush(_dark.Color);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        if (modules[x, y])
                        {
                            double px = (x + 4) * scale, py = (y + 4) * scale;
                            g.BeginFigure(new Point(px, py), true, true);
                            g.PolyLineTo([new Point(px + scale, py), new Point(px + scale, py + scale), new Point(px, py + scale)], false, false);
                        }
            geometry.Freeze();
            dc.DrawGeometry(dark, null, geometry);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void Copy()
    {
        if (_modules is null) return;
        Clipboard.SetImage(Render(_modules, 12));
        HudCard.ShowAt(Centre(), "Copied", "").Succeed(L.T("QR code copied"), L.T("Paste it into a document or chat"), null);
    }

    private void Save()
    {
        if (_modules is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = L.T("PNG image") + "|*.png|" + L.T("SVG image") + "|*.svg",
            FileName = L.T("QR code"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var path = dialog.FileName;
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(path, QrGenerator.ToSvg(_modules, ColorWell.Hex(_dark.Color), ColorWell.Hex(_light.Color)));
            else
                File.WriteAllBytes(path, QrGenerator.ToPng(_modules, 16, new ImageMagick.MagickColor(_dark.Color.R, _dark.Color.G, _dark.Color.B),
                    new ImageMagick.MagickColor(_light.Color.R, _light.Color.G, _light.Color.B)));
            HudCard.ShowAt(Centre(), "Saved", "").Succeed(L.T("QR code saved"), Path.GetFileName(path), () => ExplorerSelection.Reveal([path]));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving a QR code");
            HudCard.ShowAt(Centre(), "Couldn't save", "").Fail(L.T("Couldn't save"), ex.Message);
        }
    }

    /// <summary>Just below the window, for the confirmation card.</summary>
    private ScreenPoint Centre()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new ScreenPoint((int)((Left + ActualWidth / 2) * dpi.DpiScaleX), (int)((Top + ActualHeight + 30) * dpi.DpiScaleY));
    }
}
