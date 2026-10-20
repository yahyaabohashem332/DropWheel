using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Petal.App.Overlay;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Visuals;
using Petal.App.Localization;

namespace Petal.App.Onboarding;

/// <summary>
/// First-run window: a looping animation of the gesture, the three steps in words, and a real
/// sample file the user can drag out to try it on the spot.
/// </summary>
public sealed class WelcomeWindow : ToolWindow
{
    private static WelcomeWindow? _open;
    private readonly GestureDemo _demo = new();

    public static void ShowSingle()
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return;
        }
        _open = new WelcomeWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private WelcomeWindow() : base("Welcome to Petal", 600)
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PrimaryButton.Content = L.T("Get started");
        PrimaryButton.Click += (_, _) =>
        {
            SettingsStore.Current.OnboardingDone = true;
            SettingsStore.Save();
            Close();
        };
        ResetButton.Content = L.T("Settings");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => SettingsWindow.ShowSingle();
        FooterNote = "Petal lives in the notification area.";

        var stage = PreviewPanel(_demo, 270);
        stage.Margin = new Thickness(0, 0, 0, 18);
        Body.Children.Add(stage);

        var headline = Text("Convert without opening anything.", "Petal.Title");
        headline.FontSize = 20;
        headline.Margin = new Thickness(0, 0, 0, 6);
        Body.Children.Add(headline);
        Body.Children.Add(Text("Petal works while you drag. Everything happens on this PC — nothing is uploaded.", "Petal.Caption"));

        var steps = new UniformGrid3();
        steps.Add(Step("1", "Drag a file", "Start dragging any file in File Explorer or on the desktop."));
        steps.Add(Step("2", "Tap a key", "Shift converts, Ctrl opens tools, Alt shows actions. Let go of the key — the wheel stays."));
        steps.Add(Step("3", "Drop", "Drop on a petal. The result is saved next to the original."));
        steps.Margin = new Thickness(0, 16, 0, 16);
        Body.Children.Add(steps);

        Body.Children.Add(TryIt());
        Loaded += (_, _) => _demo.Start();
        Closed += (_, _) => _demo.Stop();
    }

    private static UIElement Step(string number, string title, string text)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock { Text = number, FontWeight = FontWeights.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        badge.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "Petal.Accent");
        stack.Children.Add(badge);
        var t = Text(title);
        t.FontWeight = FontWeights.SemiBold;
        stack.Children.Add(t);
        stack.Children.Add(Text(text, "Petal.Caption"));
        return stack;
    }

    private UIElement TryIt()
    {
        var sample = SampleFile.Ensure();
        var tile = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll,
        };
        tile.SetResourceReference(Border.BackgroundProperty, "Petal.Surface");
        tile.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        var row = new DockPanel();
        var thumb = new Image { FlowDirection = FlowDirection.LeftToRight, Width = 52, Height = 40, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 14, 0) };
        thumb.Source = new BitmapImage(new Uri(sample));
        thumb.Clip = new RectangleGeometry(new Rect(0, 0, 52, 40), 6, 6);
        DockPanel.SetDock(thumb, Dock.Left);
        row.Children.Add(thumb);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Text("Try it: drag this photo");
        title.FontWeight = FontWeights.SemiBold;
        text.Children.Add(title);
        text.Children.Add(Text("Drag it anywhere, tap Shift, and drop it on a format. The copy lands in Petal's samples folder.", "Petal.Caption"));
        row.Children.Add(text);
        tile.Child = row;

        Point start = default;
        bool armed = false;
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(tile);
            armed = true;
        };
        tile.PreviewMouseMove += (_, e) =>
        {
            if (!armed || e.LeftButton != MouseButtonState.Pressed)
                return;
            var d = e.GetPosition(tile) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            armed = false;
            DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { sample }), DragDropEffects.Copy);
        };
        return tile;
    }

    /// <summary>A tiny three-column grid without pulling in UniformGrid's equal-height rules.</summary>
    private sealed class UniformGrid3 : Grid
    {
        public UniformGrid3()
        {
            for (int i = 0; i < 3; i++)
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        public void Add(UIElement e)
        {
            SetColumn(e, Children.Count);
            Children.Add(e);
        }
    }
}

