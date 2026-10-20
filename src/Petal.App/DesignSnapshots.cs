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
    /// <summary>"Petal.exe --snapshot-tools &lt;folder&gt; &lt;test data folder&gt;" renders each tool window with sample files.</summary>
    public static async Task RunToolsAsync(string[] args)
    {
        var output = args[1];
        var data = args[2];
        Directory.CreateDirectory(output);
        var converters = EngineSetup.CreateConverters();
        var jobs = new Jobs.JobRunner(converters);
        var at = new Input.ScreenPoint(-4000, -4000);
        string F(string name) => Path.Combine(data, name);
        var cases = new List<(string Name, Func<Window> Create)>
        {
            ("annotate", () => new Tools.Windows.AnnotateWindow(Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.AnnotatePhoto), F("landscape.jpg"), jobs, at)),
            ("addbg", () => new Tools.Windows.AddBackgroundWindow(Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.AddBackground), F("figures.png"), jobs, at)),
            ("collage", () => new Tools.Windows.CollageWindow(Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.CreateCollage), [F("landscape.jpg"), F("figures.png"), F("landscape.jpg")], jobs, at)),
            ("metadata", () => new Tools.Windows.MetadataWindow(Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.Metadata), [F("landscape.jpg")], jobs, at)),
        };
        foreach (var extra in ExtraToolCases(F, jobs, at))
            cases.Add(extra);
        var only = args.Length > 3 ? args[3] : null;
        foreach (var dark in new[] { false, true })
        {
            Theme.Configure(dark ? ThemePreference.Dark : ThemePreference.Light, false);
            foreach (var (name, create) in cases)
            {
                if (only is not null && !name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var window = create();
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -6000;
                    window.Top = -6000;
                    window.ShowActivated = false;
                    window.Show();
                    await Task.Delay(2500);
                    Save(RenderWindow(window), Path.Combine(output, $"{(dark ? "dark" : "light")}-{name}.png"));
                    window.Close();
                }
                catch (Exception ex)
                {
                    File.AppendAllText(Path.Combine(output, "error.txt"), $"{name}: {ex}{Environment.NewLine}");
                }
            }
        }
    }

    /// <summary>Tool windows added later register here so the snapshot run covers them.</summary>
    private static IEnumerable<(string, Func<Window>)> ExtraToolCases(Func<string, string> file, Jobs.JobRunner jobs, Input.ScreenPoint at)
    {
        Petal.Core.Tools.ToolInfo T(Petal.Core.Tools.ToolId id) => Petal.Core.Tools.ToolRegistry.Get(id);
        var video = new[] { file("weekend.mov") };
        var audio = new[] { file("mixtape.mp3") };
        return
        [
            ("compress", () => new Tools.Windows.CompressWindow(T(Petal.Core.Tools.ToolId.Compress), video, jobs, at)),
            ("trimvideo", () => new Tools.Windows.TrimVideoWindow(T(Petal.Core.Tools.ToolId.TrimVideo), video, jobs, at)),
            ("cropvideo", () => new Tools.Windows.CropVideoWindow(T(Petal.Core.Tools.ToolId.CropVideo), video, jobs, at)),
            ("speed", () => new Tools.Windows.ChangeSpeedWindow(T(Petal.Core.Tools.ToolId.ChangeSpeed), video, jobs, at)),
            ("snapshots", () => new Tools.Windows.VideoSnapshotsWindow(T(Petal.Core.Tools.ToolId.VideoSnapshots), video, jobs, at)),
            ("split", () => new Tools.Windows.SplitVideoWindow(T(Petal.Core.Tools.ToolId.SplitVideo), video, jobs, at)),
            ("redactvideo", () => new Tools.Windows.RedactVideoWindow(T(Petal.Core.Tools.ToolId.RedactVideo), video, jobs, at)),
            ("normalize", () => new Tools.Windows.NormalizeWindow(T(Petal.Core.Tools.ToolId.NormalizeAudio), audio, jobs, at)),
            ("trimaudio", () => new Tools.Windows.TrimAudioWindow(T(Petal.Core.Tools.ToolId.TrimAudio), audio, jobs, at)),
            ("channels", () => new Tools.Windows.AudioChannelsWindow(T(Petal.Core.Tools.ToolId.AudioChannels), audio, jobs, at)),
            ("visualizer", () => new Tools.Windows.VisualizerWindow(T(Petal.Core.Tools.ToolId.AudioVisualizer), audio, jobs, at)),
            ("bleep", () => new Tools.Windows.BleepWindow(T(Petal.Core.Tools.ToolId.BleepAudio), audio, jobs, at)),
            ("editphoto", () => new Tools.Windows.EditPhotoWindow(T(Petal.Core.Tools.ToolId.EditPhoto), file("landscape.jpg"), jobs, at)),
            ("cropimage", () => new Tools.Windows.CropImageWindow(T(Petal.Core.Tools.ToolId.CropImage), [file("figures.png")], jobs, at)),
            ("redactphoto", () => new Tools.Windows.RedactPhotoWindow(T(Petal.Core.Tools.ToolId.RedactPhoto), file("landscape.jpg"), jobs, at)),
            ("organizepdf", () => new Tools.Windows.OrganizePdfWindow(T(Petal.Core.Tools.ToolId.OrganizePdf), file("album.pdf"), jobs, at)),
            ("resize", () => new Tools.Windows.ResizeImageWindow(T(Petal.Core.Tools.ToolId.ResizeImage), [file("landscape.jpg")], jobs, at)),
            ("resizebatch", () => new Tools.Windows.ResizeImageWindow(T(Petal.Core.Tools.ToolId.ResizeImage), [file("landscape.jpg"), file("figures.png")], jobs, at)),
            ("watermark", () => new Tools.Windows.WatermarkWindow(T(Petal.Core.Tools.ToolId.Watermark), [file("landscape.jpg")], jobs, at)),
            ("watermarkpdf", () => new Tools.Windows.WatermarkWindow(T(Petal.Core.Tools.ToolId.Watermark), [file("album.pdf")], jobs, at)),
            ("password", () => new Tools.Windows.PdfPasswordWindow(T(Petal.Core.Tools.ToolId.ProtectPdf), [file("album.pdf")], jobs, at)),
            ("pagenumbers", () => new Tools.Windows.PageNumbersWindow(T(Petal.Core.Tools.ToolId.NumberPages), [file("album.pdf")], jobs, at)),
            ("signpdf", () => new Tools.Windows.SignPdfWindow(T(Petal.Core.Tools.ToolId.SignPdf), file("album.pdf"), jobs, at)),
            ("zip", () => new Tools.Windows.ZipWindow([file("landscape.jpg"), file("album.pdf")], jobs, at)),
            ("rename", () => new Tools.Windows.RenameWindow([file("landscape.jpg"), file("figures.png"), file("weekend.mov")], at)),
            ("qr", () => (Window)typeof(Tools.Windows.QrWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null)),
            ("removebg", () => new Tools.Windows.RemoveBackgroundWindow(T(Petal.Core.Tools.ToolId.RemoveBackground), [file("figures.png")], jobs, at)),
            ("transcribe", () => new Tools.Windows.TranscribeWindow(T(Petal.Core.Tools.ToolId.Transcribe), video, jobs, at)),
            ("history", () => { SampleHistory(file); return Private<Tools.Windows.HistoryWindow>(); }),
            ("favourite", () => new Settings.FavouriteWindow(new Favourite { Format = "webp", LongestSide = 1600, Quality = 80 }, _ => { }, _ => { })),
            ("automation", () => new Settings.AutomationWindow(new WatchRule { Folder = Path.GetDirectoryName(file("landscape.jpg"))!, Kind = WatchedKind.Pictures, Target = "jpg" }, _ => { }, _ => { })),
            ("phone", () => Actions.PhoneWindow.ForSnapshot([file("landscape.jpg"), file("weekend.mov"), file("album.pdf")])),
            ("update", () => (Window)typeof(Shell.UpdateWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(Petal.Core.Util.UpdateManifest)])!
                .Invoke([new Petal.Core.Util.UpdateManifest { Version = "1.1.0", Url = "Petal-Setup-1.1.0.exe", Size = 148_000_000, Notes = Localization.L.T("Remove Background") + " · " + Localization.L.T("Transcribe") + " · " + Localization.L.T("Send to Phone") }])),
        ];
    }

    private static T Private<T>() where T : Window =>
        (T)typeof(T).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    /// <summary>A few made-up history entries in a scratch file, so snapshots never show the user's own.</summary>
    private static void SampleHistory(Func<string, string> file)
    {
        Jobs.History.FilePath = Path.Combine(Path.GetTempPath(), "petal-snapshot-history.json");
        Jobs.History.Reload();
        Jobs.History.Clear();
        Jobs.History.Add(Localization.L.P(3, "Renamed {0} item", "Renamed {0} items"), [], [], [new Jobs.RenamedPath(file("a.jpg"), file("landscape.jpg"))]);
        Jobs.History.Add(Localization.L.F("Converted to {0}", "PDF"), [file("landscape.jpg")], [file("album.pdf")]);
        Jobs.History.Add(Localization.L.T("Background removed"), [file("landscape.jpg")], [file("figures.png")]);
        // The oldest one shown as undone, written directly: snapshots never move real files.
        var entries = Jobs.History.Entries.ToList();
        entries[^1] = entries[^1] with { Undone = true };
        File.WriteAllText(Jobs.History.FilePath, System.Text.Json.JsonSerializer.Serialize(entries));
        Jobs.History.Reload();
    }

    /// <summary>Favourites and automations to show in snapshots of Settings and the wheel (never saved).</summary>
    private static void SampleSettings(string folder)
    {
        var s = Settings.SettingsStore.Current;
        s.Favourites =
        [
            new Favourite { Format = "webp", LongestSide = 1600, Quality = 80 },
            new Favourite { Format = "jpg", LongestSide = 1024, Name = Localization.L.T("Email") },
            new Favourite { Format = "mp4", LongestSide = 1280, Quality = 24 },
        ];
        s.Automations = [new WatchRule { Folder = folder, Kind = WatchedKind.Pictures, Target = "jpg" }];
    }

    public static void Run(string[] args)
    {
        var output = args[1];
        var background = Load(args[2]);
        var files = args.Skip(3).ToList();
        Directory.CreateDirectory(output);

        var converters = EngineSetup.CreateConverters();
        SampleSettings(Path.GetDirectoryName(Path.GetFullPath(files.FirstOrDefault() ?? args[2]))!);
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
            // The whole Settings page, every card, for reviewing the lower sections.
            var full = CreateSettings();
            Save(RenderWindow(full, 3600), Path.Combine(output, $"{(dark ? "dark" : "light")}-settings-full.png"));
            full.Close();
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
            FlowDirection = window.FlowDirection,
        };
        Localization.L.Apply(frame, mirror: false);
        if (!double.IsNaN(height)) frame.Height = height;
        else if (window.SizeToContent == SizeToContent.Manual) frame.Height = window.Height;
        frame.Measure(new Size(window.Width, double.PositiveInfinity));
        var size = new Size(window.Width, double.IsNaN(frame.Height) ? frame.DesiredSize.Height : frame.Height);
        frame.Arrange(new Rect(size));
        // A left-to-right parent, so a right-to-left frame is mirrored as it is in a real window.
        var host = new System.Windows.Controls.Border { Child = frame };
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(host);
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
