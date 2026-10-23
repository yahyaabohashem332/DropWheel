using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Visuals;
using static Petal.App.Interop.NativeMethods;
using Petal.App.Localization;

namespace Petal.App.Hud;

/// <summary>
/// The little progress card that springs out of the drop point: "Converting to MP3 ·
/// sample.wav · 42%", then "1 of 1 saved". Clicking a finished card reveals the result.
/// </summary>
public sealed class HudCard : Window
{
    private const double CardWidth = 300;
    private const double CardHeight = 84;
    // A saved file's card: its folder, and a row of buttons where the progress bar was.
    private const double SavedHeight = 100;
    // Wider, for the two buttons in every language ("Afficher dans le dossier").
    private const double SavedWidth = 360;
    private const double CardMargin = 24;

    private static readonly List<HudCard> Open = [];

    private readonly Border _card = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly Border _track = new();
    private readonly Border _fill = new();
    private readonly Grid _bar = new();
    private readonly Border _closeButton = new();
    private readonly Grid _badge = new();
    private readonly TranslateTransform _indeterminateShift = new();
    private readonly DispatcherTimer _dismissTimer = new();
    private IntPtr _hwnd;
    private bool _finished;
    private bool _indeterminate = true;
    private Palette _palette = Theme.Current;
    private readonly double _height, _width;
    private readonly string? _savedPath;

    public event Action? CancelRequested;

    /// <summary>Invoked when a finished card is clicked.</summary>
    public Action? OnClick { get; set; }

