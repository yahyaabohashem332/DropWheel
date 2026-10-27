using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Petal.App.Localization;
using Petal.App.Visuals;
using Petal.Core.Text;

namespace Petal.App.Screen;

/// <summary>
/// The card beside a circle: a translation, an explanation, an answer, conversions or colours, with what can be done
/// with them. Dark glass like the other screen tools, whatever the theme.
/// </summary>
internal sealed class CircleCard : Border
{
    public const double CardWidth = 380;

    private static readonly Brush Soft = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ChipBack = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ChipHover = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private readonly StackPanel _stack = new();
    private readonly TextBlock _title;
    private readonly TextBox _body;
    private readonly ScrollViewer _bodyScroll;
    private readonly StackPanel _rows = new();
    private readonly WrapPanel _swatches = new();
    private readonly DockPanel _statusRow = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { Foreground = Soft, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly FrameworkElement _spinner;
    private readonly RotateTransform _turn = new();
    private readonly WrapPanel _chips = new() { Margin = new Thickness(-3, 10, 0, 0) };
    private readonly Grid _askRow = new() { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBox _question;
    private readonly TextBlock _placeholder;

    public event Action? CloseRequested;

    /// <summary>A question typed in the card and sent with Enter.</summary>
    public event Action<string>? Asked;

    public CircleCard(string icon, string title)
    {
        Width = CardWidth;
        CornerRadius = new CornerRadius(16);
        Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x25));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(16, 12, 16, 14);
        Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.5, Color = Colors.Black };
        TextElement.SetForeground(this, Brushes.White);
        TextElement.SetFontFamily(this, UiFonts.Text);
        FlowDirection = L.Direction;
        Cursor = Cursors.Arrow;