/// <summary>The "sunset.png" sample used by the welcome window.</summary>
internal static class SampleFile
{
    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "Samples");

    public static string Ensure()
    {
        var path = System.IO.Path.Combine(Folder, "sunset.png");
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(Folder);
        const int w = 1200, h = 800;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(
                [new GradientStop(Color.FromRgb(0x2B, 0x1A, 0x6B), 0), new GradientStop(Color.FromRgb(0xC0, 0x3F, 0x8C), 0.55), new GradientStop(Color.FromRgb(0xFF, 0xA0, 0x5A), 1)],
                new Point(0, 0), new Point(0, 1));
            dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(0xFF, 0xE6, 0x9A), Color.FromArgb(0, 0xFF, 0xC0, 0x60)), null, new Point(w * 0.62, h * 0.66), 260, 260);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0x8A)), null, new Point(w * 0.62, h * 0.66), 92, 92);
            var hills = new StreamGeometry();
            using (var g = hills.Open())
            {
                g.BeginFigure(new Point(0, h * 0.78), true, true);
                g.BezierTo(new Point(w * 0.25, h * 0.66), new Point(w * 0.45, h * 0.88), new Point(w * 0.7, h * 0.76), true, true);
                g.BezierTo(new Point(w * 0.85, h * 0.7), new Point(w * 0.95, h * 0.78), new Point(w, h * 0.74), true, true);
                g.LineTo(new Point(w, h), true, true);
                g.LineTo(new Point(0, h), true, true);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x1E, 0x12, 0x3D)), null, hills);
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        return path;
    }
}

/// <summary>Looping illustration of drag → tap Shift → wheel → drop → new file.</summary>
internal sealed class GestureDemo : Canvas
{
    private const double Loop = 6.2;
    private readonly Border _file;
    private readonly Border _result;
    private readonly Path _cursor;
    private readonly Border _key;
    private readonly Canvas _wheel = new();
    private readonly ScaleTransform _wheelScale = new(1, 1);
    private readonly List<Path> _petals = [];
    private readonly Border _toast;
    private readonly DateTime _started = DateTime.Now;
    private bool _running;

    private static readonly Point FileHome = new(120, 120);
    private static readonly Point DropPoint = new(330, 140);
    private const int PetalCount = 6;
    private static readonly string[] Labels = ["PNG", "WEBP", "HEIC", "PDF", "AVIF", "TIFF"];

    public GestureDemo()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = true;
        Background = new LinearGradientBrush(Color.FromRgb(0x26, 0x1E, 0x4A), Color.FromRgb(0x14, 0x14, 0x22), 90);

        _file = FileTile("trip.jpg", Color.FromRgb(0x5E, 0x8B, 0xFF));
        _result = FileTile("trip.png", Color.FromRgb(0x9B, 0x7B, 0xFF));
        Children.Add(_file);
        Children.Add(_result);