    private HudCard(string? savedPath = null)
    {
        _savedPath = savedPath;
        _height = savedPath is null ? CardHeight : SavedHeight;
        _width = savedPath is null ? CardWidth : SavedWidth;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = _width + CardMargin * 2;
        Height = _height + CardMargin * 2;
        Title = L.T("Petal");
        Content = Build();
        _dismissTimer.Tick += (_, _) => Dismiss();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>Shows a card centred on a screen point (physical pixels), springing out of it.</summary>
    public static HudCard ShowAt(ScreenPoint at, string title, string subtitle)
    {
        var card = new HudCard();
        card._title.Text = Localization.L.T(title);
        card._subtitle.Text = Localization.L.T(subtitle);
        card.Place(at);
        card.Pop();
        return card;
    }

    /// <summary>
    /// A file just saved, above the notification area: what it is, the folder it is in on this PC, and buttons
    /// to show it in that folder or open it. <paramref name="note"/>, when there is one, takes the folder's
    /// line (the folder stays in its tooltip).
    /// </summary>
    public static HudCard ShowSaved(string title, string path, string? note = null)
    {
        var card = new HudCard(path);
        var folder = System.IO.Path.GetDirectoryName(path) ?? path;
        card._title.Text = Localization.L.T(title);
        card._subtitle.Text = note ?? folder;
        // A path reads left to right, whatever the language around it.
        card._subtitle.FlowDirection = note is null ? FlowDirection.LeftToRight : Localization.L.Direction;
        card._subtitle.TextAlignment = note is null && Localization.L.IsRightToLeft ? TextAlignment.Right : TextAlignment.Left;
        card._subtitle.ToolTip = note is null ? path : note + Environment.NewLine + path;
        card._title.ToolTip = System.IO.Path.GetFileName(path);
        card.Place(HudCard.SavedCornerPoint());
        card._finished = true;
        card._card.Opacity = 0;
        card._card.BeginAnimation(OpacityProperty, Motion.To(1, 120, Motion.Standard, from: 0));
        card._scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 420, Motion.Pop, from: 0.45));
        card._scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 420, Motion.Pop, from: 0.45));
        card.ShowBadge(success: true);
        // Long enough to reach for a button; the pointer on the card holds it.
        card.DismissAfter(TimeSpan.FromSeconds(7));
        return card;
    }

    /// <summary>For design snapshots: a saved file's card, built but not shown.</summary>
    internal static HudCard SavedForSnapshot(string title, string path, string? note = null)
    {
        var card = new HudCard(path);
        card._title.Text = Localization.L.T(title);
        card._subtitle.Text = note ?? System.IO.Path.GetDirectoryName(path) ?? path;
        card._subtitle.FlowDirection = note is null ? FlowDirection.LeftToRight : Localization.L.Direction;
        card._subtitle.TextAlignment = note is null && Localization.L.IsRightToLeft ? TextAlignment.Right : TextAlignment.Left;
        card._finished = true;
        card.ShowBadge(success: true);
        // Off screen its pop never plays: the badge as it ends up.
        card._badge.RenderTransform = Transform.Identity;
        return card;
    }

    private static ScreenPoint SavedCornerPoint()
    {
        var (_, work, scale) = MonitorAt(new POINT(0, 0));
        return new ScreenPoint(
            work.Right - (int)Math.Round((SavedWidth / 2 + CardMargin) * scale),
            work.Bottom - (int)Math.Round((SavedHeight / 2 + CardMargin) * scale));
    }

    private void Place(ScreenPoint at)
    {
        Show();
        var (_, work, scale) = MonitorAt(new POINT(at.X, at.Y));
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
        int x = Math.Clamp(at.X - w / 2, work.Left, work.Right - w);
        int y = Math.Clamp(at.Y - h / 2, work.Top, work.Bottom - h);

        // Do not cover a card that is still on screen.
        foreach (var other in Open)
        {
            int step = (int)(((_height + other._height) / 2 + 10) * scale);
            if (Math.Abs(other._screenY - y) < step && Math.Abs(other._screenX - x) < w * 0.6)
                y = other._screenY - step >= work.Top ? other._screenY - step : other._screenY + step;
        }
        _screenX = x;
        _screenY = y;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Open.Add(this);
    }

    private int _screenX, _screenY;

    /// <summary>Above the notification area, for work nobody dropped anywhere (watched folders).</summary>
    public static ScreenPoint CornerPoint()
    {
        var (_, work, scale) = MonitorAt(new POINT(0, 0));
        return new ScreenPoint(
            work.Right - (int)Math.Round((CardWidth / 2 + CardMargin) * scale),
            work.Bottom - (int)Math.Round((CardHeight / 2 + CardMargin) * scale));
    }

    private void Pop()
    {
        _card.Opacity = 0;
        _card.BeginAnimation(OpacityProperty, Motion.To(1, 120, Motion.Standard, from: 0));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 420, Motion.Pop, from: 0.45));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 420, Motion.Pop, from: 0.45));
        StartIndeterminate();
    }

    public void SetText(string title, string subtitle)
    {
        _title.Text = Localization.L.T(title);
        _subtitle.Text = Localization.L.T(subtitle);
    }

    /// <summary>Progress from 0 to 1, or null when unknown (shows a sweeping bar).</summary>
    public void SetProgress(double? fraction)
    {
        if (_finished)
            return;
        if (fraction is null)
        {
            if (!_indeterminate)
                StartIndeterminate();
            return;
        }
        if (_indeterminate)
        {
            _indeterminate = false;
            _indeterminateShift.BeginAnimation(TranslateTransform.XProperty, null);
            _indeterminateShift.X = 0;
        }
        double width = Math.Max(4, (CardWidth - 64) * Math.Clamp(fraction.Value, 0, 1));
        _fill.BeginAnimation(WidthProperty, Motion.To(width, 160, Motion.Standard));
    }

    private void StartIndeterminate()
    {
        _indeterminate = true;
        double track = CardWidth - 64;
        _fill.BeginAnimation(WidthProperty, null);
        _fill.Width = track * 0.28;
        var sweep = new DoubleAnimation(-track * 0.28, track, TimeSpan.FromMilliseconds(1150))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        _indeterminateShift.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    public void Succeed(string title, string subtitle, Action? onClick)
    {
        _finished = true;
        SetText(title, subtitle);
        _indeterminateShift.BeginAnimation(TranslateTransform.XProperty, null);
        _indeterminateShift.X = 0;
        _fill.BeginAnimation(WidthProperty, Motion.To(CardWidth - 64, 200, Motion.Standard));
        ShowBadge(success: true);
        OnClick = onClick;
        _card.Cursor = onClick is null ? Cursors.Arrow : Cursors.Hand;
        DismissAfter(TimeSpan.FromSeconds(2.6));
    }

    public void Fail(string title, string subtitle)
    {
        _finished = true;
        SetText(title, subtitle);
        _indeterminateShift.BeginAnimation(TranslateTransform.XProperty, null);
        _bar.Visibility = Visibility.Collapsed;
        _subtitle.TextWrapping = TextWrapping.Wrap;
        _subtitle.MaxHeight = 34;
        ShowBadge(success: false);
        DismissAfter(TimeSpan.FromSeconds(6));
    }

    private void DismissAfter(TimeSpan delay)
    {
        _dismissTimer.Stop();
        _dismissTimer.Interval = delay;
        _dismissTimer.Start();
    }

    public void Dismiss()
    {
        _dismissTimer.Stop();
        var fade = Motion.To(0, 160, Motion.Standard);
        fade.Completed += (_, _) =>
        {
            Open.Remove(this);
            Close();
        };
        _card.BeginAnimation(OpacityProperty, fade);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(0.94, 160, Motion.Standard));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(0.94, 160, Motion.Standard));
    }

    private void ShowBadge(bool success)
    {
        var p = _palette;
        _badge.Children.Clear();
        var color = success ? p.Accent : p.Danger;
        _badge.Children.Add(new Ellipse { Width = 22, Height = 22, Fill = Palette.Brush(color) });
        var icon = Icons.Create(success ? "check" : "x", 14, Brushes.White, 2.6);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        _badge.Children.Add(icon);
        _badge.Visibility = Visibility.Visible;
        _closeButton.Visibility = Visibility.Collapsed;
        _badge.RenderTransformOrigin = new Point(0.5, 0.5);
        var s = new ScaleTransform(0.3, 0.3);
        _badge.RenderTransform = s;
        s.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 380, Motion.Pop, from: 0.3));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 380, Motion.Pop, from: 0.3));
    }

    private UIElement Build()
    {
        var p = _palette;
        var root = new Grid();
        Localization.L.Apply(root);
        _card.Width = _width;
        _card.Height = _height;
        _card.CornerRadius = new CornerRadius(20);
        _card.Background = Palette.Brush(p.IsDark ? Color.FromArgb(0xF2, 0x2A, 0x2A, 0x30) : Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
        _card.BorderBrush = Palette.Brush(p.GlassEdge);
        _card.BorderThickness = new Thickness(1);
        _card.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = p.IsDark ? 0.5 : 0.18, Color = Colors.Black };
        _card.RenderTransformOrigin = new Point(0.5, 0.5);
        _card.RenderTransform = _scale;
        _card.MouseLeftButtonUp += (_, _) =>
        {
            if (_finished && OnClick is { } a)
            {
                a();
                Dismiss();
            }
        };
        _card.MouseEnter += (_, _) => { if (_finished) _dismissTimer.Stop(); };
        _card.MouseLeave += (_, _) => { if (_finished) DismissAfter(TimeSpan.FromSeconds(1.4)); };
        root.Children.Add(_card);

        var layout = new Grid { Margin = new Thickness(16, 14, 18, 14) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _card.Child = layout;

        // Close / cancel button, replaced by a success or error badge at the end.
        _closeButton.Width = _closeButton.Height = 22;
        _closeButton.CornerRadius = new CornerRadius(11);
        _closeButton.Background = Palette.Brush(p.Control);
        _closeButton.VerticalAlignment = VerticalAlignment.Top;
        _closeButton.HorizontalAlignment = HorizontalAlignment.Left;
        _closeButton.Cursor = Cursors.Hand;
        _closeButton.ToolTip = Localization.L.T("Cancel");
        var x = Icons.Create("x", 12, Palette.Brush(p.TextSecondary), 2.4);
        x.HorizontalAlignment = HorizontalAlignment.Center;
        x.VerticalAlignment = VerticalAlignment.Center;
        _closeButton.Child = x;
        _closeButton.MouseEnter += (_, _) => _closeButton.Background = Palette.Brush(p.ControlHover);
        _closeButton.MouseLeave += (_, _) => _closeButton.Background = Palette.Brush(p.Control);
        _closeButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CancelRequested?.Invoke();
        };
        layout.Children.Add(_closeButton);

        _badge.Width = _badge.Height = 22;
        _badge.VerticalAlignment = VerticalAlignment.Top;
        _badge.HorizontalAlignment = HorizontalAlignment.Left;
        _badge.Visibility = Visibility.Collapsed;
        layout.Children.Add(_badge);

        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        _title.FontFamily = UiFonts.Text;
        _title.FontSize = 13.5;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Palette.Brush(p.Text);
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _subtitle.FontFamily = UiFonts.Text;
        _subtitle.FontSize = 12;
        _subtitle.Foreground = Palette.Brush(p.TextSecondary);
        _subtitle.Margin = new Thickness(0, 2, 0, 0);
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(_title);
        text.Children.Add(_subtitle);

        _bar.Margin = new Thickness(0, 10, 0, 0);
        _bar.Height = 5;
        _bar.ClipToBounds = true;
        _track.CornerRadius = new CornerRadius(2.5);
        _track.Background = Palette.Brush(p.IsDark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0x16, 0, 0, 0));
        _fill.CornerRadius = new CornerRadius(2.5);
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
        _fill.Width = 4;
        _fill.Background = new LinearGradientBrush(Theme.Current.Accent, Theme.Current.AccentStrong, 0);
        _fill.RenderTransform = _indeterminateShift;
        _bar.Children.Add(_track);
        _bar.Children.Add(_fill);
        _bar.Clip = new RectangleGeometry(new Rect(0, 0, CardWidth - 64, 5), 2.5, 2.5);
        if (_savedPath is { } path)
        {
            _closeButton.Visibility = Visibility.Collapsed;
            _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            buttons.Children.Add(CardButton("folderOpen", "Show in Folder", () => Shell.ExplorerSelection.Reveal([path])));
            buttons.Children.Add(CardButton("externalLink", "Open", () => OpenFile(path)));
            text.Children.Add(buttons);
        }
        else
        {
            text.Children.Add(_bar);
        }
        layout.Children.Add(text);

        return root;
    }

    /// <summary>A small pill on a saved file's card; it acts on release, and the card goes.</summary>
    private Border CardButton(string icon, string label, Action act)
    {
        var p = _palette;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var glyph = Icons.Create(icon, 13, Palette.Brush(p.Text), 2);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(glyph);
        row.Children.Add(new TextBlock { Text = L.T(label), FontFamily = UiFonts.Text, FontSize = 12, Foreground = Palette.Brush(p.Text), VerticalAlignment = VerticalAlignment.Center });
        var button = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 11, 5),
            Margin = new Thickness(0, 0, 6, 0),
            Background = Palette.Brush(p.Control),
            Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L.T(label));
        button.MouseEnter += (_, _) => button.Background = Palette.Brush(p.ControlHover);
        button.MouseLeave += (_, _) => button.Background = Palette.Brush(p.Control);
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try
            {
                act();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
            {
                Log.Error(ex, "Acting on a saved file");
            }
            Dismiss();
        };
        return button;
    }

    private static void OpenFile(string path)
    {
        if (System.IO.File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        else if (System.IO.Path.GetDirectoryName(path) is { } folder && System.IO.Directory.Exists(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
