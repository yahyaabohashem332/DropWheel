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
    private Shell.WheelHotkeys? _wheelKeys;

    public static new App Current => (Application.Current as App)!;

    public PetalController Controller => _controller!;

    /// <summary>The system-wide shortcut; null in design-snapshot runs.</summary>
    public GlobalHotkey? Hotkey => _hotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before anything else: numbers are written 3.4, never "3,4", whatever the PC's region.
        Petal.Core.Util.PlainNumbers.Apply();
        base.OnStartup(e);

        // "--lang ar|en|tr|…" forces a language (used for design snapshots).
        int langArg = Array.IndexOf(e.Args, "--lang");
        if (langArg >= 0 && langArg + 1 < e.Args.Length)
            L.Initialize(L.FromCode(e.Args[langArg + 1]));
        string[] args = langArg >= 0 ? e.Args.Where((_, i) => i != langArg && i != langArg + 1).ToArray() : e.Args;

        // The uninstaller: File Explorer's thumbnails for design files come out, and nothing else happens.
        if (args.Length >= 1 && args[0] == "--unregister-shell")
        {
            Shell.ExplorerThumbnails.Apply(false);
            Shutdown();
            return;
        }

        if (args.Length >= 3 && args[0] == "--snapshot-tools")
        {
            ThemeResources.Install(this);
            var snapshotSettings = SettingsStore.Load();
            Petal.Core.Models.ModelStore.Folder = Petal.Core.Models.ModelStore.FolderFor(snapshotSettings.ModelsFolder);
            AiSetup.Apply(snapshotSettings);
            Dispatcher.BeginInvoke(async () =>
            {
                try { await DesignSnapshots.RunToolsAsync(args); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "error.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (args.Length >= 3 && args[0] == "--snapshot-circle")
        {
            ThemeResources.Install(this);
            Dispatcher.BeginInvoke(async () =>
            {
                try { await DesignSnapshots.RunCircleAsync(args); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "error.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (args.Length >= 3 && args[0] == "--captions-test")
        {
            ThemeResources.Install(this);
            Dispatcher.BeginInvoke(async () =>
            {
                try { await DesignSnapshots.RunCaptionsAsync(args); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "error.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (args.Length >= 2 && args[0] == "--captions-rise")
        {
            ThemeResources.Install(this);
            Dispatcher.BeginInvoke(async () =>
            {
                try { await DesignSnapshots.RunCaptionsRiseAsync(args); }
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

        // The heart of DropWheel: without these nothing works, so a failure here is said plainly and DropWheel
        // closes, rather than staying open with no icon, no keys and no shortcuts.
        AppSettings settings;
        ConverterRegistry converters;
        try
        {
            settings = SettingsStore.Load();
            // Ready-made wheels the settings don't have yet (all of them on first run).
            if (Wheels.WheelTemplates.Upgrade(settings))
                SettingsStore.Save();
            if (langArg < 0)
                L.Initialize(settings.Language);
            Jobs.History.Enabled = () => SettingsStore.Current.KeepHistory;
            L.Changed += () =>
            {
                BuildTrayMenu();
                ContextMenuRegistration.Apply(SettingsStore.Current.ExplorerContextMenu);
            };
            Theme.Configure(settings.Theme, settings.AccentColour, settings.AccentCustom);
            // The folder downloaded models are kept in, if the user chose one.
            Petal.Core.Models.ModelStore.Folder = Petal.Core.Models.ModelStore.FolderFor(settings.ModelsFolder);
            AiSetup.Apply(settings);
            ThemeResources.Install(this);
            SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(Theme.Refresh);

            converters = EngineSetup.CreateConverters();
            Jobs.FolderLease.Install();

            _controller = new PetalController(converters, Dispatcher);
            _controller.Start();
        }
        catch (Exception ex)
        {
            CouldNotStart(ex);
            return;
        }

        // Everything else starts part by part: one that fails is named in a card and in DropWheel Health,
        // and the others start all the same.
        StartupParts.Run("Other programs", () => ApplyLocalApi(settings.LocalApi));
        StartupParts.Run("Search by Meaning", () => Search.SearchService.Start(converters));

        StartupParts.Run("The icon by the clock", CreateTray);
        Shell.UpdateService.Found += _ => Dispatcher.BeginInvoke(() =>
        {
            _tray?.SetBadge(true, Theme.Current.Accent);
            BuildTrayMenu();
        });
        StartupParts.Run("Settings", () => SettingsStore.Watch(Dispatcher));
        SettingsStore.Reloaded += ApplyReloaded;
        SettingsStore.Saved += _ => Dispatcher.BeginInvoke(BuildTrayMenu);
        StartupParts.Run("Shortcuts", () =>
        {
            _hotkey = new GlobalHotkey();
            _hotkey.Pressed += OnShortcut;
            if (Input.Shortcut.Parse(settings.QuickShortcut) is { } shortcut && !_hotkey.Register(shortcut))
                Log.Info($"{shortcut} is already used by another program");
            _wheelKeys = new Shell.WheelHotkeys();
            _wheelKeys.Pressed += OnWheelShortcut;
            _wheelKeys.Apply(settings.Wheels);
        });
        SettingsStore.Saved += s => Dispatcher.BeginInvoke(() => _wheelKeys?.Apply(s.Wheels));
        StartupParts.Run("Start with Windows", () => LaunchAtLogin.Apply(settings.LaunchAtStartup));
        StartupParts.Run("Time Tracker", Work.TimeTracker.Resume);
        StartupParts.Run("File Explorer", () => ContextMenuRegistration.Apply(settings.ExplorerContextMenu));
        // The clipboard history listens from the start; notes come back where they were.
        StartupParts.Run("Clipboard History", Clips.ClipboardHistory.Start);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => StartupParts.Run("Notes", Notes.StickyNotes.Restore));
        StartupParts.Run("Shortcuts", () =>
        {
            if (!Words.Dictation.RegisterShortcut())
                Log.Info($"{settings.DictationShortcut} is already used by another program");
        });
        StartupParts.Run("Command Palette", () =>
        {
            Commands.PaletteSources.Register();
            if (!Commands.CommandPalette.RegisterShortcut())
                Log.Info($"{settings.PaletteShortcut} is already used by another program");
        });
        StartupParts.Run("Shortcuts", () =>
        {
            if (!Words.VoiceCommand.RegisterShortcut())
                Log.Info($"{settings.VoiceShortcut} is already used by another program");
            if (!Screen.CircleWindow.RegisterShortcut())
                Log.Info($"{settings.CircleShortcut} is already used by another program");
        });
        StartupParts.Run("Snippets", Words.TextExpander.Start);
        _ = Task.Run(Shell.VirtualDrop.Tidy);
        var thumbnails = settings.ExplorerThumbnails;
        _ = Task.Run(() => Shell.ExplorerThumbnails.Apply(thumbnails));
        StartupParts.Run("Quick Look", () => Shell.QuickLook.Start(Dispatcher, (files, at) =>
        {
            Controller.OpenWheelNow(files, at);
            return true;
        }));
        StartupParts.Run("Receiving files from other PCs", () => _ = Actions.PcDrop.ApplyAsync(settings.ReceiveFromPcs));
        if (command is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Handle(command));
        else if (!settings.OnboardingDone && !e.Args.Contains("--background"))
            StartupParts.Run("Welcome to DropWheel", Onboarding.WelcomeWindow.ShowSingle);
        else if (e.Args.Contains("--installed"))
            // Started by the setup after an update: the main window, and a word on where Petal lives.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                ShowSettings();
                Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(
                    L.F("DropWheel {0} is installed", UpdateService.Current.ToString(3)),
                    L.T("It waits in the notification area, by the clock."), null);
            });
        StartupParts.Run("Updates", () => _ = UpdateService.CheckInBackgroundAsync());
        if (Shell.Packaged.IsPackaged)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
            {
                // The Store's trial: once it is over, Petal asks to be bought before it does anything.
                if (await Shell.Packaged.CheckAsync() is { Active: false })
                    Shell.TrialEndedWindow.ShowSingle();
            });
        Log.Info($"DropWheel started, {converters.Converters.Count} converters");
        // Last of all, once the notes have had their turn: a word about any part that didn't start.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, TellWhatDidNotStart);
    }

    private readonly CommandChannel _commands = new();

    /// <summary>
    /// "--wheel &lt;path&gt;" comes from Explorer's context menu; bare paths from "Open with" or files
    /// dropped on Petal.exe open the wheel too.
    /// </summary>
    private static PetalCommand? ParseCommand(string[] args)
    {
        int i = Array.IndexOf(args, "--wheel");
        string[] files;
        if (i >= 0 && i + 1 < args.Length)
            files = args[(i + 1)..];
        else if (args.Length > 0 && args.All(a => !a.StartsWith('-') && (File.Exists(a) || Directory.Exists(a))))
            files = args;
        else
            return null;
        Interop.NativeMethods.GetCursorPos(out var pt);
        return new PetalCommand("wheel", files, pt.X, pt.Y);
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

    /// <summary>Settings read again from the file (an import, or an edit by hand) take effect everywhere at once.</summary>
    private void ApplyReloaded(AppSettings s)
    {
        if (Wheels.WheelTemplates.Upgrade(s))
            SettingsStore.Save();
        L.Configure(s.Language);
        Theme.Configure(s.Theme, s.AccentColour, s.AccentCustom);
        Petal.Core.Models.ModelStore.Folder = Petal.Core.Models.ModelStore.FolderFor(s.ModelsFolder);
        AiSetup.Apply(s);
        if (_hotkey is not null)
        {
            var wanted = Input.Shortcut.Parse(s.QuickShortcut);
            if (wanted?.ToString() != _hotkey.Current?.ToString() && !_hotkey.Register(wanted))
                Log.Info($"{wanted} is already used by another program");
        }
        LaunchAtLogin.Apply(s.LaunchAtStartup);
        ContextMenuRegistration.Apply(s.ExplorerContextMenu);
        Words.Dictation.RegisterShortcut();
        Commands.CommandPalette.RegisterShortcut();
        Words.VoiceCommand.RegisterShortcut();
        _ = Actions.PcDrop.ApplyAsync(s.ReceiveFromPcs);
        ApplyLocalApi(s.LocalApi);
        Jobs.History.Reload();
        BuildTrayMenu();
    }

    private Shell.LocalApi? _api;

    /// <summary>Starts or stops answering other programs, as the setting says.</summary>
    private void ApplyLocalApi(bool on)
    {
        if (on && _api is null && _controller is not null)
        {
            _api = new Shell.LocalApi(_controller, Dispatcher);
            _api.Listen();
        }
        else if (!on && _api is not null)
        {
            _api.Dispose();
            _api = null;
        }
    }

    private void CreateTray()
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Petal.ico");
        _tray = new TrayIcon(icon, L.T("DropWheel — drag a file and tap Shift"));
        _tray.Clicked += ShowSettings;
        BuildTrayMenu();
    }

    private void BuildTrayMenu()
    {
        if (_tray is null)
            return;
        _tray.SetTooltip(L.T(SettingsStore.Current.Paused ? "DropWheel — paused" : "DropWheel — drag a file and tap Shift"));
        var menu = new ContextMenu();
        L.Apply(menu);
        var pause = new MenuItem { Header = L.T("Pause DropWheel"), IsCheckable = true, IsChecked = SettingsStore.Current.Paused };
        pause.Click += (_, _) =>
        {
            SettingsStore.Current.Paused = pause.IsChecked;
            SettingsStore.Save();
            _tray?.SetTooltip(L.T(pause.IsChecked ? "DropWheel — paused" : "DropWheel — drag a file and tap Shift"));
        };
        var settingsItem = new MenuItem { Header = L.T("Settings…") };
        settingsItem.Click += (_, _) => ShowSettings();
        var scanItem = new MenuItem { Header = L.T("Scan with Phone…") };
        scanItem.Click += (_, _) => Actions.ScanWindow.ShowSingle();
        var searchItem = new MenuItem { Header = L.T("Search by Meaning…") };
        searchItem.Click += (_, _) => Search.SearchWindow.ShowSingle();
        var askItem = new MenuItem { Header = L.T("Ask DropWheel…") };
        askItem.Click += (_, _) => { if (_controller is not null) Tools.Windows.AskWindow.ChooseAndShow(_controller.Jobs); };
        var duplicatesItem = new MenuItem { Header = L.T("Find Duplicates…") };
        duplicatesItem.Click += (_, _) => Tools.Windows.DuplicatesWindow.ChooseAndShow();
        var qrItem = new MenuItem { Header = L.T("Create QR Code…") };
        qrItem.Click += (_, _) => Tools.Windows.QrWindow.ShowSingle();
        var historyItem = new MenuItem { Header = L.T("History…") };
        historyItem.Click += (_, _) => Tools.Windows.HistoryWindow.ShowSingle();
        var howItem = new MenuItem { Header = L.T("How it works") };
        howItem.Click += (_, _) => Onboarding.WelcomeWindow.ShowSingle();
        var guideItem = new MenuItem { Header = L.T("Tools Guide…") };
        guideItem.Click += (_, _) => Guide.GuideWindow.ShowSingle();
        var practiceItem = new MenuItem { Header = L.T("Practice…") };
        practiceItem.Click += (_, _) => Onboarding.PracticeWindow.ShowSingle();
        var statsItem = new MenuItem { Header = L.T("Your Statistics…") };
        statsItem.Click += (_, _) => Stats.StatsWindow.ShowSingle();
        var snipItem = new MenuItem { Header = L.T("Take a Screenshot") };
        snipItem.Click += (_, _) =>
        {
            if (_controller is null) return;
            var controller = _controller;
            Wheels.Screenshots.ChooseArea((picture, middle) => controller.OpenWheelNow([Wheels.Screenshots.Keep(picture)], middle));
        };
        var drawItem = new MenuItem { Header = L.T("Draw on Screen") };
        drawItem.Click += (_, _) => Screen.DrawOverlay.Toggle(Screen.DrawBoard.Live, OpenFilesFromTray);
        var recordItem = new MenuItem { Header = L.T(Screen.ScreenRecorder.IsRecording ? "Stop Recording" : "Record the Screen…") };
        recordItem.Click += (_, _) => Screen.ScreenRecorder.Toggle(OpenFilesFromTray);
        var textItem = new MenuItem { Header = L.T("Text from the Screen…") };
        textItem.Click += (_, _) => Screen.ScreenTextWindow.Start(new Input.ScreenPoint(0, 0));
        var clipsItem = new MenuItem { Header = L.T("Clipboard History…") };
        clipsItem.Click += (_, _) => Clips.ClipboardWindow.ShowSingle(IntPtr.Zero, new Input.ScreenPoint(0, 0));
        var noteItem = new MenuItem { Header = L.T("New Note") };
        noteItem.Click += (_, _) => Notes.StickyNotes.New();
        var timerItem = new MenuItem { Header = L.T("Timer") };
        timerItem.Click += (_, _) => Screen.TimerWindow.Toggle();
        var captionsItem = new MenuItem { Header = L.T(Words.LiveCaptions.IsOn ? "Turn Off Live Captions" : "Live Captions") };
        captionsItem.Click += (_, _) => Words.LiveCaptions.Toggle();
        var meetingItem = new MenuItem { Header = L.T(Words.MeetingRecorder.IsRecording ? "Stop the Meeting Notes" : "Meeting Notes") };
        meetingItem.Click += (_, _) => Words.MeetingRecorder.Toggle();
        var stepsItem = new MenuItem { Header = L.T(Screen.StepRecorder.IsRecording ? "Finish the Guide" : "Record Steps") };
        stepsItem.Click += (_, _) => Screen.StepRecorder.Toggle();
        var focusItem = new MenuItem { Header = L.T(Screen.FocusMode.IsOn ? "End the Focus Session" : "Focus…") };
        focusItem.Click += (_, _) => Screen.FocusMode.Toggle();
        var screenMenu = new MenuItem { Header = L.T("Screen & Text") };
        foreach (var item in new[] { snipItem, recordItem, drawItem, textItem, clipsItem, noteItem, timerItem })
            screenMenu.Items.Add(item);
        var workMenu = new MenuItem { Header = L.T("Work") };
        foreach (var item in new[] { captionsItem, meetingItem, stepsItem, focusItem })
            workMenu.Items.Add(item);
        var paletteItem = new MenuItem { Header = L.T("Command Palette") };
        paletteItem.Click += (_, _) => _ = Commands.CommandPalette.ToggleAsync(IntPtr.Zero);
        var updatesItem = new MenuItem { Header = Shell.UpdateService.Waiting is { } waiting ? L.F("Install DropWheel {0}…", waiting.Version) : L.T("Check for Updates") };
        updatesItem.Click += async (_, _) =>
        {
            if (Shell.UpdateService.Waiting is { } update)
                Shell.UpdateWindow.ShowFor(update);
            else
                await Shell.UpdateService.CheckFromTrayAsync();
        };
        var quit = new MenuItem { Header = L.T("Quit DropWheel") };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(settingsItem);
        menu.Items.Add(searchItem);
        menu.Items.Add(askItem);
        menu.Items.Add(scanItem);
        menu.Items.Add(duplicatesItem);
        menu.Items.Add(qrItem);
        menu.Items.Add(historyItem);
        menu.Items.Add(paletteItem);
        menu.Items.Add(screenMenu);
        menu.Items.Add(workMenu);
        menu.Items.Add(statsItem);
        var healthItem = new MenuItem { Header = L.T("DropWheel Health…") };
        healthItem.Click += (_, _) => Settings.HealthWindow.ShowSingle();
        menu.Items.Add(healthItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(howItem);
        menu.Items.Add(guideItem);
        menu.Items.Add(practiceItem);
        menu.Items.Add(updatesItem);
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        _tray.Menu = menu;
    }

    /// <summary>The icon is in the notification area (false while Windows hasn't taken it yet).</summary>
    public bool TrayShown => _tray?.Shown == true;

    /// <summary>
    /// The heart of DropWheel didn't start: says so with the reason, offers the activity log to send with a
    /// question, and closes, so opening DropWheel again tries afresh instead of finding a broken copy running.
    /// </summary>
    private void CouldNotStart(Exception ex)
    {
        Log.Error(ex, "Start-up: DropWheel couldn't start");
        var reason = new StartupFailure("", ex).Reason;
        var options = L.IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
        var answer = MessageBox.Show(
            L.T("Something on this PC stopped DropWheel while it was starting:") + "\n\n" + reason + "\n\n"
                + L.T("The details are in DropWheel's activity log. Show it, to send it with your question?"),
            L.T("DropWheel couldn't start"), MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.Yes, options);
        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{Log.FilePath}\"") { UseShellExecute = true });
            }
            catch (Exception open) when (open is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Error(open, "Showing the activity log");
            }
        }
        _commands.Dispose();
        Shutdown(1);
    }

    /// <summary>Names the parts that didn't start, once, in a card that opens DropWheel Health.</summary>
    private void TellWhatDidNotStart()
    {
        var parts = StartupParts.Failed.Select(f => f.Part).ToList();
        if (parts.Count == 0)
            return;
        try
        {
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Fail(
                L.T("Some parts of DropWheel didn't start"),
                L.F("{0}. Everything else works; click for DropWheel Health.", StartupParts.Names(parts)),
                Settings.HealthWindow.ShowSingle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Telling what didn't start");
        }
    }

    public void ShowSettings() => SettingsWindow.ShowSingle();

    /// <summary>For screen tools started from the tray: the conversion wheel on what they made.</summary>
    private void OpenFilesFromTray(string path, Input.ScreenPoint at) => _controller?.OpenWheelNow([path], at);

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
        // Nothing selected: the wheels without files, the clipboard's wheel, or Petal itself.
        Interop.NativeMethods.GetCursorPos(out var cursor);
        var at = new Input.ScreenPoint(cursor.X, cursor.Y);
        switch (SettingsStore.Current.NothingSelected)
        {
            case Wheels.NothingSelected.Wheels:
                await _controller.OpenRingsAsync(front, WheelPointFor(front));
                break;
            case Wheels.NothingSelected.Clipboard:
                _controller.OpenClipboard(at);
                break;
            default:
                ShowSettings();
                break;
        }
    }

    /// <summary>A wheel's own shortcut: that wheel, over the window in front; pressed again, it closes.</summary>
    private async void OnWheelShortcut(string wheel)
    {
        if (_controller is null || _controller.IsDragging)
            return;
        if (_controller.IsWheelOpen)
        {
            _controller.CloseWheel();
            return;
        }
        var front = Interop.NativeMethods.GetForegroundWindow();
        await _controller.OpenRingsAsync(front, WheelPointFor(front), wheel);
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
        _wheelKeys?.Dispose();
        _tray?.Dispose();
        _controller?.Dispose();
        Search.SearchService.Stop();
        Clips.ClipboardHistory.Stop();
        Notes.StickyNotes.Save();
        Words.Dictation.Dispose();
        Commands.CommandPalette.Dispose();
        Words.VoiceCommand.Dispose();
        _api?.Dispose();
        if (Words.LiveCaptions.IsOn)
            Words.LiveCaptions.Stop();
        Screen.FocusMode.End(early: true);
        _ = Actions.PcDrop.ApplyAsync(false);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Work.TimeTracker.Flush();
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
        // The Store's package starts Petal through its own startup task, which Windows lets the user see.
        if (Shell.Packaged.IsPackaged)
        {
            _ = Shell.Packaged.SetStartupAsync(enabled);
            return;
        }
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
