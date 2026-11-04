using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Shell;
using Petal.App.Tools;
using Petal.Engines.Files.Sharing;
using Petal.Engines.Imaging;

namespace Petal.App.Actions;

/// <summary>
/// Scan with Phone: a QR code opens a camera page on the phone. Each photo's page is found,
/// straightened and cleaned on this PC, and the pages are saved as one searchable PDF.
/// </summary>
public sealed class ScanWindow : ToolWindow
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(20);
    private static ScanWindow? _open;

    private readonly LanShare? _share;
    private readonly ScanSession? _session;
    private readonly TextBlock _status;
    private readonly TextBlock _folder = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _idle;
    private readonly List<string> _saved = [];

    public static void ShowSingle()
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new ScanWindow();
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    /// <summary>A window on the loopback address, for design snapshots (no firewall prompt).</summary>
    internal static ScanWindow ForSnapshot() => new(System.Net.IPAddress.Loopback);

    private ScanWindow(System.Net.IPAddress? address = null) : base("Scan with Phone", 380)
    {
        PrimaryButton.Content = L.T("Done");
        PrimaryButton.Click += (_, _) => Close();
        _status = Text("", "Petal.Caption");
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.TextAlignment = TextAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;
        _idle = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => CheckIdle(), Dispatcher);

        address ??= LanShare.LocalAddress();
        if (address is null)
        {
            Body.Children.Add(Text("This PC isn't connected to a network. Connect it to the same Wi-Fi as the phone, then try again.", "Petal.Body").WithMargin(0, 6, 0, 6));
            return;
        }
        _session = new ScanSession(SaveFolder());
        try
        {
            _share = LanShare.Start([], address, null, Environment.MachineName);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Log.Error(ex, "Starting the phone scanner");
            Body.Children.Add(Text("DropWheel couldn't start sharing on this network.", "Petal.Body").WithMargin(0, 6, 0, 6));
            _session.Dispose();
            _session = null;
            return;
        }
        var page = ScanPage.Html(PageText(), Environment.MachineName);
        _share.CustomPage = () => page;
        _share.Api = _session.HandleAsync;
        _share.Activity += a => Dispatcher.BeginInvoke(() =>
        {
            if (a.Kind == ShareActivityKind.Opened && _saved.Count == 0 && _session.PageCount == 0)
                SetStatus(L.T("The phone is connected. Take a photo of the first page."));
        });
        _session.PagesChanged += n => Dispatcher.BeginInvoke(() => SetStatus(n == 0 && _saved.Count > 0
            ? L.F("Saved: {0}", Path.GetFileName(_saved[^1]))
            : L.P(n, "{0} page so far", "{0} pages so far")));
        _session.Saved += path => Dispatcher.BeginInvoke(() => OnSaved(path));

        var code = new Image { Width = 200, Height = 200, Source = PhoneWindow.QrImage(QrGenerator.Encode(_share.Url, QrRedundancy.Medium)), FlowDirection = FlowDirection.LeftToRight };
        RenderOptions.SetEdgeMode(code, EdgeMode.Aliased);
        Body.Children.Add(new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = code,
        });
        var how = Text("Scan it with the phone's camera, then photograph each page. DropWheel finds the page, straightens it and saves them as one searchable PDF.", "Petal.Body");
        how.TextAlignment = TextAlignment.Center;
        how.TextWrapping = TextWrapping.Wrap;
        how.Margin = new Thickness(0, 12, 0, 4);
        Body.Children.Add(how);

        var url = new TextBox { Text = _share.Url, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center };
        url.SetResourceReference(ForegroundProperty, "Petal.TextSecondary");
        url.FontSize = 11.5;
        Body.Children.Add(url);

        Body.Children.Add(Divider().WithMargin(0, 10, 0, 10));
        _folder.Style = (Style)FindResource("Petal.Body");
        var choose = new Button { Content = L.T("Choose…"), Margin = new Thickness(8, 0, 0, 0) };
        choose.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = L.T("Save scans in"), InitialDirectory = _session.SaveFolder };
            if (dialog.ShowDialog(this) == true)
            {
                _session.SaveFolder = dialog.FolderName;
                SettingsStore.Current.ScanFolder = dialog.FolderName;
                SettingsStore.Save();
                ShowFolder();
            }
        };
        var folderRow = new DockPanel();
        DockPanel.SetDock(choose, Dock.Right);
        folderRow.Children.Add(choose);
        folderRow.Children.Add(_folder);
        Body.Children.Add(Row("Save in", folderRow, 70, 0));
        ShowFolder();

        _status.Margin = new Thickness(0, 14, 0, 0);
        Body.Children.Add(_status);
        Body.Children.Add(Text("If Windows asks, allow DropWheel on private networks. On a network set to Public, the phone can't reach this PC.", "Petal.Caption").WithMargin(0, 10, 0, 0));
        FooterNote = "Stops when this window closes";
        SetStatus(L.T("Waiting for the phone…"));
        _idle.Start();

        Closed += async (_, _) =>
        {
            _idle.Stop();
            await _share.DisposeAsync();
            _session.Dispose();
        };
    }

    private void SetStatus(string text) => _status.Text = text;

    private void ShowFolder()
    {
        if (_session is null)
            return;
        _folder.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(_session.SaveFolder));
        _folder.ToolTip = _session.SaveFolder;
    }

    private void OnSaved(string path)
    {
        _saved.Add(path);
        History.Add(L.T("Scanned with the phone"), [], [path]);
        SetStatus(L.F("Saved: {0}", Path.GetFileName(path)));
        ExplorerSelection.SelectIfVisible([path]);
    }

    private void CheckIdle()
    {
        if (_share is not null && DateTime.UtcNow - _share.LastActivity > Idle)
            Close();
    }

    /// <summary>The folder chosen before, else Documents\Scans.</summary>
    private static string SaveFolder() => SavedPlaces.Folder(SavedKind.Scans);

    private static ScanPageText PageText() => new()
    {
        Language = L.Code,
        RightToLeft = L.IsRightToLeft,
        Title = L.T("Scan to {0}"),
        Hint = L.T("Photograph each page. DropWheel straightens it and saves them as one PDF on the PC."),
        Take = L.T("Take a photo of a page"),
        Choose = L.T("Choose photos"),
        DragHint = L.T("Drag the corners onto the corners of the page."),
        Retake = L.T("Retake"),
        Rotate = L.T("Turn"),
        AddPage = L.T("Add page"),
        LookDocument = L.T("Document"),
        LookColor = L.T("Colour"),
        LookGray = L.T("Grey"),
        LookBlackWhite = L.T("Black & white"),
        Preparing = L.T("Preparing…"),
        Sending = L.T("Sending…"),
        Straightening = L.T("Straightening…"),
        Saving = L.T("Saving the PDF…"),
        Save = L.T("Save as PDF"),
        Saved = L.T("Saved on the PC: {0}"),
        Failed = L.T("That didn't work. Try again."),
        Unreadable = L.T("This phone can't open that photo. Take it with the camera instead."),
        Remove = L.T("Remove"),
        Footer = L.T("Straight from the PC over Wi-Fi. Nothing goes through the internet."),
    };
}
