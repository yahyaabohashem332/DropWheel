using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.App.Visuals;
using Petal.Core.Jobs;
using Petal.Engines.Imaging;
using Petal.App.Localization;

namespace Petal.App;

public partial class App : Application
{
    private const string InstanceName = "Petal.SingleInstance.v1";
    private Mutex? _instanceMutex;
    private PetalController? _controller;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;

    public static new App Current => (App)Application.Current;

    public PetalController Controller => _controller!;

    /// <summary>The system-wide shortcut; null in design-snapshot runs.</summary>
    public GlobalHotkey? Hotkey => _hotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "--lang ar|en" forces a language (used for design snapshots).
        int langArg = Array.IndexOf(e.Args, "--lang");
        if (langArg >= 0 && langArg + 1 < e.Args.Length)
            L.Initialize(e.Args[langArg + 1] == "ar" ? UiLanguage.Arabic : UiLanguage.English);
        string[] args = langArg >= 0 ? e.Args.Where((_, i) => i != langArg && i != langArg + 1).ToArray() : e.Args;

        if (args.Length >= 3 && args[0] == "--snapshot-tools")
        {
            ThemeResources.Install(this);
            Dispatcher.BeginInvoke(async () =>
            {
                try { await DesignSnapshots.RunToolsAsync(args); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "error.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (args.Length >= 2 && args[0] == "--show-dialog")
        {
            ThemeResources.Install(this);
            DesignSnapshots.ShowOnScreen(args);
            return;
        }

        if (args.Length >= 3 && args[0] == "--snapshots")
        {
            ThemeResources.Install(this);
            try
            {
                DesignSnapshots.Run(args);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(args[1], "error.txt"), ex.ToString());
            }
            Shutdown();
            return;
        }

        var command = ParseCommand(e.Args);
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first)
        {
            // Already running: hand our request to that instance, then leave.
            CommandChannel.Send(command ?? new PetalCommand("settings", [], 0, 0));
            Shutdown();
            return;
        }
        _commands.Received += c => Dispatcher.BeginInvoke(() => Handle(c));
        _commands.Listen();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.ExceptionObject as Exception ?? new Exception("unknown"), "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var settings = SettingsStore.Load();
        if (langArg < 0)
            L.Initialize(settings.Language);
        Jobs.History.Enabled = () => SettingsStore.Current.KeepHistory;
        L.Changed += () =>
        {
            BuildTrayMenu();
            ContextMenuRegistration.Apply(SettingsStore.Current.ExplorerContextMenu);
        };
        Theme.Configure(settings.Theme, settings.UseWindowsAccent);
        ThemeResources.Install(this);
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(Theme.Refresh);

        var converters = EngineSetup.CreateConverters();

        _controller = new PetalController(converters, Dispatcher);
        _controller.Start();
        Search.SearchService.Start(converters);

        CreateTray();
        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += OnShortcut;
        if (Input.Shortcut.Parse(settings.QuickShortcut) is { } shortcut && !_hotkey.Register(shortcut))
            Log.Info($"{shortcut} is already used by another program");
        LaunchAtLogin.Apply(settings.LaunchAtStartup);
        ContextMenuRegistration.Apply(settings.ExplorerContextMenu);
        if (command is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Handle(command));
        else if (!settings.OnboardingDone && !e.Args.Contains("--background"))
            Onboarding.WelcomeWindow.ShowSingle();
        _ = UpdateService.CheckInBackgroundAsync();
        Log.Info($"Petal started, {converters.Converters.Count} converters");
    }

    private readonly CommandChannel _commands = new();

    /// <summary>"--wheel &lt;path&gt;" comes from Explorer's context menu.</summary>
    private static PetalCommand? ParseCommand(string[] args)
    {
        int i = Array.IndexOf(args, "--wheel");
        if (i < 0 || i + 1 >= args.Length)
            return null;
        Interop.NativeMethods.GetCursorPos(out var pt);
        return new PetalCommand("wheel", args[(i + 1)..], pt.X, pt.Y);
    }

    private void Handle(PetalCommand command)
    {
        switch (command.Name)
        {
            case "wheel":
                _controller?.ShowWheelFor(command.Files, new Input.ScreenPoint(command.X, command.Y));
                break;
            default:
                ShowSettings();
                break;
        }
    }

    private void CreateTray()
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Petal.ico");
        _tray = new TrayIcon(icon, L.T("Petal — drag a file and tap Shift"));
        _tray.Clicked += ShowSettings;
        BuildTrayMenu();
    }

