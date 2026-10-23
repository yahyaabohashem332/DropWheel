using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Petal.App.Localization;
using Petal.App.Visuals;

namespace Petal.App.Settings;

/// <summary>
/// A guided tour of a window: a small card on each part in turn, with what it is for, Back and Next. The part
/// is scrolled into view and outlined in the accent colour.
/// </summary>
internal sealed class TourOverlay(Window owner, ScrollViewer scroll, IReadOnlyList<(FrameworkElement Target, string Title, string Text)> stops)
{
    private TourAdorner? _current;
    private int _index;

    public static event Action? Finished;

    public void Start()
    {
        if (stops.Count == 0) return;
        owner.Closed += (_, _) => Remove();
        Show(0);
    }

    private void Show(int index)
    {
        Remove();
        _index = index;
        var (target, title, text) = stops[index];
        // Scroll so the part starts near the top of the page.
        var top = target.TransformToAncestor((Visual)scroll.Content).Transform(new Point(0, 0)).Y;
        scroll.ScrollToVerticalOffset(Math.Max(0, top - 12));
        owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer is null) return;
            _current = new TourAdorner(target, Bubble(title, text, index));
            layer.Add(_current);
        });
    }

    private void Remove()
    {
        if (_current is null) return;
        AdornerLayer.GetAdornerLayer(_current.AdornedElement)?.Remove(_current);
        _current = null;
    }

    private Border Bubble(string title, string text, int index)
    {
        var p = Theme.Current;
        var heading = new TextBlock { Text = L.T(title), FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Palette.Brush(p.TextOnAccent) };
        var body = new TextBlock { Text = L.T(text), TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(p.TextOnAccent), Margin = new Thickness(0, 4, 0, 10), FontSize = 12.5 };
        var count = new TextBlock { Text = L.F("{0} of {1}", index + 1, stops.Count), Foreground = Palette.Brush(Color.FromArgb(0xC0, p.TextOnAccent.R, p.TextOnAccent.G, p.TextOnAccent.B)), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var back = new Button { Content = L.T("Back"), IsEnabled = index > 0, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 4) };
        back.Click += (_, _) => Show(_index - 1);
        bool last = index == stops.Count - 1;
        var next = new Button { Content = L.T(last ? "Done" : "Next"), Padding = new Thickness(12, 3, 12, 4) };
        next.Click += (_, _) =>
        {
            if (last)
            {
                Remove();
                Finished?.Invoke();
            }
            else
            {
                Show(_index + 1);
            }
        };
        var close = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = L.T("End the tour"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Palette.Brush(p.TextOnAccent) };
        System.Windows.Automation.AutomationProperties.SetName(close, L.T("End the tour"));
        close.Click += (_, _) => Remove();
        var buttons = new DockPanel();
        DockPanel.SetDock(count, Dock.Left);
        buttons.Children.Add(count);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { back, next, close } };
        buttons.Children.Add(right);
        var bubble = new Border
        {
            Width = 280,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Background = Palette.Brush(p.Accent),
            Child = new StackPanel { Children = { heading, body, buttons } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35 },
            FlowDirection = L.Direction,
        };
        System.Windows.Automation.AutomationProperties.SetName(bubble, L.T(title));
        return bubble;
    }

    /// <summary>The outline around the part, and the card at its top corner.</summary>
    private sealed class TourAdorner : Adorner
    {
        private readonly Border _bubble;
        private readonly VisualCollection _children;

        public TourAdorner(UIElement target, Border bubble) : base(target)
        {
            _bubble = bubble;
            _children = new VisualCollection(this) { bubble };
        }

        protected override int VisualChildrenCount => _children.Count;

        protected override Visual GetVisualChild(int index) => _children[index];

        protected override Size MeasureOverride(Size constraint)
        {
            _bubble.Measure(new Size(280, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = _bubble.DesiredSize;
            double x = L.IsRightToLeft ? 12 : AdornedElement.RenderSize.Width - size.Width - 12;
            _bubble.Arrange(new Rect(new Point(Math.Max(0, x), 10), size));
            return finalSize;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var pen = new Pen(Palette.Brush(Theme.Current.Accent), 2);
            drawingContext.DrawRoundedRectangle(null, pen, new Rect(new Point(-1, -1), new Size(AdornedElement.RenderSize.Width + 2, AdornedElement.RenderSize.Height + 2)), 13, 13);
        }
    }
}
