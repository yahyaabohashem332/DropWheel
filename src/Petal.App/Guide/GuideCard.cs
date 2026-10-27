using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Petal.App.Localization;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Guide;

/// <summary>
/// The explanation of a petal, beside the wheel: what it does, how to use it and a few examples, from the mark
/// under its name in the wheel's centre (or F1). It doesn't take the keyboard, so the wheel stays open and usable;
/// pointing at another petal shows that one's, and it goes with the wheel. "More in the Tools Guide" opens the
/// guide at the same entry, where it can be tried or asked about.
/// </summary>
public sealed class GuideCard : Window
{
    private const double CardWidth = 360;
    private static GuideCard? _open;

    private readonly Border _card = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 430 };
    private string _key = "";

    /// <summary>Whether a card is on screen now (the wheel then keeps it up to date).</summary>
    public static bool IsOpen => _open is not null;

    private GuideCard()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = CardWidth + 32;
        Title = L.T("DropWheel");
        L.Apply(this);
        Content = Build();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>Shows the card for an entry beside the wheel's area (physical pixels), or changes the one shown.</summary>
    public static void Show(string key, string? title, string? own, Int32Rect wheel)
    {
        bool fresh = _open is null;
        _open ??= new GuideCard();
        _open.Fill(key, title, own);
        if (fresh)
        {
            _open.Show();
            _open.PlaceBeside(wheel);
        }
    }

    /// <summary>Follows the petal now lit in the wheel, when a card is open.</summary>
    public static void Follow(string? key, string? title, string? own)
    {
        if (_open is { } card && key is not null && (key != card._key || title is not null))
            card.Fill(key, title, own);
    }

    /// <summary>Takes the card away (the wheel closed).</summary>
    public static void CloseCard()
    {
        _open?.Close();
        _open = null;
    }

    private void Fill(string key, string? title, string? own)
    {
        _key = key;
        _scroll.Content = GuideViews.Explanation(key, title, own);
        _scroll.ScrollToTop();
    }

    /// <summary>To the right of the wheel, or to its left when the screen ends there; at the wheel's height.</summary>
    private void PlaceBeside(Int32Rect wheel)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        UpdateLayout();
        var (_, work, scale) = MonitorAt(new POINT(wheel.X + wheel.Width / 2, wheel.Y + wheel.Height / 2));
        int w = (int)Math.Round(ActualWidth * scale), h = (int)Math.Round(ActualHeight * scale);
        // The wheel's ring is smaller than its window: sit just outside the ring.
        int inset = (int)(wheel.Width * 0.22);
        int x = wheel.X + wheel.Width - inset;
        if (x + w > work.Right)
            x = wheel.X + inset - w;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        int y = Math.Clamp(wheel.Y + wheel.Height / 2 - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_SHOWWINDOW);
    }

    private UIElement Build()
    {
        var p = Theme.Current;
        _card.Width = CardWidth;
        _card.Margin = new Thickness(16);
        _card.CornerRadius = new CornerRadius(18);
        _card.Padding = new Thickness(18, 14, 18, 14);
        _card.Background = Palette.Brush(p.IsDark ? Color.FromArgb(0xF4, 0x2A, 0x2A, 0x30) : Color.FromArgb(0xF8, 0xFF, 0xFF, 0xFF));
        _card.BorderBrush = Palette.Brush(p.GlassEdge);
        _card.BorderThickness = new Thickness(1);
        _card.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = p.IsDark ? 0.5 : 0.18, Color = Colors.Black };

        var layout = new DockPanel();
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var more = Link("More in the Tools Guide", () =>
        {
            var key = _key;
            CloseCard();
            GuideWindow.ShowSingle(key);
        });
        DockPanel.SetDock(more, Dock.Left);
        footer.Children.Add(more);
        var close = Link("Close", CloseCard);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(close);
        layout.Children.Add(footer);
        layout.Children.Add(_scroll);
        _card.Child = layout;
        return _card;
    }

    /// <summary>A word that acts when clicked, without taking the keyboard from the wheel.</summary>
    private static TextBlock Link(string text, Action act)
    {
        var link = new TextBlock { Text = L.T(text), FontFamily = UiFonts.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand };
        link.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Accent");
        link.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            act();
        };
        return link;
    }

    /// <summary>For design snapshots: a card, filled but not placed.</summary>
    internal static GuideCard ForSnapshot(string key)
    {
        var card = new GuideCard();
        card.Fill(key, null, null);
        return card;
    }
}