    private void BuildTrayMenu()
    {
        if (_tray is null)
            return;
        _tray.SetTooltip(L.T(SettingsStore.Current.Paused ? "Petal — paused" : "Petal — drag a file and tap Shift"));
        var menu = new ContextMenu();
        L.Apply(menu);
        var pause = new MenuItem { Header = L.T("Pause Petal"), IsCheckable = true, IsChecked = SettingsStore.Current.Paused };
        pause.Click += (_, _) =>
        {
            SettingsStore.Current.Paused = pause.IsChecked;
            SettingsStore.Save();
            _tray?.SetTooltip(L.T(pause.IsChecked ? "Petal — paused" : "Petal — drag a file and tap Shift"));
        };
        var settingsItem = new MenuItem { Header = L.T("Settings…") };
        settingsItem.Click += (_, _) => ShowSettings();
        var scanItem = new MenuItem { Header = L.T("Scan with Phone…") };
        scanItem.Click += (_, _) => Actions.ScanWindow.ShowSingle();
        var searchItem = new MenuItem { Header = L.T("Search by Meaning…") };
        searchItem.Click += (_, _) => Search.SearchWindow.ShowSingle();
        var askItem = new MenuItem { Header = L.T("Ask Petal…") };
        askItem.Click += (_, _) => { if (_controller is not null) Tools.Windows.AskWindow.ChooseAndShow(_controller.Jobs); };
        var duplicatesItem = new MenuItem { Header = L.T("Find Duplicates…") };
        duplicatesItem.Click += (_, _) => Tools.Windows.DuplicatesWindow.ChooseAndShow();
        var qrItem = new MenuItem { Header = L.T("Create QR Code…") };
        qrItem.Click += (_, _) => Tools.Windows.QrWindow.ShowSingle();
        var historyItem = new MenuItem { Header = L.T("History…") };
        historyItem.Click += (_, _) => Tools.Windows.HistoryWindow.ShowSingle();
        var howItem = new MenuItem { Header = L.T("How it works") };
        howItem.Click += (_, _) => Onboarding.WelcomeWindow.ShowSingle();
        var quit = new MenuItem { Header = L.T("Quit Petal") };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(settingsItem);
        menu.Items.Add(searchItem);
        menu.Items.Add(askItem);
        menu.Items.Add(scanItem);
        menu.Items.Add(duplicatesItem);
        menu.Items.Add(qrItem);
        menu.Items.Add(historyItem);
        menu.Items.Add(howItem);
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        _tray.Menu = menu;
    }

    public void ShowSettings() => SettingsWindow.ShowSingle();

    /// <summary>
    /// The shortcut: with files selected in the File Explorer window in front (or on the desktop),
    /// the wheel opens for them; otherwise Petal's main window opens. Pressed again, it closes the wheel.
    /// </summary>
    private async void OnShortcut()
    {
        if (_controller is null || _controller.IsDragging)
            return;
        if (_controller.IsWheelOpen)
        {
            _controller.CloseWheel();
            return;
        }
        var front = Interop.NativeMethods.GetForegroundWindow();
        var files = await ExplorerSelection.SelectionInAsync(front);
        if (files.Count > 0)
        {
            _controller.OpenWheelNow(files, WheelPointFor(front));
            return;
        }
        // Nothing selected: the clipboard's files, picture or text, else Petal itself.
        switch (SettingsStore.Current.ClipboardShortcut ? ClipboardSource.Take() : null)
        {
            case ClipboardContent.Files clip:
                Interop.NativeMethods.GetCursorPos(out var cursor);
                _controller.OpenWheelNow(clip.Paths, new Input.ScreenPoint(cursor.X, cursor.Y));
                break;
            case ClipboardContent.Link link:
                Tools.Windows.QrWindow.ShowSingle(link.Url);
                break;
            default:
                ShowSettings();
                break;
        }
    }

    /// <summary>At the mouse when it is over that window, else in the middle of the window.</summary>
    private static Input.ScreenPoint WheelPointFor(IntPtr window)
    {
        Interop.NativeMethods.GetCursorPos(out var cursor);
        if (!Interop.NativeMethods.GetWindowRect(window, out var rect) || rect.Width <= 0
            || (cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom))
            return new Input.ScreenPoint(cursor.X, cursor.Y);
        return new Input.ScreenPoint(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
    }

    public void Quit()
    {
        _commands.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _controller?.Dispose();
        Search.SearchService.Stop();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Launch at sign-in through the per-user Run key.</summary>
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            var exe = Environment.ProcessPath;
            // Never register a development build to run at sign-in.
            if (exe is not null && exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase))
                return;
            if (enabled && exe is not null)
                key.SetValue("Petal", $"\"{exe}\" --background");
            else
                key.DeleteValue("Petal", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Updating launch at startup");
        }
    }
}
