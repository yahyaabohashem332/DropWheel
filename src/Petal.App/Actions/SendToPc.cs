using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.App.Util;
using Petal.Engines.Files.Sharing;

namespace Petal.App.Actions;

/// <summary>
/// This PC's side of sending to other PCs: receiving while it is switched on (asking each time), and
/// the name and id other PCs see.
/// </summary>
internal static class PcDrop
{
    private static DropReceiver? _receiver;

    public static string Name => Environment.MachineName;

    public static string Folder => Path.Combine(PhoneWindow.DownloadsFolder(), "Petal");

    /// <summary>Stable and random, so a PC never finds itself.</summary>
    public static string Id
    {
        get
        {
            var settings = SettingsStore.Current;
            if (string.IsNullOrEmpty(settings.DropId))
            {
                settings.DropId = Guid.NewGuid().ToString("N");
                SettingsStore.Save();
            }
            return settings.DropId;
        }
    }

    public static bool Receiving => _receiver is not null;

    /// <summary>Starts or stops receiving as the setting says.</summary>
    public static async Task ApplyAsync(bool on)
    {
        if (on == Receiving)
            return;
        if (!on)
        {
            var old = _receiver;
            _receiver = null;
            if (old is not null)
                await old.DisposeAsync();
            return;
        }
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            var receiver = DropReceiver.Start(Name, Id, Folder, offer => dispatcher.Invoke(() => ReceiveWindow.AskAsync(offer)));
            Hud.HudCard? card = null;
            receiver.Progress += (from, p) => dispatcher.BeginInvoke(() =>
            {
                card ??= Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), L.F("Receiving from {0}", from), "");
                card.SetProgress(p);
            });
            receiver.Received += (from, files) => dispatcher.BeginInvoke(() =>
            {
                var shown = card ?? Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "");
                card = null;
                shown.Succeed(L.P(files.Count, "{0} file arrived", "{0} files arrived"), L.F("From {0} · click to show", from), () => Shell.ExplorerSelection.Reveal(files));
            });
            _receiver = receiver;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Log.Error(ex, "Receiving from other PCs");
        }
    }
}

/// <summary>Finds the PCs on the network with Petal receiving, and sends the files to the one chosen.</summary>
internal sealed class SendToPcWindow : ToolWindow
{
    private readonly IReadOnlyList<string> _paths;
    private readonly StackPanel _pcs = new();
    private readonly TextBlock _status;

    public static void Show(IReadOnlyList<string> paths, ScreenPoint at)
    {
        var window = new SendToPcWindow(paths);
        window.ShowNear(at);
        window.Activate();
    }

    internal static SendToPcWindow ForSnapshot(IReadOnlyList<string> paths, IReadOnlyList<DropPeer> peers)
    {
        var window = new SendToPcWindow(paths, search: false);
        window.ShowPeers(peers);
        return window;
    }

    private SendToPcWindow(IReadOnlyList<string> paths, bool search = true) : base("Send to PC", 420)
    {
        _paths = paths;
        var files = PetalDrop.Collect(paths);
        Body.Children.Add(Text(FileLabel(paths) + " · " + Sizes.Format(files.Sum(f => f.File.Size)), "Petal.Section"));
        _status = Text("Looking for PCs with Petal on this network…", "Petal.Caption");
        _status.Margin = new Thickness(0, 8, 0, 8);
        Body.Children.Add(_status);
        Body.Children.Add(_pcs);
        Body.Children.Add(Text("The other PC needs Petal with “Let other PCs send me files” on (Settings › Work & Sharing). It asks before saving anything, and the files go straight across your network.", "Petal.Caption").WithMarginT(10));
        ResetButton.Content = L.T("Look Again");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += async (_, _) => await SearchAsync();
        PrimaryButton.Content = L.T("Close");
        PrimaryButton.Click += (_, _) => Close();
        if (search)
            Loaded += async (_, _) => await SearchAsync();
    }

    private async Task SearchAsync()
    {
        _pcs.Children.Clear();
        _status.Text = L.T("Looking for PCs with Petal on this network…");
        ResetButton.IsEnabled = false;
        var peers = await PetalDrop.DiscoverAsync(TimeSpan.FromSeconds(1.5), PcDrop.Id);
        ResetButton.IsEnabled = true;
        ShowPeers(peers);
    }

    private void ShowPeers(IReadOnlyList<DropPeer> peers)
    {
        _pcs.Children.Clear();
        _status.Text = peers.Count == 0 ? L.T("No PC with Petal receiving was found.") : L.T("Choose the PC to send to:");
        foreach (var peer in peers)
        {
            var target = peer;
            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { ThemedIcon("monitor", 18).WithMargin(0, 0, 10, 0), new TextBlock { Text = peer.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center } },
                },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
            };
            button.Click += async (_, _) => await SendAsync(target);
            _pcs.Children.Add(button);
        }
    }

    private async Task SendAsync(DropPeer peer)
    {
        Close();
        var card = Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), L.F("Waiting for {0} to accept…", peer.Name), "");
        card.SetProgress(null);
        var progress = new Progress<double>(p =>
        {
            card.SetProgress(p);
        });
        var outcome = await PetalDrop.SendAsync(peer, PcDrop.Name, _paths, progress);
        switch (outcome)
        {
            case DropOutcome.Sent:
                card.Succeed(L.F("Sent to {0}", peer.Name), L.P(_paths.Count, "{0} item", "{0} items"), null);
                break;
            case DropOutcome.Declined:
                card.Fail(L.F("{0} said no", peer.Name), L.T("Nothing was sent."));
                break;
            case DropOutcome.Unreachable:
                card.Fail(L.F("{0} can't be reached", peer.Name), L.T("It may have left the network, or its firewall keeps Petal out."));
                break;
            default:
                card.Fail(L.T("Not sent"), L.T("The connection broke off. Try again."));
                break;
        }
    }
}

/// <summary>"Files from …": what another PC wants to send, to accept or decline. Declines by itself after two minutes.</summary>
internal sealed class ReceiveWindow : ToolWindow
{
    private readonly TaskCompletionSource<bool> _answer = new();

    public static Task<bool> AskAsync(DropOffer offer)
    {
        var window = new ReceiveWindow(offer) { WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
        window.Show();
        window.Activate();
        return window._answer.Task;
    }

    internal static ReceiveWindow ForSnapshot(DropOffer offer) => new(offer);

    private ReceiveWindow(DropOffer offer) : base("Files from Another PC", 420)
    {
        Body.Children.Add(Text(L.F("{0} wants to send you {1} ({2}).", offer.From, L.P(offer.Files.Count, "{0} file", "{0} files"), Sizes.Format(offer.Total)), "Petal.Body"));
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var file in offer.Files.Take(12))
            list.Children.Add(Text($"{file.Name} · {Sizes.Format(file.Size)}", "Petal.Caption"));
        if (offer.Files.Count > 12)
            list.Children.Add(Text(L.F("…and {0} more", offer.Files.Count - 12), "Petal.Caption"));
        Body.Children.Add(list);
        Body.Children.Add(Text(L.F("They are saved in {0}.", PcDrop.Folder), "Petal.Caption").WithMarginT(10));
        ResetButton.Content = L.T("Decline");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => Answer(false);
        PrimaryButton.Content = L.T("Accept");
        PrimaryButton.Click += (_, _) => Answer(true);
        Closed += (_, _) => _answer.TrySetResult(false);
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            Answer(false);
        };
        timeout.Start();
    }

    private void Answer(bool yes)
    {
        _answer.TrySetResult(yes);
        Close();
    }
}
