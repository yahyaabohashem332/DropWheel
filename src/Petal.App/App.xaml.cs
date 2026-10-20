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

namespace Petal.App;

public partial class App : Application
{
    private const string InstanceName = "Petal.SingleInstance.v1";
    private Mutex? _instanceMutex;
    private PetalController? _controller;
    private TrayIcon? _tray;

    public static new App Current => (App)Application.Current;

    public PetalController Controller => _controller!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 3 && e.Args[0] == "--snapshot-tools")
        {
            ThemeResources.Install(this);
            Dispatcher.BeginInvoke(async () =>
            {
                try { await DesignSnapshots.RunToolsAsync(e.Args); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0] == "--snapshots")
        {
            ThemeResources.Install(this);
            try
            {
                DesignSnapshots.Run(e.Args);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString());
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
        Theme.Configure(settings.Theme, settings.UseWindowsAccent);
        ThemeResources.Install(this);
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(Theme.Refresh);

        var converters = EngineSetup.CreateConverters();

        _controller = new PetalController(converters, Dispatcher);
        _controller.Start();

        CreateTray();
        LaunchAtLogin.Apply(settings.LaunchAtStartup);
        ContextMenuRegistration.Apply(settings.ExplorerContextMenu);
        if (command is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Handle(command));
        else if (!settings.OnboardingDone && !e.Args.Contains("--background"))
            Onboarding.WelcomeWindow.ShowSingle();
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
        _tray = new TrayIcon(icon, "Petal — drag a file and tap Shift");
        _tray.Clicked += ShowSettings;

        var menu = new ContextMenu();
        var pause = new MenuItem { Header = "Pause Petal", IsCheckable = true, IsChecked = SettingsStore.Current.Paused };
        pause.Click += (_, _) =>
        {
            SettingsStore.Current.Paused = pause.IsChecked;
            SettingsStore.Save();
            _tray?.SetTooltip(pause.IsChecked ? "Petal — paused" : "Petal — drag a file and tap Shift");
        };
        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => ShowSettings();
        var howItem = new MenuItem { Header = "How it works" };
        howItem.Click += (_, _) => Onboarding.WelcomeWindow.ShowSingle();
        var quit = new MenuItem { Header = "Quit Petal" };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(settingsItem);
        menu.Items.Add(howItem);
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        _tray.Menu = menu;
    }

    public void ShowSettings() => SettingsWindow.ShowSingle();

    public void Quit()
    {
        _commands.Dispose();
        _tray?.Dispose();
        _controller?.Dispose();
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
