using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Tools;
using Petal.App.Visuals;
using Petal.Core.Formats;
using Petal.Core.Tools;
using Petal.Core.Util;

namespace Petal.App.Settings;

/// <summary>Petal's only "main" window. Every change is saved immediately.</summary>
public sealed class SettingsWindow : ToolWindow
{
    private static SettingsWindow? _open;
    private readonly AppSettings _s = SettingsStore.Current;
    private readonly TextBlock _keyWarning = new();

    public static void ShowSingle()
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return;
        }
        _open = new SettingsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow() : base("Petal Settings", 560)
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.Manual;
        Height = Math.Min(780, SystemParameters.WorkArea.Height - 40);
        PrimaryButton.Content = "Done";
        PrimaryButton.Click += (_, _) => Close();
        FooterNote = $"Petal {Version} · {ConversionMatrix.TotalPaths} conversions · {ToolRegistry.All.Count} tools · 6 actions";

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(-18, -14, -18, -16) };
        var page = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };
        scroll.Content = page;
        Body.Children.Add(scroll);
        Body.SizeChanged += (_, _) => { };
        // Let the scroll viewer fill the window height.
        Loaded += (_, _) =>
        {
            scroll.Height = Math.Max(200, ActualHeight - 44 - 1 - 1 - 56);
        };

        page.Children.Add(Hero());
        page.Children.Add(Card("Keys", "Start dragging a file, then tap one of these keys. The wheel stays open until you drop.", KeysSection()));
        page.Children.Add(Card("Appearance", null, AppearanceSection()));
        page.Children.Add(Card("Results", null, OutputSection()));
        page.Children.Add(Card("Quality", "Defaults used by conversions. Tools have their own settings in their windows.", QualitySection()));
        page.Children.Add(Card("Helpers", "Petal uses these programs when they're installed. Everything runs on this PC.", EnginesSection()));
        page.Children.Add(Card("General", null, GeneralSection()));
    }

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";

    private static void Save() => SettingsStore.Save();

    // ---------- sections ----------

    private UIElement Hero()
    {
        var grid = new Grid { Margin = new Thickness(2, 2, 2, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Image { Width = 56, Height = 56, Source = AppIcon.Large, Margin = new Thickness(0, 0, 16, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        var title = Text("Petal", "Petal.Title");
        title.FontSize = 22;
        text.Children.Add(title);
        text.Children.Add(Text("The zero-click file converter. Drag a file, tap Shift, and drop.", "Petal.Caption"));
        var how = new Button { Content = "How it works", Style = (Style)FindResource("Petal.SubtleButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 4, 0, 0) };
        how.SetResourceReference(ForegroundProperty, "Petal.Accent");
        how.Click += (_, _) => Onboarding.WelcomeWindow.ShowSingle();
        text.Children.Add(how);
        grid.Children.Add(text);
        return grid;
    }

    private UIElement KeysSection()
    {
        var panel = new StackPanel();
        var options = new[] { (ModifierKey.Shift, "Shift ⇧"), (ModifierKey.Control, "Ctrl"), (ModifierKey.Alt, "Alt"), (ModifierKey.Windows, "Windows ⊞") };
        ComboBox Key(ModifierKey current, Action<ModifierKey> set)
        {
            var combo = Choice(options, current);
            combo.SelectionChanged += (_, _) =>
            {
                set(ValueOf<ModifierKey>(combo));
                ValidateKeys();
                Save();
            };
            return combo;
        }
        panel.Children.Add(Row("Convert formats", Key(_s.ConvertKey, k => _s.ConvertKey = k), 150, 0));
        panel.Children.Add(Row("File tools", Key(_s.ToolsKey, k => _s.ToolsKey = k), 150));
        panel.Children.Add(Row("File actions", Key(_s.ActionsKey, k => _s.ActionsKey = k), 150));
        _keyWarning.Style = (Style)FindResource("Petal.Caption");
        _keyWarning.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        _keyWarning.Margin = new Thickness(150, 6, 0, 0);
        panel.Children.Add(_keyWarning);
        ValidateKeys();
        return panel;
    }

    private void ValidateKeys()
    {
        var keys = new[] { _s.ConvertKey, _s.ToolsKey, _s.ActionsKey };
        _keyWarning.Text = keys.Distinct().Count() < 3
            ? "Each wheel needs its own key."
            : keys.Contains(ModifierKey.Windows) ? "Tip: tapping Windows alone may also open Start." : "";
        _keyWarning.Visibility = _keyWarning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement AppearanceSection()
    {
        var panel = new StackPanel();
        var theme = Segments([(ThemePreference.System, "System"), (ThemePreference.Light, "Light"), (ThemePreference.Dark, "Dark")], _s.Theme);
        theme.Changed += t =>
        {
            _s.Theme = t;
            Theme.Configure(t, _s.UseWindowsAccent);
            Save();
        };
        panel.Children.Add(Row("Theme", theme, 150, 0));
        var accent = Segments([(false, "Petal violet"), (true, "Windows accent")], _s.UseWindowsAccent);
        accent.Changed += v =>
        {
            _s.UseWindowsAccent = v;
            Theme.Configure(_s.Theme, v);
            Save();
        };
        panel.Children.Add(Row("Accent colour", accent, 150, 10));
        panel.Children.Add(Switch("Sounds", "A soft tick as you move across the wheel.", _s.Sounds, v => _s.Sounds = v));
        panel.Children.Add(Switch("Key hints", "Show Shift · Ctrl · Alt above the wheel.", _s.ShowKeyHints, v => _s.ShowKeyHints = v));
        return panel;
    }

    private UIElement OutputSection()
    {
        var panel = new StackPanel();
        var folderText = Text(string.IsNullOrEmpty(_s.OutputFolder) ? "No folder chosen" : _s.OutputFolder!, "Petal.Caption");
        folderText.TextTrimming = TextTrimming.CharacterEllipsis;
        folderText.TextWrapping = TextWrapping.NoWrap;
        var choose = new Button { Content = "Choose…", HorizontalAlignment = HorizontalAlignment.Left };
        var folderRow = new DockPanel { Margin = new Thickness(150, 8, 0, 0) };
        DockPanel.SetDock(choose, Dock.Left);
        folderText.Margin = new Thickness(10, 0, 0, 0);
        folderText.VerticalAlignment = VerticalAlignment.Center;
        folderRow.Children.Add(choose);
        folderRow.Children.Add(folderText);

        var where = Segments([(OutputLocation.BesideOriginal, "Beside the original"), (OutputLocation.Folder, "In a folder")], _s.Output);
        void Sync() => folderRow.Visibility = _s.Output == OutputLocation.Folder ? Visibility.Visible : Visibility.Collapsed;
        where.Changed += v =>
        {
            _s.Output = v;
            Sync();
            Save();
        };
        choose.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Save Petal results in" };
            if (dialog.ShowDialog(this) == true)
            {
                _s.OutputFolder = dialog.FolderName;
                folderText.Text = dialog.FolderName;
                Save();
            }
        };
        Sync();
        panel.Children.Add(Row("Save results", where, 150, 0));
        panel.Children.Add(folderRow);
        panel.Children.Add(Switch("Select results", "Highlight new files in the open File Explorer window.", _s.SelectResultInExplorer, v => _s.SelectResultInExplorer = v));
        panel.Children.Add(Text("Originals are never changed. Results get names like “photo.png” or “photo Compressed.jpg”.", "Petal.Caption").WithMargin(150, 10, 0, 0));
        return panel;
    }

    private UIElement QualitySection()
    {
        var c = _s.Conversion;
        var panel = new StackPanel();
        var image = Slider(40, 100, c.ImageQuality, v => $"{v:0}", 1);
        image.Changed += v => { c.ImageQuality = (int)v; Save(); };
        panel.Children.Add(Row("Image quality", image, 150, 0));
        panel.Children.Add(Text("JPG, WEBP, HEIC and AVIF.", "Petal.Caption").WithMargin(150, 0, 0, 4));

        var dpi = Segments([(150, "150"), (200, "200"), (300, "300"), (600, "600")], c.RenderDpi);
        dpi.Changed += v => { c.RenderDpi = v; Save(); };
        panel.Children.Add(Row("Page images (DPI)", dpi, 150, 10));

        var video = Segments([(17, "Best"), (20, "High"), (24, "Balanced"), (28, "Small")], NearestCrf(c.VideoCrf));
        video.Changed += v => { c.VideoCrf = v; Save(); };
        panel.Children.Add(Row("Video quality", video, 150, 10));

        var audio = Segments([(128, "128"), (192, "192"), (256, "256"), (320, "320")], c.AudioBitrateKbps);
        audio.Changed += v => { c.AudioBitrateKbps = v; Save(); };
        panel.Children.Add(Row("Audio (kbps)", audio, 150, 10));

        var gifFps = Segments([(10, "10"), (15, "15"), (24, "24"), (30, "30")], c.GifFps);
        gifFps.Changed += v => { c.GifFps = v; Save(); };
        panel.Children.Add(Row("GIF frame rate", gifFps, 150, 10));

        var gifWidth = Segments([(480, "480"), (720, "720"), (1080, "1080"), (0, "Original")], c.GifMaxWidth);
        gifWidth.Changed += v => { c.GifMaxWidth = v; Save(); };
        panel.Children.Add(Row("GIF width", gifWidth, 150, 10));

        panel.Children.Add(Switch("Hardware video encoding", "Use the graphics card when it can encode H.264 faster.", c.HardwareVideoEncoding, v => c.HardwareVideoEncoding = v));
        return panel;
    }

    private static int NearestCrf(int crf) => new[] { 17, 20, 24, 28 }.MinBy(v => Math.Abs(v - crf));

    private UIElement EnginesSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Engine("FFmpeg", "Audio, video and GIF", ToolLocator.Ffmpeg, "https://www.gyan.dev/ffmpeg/builds/"));
        panel.Children.Add(Engine("ExifTool", "Detailed metadata for every file type", ToolLocator.ExifTool, "https://exiftool.org/"));
        panel.Children.Add(Engine("Microsoft Office", "Exact layout for Word, Excel and PowerPoint files", OfficePath(), null));
        panel.Children.Add(Engine("LibreOffice", "Slides and office files when Office isn't installed", ToolLocator.LibreOffice, "https://www.libreoffice.org/download/"));
        return panel;
    }

    private static string? OfficePath()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"PowerPoint.Application\CLSID");
            return key is null ? null : "Installed";
        }
        catch
        {
            return null;
        }
    }

    private UIElement Engine(string name, string purpose, string? path, string? url)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Margin = new Thickness(2, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Border.BackgroundProperty, path is null ? "Petal.TextDisabled" : "Petal.Success");
        grid.Children.Add(dot);
        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        var title = Text(name);
        title.FontWeight = FontWeights.SemiBold;
        text.Children.Add(title);
        text.Children.Add(Text(path is null ? $"{purpose} — not found" : $"{purpose}", "Petal.Caption"));
        grid.Children.Add(text);
        if (path is null && url is not null)
        {
            var get = new Button { Content = "Get", VerticalAlignment = VerticalAlignment.Center };
            get.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Grid.SetColumn(get, 2);
            grid.Children.Add(get);
        }
        else if (path is not null && File.Exists(path))
        {
            grid.ToolTip = path;
        }
        return grid;
    }

    private UIElement GeneralSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Switch("Start with Windows", "Petal waits quietly in the notification area.", _s.LaunchAtStartup, v =>
        {
            _s.LaunchAtStartup = v;
            LaunchAtLogin.Apply(v);
        }, top: 0));
        panel.Children.Add(Switch("Pause Petal", "Ignore key taps while dragging until you turn this off.", _s.Paused, v => _s.Paused = v));
        var log = new Button { Content = "Open activity log", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        log.Click += (_, _) =>
        {
            if (File.Exists(Log.FilePath))
                Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        };
        panel.Children.Add(log);
        return panel;
    }

    // ---------- pieces ----------

    private UIElement Switch(string title, string description, bool value, Action<bool> set, double top = 12)
    {
        var text = new StackPanel();
        var t = Text(title);
        text.Children.Add(t);
        text.Children.Add(Text(description, "Petal.Caption"));
        var toggle = new CheckBox { Style = (Style)FindResource("Petal.Switch"), Content = text, IsChecked = value, Margin = new Thickness(0, top, 0, 0) };
        toggle.Checked += (_, _) => { set(true); Save(); };
        toggle.Unchecked += (_, _) => { set(false); Save(); };
        return toggle;
    }

    private UIElement Card(string title, string? subtitle, UIElement content)
    {
        var stack = new StackPanel();
        var heading = Text(title, "Petal.Section");
        heading.FontSize = 14;
        heading.Margin = new Thickness(0, 0, 0, subtitle is null ? 10 : 2);
        stack.Children.Add(heading);
        if (subtitle is not null)
            stack.Children.Add(Text(subtitle, "Petal.Caption").WithMargin(0, 0, 0, 12));
        stack.Children.Add(content);
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 16, 18, 18),
            Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Petal.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        return card;
    }
}

internal static class MarginExtensions
{
    public static T WithMargin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }
}
