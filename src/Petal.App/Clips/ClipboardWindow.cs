using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using Petal.App.Tools.Windows;
using Petal.Core.Text;

namespace Petal.App.Clips;

/// <summary>
/// Everything in the clipboard history: search it, pin what should stay, delete what shouldn't, and paste
/// one into the app that was in front (or just put it back on the clipboard).
/// </summary>
internal sealed class ClipboardWindow : ToolWindow
{
    private static ClipboardWindow? _open;
    private readonly IntPtr _target;
    private readonly TextBox _search = new() { Padding = new Thickness(6, 4, 6, 4) };
    private readonly ListBox _list = new() { Height = 340, BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _empty;

    public static void ShowSingle(IntPtr target, ScreenPoint at)
    {
        _open?.Close();
        _open = new ClipboardWindow(target);
        _open.Closed += (_, _) => _open = null;
        if (at.X == 0 && at.Y == 0)
        {
            _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _open.Show();
        }
        else
        {
            _open.ShowNear(at);
        }
        _open.Activate();
    }

    internal static ClipboardWindow ForSnapshot() => new(IntPtr.Zero);

    private ClipboardWindow(IntPtr target) : base("Clipboard History", 460)
    {
        _target = target;
        var placeholder = Text("Search what you copied", "Petal.Caption");
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(9, 5, 0, 0);
        _search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Fill();
        };
        Body.Children.Add(new Grid { Children = { _search, placeholder } });
        _list.Margin = new Thickness(0, 10, 0, 0);
        _list.MouseDoubleClick += async (_, _) => await PasteAsync();
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Return)
            {
                e.Handled = true;
                await PasteAsync();
            }
            else if (e.Key == Key.Delete && _list.SelectedItem is ListBoxItem { Tag: ClipItem item })
            {
                ClipboardHistory.Remove(item);
            }
        };
        Body.Children.Add(_list);
        _empty = Text("Nothing yet. What you copy appears here.", "Petal.Caption");
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Margin = new Thickness(0, 12, 0, 0);
        Body.Children.Add(_empty);
        if (!SettingsStore.Current.KeepClipboardHistory)
            Body.Children.Add(Text("Clipboard history is off. Turn it on in Settings › Screen & Text.", "Petal.Caption").WithMarginT(8));
        PrimaryButton.Content = L.T(target == IntPtr.Zero ? "Copy" : "Paste");
        PrimaryButton.Click += async (_, _) => await PasteAsync();
        ResetButton.Content = L.T("Clear All");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => ClipboardHistory.Clear(keepPinned: true);
        FooterNote = "Pinned clips stay";
        ClipboardHistory.Changed += OnChanged;
        Closed += (_, _) => ClipboardHistory.Changed -= OnChanged;
        Fill();
        Loaded += (_, _) => _search.Focus();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Fill);

    private void Fill()
    {
        var query = _search.Text.Trim();
        var selected = (_list.SelectedItem as ListBoxItem)?.Tag as ClipItem;
        _list.Items.Clear();
        foreach (var item in ClipboardHistory.All)
        {
            if (query.Length > 0 && !(item.Text ?? item.Preview).Contains(query, StringComparison.CurrentCultureIgnoreCase))
                continue;
            var row = Row(item);
            _list.Items.Add(row);
            if (item == selected)
                _list.SelectedItem = row;
        }
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0)
            _list.SelectedIndex = 0;
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ListBoxItem Row(ClipItem item)
    {
        var dock = new DockPanel { Margin = new Thickness(2, 4, 2, 4) };
        var delete = IconButton("trash", "Delete", 13);
        delete.Click += (_, _) => ClipboardHistory.Remove(item);
        DockPanel.SetDock(delete, Dock.Right);
        dock.Children.Add(delete);
        var pin = IconButton("pin", item.Pinned ? "Unpin" : "Pin", 13, filled: item.Pinned);
        pin.Opacity = item.Pinned ? 1 : 0.55;
        pin.Click += (_, _) => ClipboardHistory.TogglePin(item);
        DockPanel.SetDock(pin, Dock.Right);
        dock.Children.Add(pin);
        if (item.Kind == ClipKind.Picture && item.Picture() is { } picture)
        {
            var thumb = new Image { Source = picture, Height = 40, MaxWidth = 70, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(thumb, Dock.Left);
            dock.Children.Add(thumb);
        }
        else
        {
            var icon = ThemedIcon(item.Kind == ClipKind.Files ? "folder" : "fileText", 16, "Petal.TextSecondary");
            icon.Margin = new Thickness(2, 0, 10, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var line = Text(item.Preview, "Petal.Body");
        line.TextTrimming = TextTrimming.CharacterEllipsis;
        line.FlowDirection = KeyboardLayout.LooksArabic(item.Preview) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        // Text in the window's own direction starts where the window's text does; the other kind is pushed over to match.
        line.TextAlignment = line.FlowDirection == L.Direction ? TextAlignment.Left : TextAlignment.Right;
        text.Children.Add(line);
        text.Children.Add(Text(When(item.Copied), "Petal.Caption"));
        dock.Children.Add(text);
        return new ListBoxItem { Content = dock, Tag = item, ToolTip = item.Kind == ClipKind.Text && item.Text!.Length > 80 ? item.Text[..Math.Min(item.Text.Length, 600)] : null };
    }

    private static string When(DateTime copied)
    {
        var ago = DateTime.Now - copied;
        if (ago < TimeSpan.FromMinutes(1)) return L.T("Just now");
        if (ago < TimeSpan.FromHours(1)) return L.P((int)ago.TotalMinutes, "{0} minute ago", "{0} minutes ago");
        if (copied.Date == DateTime.Today) return copied.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        return copied.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    }

    private async Task PasteAsync()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: ClipItem item })
            return;
        Close();
        if (_target == IntPtr.Zero)
        {
            ClipboardHistory.Put(item);
            return;
        }
        if (!await ClipboardHistory.PasteAsync(item, _target))
            Hud.HudCard.ShowAt(Hud.HudCard.CornerPoint(), "", "").Succeed(L.T("On the clipboard"), L.T("Paste it with Ctrl+V."), null);
    }
}
