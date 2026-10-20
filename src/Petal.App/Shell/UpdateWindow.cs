using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Hud;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.Core.Util;

namespace Petal.App.Shell;

/// <summary>
/// Looks for a newer Petal on the update feed set in Settings (none by default), and installs it
/// when the user agrees. The installer is checked against the feed's SHA-256 before it runs.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static UpdateService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd($"Petal/{Current}");

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentText => Current.ToString(3);

    /// <summary>Where downloaded installers wait to run.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "updates");

    public static Task<UpdateManifest?> CheckAsync(CancellationToken cancellationToken = default) =>
        Updates.CheckAsync(SettingsStore.Current.UpdateFeed, Current, Http, cancellationToken);

    /// <summary>Once a day, a while after start: a quiet card when there is something new.</summary>
    public static async Task CheckInBackgroundAsync()
    {
        var s = SettingsStore.Current;
        if (!s.CheckForUpdates || string.IsNullOrWhiteSpace(s.UpdateFeed) || s.LastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromDays(1))
            return;
        await Task.Delay(TimeSpan.FromSeconds(45));
        try
        {
            var update = await CheckAsync();
            s.LastUpdateCheck = DateTime.UtcNow;
            SettingsStore.Save();
            if (update is null || update.Version == s.SkippedVersion)
                return;
            var card = HudCard.ShowAt(HudCard.CornerPoint(), "", "");
            card.Succeed(L.F("Petal {0} is available", update.Version), L.T("Click to see what's new"), () => UpdateWindow.ShowFor(update));
        }
        catch (UpdateException ex)
        {
            Log.Info($"Update check: {ex.Message} {ex.InnerException?.Message}");
        }
    }

    public static Task<string> DownloadAsync(UpdateManifest update, IProgress<double> progress, CancellationToken cancellationToken) =>
        Updates.DownloadAsync(update, Folder, Http, progress, cancellationToken);

    /// <summary>Runs the installer quietly; it closes Petal, updates it and starts it again.</summary>
    public static void Install(string installer)
    {
        Process.Start(new ProcessStartInfo(installer)
        {
            ArgumentList = { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS" },
            UseShellExecute = true,
        });
        App.Current.Quit();
    }
}

/// <summary>"Petal 1.2.0 is available": what's new, and Install or Skip.</summary>
public sealed class UpdateWindow : ToolWindow
{
    private static UpdateWindow? _open;
    private readonly UpdateManifest _update;
    private readonly Border _track = new() { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly TextBlock _status;
    private CancellationTokenSource? _cts;

    public static void ShowFor(UpdateManifest update)
    {
        _open?.Close();
        _open = new UpdateWindow(update);
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    private UpdateWindow(UpdateManifest update) : base("Update Petal", 420)
    {
        _update = update;
        var title = Text(L.F("Petal {0} is available", update.Version), "Petal.Section");
        title.FontSize = 15;
        Body.Children.Add(title);
        var size = update.Size > 0 ? " · " + Util.Sizes.Format(update.Size) : "";
        Body.Children.Add(Text(L.F("You have {0}", UpdateService.CurrentText) + size, "Petal.Caption").WithMargin(0, 2, 0, 10));
        if (!string.IsNullOrWhiteSpace(update.Notes))
        {
            var notes = Text(update.Notes.Trim(), "Petal.Body");
            notes.TextWrapping = TextWrapping.Wrap;
            Body.Children.Add(new ScrollViewer { Content = notes, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        _track.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _fill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        _track.Child = _fill;
        Body.Children.Add(_track);
        _status = Text("Petal closes while the update installs, then starts again.", "Petal.Caption");
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_status);

        PrimaryButton.Content = L.T("Install");
        PrimaryButton.Click += async (_, _) => await InstallAsync();
        ResetButton.Content = L.T("Skip This Version");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) =>
        {
            SettingsStore.Current.SkippedVersion = update.Version;
            SettingsStore.Save();
            Close();
        };
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task InstallAsync()
    {
        PrimaryButton.IsEnabled = ResetButton.IsEnabled = false;
        _track.Visibility = Visibility.Visible;
        _status.Text = L.T("Downloading…");
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(f => _fill.Width = Math.Max(0, _track.ActualWidth * f));
            var installer = await UpdateService.DownloadAsync(_update, progress, _cts.Token);
            _status.Text = L.T("Installing…");
            UpdateService.Install(installer);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is UpdateException or System.ComponentModel.Win32Exception)
        {
            Log.Error(ex, "Installing an update");
            _status.Text = ex is UpdateException ? L.T(ex.Message) : L.T("The installer didn't start.");
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
            _track.Visibility = Visibility.Collapsed;
            PrimaryButton.IsEnabled = ResetButton.IsEnabled = true;
        }
    }
}
