using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Tools;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;
using Petal.Core.Formats;
using Petal.Core.Tools;
using Petal.Core.Util;
using Petal.App.Localization;

namespace Petal.App.Settings;

/// <summary>Petal's only "main" window. Every change is saved immediately.</summary>
public sealed partial class SettingsWindow : ToolWindow
{
    private static SettingsWindow? _open;
    private readonly AppSettings _s = SettingsStore.Current;
    private readonly TextBlock _keyWarning = new();
    private readonly TextBlock _shortcutWarning = new();

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
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        FooterNote = L.F("Petal {0} · {1} conversions · {2} tools · {3} actions", Version, ConversionMatrix.TotalPaths, ToolRegistry.All.Count, Petal.Core.Actions.FileActionRegistry.All.Count);

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
        page.Children.Add(Card("Wheels", "Wheels that need no files: quick actions, what's playing, shortcuts for the app in front, and your own. The quick shortcut opens them when nothing is selected; Tab or the mouse wheel goes to the next.", WheelsSection()));
        page.Children.Add(Card("Appearance", null, AppearanceSection()));
        page.Children.Add(Card("The Wheel", "How the wheel behaves under the pointer.", BehaviourSection()));
        page.Children.Add(Card("Screen & Text", "Dictation and live captions, showing clicks and keys, screen recording, and the clipboard history.", ScreenTextSection()));
        page.Children.Add(Card("Work & Sharing", "The command palette, snippets, workspaces, and receiving files from other PCs.", WorkSection()));
        page.Children.Add(Card("Actions Wheel", "The actions on the wheel itself (the rest are under More), and folders that dropped files are copied or moved to.", ActionsWheelSection()));
        page.Children.Add(Card("Favourites", "Conversions you use often, with their size and quality. They come first on the conversion wheel, with a star.", FavouritesSection()));
        page.Children.Add(Card("Recipes", "Several steps in one petal on the tools wheel: remove a background, resize, add a watermark, save as WEBP and zip, in one go.", RecipesSection()));
        page.Children.Add(Card("Automations", "Watch a folder and convert what arrives in it, such as new pictures in Downloads.", AutomationsSection()));
        page.Children.Add(Card("Your Petals", "Tools of your own on the tools wheel: a small file says which files a petal takes and which program it runs. Petal shows the command before a petal's first run.", PetalsSection()));
        page.Children.Add(Card("Search by Meaning", "Find files by what they're about: the words in documents and screenshots, and what photos and videos show. Petal reads the folders below while the PC is idle; the index stays on this PC.", SearchSection()));
        page.Children.Add(Card("Results", null, OutputSection()));
        page.Children.Add(Card("Quality", "Defaults used by conversions. Tools have their own settings in their windows.", QualitySection()));
        page.Children.Add(Card("Helpers", "Petal uses these programs when they're installed. Everything runs on this PC.", EnginesSection()));
        page.Children.Add(Card("Statistics", "What Petal has done for you, counted on this PC only.", StatisticsSection()));
        page.Children.Add(Card("General", null, GeneralSection()));
        page.Children.Add(Card("Backup", "All your settings, wheels, favourites, recipes, automations and petals in one file: to keep, or to bring to another PC.", BackupSection()));
        page.Children.Add(Card("Updates", null, UpdatesSection()));
        _scroll = scroll;
        WatchForChanges();
    }

    private static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";

    private static void Save() => SettingsStore.Save();

    // ---------- sections ----------

    private UIElement Hero()
    {
        var grid = new Grid { Margin = new Thickness(2, 2, 2, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Image { FlowDirection = FlowDirection.LeftToRight, Width = 56, Height = 56, Source = AppIcon.Large, Margin = new Thickness(0, 0, 16, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        var title = Text("Petal", "Petal.Title");
        title.FontSize = 22;
        text.Children.Add(title);
        text.Children.Add(Text("The zero-click file converter. Drag a file, tap Shift, and drop.", "Petal.Caption"));
        var how = new Button { Content = L.T("How it works"), Style = (Style)FindResource("Petal.SubtleButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 4, 0, 0) };
        how.SetResourceReference(ForegroundProperty, "Petal.Accent");
        how.Click += (_, _) => Onboarding.WelcomeWindow.ShowSingle();
        var qr = new Button { Content = L.T("Create QR Code"), Style = (Style)FindResource("Petal.SubtleButton"), Margin = new Thickness(4, 4, 0, 0) };
        qr.SetResourceReference(ForegroundProperty, "Petal.Accent");
        qr.Click += (_, _) => Tools.Windows.QrWindow.ShowSingle();
        var history = new Button { Content = L.T("History"), Style = (Style)FindResource("Petal.SubtleButton"), Margin = new Thickness(4, 4, 0, 0) };
        history.SetResourceReference(ForegroundProperty, "Petal.Accent");
        history.Click += (_, _) => Tools.Windows.HistoryWindow.ShowSingle();
        var practice = new Button { Content = L.T("Practice"), Style = (Style)FindResource("Petal.SubtleButton"), Margin = new Thickness(4, 4, 0, 0) };
        practice.SetResourceReference(ForegroundProperty, "Petal.Accent");
        practice.Click += (_, _) => Onboarding.PracticeWindow.ShowSingle();
        if (Shell.Packaged.Current is { Trial: true } trial)
        {
            var buy = new Button { Content = L.T("Buy Petal"), Style = (Style)FindResource("Petal.SubtleButton"), Margin = new Thickness(-8, 4, 0, 0) };
            buy.SetResourceReference(ForegroundProperty, "Petal.Accent");
            buy.Click += async (_, _) => await Shell.Packaged.BuyAsync(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            var days = (int)Math.Ceiling(trial.TrialLeft.TotalDays);
            text.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { Text(L.P(days, "Trial: {0} day left", "Trial: {0} days left"), "Petal.Caption").WithMargin(0, 6, 12, 0), buy },
            });
        }
        var tour = new Button { Content = L.T("Take the Tour"), Style = (Style)FindResource("Petal.SubtleButton"), Margin = new Thickness(4, 4, 0, 0) };
        tour.SetResourceReference(ForegroundProperty, "Petal.Accent");
        tour.Click += (_, _) => StartTour();
        var guide = new Button { Content = L.T("Tools Guide"), Style = (Style)FindResource("Petal.SubtleButton"), Margin = new Thickness(4, 4, 0, 0) };
        guide.SetResourceReference(ForegroundProperty, "Petal.Accent");
        guide.Click += (_, _) => Guide.GuideWindow.ShowSingle();
        text.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { how, guide, practice, tour, qr, history } });
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
        panel.Children.Add(Row("Quick shortcut", ShortcutPicker(), 150, 16));
        panel.Children.Add(Text("With files selected in File Explorer, it opens the wheel for them.", "Petal.Caption").WithMargin(150, 4, 0, 0));
        var nothing = Choice([(Wheels.NothingSelected.Wheels, "The wheels without files"), (Wheels.NothingSelected.Clipboard, "The clipboard's wheel"), (Wheels.NothingSelected.Petal, "Petal's settings")], _s.NothingSelected);
        nothing.SelectionChanged += (_, _) =>
        {
            _s.NothingSelected = ValueOf<Wheels.NothingSelected>(nothing);
            Save();
        };
        panel.Children.Add(Row("Nothing selected", nothing, 150, 10));
        panel.Children.Add(Text("The clipboard's wheel is also a petal on the quick wheel: files, a screenshot or text you copied. A copied link becomes a QR code.", "Petal.Caption").WithMargin(150, 4, 0, 0));
        var mouse = Choice([(Wheels.MouseTrigger.None, "None"), (Wheels.MouseTrigger.Back, "Back side button"), (Wheels.MouseTrigger.Forward, "Forward side button")], _s.MouseButton);
        mouse.SelectionChanged += (_, _) =>
        {
            _s.MouseButton = ValueOf<Wheels.MouseTrigger>(mouse);
            Save();
        };
        panel.Children.Add(Row("Mouse button", mouse, 150, 12));
        panel.Children.Add(Text("Opens the wheel where the pointer is, like the shortcut. The button no longer goes back or forward in other apps.", "Petal.Caption").WithMargin(150, 4, 0, 0));
        _shortcutWarning.Style = (Style)FindResource("Petal.Caption");
        _shortcutWarning.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        _shortcutWarning.Margin = new Thickness(150, 6, 0, 0);
        _shortcutWarning.Visibility = Visibility.Collapsed;
        panel.Children.Add(_shortcutWarning);
        return panel;
    }

    /// <summary>
    /// Click the button, then press the keys. Esc cancels; the two small buttons restore the
    /// default and turn the shortcut off. Windows is asked first, so a combination another
    /// program owns is refused with a message instead of silently not working.
    /// </summary>
    private UIElement ShortcutPicker()
    {
        var hotkey = App.Current.Hotkey;
        var saved = Shortcut.Parse(_s.QuickShortcut);
        bool recording = false;
        var record = new Button { MinWidth = 180, ToolTip = L.T("Click, then press the keys you want.") };
        void Show() => record.Content = recording ? L.T("Press the new shortcut…") : saved?.Display ?? L.T("Off");
        void Warn(string? text)
        {
            _shortcutWarning.Text = text ?? "";
            _shortcutWarning.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }
        void Stop()
        {
            recording = false;
            hotkey?.Resume();
            Show();
        }
        void Apply(Shortcut? shortcut)
        {
            recording = false;
            if (hotkey is null || hotkey.Register(shortcut))
            {
                saved = shortcut;
                _s.QuickShortcut = shortcut?.ToString() ?? "";
                Save();
                Warn(null);
            }
            else
            {
                Warn(L.F("Another program already uses {0}. Choose a different shortcut.", shortcut!.Display));
            }
            Show();
        }

        record.Click += (_, _) =>
        {
            if (recording) return;
            recording = true;
            // Let the current shortcut through, so it can be pressed again.
            hotkey?.Suspend();
            Warn(null);
            Show();
            record.Focus();
        };
        record.PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key == System.Windows.Input.Key.Escape)
            {
                Stop();
                return;
            }
            if (Shortcut.IsModifier(key) || key is System.Windows.Input.Key.ImeProcessed or System.Windows.Input.Key.DeadCharProcessed)
                return;
            var candidate = new Shortcut(System.Windows.Input.Keyboard.Modifiers, key);
            if (!candidate.IsValid)
            {
                Warn(L.T("Use Ctrl, Alt or Windows together with another key."));
                return;
            }
            Apply(candidate);
        };
        record.LostKeyboardFocus += (_, _) => { if (recording) Stop(); };

        var reset = IconButton("rotateCcw", "Restore the default shortcut", 14);
        reset.Click += (_, _) => Apply(Shortcut.Default);
        var off = IconButton("x", "Turn the shortcut off", 14);
        off.Click += (_, _) => Apply(null);

        // A saved shortcut that Windows refused at start-up belongs to another program.
        if (saved is not null && hotkey is not null && hotkey.Current is null)
            Warn(L.F("Another program already uses {0}. Choose a different shortcut.", saved.Display));
        Show();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { record, reset.WithMargin(6, 0, 0, 0), off.WithMargin(2, 0, 0, 0) },
        };
    }

    private void ValidateKeys()
    {
        var keys = new[] { _s.ConvertKey, _s.ToolsKey, _s.ActionsKey };
        _keyWarning.Text = keys.Distinct().Count() < 3
            ? L.T("Each wheel needs its own key.")
            : keys.Contains(ModifierKey.Windows) ? L.T("Tip: tapping Windows alone may also open Start.") : "";
        _keyWarning.Visibility = _keyWarning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement AppearanceSection()
    {
        var panel = new StackPanel();
        // Each language by its own name, so anyone can find theirs.
        var language = Choice(new[] { (UiLanguage.System, L.T("Automatic")) }.Concat(L.Languages.Select(l => (l.Language, l.Name))), _s.Language);
        language.MinWidth = 220;
        language.HorizontalAlignment = HorizontalAlignment.Left;
        language.SelectionChanged += (_, _) =>
        {
            var l = ValueOf<UiLanguage>(language);
            if (l == _s.Language)
                return;
            _s.Language = l;
            Save();
            // Rebuild this window in the new language and direction, where it was.
            var (left, top) = (Left, Top);
            L.Configure(l);
            Close();
            ShowSingle();
            if (_open is not null)
            {
                _open.WindowStartupLocation = WindowStartupLocation.Manual;
                (_open.Left, _open.Top) = (left, top);
            }
        };
        panel.Children.Add(Row("Language", language, 150, 0));
        panel.Children.Add(Text("Automatic follows the Windows display language.", "Petal.Caption").WithMargin(150, 4, 0, 0));
        var theme = Segments([(ThemePreference.System, "System"), (ThemePreference.Light, "Light"), (ThemePreference.Dark, "Dark")], _s.Theme);
        theme.Changed += t =>
        {
            _s.Theme = t;
            Theme.Configure(t, _s.UseWindowsAccent);
            Save();
        };
        panel.Children.Add(Row("Theme", theme, 150, 12));
        panel.Children.Add(Row("Accent colour", AccentPicker(), 150, 10));
        var style = Segments([(Overlay.WheelStyle.Glass, "Glass"), (Overlay.WheelStyle.Solid, "Solid"), (Overlay.WheelStyle.Mono, "Mono")], _s.WheelStyle);
        style.Changed += v => { _s.WheelStyle = v; Save(); };
        panel.Children.Add(Row("Wheel", style, 150, 10));
        panel.Children.Add(Text("Glass shows what is behind the wheel, frosted. Mono leaves out the accent colour.", "Petal.Caption").WithMargin(150, 4, 0, 0));
        var motion = Choice([(Overlay.WheelMotion.Bloom, "Bloom"), (Overlay.WheelMotion.Pop, "Pop"), (Overlay.WheelMotion.Fade, "Fade"), (Overlay.WheelMotion.Unfold, "Unfold"), (Overlay.WheelMotion.None, "None")], _s.WheelMotion);
        motion.SelectionChanged += (_, _) => { _s.WheelMotion = ValueOf<Overlay.WheelMotion>(motion); Save(); };
        panel.Children.Add(Row("Opening", motion, 150, 10));
        var names = Segments([(true, "Names"), (false, "Icons only")], _s.ShowPetalNames);
        names.Changed += v => { _s.ShowPetalNames = v; Save(); };
        panel.Children.Add(Row("Petals show", names, 150, 10));
        panel.Children.Add(Text("Formats always show their names.", "Petal.Caption").WithMargin(150, 4, 0, 0));
        var colours = new Button { Content = L.T("Petal Colours…"), HorizontalAlignment = HorizontalAlignment.Left };
        colours.Click += (_, _) => new PetalColoursWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        panel.Children.Add(Row("Colours", colours, 150, 10));
        panel.Children.Add(Switch("Sounds", "A soft tick as you move across the wheel.", _s.Sounds, v => _s.Sounds = v));
        var sound = Segments([(Overlay.SoundStyle.Soft, "Soft"), (Overlay.SoundStyle.Crisp, "Crisp"), (Overlay.SoundStyle.Wood, "Wood")], _s.SoundStyle);
        sound.Changed += v =>
        {
            _s.SoundStyle = v;
            Save();
            App.Current?.Controller?.Sound.Preview();
        };
        panel.Children.Add(Row("Sound", sound, 150, 8));
        panel.Children.Add(Switch("Key hints", "Show Shift · Ctrl · Alt above the wheel.", _s.ShowKeyHints, v => _s.ShowKeyHints = v));
        return panel;
    }

    private UIElement OutputSection()
    {
        var panel = new StackPanel();
        var folderText = Text(string.IsNullOrEmpty(_s.OutputFolder) ? "No folder chosen" : _s.OutputFolder!, "Petal.Caption");
        folderText.TextTrimming = TextTrimming.CharacterEllipsis;
        folderText.TextWrapping = TextWrapping.NoWrap;
        var choose = new Button { Content = L.T("Choose…"), HorizontalAlignment = HorizontalAlignment.Left };
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
            var dialog = new OpenFolderDialog { Title = L.T("Save Petal results in") };
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
        text.Children.Add(Text(path is null ? L.F("{0} — not found", L.T(purpose)) : purpose, "Petal.Caption"));
        grid.Children.Add(text);
        if (path is null && url is not null)
        {
            var get = new Button { Content = L.T("Get"), VerticalAlignment = VerticalAlignment.Center };
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

    private readonly StackPanel _favourites = new();

    private UIElement FavouritesSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(_favourites);
        var add = new Button { Content = L.T("Add a Favourite…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        add.Click += (_, _) => Edit(null);
        panel.Children.Add(add);
        FillFavourites();
        return panel;

        void Edit(Petal.Core.Jobs.Favourite? existing)
        {
            var window = new FavouriteWindow(existing, saved =>
            {
                int i = _s.Favourites.FindIndex(f => f.Id == saved.Id);
                if (i >= 0)
                    _s.Favourites[i] = saved;
                else
                    _s.Favourites.Add(saved);
                Save();
                FillFavourites();
            }, removed =>
            {
                _s.Favourites.RemoveAll(f => f.Id == removed.Id);
                Save();
                FillFavourites();
            })
            { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            window.ShowDialog();
        }

        void FillFavourites()
        {
            _favourites.Children.Clear();
            add.IsEnabled = _s.Favourites.Count < MaxFavourites;
            if (_s.Favourites.Count == 0)
            {
                _favourites.Children.Add(Text("None yet. Try “WEBP, 1600 px” for pictures you put online, or “MP4, 720p” for videos you send.", "Petal.Caption"));
                return;
            }
            foreach (var favourite in _s.Favourites.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var star = ThemedIcon("star", 16, "Petal.Accent");
                star.VerticalAlignment = VerticalAlignment.Center;
                star.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(star);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = Text(Jobs.FavouriteText.Label(favourite), "Petal.Body");
                name.FontWeight = FontWeights.SemiBold;
                words.Children.Add(name);
                words.Children.Add(Text(Jobs.FavouriteText.Describe(favourite), "Petal.Caption"));
                Grid.SetColumn(words, 1);
                row.Children.Add(words);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                int index = _s.Favourites.IndexOf(favourite);
                var up = IconButton("chevronUp", "Move up", 15);
                up.IsEnabled = index > 0;
                up.Click += (_, _) =>
                {
                    _s.Favourites.RemoveAt(index);
                    _s.Favourites.Insert(index - 1, favourite);
                    Save();
                    FillFavourites();
                };
                var edit = IconButton("pencil", "Change", 15);
                edit.Click += (_, _) => Edit(favourite);
                var remove = IconButton("trash", "Remove", 15);
                remove.Click += (_, _) =>
                {
                    _s.Favourites.Remove(favourite);
                    Save();
                    FillFavourites();
                };
                buttons.Children.Add(up);
                buttons.Children.Add(edit);
                buttons.Children.Add(remove);
                Grid.SetColumn(buttons, 2);
                row.Children.Add(buttons);
                _favourites.Children.Add(row);
            }
        }
    }

    private readonly StackPanel _recipes = new();

    private UIElement RecipesSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(_recipes);
        var add = new Button { Content = L.T("New Recipe…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        add.Click += (_, _) => Edit(null);
        panel.Children.Add(add);
        Fill();
        return panel;

        void Edit(Petal.Recipes.Recipe? existing)
        {
            var window = new RecipeWindow(existing, saved =>
            {
                int i = _s.Recipes.FindIndex(r => r.Id == saved.Id);
                if (i >= 0)
                    _s.Recipes[i] = saved;
                else
                    _s.Recipes.Add(saved);
                Save();
                Fill();
            }, removed =>
            {
                _s.Recipes.RemoveAll(r => r.Id == removed.Id);
                Save();
                Fill();
            })
            { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            window.ShowDialog();
        }

        void Fill()
        {
            _recipes.Children.Clear();
            if (_s.Recipes.Count == 0)
            {
                _recipes.Children.Add(Text("None yet.", "Petal.Caption"));
                return;
            }
            foreach (var recipe in _s.Recipes.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = ThemedIcon("workflow", 16, "Petal.Accent");
                icon.VerticalAlignment = VerticalAlignment.Center;
                icon.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(icon);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = Text(recipe.Name, "Petal.Body");
                name.FontWeight = FontWeights.SemiBold;
                words.Children.Add(name);
                var steps = Text(Jobs.RecipeText.Describe(recipe), "Petal.Caption");
                steps.TextTrimming = TextTrimming.CharacterEllipsis;
                words.Children.Add(steps);
                Grid.SetColumn(words, 1);
                row.Children.Add(words);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var edit = IconButton("pencil", "Change", 15);
                edit.Click += (_, _) => Edit(recipe);
                var remove = IconButton("trash", "Remove", 15);
                remove.Click += (_, _) =>
                {
                    _s.Recipes.Remove(recipe);
                    Save();
                    Fill();
                };
                buttons.Children.Add(edit);
                buttons.Children.Add(remove);
                Grid.SetColumn(buttons, 2);
                row.Children.Add(buttons);
                _recipes.Children.Add(row);
            }
        }
    }

    private readonly StackPanel _automations = new();

    private UIElement AutomationsSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(_automations);
        var add = new Button { Content = L.T("Add an Automation…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        add.Click += (_, _) => Edit(null);
        panel.Children.Add(add);
        Fill();
        return panel;

        void Edit(Petal.Core.Jobs.WatchRule? existing)
        {
            var window = new AutomationWindow(existing, saved =>
            {
                int i = _s.Automations.FindIndex(r => r.Id == saved.Id);
                if (i >= 0)
                    _s.Automations[i] = saved;
                else
                    _s.Automations.Add(saved);
                Save();
                Fill();
            }, removed =>
            {
                _s.Automations.RemoveAll(r => r.Id == removed.Id);
                Save();
                Fill();
            })
            { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            window.ShowDialog();
        }

        void Fill()
        {
            _automations.Children.Clear();
            add.IsEnabled = _s.Automations.Count < MaxAutomations;
            if (_s.Automations.Count == 0)
            {
                _automations.Children.Add(Text("None yet.", "Petal.Caption"));
                return;
            }
            foreach (var rule in _s.Automations.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var on = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = L.T("On") };
                on.Click += (_, _) =>
                {
                    rule.Enabled = on.IsChecked == true;
                    Save();
                };
                row.Children.Add(on);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = Text(AutomationWindow.Describe(rule), "Petal.Body");
                name.FontWeight = FontWeights.SemiBold;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                words.Children.Add(name);
                var where = Text(Directory.Exists(rule.Folder) ? rule.Folder : L.F("{0} — folder not found", rule.Folder), "Petal.Caption");
                where.TextTrimming = TextTrimming.CharacterEllipsis;
                where.FlowDirection = FlowDirection.LeftToRight;
                // Left is the start of the line, mirrored in Arabic.
                where.HorizontalAlignment = HorizontalAlignment.Left;
                if (!Directory.Exists(rule.Folder))
                    where.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
                words.Children.Add(where);
                Grid.SetColumn(words, 1);
                row.Children.Add(words);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var open = IconButton("folderOpen", "Open the results folder", 15);
                open.IsEnabled = Directory.Exists(rule.OutputFolder);
                open.Click += (_, _) => Process.Start(new ProcessStartInfo(rule.OutputFolder) { UseShellExecute = true });
                var edit = IconButton("pencil", "Change", 15);
                edit.Click += (_, _) => Edit(rule);
                var remove = IconButton("trash", "Remove", 15);
                remove.Click += (_, _) =>
                {
                    _s.Automations.Remove(rule);
                    Save();
                    Fill();
                };
                buttons.Children.Add(open);
                buttons.Children.Add(edit);
                buttons.Children.Add(remove);
                Grid.SetColumn(buttons, 2);
                row.Children.Add(buttons);
                _automations.Children.Add(row);
            }
        }
    }

    private const int MaxAutomations = 8;

    private UIElement PetalsSection()
    {
        var panel = new StackPanel();
        var list = new StackPanel();
        panel.Children.Add(list);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var add = new Button { Content = L.T("Add from a File…"), Margin = new Thickness(0, 0, 8, 6) };
        var example = new Button { Content = L.T("Start from an Example"), Margin = new Thickness(0, 0, 8, 6) };
        var folder = new Button { Content = L.T("Open the Petals Folder"), Margin = new Thickness(0, 0, 8, 6) };
        buttons.Children.Add(add);
        buttons.Children.Add(example);
        buttons.Children.Add(folder);
        panel.Children.Add(buttons);
        add.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = L.T("A petal's file"), Filter = "Petal (*.json)|*.json" };
            if (dialog.ShowDialog(this) != true)
                return;
            Directory.CreateDirectory(Petal.Recipes.CustomPetal.Folder);
            var target = Petal.Core.Jobs.OutputNaming.Unique(Petal.Recipes.CustomPetal.Folder, Path.GetFileNameWithoutExtension(dialog.FileName), "json");
            File.Copy(dialog.FileName, target);
            Fill();
        };
        example.Click += (_, _) =>
        {
            var path = Petal.Recipes.CustomPetal.WriteExample();
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
            Fill();
        };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(Petal.Recipes.CustomPetal.Folder);
            Process.Start(new ProcessStartInfo(Petal.Recipes.CustomPetal.Folder) { UseShellExecute = true });
        };
        // The list follows edits made in Notepad.
        IsVisibleChanged += (_, _) => Fill();
        Activated += (_, _) => Fill();
        Fill();
        return panel;

        void Fill()
        {
            list.Children.Clear();
            var petals = Petal.Recipes.CustomPetal.LoadAll();
            if (petals.Count == 0)
            {
                list.Children.Add(Text("None yet.", "Petal.Caption"));
                return;
            }
            foreach (var petal in petals)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var edit = IconButton("pencil", "Change", 15);
                edit.Click += (_, _) => Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { petal.File }, UseShellExecute = false });
                var remove = IconButton("trash", "Remove", 15);
                remove.Click += (_, _) =>
                {
                    try
                    {
                        Util.RecycleBin.Send(petal.File);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                    {
                        Log.Error(ex, $"Removing {petal.File}");
                    }
                    Fill();
                };
                actions.Children.Add(edit);
                actions.Children.Add(remove);
                DockPanel.SetDock(actions, Dock.Right);
                row.Children.Add(actions);
                var words = new StackPanel();
                var name = Text(petal.DisplayName(L.IsArabic), "Petal.Body");
                name.FontWeight = FontWeights.SemiBold;
                words.Children.Add(name);
                var takes = string.Join(", ", petal.Accepts) + " · " + Path.GetFileName(petal.ResolveProgram() ?? petal.Program);
                var problem = petal.ProblemFormat is null ? null : petal.ProblemDetail is null ? L.T(petal.ProblemFormat) : L.T(petal.ProblemFormat).Replace("{0}", petal.ProblemDetail);
                var detail = Text(problem ?? takes, "Petal.Caption");
                detail.TextTrimming = TextTrimming.CharacterEllipsis;
                if (petal.Problem is not null)
                    detail.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
                words.Children.Add(detail);
                row.Children.Add(words);
                list.Children.Add(row);
            }
        }
    }

    private UIElement SearchSection()
    {
        var panel = new StackPanel();
        var on = new CheckBox { Content = L.T("Index my files for search"), IsChecked = _s.SearchEnabled };
        panel.Children.Add(on);
        var folders = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(folders);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var add = new Button { Content = L.T("Add a Folder…") };
        var open = new Button { Content = L.T("Open Search"), Margin = new Thickness(8, 0, 0, 0) };
        var again = new Button { Content = L.T("Read Everything Again"), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(open);
        buttons.Children.Add(again);
        panel.Children.Add(buttons);
        var status = Text("", "Petal.Caption");
        status.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(status);
        var models = new ModelPanel(Petal.Engines.Imaging.Meaning.Models);
        models.Ready += Search.SearchService.ReadPicturesAgain;
        panel.Children.Add(models);

        on.Click += (_, _) =>
        {
            _s.SearchEnabled = on.IsChecked == true;
            Save();
            if (_s.SearchEnabled && App.Current?.Controller?.Jobs.Converters is { } converters)
                Search.SearchService.Start(converters);
            else if (!_s.SearchEnabled)
                Search.SearchService.Stop();
            Fill();
        };
        add.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = L.T("Folder to search") };
            if (dialog.ShowDialog(this) != true)
                return;
            if (_s.SearchFolders.Count == 0)
                _s.SearchFolders.AddRange(Search.SearchService.DefaultFolders());
            if (!_s.SearchFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
                _s.SearchFolders.Add(dialog.FolderName);
            Save();
            Search.SearchService.Rescan();
            Fill();
        };
        open.Click += (_, _) => Search.SearchWindow.ShowSingle();
        again.Click += (_, _) =>
        {
            Search.SearchService.Rebuild();
            Fill();
        };
        Fill();
        return panel;

        void Fill()
        {
            folders.Children.Clear();
            folders.IsEnabled = add.IsEnabled = again.IsEnabled = _s.SearchEnabled;
            models.Visibility = _s.SearchEnabled && !Petal.Engines.Imaging.Meaning.IsReady ? Visibility.Visible : Visibility.Collapsed;
            foreach (var folder in Search.SearchService.Folders)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var remove = IconButton("x", "Remove", 14);
                remove.Click += (_, _) =>
                {
                    if (_s.SearchFolders.Count == 0)
                        _s.SearchFolders.AddRange(Search.SearchService.DefaultFolders());
                    _s.SearchFolders.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
                    // An emptied list would mean "the usual folders" again; keep one at least.
                    if (_s.SearchFolders.Count == 0)
                        _s.SearchFolders.Add(folder);
                    Save();
                    Search.SearchService.Rescan();
                    Fill();
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                var path = Text(folder, "Petal.Body");
                path.TextTrimming = TextTrimming.CharacterEllipsis;
                path.FlowDirection = FlowDirection.LeftToRight;
                // Left is the start of the line, mirrored in Arabic.
                path.HorizontalAlignment = HorizontalAlignment.Left;
                row.Children.Add(path);
                folders.Children.Add(row);
            }
            var counts = Search.SearchService.Status();
            status.Text = !_s.SearchEnabled
                ? L.T("Off. Nothing is read or kept.")
                : L.P(counts.Files, "{0} file in the index", "{0} files in the index") + (counts.Waiting > 0 ? " · " + L.P(counts.Waiting, "{0} waiting to be read", "{0} waiting to be read") : "");
        }
    }

    private UIElement UpdatesSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(L.F("This is Petal {0}.", Shell.UpdateService.CurrentText), "Petal.Body"));
        if (Shell.Packaged.IsPackaged)
        {
            panel.Children.Add(Text("The Microsoft Store keeps Petal up to date.", "Petal.Caption").WithMargin(0, 4, 0, 0));
            return panel;
        }
        var feed = new TextBox { Text = _s.UpdateFeed, FlowDirection = FlowDirection.LeftToRight };
        feed.LostFocus += (_, _) =>
        {
            if (feed.Text.Trim() == _s.UpdateFeed)
                return;
            _s.UpdateFeed = feed.Text.Trim();
            _s.LastUpdateCheck = null;
            Save();
        };
        panel.Children.Add(Row("Update address", feed, 150, 12));
        panel.Children.Add(Text("Where Petal looks for new versions: an update.json over https, or in a shared folder. Leave it empty and Petal never looks. Only updates signed by Petal's maker are offered, and each installer is checked before it runs.", "Petal.Caption").WithMargin(150, 4, 0, 0));
        panel.Children.Add(Switch("Check once a day", "Show a small card when a new version is out. Nothing is installed without asking.", _s.CheckForUpdates, v => _s.CheckForUpdates = v));
        var status = Text("", "Petal.Caption");
        status.TextWrapping = TextWrapping.Wrap;
        var check = new Button { Content = L.T("Check Now"), HorizontalAlignment = HorizontalAlignment.Left };
        check.Click += async (_, _) =>
        {
            _s.UpdateFeed = feed.Text.Trim();
            Save();
            status.ClearValue(TextBlock.ForegroundProperty);
            if (_s.UpdateFeed.Length == 0)
            {
                status.Text = L.T("No update address is set, so there is nowhere to look.");
                return;
            }
            check.IsEnabled = false;
            status.Text = L.T("Checking…");
            try
            {
                var update = await Shell.UpdateService.CheckAsync();
                _s.LastUpdateCheck = DateTime.UtcNow;
                Save();
                if (update is null)
                {
                    status.Text = L.T("Petal is up to date.");
                }
                else
                {
                    status.Text = L.F("Petal {0} is available", update.Version);
                    Shell.UpdateService.Offer(update);
                    Shell.UpdateWindow.ShowFor(update);
                }
            }
            catch (UpdateException ex)
            {
                status.Text = L.T(ex.Message);
                status.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
            }
            finally
            {
                check.IsEnabled = true;
            }
        };
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(check, Dock.Left);
        row.Children.Add(check);
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(12, 0, 0, 0);
        row.Children.Add(status);
        panel.Children.Add(row);
        return panel;
    }

    /// <summary>More than this crowds the wheel.</summary>
    private const int MaxFavourites = 6;

    private UIElement GeneralSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Switch("Start with Windows", "Petal waits quietly in the notification area.", _s.LaunchAtStartup, v =>
        {
            _s.LaunchAtStartup = v;
            LaunchAtLogin.Apply(v);
        }, top: 0));
        if (!Shell.Packaged.IsPackaged)
            panel.Children.Add(Switch("Right-click menu", "Add “Convert with Petal” to File Explorer, to use the wheel without dragging.", _s.ExplorerContextMenu, v =>
            {
                _s.ExplorerContextMenu = v;
                Shell.ContextMenuRegistration.Apply(v);
            }));
        panel.Children.Add(Switch("Pause Petal", "Ignore key taps while dragging until you turn this off.", _s.Paused, v => _s.Paused = v));
        panel.Children.Add(Switch("Keep a history", "Remember what Petal made, so you can find a result again or undo it.", _s.KeepHistory, v => _s.KeepHistory = v));
        var log =new Button { Content = L.T("Open activity log"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
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
        // For the tour, which points at each card by its title.
        _cards[title] = card;
        System.Windows.Automation.AutomationProperties.SetName(card, L.T(title));
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
