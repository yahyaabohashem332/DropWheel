using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.App.Tools;
using Petal.App.Util;
using Petal.Engines.Files.Sharing;
using Petal.Engines.Imaging;
using Petal.Recipes;

namespace Petal.App.Actions;

/// <summary>
/// Send to Phone: a QR code for a phone on the same network, which opens a page to save the files.
/// Sharing stops when the window closes, or after a quarter of an hour with nothing happening.
/// </summary>
public sealed class PhoneWindow : ToolWindow
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);
    private static PhoneWindow? _open;

    private readonly LanShare? _share;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _idle;
    private readonly List<string> _received = [];
    private int _downloads;

    public static void ShowFor(IReadOnlyList<string> paths, ScreenPoint at) => Show(paths, at, null);

    /// <param name="temporary">A folder of cleaned copies, deleted when sharing stops.</param>
    private static void Show(IReadOnlyList<string> paths, ScreenPoint at, string? temporary)
    {
        _open?.Close();
        _open = new PhoneWindow(paths, null, temporary);
        _open.Closed += (_, _) => _open = null;
        _open.ShowNear(at);
    }

    /// <summary>A window sharing on this PC's loopback address, for design snapshots (no firewall prompt).</summary>
    internal static PhoneWindow ForSnapshot(IReadOnlyList<string> paths) => new(paths, System.Net.IPAddress.Loopback);

    private PhoneWindow(IReadOnlyList<string> paths, System.Net.IPAddress? address = null, string? temporary = null) : base("Send to Phone", 380)
    {
        _paths = paths;
        if (temporary is not null)
            Closed += (_, _) => { try { Directory.Delete(temporary, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } };
        PrimaryButton.Content = L.T("Stop Sharing");
        PrimaryButton.Click += (_, _) => Close();
        _status = Text("", "Petal.Caption");
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.TextAlignment = TextAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;
        _idle = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => CheckIdle(), Dispatcher);

        address ??= LanShare.LocalAddress();
        if (address is null)
        {
            Body.Children.Add(Text("This PC isn't connected to a network. Connect it to the same Wi-Fi as the phone, then try again.", "Petal.Body").WithMargin(0, 6, 0, 6));
            PrimaryButton.Content = L.T("Done");
            return;
        }
        try
        {
            _share = LanShare.Start(paths, address, PageText(), Environment.MachineName);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Log.Error(ex, "Starting the phone share");
            Body.Children.Add(Text("Petal couldn't start sharing on this network.", "Petal.Body").WithMargin(0, 6, 0, 6));
            PrimaryButton.Content = L.T("Done");
            return;
        }
        _share.Activity += a => Dispatcher.BeginInvoke(() => OnActivity(a));

        var code = new Image { Width = 210, Height = 210, Source = QrImage(QrGenerator.Encode(_share.Url, QrRedundancy.Medium)), FlowDirection = FlowDirection.LeftToRight };
        RenderOptions.SetEdgeMode(code, EdgeMode.Aliased);
        var plate = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = code,
        };
        Body.Children.Add(plate);
        var how = Text("Scan it with the phone's camera. The phone has to be on the same Wi-Fi as this PC.", "Petal.Body");
        how.TextAlignment = TextAlignment.Center;
        how.TextWrapping = TextWrapping.Wrap;
        how.Margin = new Thickness(0, 12, 0, 4);
        Body.Children.Add(how);

        // The address, for typing it in by hand.
        var url = new TextBox { Text = _share.Url, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center };
        url.SetResourceReference(ForegroundProperty, "Petal.TextSecondary");
        url.FontSize = 11.5;
        var copy = IconButton("copy", "Copy the address", 14);
        copy.Click += (_, _) => Clipboard.SetText(_share.Url);
        var urlRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(copy, Dock.Right);
        urlRow.Children.Add(copy);
        urlRow.Children.Add(url);
        Body.Children.Add(urlRow);

        Body.Children.Add(Divider().WithMargin(0, 10, 0, 10));
        long total = _share.Items.Sum(i => i.Size);
        Body.Children.Add(Text(FileLabel(_share.Items.Select(i => i.Path).ToList()) + " · " + Sizes.Format(total), "Petal.Label"));

        var upload = new CheckBox { Content = L.T("Let the phone send files to this PC"), Margin = new Thickness(0, 10, 0, 0) };
        var downloads = DownloadsFolder();
        upload.Click += (_, _) => _share.UploadFolder = upload.IsChecked == true ? downloads : null;
        Body.Children.Add(upload);
        Body.Children.Add(Text(L.F("They're saved in {0}. Reload the page on the phone after turning this on.", Path.GetFileName(downloads)), "Petal.Caption").WithMargin(24, 2, 0, 0));

        // Photos and videos that record where they were taken: offer clean copies instead.
        _locationPanel.Visibility = Visibility.Collapsed;
        _locationPanel.Margin = new Thickness(0, 12, 0, 0);
        _locationPanel.CornerRadius = new CornerRadius(8);
        _locationPanel.Padding = new Thickness(10, 8, 10, 8);
        _locationPanel.SetResourceReference(Border.BackgroundProperty, "Petal.AccentSoft");
        var withoutLocation = new Button { Content = L.T("Share Without Location"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        withoutLocation.Click += async (_, _) => await ShareCleanAsync(withoutLocation);
        _location.TextWrapping = TextWrapping.Wrap;
        _locationPanel.Child = new StackPanel { Children = { _location, withoutLocation } };
        Body.Children.Add(_locationPanel);
        _ = WarnAboutLocationAsync();

        _status.Margin = new Thickness(0, 12, 0, 0);
        Body.Children.Add(_status);
        Body.Children.Add(Text("If Windows asks, allow Petal on private networks. On a network set to Public, the phone can't reach this PC.", "Petal.Caption").WithMargin(0, 10, 0, 0));
        FooterNote = "Stops when this window closes";
        SetStatus(L.T("Waiting for the phone…"));
        _idle.Start();

        Closed += async (_, _) =>
        {
            _idle.Stop();
            await _share.DisposeAsync();
        };
    }

    private void SetStatus(string text) => _status.Text = text;

    private readonly IReadOnlyList<string> _paths;
    private readonly Border _locationPanel = new();
    private readonly TextBlock _location = new();

    private async Task WarnAboutLocationAsync()
    {
        int located = 0;
        foreach (var path in _paths.Where(File.Exists))
        {
            if (await SharingCleaner.HasLocationAsync(path))
                located++;
        }
        if (located == 0 || !IsLoaded && !IsVisible)
            return;
        _location.Text = L.P(located, "{0} photo or video shows where it was taken.", "{0} photos or videos show where they were taken.");
        _locationPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Shares cleaned copies (no location, device or author) in place of the originals.</summary>
    private async Task ShareCleanAsync(Button button)
    {
        button.IsEnabled = false;
        button.Content = L.T("Cleaning…");
        var folder = Path.Combine(Path.GetTempPath(), "Petal", "clean-share", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var shared = new List<string>();
        try
        {
            foreach (var path in _paths)
            {
                if (File.Exists(path) && SharingCleaner.CanClean(path))
                {
                    var copy = Path.Combine(folder, Path.GetFileName(path));
                    await SharingCleaner.CleanAsync(path, copy, null, CancellationToken.None);
                    shared.Add(copy);
                }
                else
                {
                    shared.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(ex, "Cleaning files to share");
            button.Content = L.T("Couldn't clean them");
            return;
        }
        var at = new ScreenPoint((int)((Left + ActualWidth / 2) * Dpi.X), (int)((Top + ActualHeight / 2) * Dpi.Y));
        Show(shared, at, folder);
    }

    private (double X, double Y) Dpi
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return (dpi.DpiScaleX, dpi.DpiScaleY);
        }
    }

    private void OnActivity(ShareActivity activity)
    {
        switch (activity.Kind)
        {
            case ShareActivityKind.Opened:
                if (_downloads == 0 && _received.Count == 0)
                    SetStatus(L.T("The phone is connected"));
                break;
            case ShareActivityKind.Downloaded:
                _downloads++;
                SetStatus(L.F("Saved on the phone: {0}", activity.Name));
                break;
            case ShareActivityKind.Received:
                _received.Add(activity.Name);
                Jobs.History.Add(L.T("Received from the phone"), [], [activity.Name]);
                SetStatus(L.P(_received.Count, "{0} file received from the phone", "{0} files received from the phone"));
                ExplorerSelection.SelectIfVisible(_received);
                break;
        }
    }

    private void CheckIdle()
    {
        if (_share is not null && DateTime.UtcNow - _share.LastActivity > Idle)
            Close();
    }

    /// <summary>The Downloads folder, wherever the user moved it.</summary>
    internal static string DownloadsFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string value)
            {
                var path = Environment.ExpandEnvironmentVariables(value);
                if (Directory.Exists(path))
                    return path;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static SharePageText PageText() => new()
    {
        Language = L.Code,
        RightToLeft = L.IsRightToLeft,
        Title = L.T("Files from {0}"),
        Subtitle = L.T("Tap a file to save it on this phone."),
        Folder = L.T("Folder · downloads as ZIP"),
        All = L.T("Download everything as ZIP"),
        SendTitle = L.T("Send to this PC"),
        SendHint = L.T("Files you choose are saved in {0} on the PC."),
        Choose = L.T("Choose files"),
        Sending = L.T("Sending…"),
        Sent = L.T("Sent"),
        Failed = L.T("Didn't arrive"),
        Footer = L.T("Straight from the PC over Wi-Fi. Nothing goes through the internet."),
        FormatSize = Sizes.Format,
    };

    /// <summary>The code as crisp vector squares, with the standard four-module quiet zone.</summary>
    internal static DrawingImage QrImage(bool[,] modules)
    {
        int n = modules.GetLength(0);
        var dark = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                if (modules[x, y])
                    dark.Children.Add(new RectangleGeometry(new Rect(x + 4, y + 4, 1, 1)));
            }
        }
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, n + 8, n + 8))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)), null, dark));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
