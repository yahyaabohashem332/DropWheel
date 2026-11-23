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

    static UpdateService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd($"DropWheel/{Current}");

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentText => Current.ToString(3);

    /// <summary>Where downloaded installers wait to run.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "updates");

    public static Task<UpdateManifest?> CheckAsync(CancellationToken cancellationToken = default) =>
        Updates.CheckAsync(SettingsStore.Current.UpdateFeed, Current, Http, cancellationToken);

    /// <summary>A newer version found and not skipped, waiting to be installed; the tray icon shows a dot meanwhile.</summary>
    public static UpdateManifest? Waiting { get; private set; }

    public static event Action<UpdateManifest>? Found;

    /// <summary>True when the user skipped this version and it may be skipped: a required update never is.</summary>
    public static bool Skipped(UpdateManifest update) =>
        update.Level != UpdateUrgency.Required && update.Version == SettingsStore.Current.SkippedVersion;

    /// <summary>Notes a newer version (unless the user skipped it), for the tray's dot and menu.</summary>
    public static void Offer(UpdateManifest update)
    {
        if (Skipped(update))
            return;
        Waiting = update;
        Found?.Invoke(update);
    }

    /// <summary>"Check for Updates" in the tray: the update window when there is one, else a card saying how things are.</summary>
    public static async Task CheckFromTrayAsync()
    {
        var s = SettingsStore.Current;
        if (string.IsNullOrWhiteSpace(s.UpdateFeed))
        {
            var empty = HudCard.ShowAt(HudCard.CornerPoint(), "", "");
            empty.Fail(L.T("No update address is set"), L.T("Set one in Settings › Updates."));
            return;
        }
        try
        {
            var update = await CheckAsync();
            s.LastUpdateCheck = DateTime.UtcNow;
            SettingsStore.Save();
            if (update is null)
            {
                var card = HudCard.ShowAt(HudCard.CornerPoint(), "", "");
                card.Succeed(L.T("DropWheel is up to date."), L.F("This is DropWheel {0}.", CurrentText), null);
                return;
            }
            Offer(update);
            UpdateWindow.ShowFor(update);
        }
        catch (UpdateException ex)
        {
            var card = HudCard.ShowAt(HudCard.CornerPoint(), "", "");
            card.Fail(L.T("Couldn't check for updates"), L.T(ex.Message));
        }
    }

    /// <summary>Once a day, a while after start: a quiet card when there is something new.</summary>
    public static async Task CheckInBackgroundAsync()
    {
        var s = SettingsStore.Current;
        // The Store updates its own copy.
        if (Packaged.IsPackaged)
            return;
        if (!s.CheckForUpdates || string.IsNullOrWhiteSpace(s.UpdateFeed) || s.LastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromDays(1))
            return;
        await Task.Delay(TimeSpan.FromSeconds(45));
        try
        {
            var update = await CheckAsync();
            s.LastUpdateCheck = DateTime.UtcNow;
            SettingsStore.Save();
            if (update is null || Skipped(update))
                return;
            Offer(update);
            // An optional update waits quietly behind the tray's dot.
            if (update.Level == UpdateUrgency.Optional)
                return;
            var card = HudCard.ShowAt(HudCard.CornerPoint(), "", "");
            if (update.Level == UpdateUrgency.Required)
                card.Succeed(L.F("An important update: DropWheel {0}", update.Version), L.T("Click to install it"), () => UpdateWindow.ShowFor(update));
            else
                card.Succeed(L.F("DropWheel {0} is available", update.Version), L.T("Click to see what's new"), () => UpdateWindow.ShowFor(update));
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

/// <summary>"Petal 1.2.0 is available": how much it matters, what's new, and Install or Skip (or Later, for a required one).</summary>
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

    private UpdateWindow(UpdateManifest update) : base("Update DropWheel", 420)
    {
        _update = update;
        var title = Text(L.F("DropWheel {0} is available", update.Version), "Petal.Section");
        title.FontSize = 15;
        Body.Children.Add(title);
        var size = update.Size > 0 ? " · " + Util.Sizes.Format(update.Size) : "";
        Body.Children.Add(Text(L.F("You have {0}", UpdateService.CurrentText) + size, "Petal.Caption").WithMargin(0, 2, 0, 10));
        Body.Children.Add(Urgency(update.Level));
        var details = new StackPanel();
        if (!string.IsNullOrWhiteSpace(update.Notes))
        {
            var notes = Text(update.Notes.Trim(), "Petal.Body");
            notes.TextWrapping = TextWrapping.Wrap;
            details.Children.Add(notes);
        }
        if (update.Changes is { Count: > 0 } changes)
        {
            details.Children.Add(Text("What changed", "Petal.Label").WithMargin(0, details.Children.Count > 0 ? 10 : 0, 0, 4));
            foreach (var change in changes)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                var dot = Text("•", "Petal.Body");
                dot.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                // The maker's own words, as they are: not looked up in the tables.
                var line = Text("", "Petal.Body");
                line.Text = change;
                line.TextWrapping = TextWrapping.Wrap;
                row.Children.Add(line);
                details.Children.Add(row);
            }
        }
        if (details.Children.Count > 0)
            Body.Children.Add(new ScrollViewer { Content = details, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _track.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        _fill.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");
        _track.Child = _fill;
        Body.Children.Add(_track);
        _status = Text("DropWheel closes while the update installs, then starts again.", "Petal.Caption");
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_status);

        PrimaryButton.Content = L.T("Install");
        PrimaryButton.Click += async (_, _) => await InstallAsync();
        ResetButton.Visibility = Visibility.Visible;
        if (update.Level == UpdateUrgency.Required)
        {
            // A required update comes back until it is installed.
            ResetButton.Content = L.T("Later");
            ResetButton.Click += (_, _) => Close();
        }
        else
        {
            ResetButton.Content = L.T("Skip This Version");
            ResetButton.Click += (_, _) =>
            {
                SettingsStore.Current.SkippedVersion = update.Version;
                SettingsStore.Save();
                Close();
            };
        }
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>A line that says how much the update matters, with a coloured mark.</summary>
    private static FrameworkElement Urgency(UpdateUrgency level)
    {
        var (title, words, brush) = level switch
        {
            UpdateUrgency.Required => ("Required update", "It fixes something important, so it can't be skipped.", "Petal.Danger"),
            UpdateUrgency.Optional => ("Optional update", "Install it whenever it suits you.", "Petal.TextSecondary"),
            _ => ("Recommended update", "Recommended for everyone.", "Petal.Accent"),
        };
        var mark = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(Border.BackgroundProperty, brush);
        var name = Text(title, "Petal.Body");
        name.FontWeight = FontWeights.SemiBold;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, name } };
        var said = Text(words, "Petal.Caption");
        said.TextWrapping = TextWrapping.Wrap;
        return new StackPanel { Margin = new Thickness(0, 0, 0, 10), Children = { line, said } };
    }

    internal string SkipLabelForTest => ResetButton.Content as string ?? "";

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
