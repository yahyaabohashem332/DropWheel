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
using Petal.Core.Util;
using Petal.Engines.Imaging;

namespace Petal.App.Tools.Windows;

// ---------------------------------------------------------------------------------------------
// Create QR Code — text or a link, a Wi-Fi network, an e-mail, a number to call, a text message or a
// contact, saved as PNG (in three sizes) or SVG, or copied. As a page: the options beside a large code.
// ---------------------------------------------------------------------------------------------

public sealed class QrWindow : ToolWindow
{
    private static QrWindow? _open;
    /// <summary>What the code holds: text, wifi, email, phone, sms or contact.</summary>
    private string _kind = "text";
    private readonly TextBox _mailTo = new() { FlowDirection = FlowDirection.LeftToRight }, _subject = new(), _phone = new() { FlowDirection = FlowDirection.LeftToRight },
        _smsNumber = new() { FlowDirection = FlowDirection.LeftToRight }, _name = new(), _contactPhone = new() { FlowDirection = FlowDirection.LeftToRight },
        _contactEmail = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBox _mailBody = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _smsText = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _mailPanel = new(), _phonePanel = new(), _smsPanel = new(), _contactPanel = new();
    /// <summary>On the page: pixels a module when saved as PNG (Small, Medium, Large), and what that makes.</summary>
    private Segmented<int>? _size;
    private readonly TextBlock _sizeSays = new() { Margin = new Thickness(0, 4, 0, 0) };
    private Button? _copy, _save;
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

    /// <summary>The QR maker as a page of the main window.</summary>
    internal static QrWindow ForPage() => new(asPage: true);

    private static readonly (string Kind, string Label)[] Kinds =
        [("text", "Text or link"), ("wifi", "Wi-Fi"), ("email", "E-mail"), ("phone", "Phone"), ("sms", "SMS"), ("contact", "Contact")];

