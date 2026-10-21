using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.Core.Actions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;

namespace Petal.App.Onboarding;

/// <summary>
/// Practice mode: while it is on, every wheel works as usual but nothing runs; each choice says what would
/// have happened instead. A sample photo to drag comes with it.
/// </summary>
public static class Practice
{
    public static bool IsOn { get; private set; }

    /// <summary>What a choice would have done, for the practice window and the welcome exercise.</summary>
    public static event Action<string>? Explained;

    /// <summary>A petal chosen while practising: the petal, the files, the page ("files:Tools") and what was said.</summary>
    public static event Action<WheelItem, IReadOnlyList<string>, string, string>? Chosen;

    public static string Folder => Path.Combine(Path.GetTempPath(), "Petal Practice");

    public static void Start() => IsOn = true;

    public static void Stop() => IsOn = false;

    public static bool Owns(IReadOnlyList<string> files) =>
        files.Count > 0 && files.All(f => f.StartsWith(Folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>A sample photo to drag, drawn on first use.</summary>
    public static string SamplePhoto()
    {
        var path = Path.Combine(Folder, L.IsArabic ? "صورة للتجربة.jpg" : "Sample photo.jpg");
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(Folder);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(Color.FromRgb(0xFF, 0xB3, 0x6B), Color.FromRgb(0x7B, 0x5C, 0xFF), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, 1200, 800));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xE8, 0xA0)), null, new Point(840, 300), 110, 110);
            var hills = new StreamGeometry();
            using (var g = hills.Open())
            {
                g.BeginFigure(new Point(0, 620), true, true);
                g.BezierTo(new Point(260, 470), new Point(520, 700), new Point(760, 560), true, false);
                g.BezierTo(new Point(940, 460), new Point(1100, 560), new Point(1200, 520), true, false);
                g.LineTo(new Point(1200, 800), true, false);
                g.LineTo(new Point(0, 800), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x2E, 0x22, 0x5E)), null, hills);
        }
        var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        return path;
    }

    /// <summary>Says what a petal would have done, on a card and to whoever listens.</summary>
    public static void Explain(WheelItem item, IReadOnlyList<string> files, ScreenPoint at, string page = "")
    {
        var text = Describe(item, files);
        var card = Hud.HudCard.ShowAt(at, L.T("Practice: nothing was changed"), text);
        card.Succeed(L.T("Practice: nothing was changed"), text, null);
        Explained?.Invoke(text);
        Chosen?.Invoke(item, files, page, text);
    }

    /// <summary>What choosing the petal for these files would have done, in a sentence.</summary>
    public static string Describe(WheelItem item, IReadOnlyList<string> files)
    {
        var name = files.Count == 1 ? Path.GetFileName(files[0]) : L.P(files.Count, "{0} file", "{0} files");
        return item.Payload switch
        {
            FileFormat f when files.Count > 0 => L.F("Petal would save {0} as {1} beside the original.", name, f.Label),
            Favourite when files.Count > 0 => L.F("Petal would make {0} with your favourite “{1}”.", name, L.T(item.Label)),
            ToolInfo { HasWindow: true } t => L.F("The {0} window would open for {1}.", L.T(t.Name), name),
            ToolInfo t => L.F("{0} would run on {1}.", L.T(t.Name), name),
            FileActionInfo a => L.F("{0} would run on {1}.", L.T(a.Name), name),
            _ when files.Count > 0 => L.F("{0} would run on {1}.", L.T(item.Name), name),
            _ => L.F("“{0}” would run.", L.T(item.Name)),
        };
    }
}

/// <summary>
/// The practice window: a sample photo to drag (tap Shift, Ctrl or Alt on the way and drop on a petal), a
/// button for the keyboard wheel, and one for the wheels without files. Practice is on while it is open.
/// </summary>
public sealed class PracticeWindow : ToolWindow
{
    private static PracticeWindow? _open;
    private readonly TextBlock _said = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };

    public static void ShowSingle()
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return;
        }
        _open = new PracticeWindow();
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    private PracticeWindow() : base("Practice", 460)
    {
        Practice.Start();
        Closed += (_, _) =>
        {
            Practice.Stop();
            Practice.Explained -= OnExplained;
        };
        Practice.Explained += OnExplained;
        var photo = Practice.SamplePhoto();
        Body.Children.Add(Text("Try the wheel here: nothing runs while this window is open. Each choice says what would have happened.", "Petal.Body"));
        Body.Children.Add(Text("Drag the photo, tap Shift on the way, and drop it on a petal. Ctrl and Alt switch to the tools and actions.", "Petal.Caption").WithMargin(0, 6, 0, 0));
        Body.Children.Add(SampleTile(photo));
        var keyboard = new Button { Content = L.T("Open the Wheel for It"), Margin = new Thickness(0, 0, 8, 0) };
        keyboard.Click += (_, _) =>
        {
            Interop.NativeMethods.GetCursorPos(out var cursor);
            App.Current.Controller.OpenWheelNow([photo], new ScreenPoint(cursor.X, cursor.Y));
        };
        var rings = new Button { Content = L.T("Try the Wheels Without Files") };
        rings.Click += async (_, _) =>
        {
            Interop.NativeMethods.GetCursorPos(out var cursor);
            await App.Current.Controller.OpenRingsAsync(Interop.NativeMethods.GetForegroundWindow(), new ScreenPoint(cursor.X, cursor.Y));
        };
        Body.Children.Add(new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { keyboard, rings } });
        _said.Text = L.T("Nothing chosen yet.");
        _said.SetResourceReference(TextBlock.ForegroundProperty, "Petal.TextSecondary");
        Body.Children.Add(_said);
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
    }

    private void OnExplained(string text) => Dispatcher.BeginInvoke(() =>
    {
        _said.Text = text;
        _said.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Text");
    });

    /// <summary>The photo as a tile that drags like a file from File Explorer.</summary>
    private static Border SampleTile(string photo)
    {
        var image = new Image { Source = new BitmapImage(new Uri(photo)), Height = 120, Stretch = Stretch.Uniform };
        var caption = new TextBlock { Text = Path.GetFileName(photo), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        var tile = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 12, 0, 0),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new StackPanel { Children = { image, caption } },
            ToolTip = L.T("Drag me"),
        };
        tile.SetResourceReference(Border.BackgroundProperty, "Petal.Surface");
        tile.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        System.Windows.Automation.AutomationProperties.SetName(tile, L.T("Sample photo to drag"));
        Point? down = null;
        tile.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(tile);
        tile.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed)
                return;
            var now = e.GetPosition(tile);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            down = null;
            var data = new DataObject(DataFormats.FileDrop, new[] { photo });
            DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy);
        };
        tile.PreviewMouseLeftButtonUp += (_, _) => down = null;
        return tile;
    }
}
