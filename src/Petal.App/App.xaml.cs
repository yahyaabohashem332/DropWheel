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
    private EventWaitHandle? _showSignal;
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

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first)
        {
            // Already running: ask that instance to open its settings, then leave.
            try { EventWaitHandle.OpenExisting(InstanceName + ".show").Set(); } catch { /* not ready yet */ }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".show");
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);

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
        if (!settings.OnboardingDone && !e.Args.Contains("--background"))
            Onboarding.WelcomeWindow.ShowSingle();
        Log.Info($"Petal started, {converters.Converters.Count} converters");
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