    private QrWindow(bool asPage = false) : base("Create QR Code", 420, asPage)
    {
        RenderOptions.SetBitmapScalingMode(_code, BitmapScalingMode.NearestNeighbor);
        var hint = Text("Type text or a link, and the code appears here.", "Petal.Caption");
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextAlignment = TextAlignment.Center;
        hint.Margin = new Thickness(16, 0, 16, 0);
        _hint = hint;
        if (asPage)
            _code.Width = _code.Height = 300;
        var stage = new Grid { Height = asPage ? 340 : 230 };
        stage.Children.Add(hint);
        stage.Children.Add(_code);
        var panel = new Border { CornerRadius = new CornerRadius(12), Child = stage };
        panel.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _problem.Style = (Style)FindResource("Petal.Caption");
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        _problem.TextWrapping = TextWrapping.Wrap;
        _problem.TextAlignment = TextAlignment.Center;

        // The kind of contents: a row of choices on the page, a list in the small window.
        double labels = asPage ? 118 : 100;
        FrameworkElement kinds;
        if (asPage)
        {
            var segments = Segments(Kinds.Select(k => (k.Kind, k.Label)), "text");
            segments.Changed += kind => { _kind = kind; Sync(); };
            kinds = segments;
        }
        else
        {
            var choice = Choice(Kinds.Select(k => (k.Kind, k.Label)), "text");
            choice.SelectionChanged += (_, _) => { _kind = ValueOf<string>(choice); Sync(); };
            kinds = choice;
        }
        var options = new StackPanel();
        options.Children.Add(Row("Contents", kinds, labels, asPage ? 0 : 12));

        _textPanel.Children.Add(Row("Text", _text, labels, 10));
        options.Children.Add(_textPanel);
        _wifiPanel.Children.Add(Row("Network", _network, labels, 10));
        _wifiPanel.Children.Add(Row("Password", _wifiPassword, labels, 6));
        _hidden.Margin = new Thickness(labels, 8, 0, 0);
        _wifiPanel.Children.Add(_hidden);
        _wifiPanel.Children.Add(Text("Phones that scan it join the network without typing the password.", "Petal.Caption").WithMargin(labels, 4, 0, 0));
        options.Children.Add(_wifiPanel);
        _mailPanel.Children.Add(Row("To", _mailTo, labels, 10));
        _mailPanel.Children.Add(Row("Subject", _subject, labels, 6));
        _mailPanel.Children.Add(Row("Message", _mailBody, labels, 6));
        _mailPanel.Children.Add(Text("Phones that scan it open a new e-mail, ready to send.", "Petal.Caption").WithMargin(labels, 4, 0, 0));
        options.Children.Add(_mailPanel);
        _phonePanel.Children.Add(Row("Number", _phone, labels, 10));
        _phonePanel.Children.Add(Text("Phones that scan it offer to call the number.", "Petal.Caption").WithMargin(labels, 4, 0, 0));
        options.Children.Add(_phonePanel);
        _smsPanel.Children.Add(Row("Number", _smsNumber, labels, 10));
        _smsPanel.Children.Add(Row("Message", _smsText, labels, 6));
        _smsPanel.Children.Add(Text("Phones that scan it open a text message, ready to send.", "Petal.Caption").WithMargin(labels, 4, 0, 0));
        options.Children.Add(_smsPanel);
        _contactPanel.Children.Add(Row("Name", _name, labels, 10));
        _contactPanel.Children.Add(Row("Phone", _contactPhone, labels, 6));
        _contactPanel.Children.Add(Row("E-mail", _contactEmail, labels, 6));
        _contactPanel.Children.Add(Text("Phones that scan it offer to save the contact.", "Petal.Caption").WithMargin(labels, 4, 0, 0));
        options.Children.Add(_contactPanel);

        var colours = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dark, _light } };
        _light.Margin = new Thickness(8, 0, 0, 0);
        options.Children.Add(Row("Colours", colours, labels, 14));
        _strength = Segments([(QrRedundancy.Medium, "Standard"), (QrRedundancy.Quartile, "Strong"), (QrRedundancy.High, "Strongest")], QrRedundancy.Medium);
        options.Children.Add(Row("Strength", _strength, labels, 10));
        options.Children.Add(Text("Stronger codes still read when scratched or partly covered, but are denser.", "Petal.Caption").WithMargin(labels, 4, 0, 0));

        foreach (var box in new[] { _text, _network, _wifiPassword, _mailTo, _subject, _mailBody, _phone, _smsNumber, _smsText, _name, _contactPhone, _contactEmail })
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

        if (asPage)
            PageRoot = PageLayout(options, panel);
        else
        {
            Body.Children.Add(panel);
            Body.Children.Add(_problem);
            foreach (var child in options.Children.OfType<UIElement>().ToList())
            {
                options.Children.Remove(child);
                Body.Children.Add(child);
            }
        }
        _redraw = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Encode(), Dispatcher);
        Loaded += (_, _) => _text.Focus();
        if (asPage)
            _text.Loaded += (_, _) => _text.Focus();
        Sync();
    }

    /// <summary>
    /// The page: the options on one side, the code large on the other with Copy Image, Save and the size it is saved
    /// at under it; on a narrow page the code goes above the options.
    /// </summary>
    private FrameworkElement PageLayout(StackPanel options, Border preview)
    {
        PrimaryButton.Visibility = ResetButton.Visibility = Visibility.Collapsed;
        FooterNote = "";
        _copy = new Button { Content = L.T("Copy Image"), Margin = new Thickness(0, 0, 8, 0), MinWidth = 110 };
        _copy.Click += (_, _) => Copy();
        _save = new Button { Content = L.T("Save…"), Style = (Style)FindResource("Petal.PrimaryButton"), MinWidth = 110 };
        _save.Click += (_, _) => Save();
        _size = Segments([(8, "Small"), (16, "Medium"), (32, "Large")], 16);
        _size.Changed += _ => SayingSize();
        _sizeSays.Style = (Style)FindResource("Petal.Caption");
        var side = new StackPanel { Width = 360 };
        side.Children.Add(preview);
        side.Children.Add(_problem);
        side.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), Children = { _copy, _save } });
        var size = Text("Size", "Petal.Caption").WithMargin(0, 16, 0, 6);
        side.Children.Add(size);
        side.Children.Add(_size);
        side.Children.Add(_sizeSays);
        side.Children.Add(Text("PNG · SVG", "Petal.Caption").WithMargin(0, 2, 0, 0));

        // The code at the end side, the options filling the rest; on a narrow page the code on top.
        options.MaxWidth = 640;
        options.HorizontalAlignment = HorizontalAlignment.Left;
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(side);
        dock.Children.Add(options);
        void Arrange(double width)
        {
            var narrow = width < 860;
            DockPanel.SetDock(side, narrow ? Dock.Top : Dock.Right);
            side.Margin = narrow ? new Thickness(0, 0, 0, 22) : new Thickness(36, 0, 0, 0);
            side.HorizontalAlignment = HorizontalAlignment.Left;
            side.VerticalAlignment = VerticalAlignment.Top;
        }
        dock.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        Arrange(1000);
        return dock;
    }

    /// <summary>How large the PNG will be, in pixels.</summary>
    private void SayingSize()
    {
        if (_size is null)
            return;
        var modules = _modules?.GetLength(0) ?? 21;
        var pixels = (modules + 8) * _size.Value;
        _sizeSays.Text = L.F("{0} px", $"{pixels} × {pixels}");
    }

    private string Payload() => _kind switch
    {
        "wifi" => _network.Text.Length > 0 ? QrGenerator.WifiPayload(_network.Text, _wifiPassword.Text, _hidden.IsChecked == true) : "",
        "email" => _mailTo.Text.Trim().Length > 0 ? QrGenerator.EmailPayload(_mailTo.Text, _subject.Text, _mailBody.Text) : "",
        "phone" => QrGenerator.PhonePayload(_phone.Text) is { Length: > 4 } tel ? tel : "",
        "sms" => QrGenerator.SmsPayload(_smsNumber.Text, _smsText.Text) is var sms && _smsNumber.Text.Any(char.IsDigit) ? sms : "",
        "contact" => _name.Text.Trim().Length > 0 ? QrGenerator.ContactPayload(_name.Text, _contactPhone.Text, _contactEmail.Text) : "",
        _ => _text.Text,
    };

    private void Sync()
    {
        _textPanel.Visibility = _kind == "text" ? Visibility.Visible : Visibility.Collapsed;
        _wifiPanel.Visibility = _kind == "wifi" ? Visibility.Visible : Visibility.Collapsed;
        _mailPanel.Visibility = _kind == "email" ? Visibility.Visible : Visibility.Collapsed;
        _phonePanel.Visibility = _kind == "phone" ? Visibility.Visible : Visibility.Collapsed;
        _smsPanel.Visibility = _kind == "sms" ? Visibility.Visible : Visibility.Collapsed;
        _contactPanel.Visibility = _kind == "contact" ? Visibility.Visible : Visibility.Collapsed;
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

                // Check for fake link if payload is a URL
                if (IsLikelyUrl(payload))
                {
                    var analysis = FakeLinkDetector.Analyze(payload);
                    if (analysis.IsSuspicious)
                    {
                        var topWarning = analysis.Warnings
                            .OrderByDescending(w => GetWarningSeverity(w.Type))
                            .First();
                        _problem.Text = L.F("⚠ {0} (Risk: {1}%)", topWarning.Message, analysis.RiskScore);
                        _problem.ToolTip = string.Join("\n", analysis.Warnings.Select(w => $"{w.Message}: {w.Detail}"));
                    }
                }
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
        if (_copy is not null && _save is not null)
            _copy.IsEnabled = _save.IsEnabled = _modules is not null;
        SayingSize();
    }

    private static bool IsLikelyUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains(' ')) return false;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
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
        if (dialog.ShowDialog(Host) != true)
            return;
        try
        {
            var path = dialog.FileName;
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(path, QrGenerator.ToSvg(_modules, ColorWell.Hex(_dark.Color), ColorWell.Hex(_light.Color)));
            else
                File.WriteAllBytes(path, QrGenerator.ToPng(_modules, _size?.Value ?? 16, new ImageMagick.MagickColor(_dark.Color.R, _dark.Color.G, _dark.Color.B),
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
        var host = Host;
        var dpi = VisualTreeHelper.GetDpi(host);
        if (IsPage)
        {
            // Under the code on its page.
            var under = _code.PointToScreen(new Point(_code.ActualWidth / 2, _code.ActualHeight + 40));
            return new ScreenPoint((int)under.X, (int)under.Y);
        }
        return new ScreenPoint((int)((host.Left + host.ActualWidth / 2) * dpi.DpiScaleX), (int)((host.Top + host.ActualHeight + 30) * dpi.DpiScaleY));
    }
}