        // Mini wheel
        _wheel.RenderTransform = _wheelScale;
        for (int i = 0; i < PetalCount; i++)
        {
            var petal = new Path
            {
                Data = WheelGeometry.Petal(i, PetalCount),
                Fill = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)),
                Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                StrokeThickness = 0.8,
            };
            _petals.Add(petal);
            _wheel.Children.Add(petal);
            var p = WheelGeometry.LabelPosition(i, PetalCount);
            var label = new TextBlock { Text = Labels[i], FontFamily = UiFonts.Display, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Brushes.Black };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(label, p.X - label.DesiredSize.Width / 2);
            SetTop(label, p.Y - label.DesiredSize.Height / 2);
            label.Tag = i;
            _wheel.Children.Add(label);
        }
        var hub = new Ellipse { Width = 80, Height = 80, Fill = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)) };
        SetLeft(hub, -40);
        SetTop(hub, -40);
        _wheel.Children.Add(hub);
        _wheel.LayoutTransform = new ScaleTransform(0.62, 0.62);
        Children.Add(_wheel);

        _key = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 7),
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xC0, 0xF0)),
            BorderThickness = new Thickness(1, 1, 1, 3),
            Child = new TextBlock { Text = L.T("⇧ Shift"), FontFamily = UiFonts.Text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x3A, 0x9A)) },
        };
        Children.Add(_key);

        _toast = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 9, 16, 10),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = L.T("Converted to PNG"), FontFamily = UiFonts.Text, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Brushes.Black },
                    new TextBlock { Text = L.T("1 of 1 saved"), FontFamily = UiFonts.Text, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x70, 0x78)) },
                },
            },
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        Children.Add(_toast);

        _cursor = new Path
        {
            Data = Geometry.Parse("M0,0 L0,17 L4.2,13 L7.2,19.6 L10,18.4 L7,11.8 L12.6,11.6 Z"),
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 1.1,
            StrokeLineJoin = PenLineJoin.Round,
        };
        Children.Add(_cursor);
        OnFrame(null, EventArgs.Empty);
    }

    private static Border FileTile(string name, Color tint)
    {
        var icon = new Border
        {
            Width = 54,
            Height = 64,
            CornerRadius = new CornerRadius(8),
            Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0xB0, 0x6A), tint, 45),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        var stack = new StackPanel();
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = name,
            FontFamily = UiFonts.Text,
            FontSize = 11.5,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
        });
        return new Border { Child = stack, Width = 80 };
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private static double Phase(double t, double from, double to) => Math.Clamp((t - from) / (to - from), 0, 1);

    private static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = (DateTime.Now - _started).TotalSeconds % Loop;
        double width = ActualWidth > 0 ? ActualWidth : 560;
        double offsetX = (width - 560) / 2;

        // Cursor path: to the file, drag it to the drop point, visit two petals, drop.
        var fileCentre = new Point(FileHome.X + 40, FileHome.Y + 32);
        var petalPng = new Point(DropPoint.X, DropPoint.Y - 52);
        var petalWebp = new Point(DropPoint.X + 45, DropPoint.Y - 26);
        Point cursor;
        if (t < 0.7) cursor = Lerp(new Point(40, 230), fileCentre, Ease(Phase(t, 0, 0.7)));
        else if (t < 1.0) cursor = fileCentre;
        else if (t < 1.8) cursor = Lerp(fileCentre, DropPoint, Ease(Phase(t, 1.0, 1.8)));
        else if (t < 2.5) cursor = DropPoint;
        else if (t < 2.9) cursor = Lerp(DropPoint, petalWebp, Ease(Phase(t, 2.5, 2.9)));
        else if (t < 3.3) cursor = Lerp(petalWebp, petalPng, Ease(Phase(t, 2.9, 3.3)));
        else cursor = petalPng;

        SetLeft(_cursor, cursor.X + offsetX);
        SetTop(_cursor, cursor.Y);

        // The dragged file follows the cursor while the button is down (1.0 – 3.6 s).
        bool dragging = t >= 1.0 && t < 3.6;
        var filePos = dragging ? new Point(cursor.X - 40, cursor.Y - 30) : FileHome;
        SetLeft(_file, filePos.X + offsetX);
        SetTop(_file, filePos.Y);
        _file.Opacity = dragging ? 0.75 : 1;

        // Shift key taps at 1.9 s.
        double keyIn = Phase(t, 1.8, 1.95), keyOut = Phase(t, 3.6, 3.9);
        _key.Opacity = keyIn * (1 - keyOut);
        _key.BorderThickness = t > 1.95 && t < 2.15 ? new Thickness(1, 1, 1, 1) : new Thickness(1, 1, 1, 3);
        SetLeft(_key, 40 + offsetX);
        SetTop(_key, 30);

        // Wheel blooms at 2.0 s and vanishes on drop at 3.6 s.
        bool wheelVisible = t >= 2.0 && t < 3.6;
        double bloom = Ease(Phase(t, 2.0, 2.2));
        _wheel.Visibility = wheelVisible ? Visibility.Visible : Visibility.Hidden;
        _wheelScale.ScaleX = _wheelScale.ScaleY = 0.4 + 0.6 * bloom;
        _wheel.Opacity = bloom;
        SetLeft(_wheel, DropPoint.X + offsetX - 0.62 * 124);
        SetTop(_wheel, DropPoint.Y - 0.62 * 124);
        int highlight = t >= 2.75 && t < 3.15 ? 1 : t >= 3.15 && t < 3.6 ? 0 : -1;
        for (int i = 0; i < _petals.Count; i++)
            _petals[i].Fill = i == highlight
                ? new SolidColorBrush(Theme.BrandAccent)
                : new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
        foreach (var child in _wheel.Children.OfType<TextBlock>())
            child.Foreground = (int)child.Tag! == highlight ? Brushes.White : Brushes.Black;

        // Toast pops out of the drop point, then the new file appears beside the original.
        double pop = Phase(t, 3.65, 4.05);
        double spring = pop <= 0 ? 0 : 1 - Math.Exp(-6 * pop) * Math.Cos(9 * pop);
        _toast.Opacity = t >= 3.65 && t < 5.2 ? Math.Min(1, pop * 3) * (1 - Phase(t, 4.9, 5.2)) : 0;
        _toast.RenderTransform = new ScaleTransform(0.5 + 0.5 * spring, 0.5 + 0.5 * spring);
        SetLeft(_toast, DropPoint.X + offsetX - 70);
        SetTop(_toast, DropPoint.Y - 30);

        double appear = Ease(Phase(t, 4.1, 4.5));
        _result.Opacity = appear * (1 - Phase(t, 5.8, 6.2));
        _file.Opacity *= 1 - Phase(t, 5.8, 6.2) * 0.0;
        SetLeft(_result, FileHome.X + 100 + offsetX);
        SetTop(_result, FileHome.Y + (1 - appear) * 10);
    }
}
