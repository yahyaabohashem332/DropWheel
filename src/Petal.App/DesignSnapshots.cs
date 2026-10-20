using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Overlay;
using Petal.App.Visuals;
using Petal.Core.Jobs;
using Petal.Engines.Imaging;

namespace Petal.App;

/// <summary>
/// "Petal.exe --snapshots &lt;folder&gt; &lt;background image&gt; &lt;files…&gt;" renders the wheel in every
/// mode, light and dark, to PNG files without touching the screen. Used for design reviews.
/// </summary>
internal static class DesignSnapshots
{
    public static void Run(string[] args)
    {
        var output = args[1];
        var background = Load(args[2]);
        var files = args.Skip(3).ToList();
        Directory.CreateDirectory(output);

        var converters = new ConverterRegistry();
        converters.Register(new ImageConverter());
        var controller = new PetalController(converters, Application.Current.Dispatcher);
        var wheel = controller.CreateWheelForSnapshots();

        foreach (var dark in new[] { false, true })
        {
            Theme.Configure(dark ? ThemePreference.Dark : ThemePreference.Light, false);
            Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            foreach (var (name, window) in new (string, Window)[]
            {
                ("settings", CreateSettings()),
                ("welcome", CreateWelcome()),
            })
            {
                Save(RenderWindow(window), Path.Combine(output, $"{(dark ? "dark" : "light")}-{name}.png"));
                window.Close();
            }
            foreach (var mode in Enum.GetValues<WheelMode>())
            {
                foreach (int? highlight in new int?[] { null, 1 })
                {
                    var image = wheel.Snapshot(files, mode, highlight, background);
                    var name = $"{(dark ? "dark" : "light")}-{mode.ToString().ToLowerInvariant()}{(highlight is null ? "" : "-hover")}.png";
                    Save(image, Path.Combine(output, name));
                }
            }
        }
    }

    private static Window CreateSettings() =>
        (Window)typeof(Settings.SettingsWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    private static Window CreateWelcome() =>
        (Window)typeof(Onboarding.WelcomeWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    /// <summary>Lays out a window's content at its width and renders it over the window background.</summary>
    public static BitmapSource RenderWindow(Window window, double height = double.NaN)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var frame = new System.Windows.Controls.Border
        {
            Width = window.Width,
            Background = Palette.Brush(Theme.Current.WindowBackground),
            Child = content,
            BorderBrush = Palette.Brush(Theme.Current.Divider),
            BorderThickness = new Thickness(1),
        };
        if (!double.IsNaN(height)) frame.Height = height;
        else if (window.SizeToContent == SizeToContent.Manual) frame.Height = window.Height;
        frame.Measure(new Size(window.Width, double.PositiveInfinity));
        var size = new Size(window.Width, double.IsNaN(frame.Height) ? frame.DesiredSize.Height : frame.Height);
        frame.Arrange(new Rect(size));
        frame.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path));
        image.EndInit();
        image.Freeze();
        // Normalise DPI so pixel sizes match layout units.
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        var normal = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        normal.Freeze();
        return normal;
    }

    private static void Save(BitmapSource image, string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }
}