        // Title: the icon, what the card is, and the close button.
        var head = new DockPanel();
        var close = new StripButton("x", "Close", () => CloseRequested?.Invoke(), size: 28);
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var mark = Icons.Create(icon, 16, new SolidColorBrush(Theme.Current.Accent), 2);
        mark.Margin = new Thickness(0, 0, 8, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(mark, Dock.Left);
        head.Children.Add(mark);
        _title = new TextBlock { Text = L.T(title), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        head.Children.Add(_title);
        _stack.Children.Add(head);

        _body = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            SelectionBrush = new SolidColorBrush(Theme.Current.Accent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontSize = 15,
            FontFamily = UiFonts.Text,
            Visibility = Visibility.Collapsed,
        };
        _body.TextChanged += (_, _) =>
        {
            _body.FlowDirection = KeyboardLayout.LooksArabic(_body.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            _body.Visibility = _body.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        _bodyScroll = new ScrollViewer
        {
            Content = _body,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _stack.Children.Add(_bodyScroll);
        _stack.Children.Add(_rows);
        _swatches.Margin = new Thickness(0, 10, 0, 0);
        _stack.Children.Add(_swatches);

        // A turning ring while something is worked out on this PC.
        _spinner = Icons.Create("loader", 14, Soft, 2);
        _spinner.Margin = new Thickness(0, 0, 8, 0);
        _spinner.VerticalAlignment = VerticalAlignment.Center;
        _spinner.RenderTransformOrigin = new Point(0.5, 0.5);
        _spinner.RenderTransform = _turn;
        _spinner.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_spinner, Dock.Left);
        _statusRow.Children.Add(_spinner);
        _statusRow.Children.Add(_status);
        _statusRow.Visibility = Visibility.Collapsed;
        _stack.Children.Add(_statusRow);

        // The question box for Ask: Enter sends it.
        _question = new TextBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            FontSize = 14,
            FontFamily = UiFonts.Text,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            MaxHeight = 90,
        };
        _question.Resources[typeof(Border)] = new Style(typeof(Border)) { Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(10)) } };
        _placeholder = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), FontSize = 14, Margin = new Thickness(12, 8, 12, 0), IsHitTestVisible = false };
        _question.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _question.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _question.FlowDirection = _question.Text.Length == 0 ? L.Direction : KeyboardLayout.LooksArabic(_question.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        };
        _question.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Return || _question.Text.Trim().Length == 0)
                return;
            e.Handled = true;
            var q = _question.Text.Trim();
            _question.Text = "";
            Asked?.Invoke(q);
        };
        _askRow.Children.Add(_question);
        _askRow.Children.Add(_placeholder);
        _stack.Children.Add(_askRow);
        _stack.Children.Add(_chips);
        Child = _stack;
        Unloaded += (_, _) => _turn.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    public string Title
    {
        set => _title.Text = value;
    }

    /// <summary>The card's text: the answer, the translation. It can be selected and copied in part.</summary>
    public string Text
    {
        get => _body.Text;
        set
        {
            _body.Text = value;
            _bodyScroll.ScrollToEnd();
        }
    }

    /// <summary>Something being worked out: the line says what, and a ring turns.</summary>
    public void Busy(string status)
    {
        _status.Text = L.T(status);
        _statusRow.Visibility = Visibility.Visible;
        _spinner.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
            _turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>Done: the ring stops; the line says <paramref name="status"/> (already in the reader's language), or goes.</summary>
    public void Done(string status = "")
    {
        _turn.BeginAnimation(RotateTransform.AngleProperty, null);
        _spinner.Visibility = Visibility.Collapsed;
        _status.Text = status;
        _statusRow.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A button under the card: an icon and a word or two. Returns it, to change its words later.</summary>
    public Border Chip(string icon, string label, Action click)
    {
        var text = new TextBlock { Text = L.T(label), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var glyph = Icons.Create(icon, 14, Brushes.White, 1.9);
        glyph.Margin = new Thickness(0, 0, 6, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ChipBack,
            Padding = new Thickness(10, 5, 12, 6),
            Margin = new Thickness(3),
            Cursor = Cursors.Hand,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, text } },
            Tag = text,
        };
        System.Windows.Automation.AutomationProperties.SetName(chip, text.Text);
        chip.MouseEnter += (_, _) => chip.Background = ChipHover;
        chip.MouseLeave += (_, _) => chip.Background = ChipBack;
        chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        _chips.Children.Add(chip);
        return chip;
    }

    /// <summary>Changes a chip's words (Copy becomes Copied for a moment).</summary>
    public static void Relabel(Border chip, string label)
    {
        if (chip.Tag is TextBlock text)
            text.Text = L.T(label);
    }

    /// <summary>A chip that copies <paramref name="text"/> and says so for a moment.</summary>
    public Border CopyChip(string label, Func<string> text)
    {
        Border? chip = null;
        chip = Chip("copyPlain", label, () =>
        {
            if (Words.SelectedText.TrySet(text()))
                Flash(chip!, "Copied", label);
        });
        return chip;
    }

    public static void Flash(Border chip, string now, string after)
    {
        Relabel(chip, now);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Relabel(chip, after);
        };
        timer.Start();
    }

    public void ClearChips() => _chips.Children.Clear();

    /// <summary>A line of the card that can be copied by itself: a conversion, a colour, a time.</summary>
    public void Row(string text, string? detail = null, string? copy = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var button = new StripButton("copyPlain", "Copy", () => Words.SelectedText.TrySet(copy ?? text), size: 28);
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var main = new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        main.FlowDirection = KeyboardLayout.LooksArabic(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        main.HorizontalAlignment = L.IsRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        lines.Children.Add(main);
        if (detail is not null)
            lines.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Foreground = Soft,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // "1,299 USD" stays as written in an Arabic card, on the card's own side.
                FlowDirection = KeyboardLayout.LooksArabic(detail) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                HorizontalAlignment = L.IsRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            });
        row.Children.Add(lines);
        _rows.Children.Add(row);
    }

    public void ClearRows() => _rows.Children.Clear();

    /// <summary>Colours as round swatches; a click copies one's HEX code.</summary>
    public void Swatch(Color colour, string hex)
    {
        var dot = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Margin = new Thickness(0, 0, 8, 8),
            Background = new SolidColorBrush(colour),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = hex,
        };
        System.Windows.Automation.AutomationProperties.SetName(dot, hex);
        dot.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (Words.SelectedText.TrySet(hex))
                Done(L.F("{0} copied", hex));
        };
        _swatches.Children.Add(dot);
    }

    /// <summary>Shows the question box, empty, with <paramref name="hint"/> in it, and puts the keyboard there.</summary>
    public void AskBox(string hint)
    {
        _placeholder.Text = L.T(hint);
        _askRow.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            _question.Focus();
            Keyboard.Focus(_question);
        });
    }

    public bool AskBoxShown => _askRow.Visibility == Visibility.Visible;
}
