using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Petal.App.Tools;

/// <summary>A row of mutually exclusive options drawn as a segmented control.</summary>
public sealed class Segmented<T> : Border where T : notnull
{
    private readonly List<(T Value, RadioButton Button)> _buttons = [];
    private T _value;

    public event Action<T>? Changed;

    public Segmented(IEnumerable<(T Value, string Label)> options, T selected)
    {
        Style = (Style)Application.Current.FindResource("Petal.Segments");
        HorizontalAlignment = HorizontalAlignment.Left;
        _value = selected;
        var panel = new SegmentPanel();
        var group = Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var button = new RadioButton
            {
                Content = Localization.L.T(label),
                GroupName = group,
                Style = (Style)Application.Current.FindResource("Petal.Segment"),
                IsChecked = EqualityComparer<T>.Default.Equals(value, selected),
            };
            var v = value;
            button.Checked += (_, _) =>
            {
                _value = v;
                Changed?.Invoke(v);
            };
            _buttons.Add((value, button));
            panel.Children.Add(button);
        }
        Child = panel;
    }

    public T Value
    {
        get => _value;
        set
        {
            foreach (var (v, b) in _buttons)
                b.IsChecked = EqualityComparer<T>.Default.Equals(v, value);
            _value = value;
        }
    }

    public bool Stretch
    {
        set => HorizontalAlignment = value ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }
}

/// <summary>
/// Equal segments when they fit; otherwise each as wide as its words, so a label that is long
/// in one language ("Tel que prononcé") isn't cut while short ones keep their room; and when
/// even that is too wide, the segments go on as many rows as they need.
/// </summary>
internal sealed class SegmentPanel : Panel
{
    private double _rowHeight;

    /// <summary>The segments of each row, filled in order while they fit the width.</summary>
    private List<List<UIElement>> Rows(double width)
    {
        var rows = new List<List<UIElement>> { new() };
        double used = 0;
        foreach (UIElement child in InternalChildren)
        {
            double w = child.DesiredSize.Width;
            if (rows[^1].Count > 0 && used + w > width + 0.5)
            {
                rows.Add([]);
                used = 0;
            }
            rows[^1].Add(child);
            used += w;
        }
        return rows;
    }

    protected override Size MeasureOverride(Size available)
    {
        double widest = 0, total = 0;
        _rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, child.DesiredSize.Width);
            total += child.DesiredSize.Width;
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }
        double uniform = widest * InternalChildren.Count;
        if (uniform <= available.Width)
            return new Size(uniform, _rowHeight);
        if (total <= available.Width)
            return new Size(total, _rowHeight);
        return new Size(available.Width, _rowHeight * Rows(available.Width).Count);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int count = InternalChildren.Count;
        if (count == 0)
            return final;
        double widest = 0;
        foreach (UIElement child in InternalChildren)
            widest = Math.Max(widest, child.DesiredSize.Width);
        List<List<UIElement>> rows = widest * count <= final.Width + 0.5 ? [InternalChildren.Cast<UIElement>().ToList()] : Rows(final.Width);
        bool uniform = rows.Count == 1 && widest * count <= final.Width + 0.5;
        double rowHeight = final.Height / rows.Count;
        for (int r = 0; r < rows.Count; r++)
        {
            // Each row fills the width, its segments sharing it by the room their words take.
            double words = rows[r].Sum(c => c.DesiredSize.Width);
            double x = 0;
            foreach (var child in rows[r])
            {
                double width = uniform || words <= 0 ? final.Width / rows[r].Count : child.DesiredSize.Width * final.Width / words;
                child.Arrange(new Rect(x, r * rowHeight, width, rowHeight));
                x += width;
            }
        }
        return final;
    }
}

/// <summary>Slider with a live, formatted value to its right.</summary>
public sealed class LabeledSlider : Grid
{
    private readonly Slider _slider;
    private readonly TextBlock _value;
    private readonly Func<double, string> _format;

    public event Action<double>? Changed;

    public LabeledSlider(double min, double max, double value, Func<double, string> format, double step, bool ticks)
    {
        _format = format;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Room for the usual value; a longer one ("2,048 بكسل") takes a little from the slider rather than being cut.
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 70 });
        _slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            SmallChange = step,
            LargeChange = step * 5,
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
            TickPlacement = ticks ? TickPlacement.BottomRight : TickPlacement.None,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _value = new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Petal.Value"),
            HorizontalAlignment = HorizontalAlignment.Right,
            FlowDirection = FlowDirection.LeftToRight,
            Text = format(value),
        };
        SetColumn(_value, 1);
        Children.Add(_slider);
        Children.Add(_value);
        _slider.ValueChanged += (_, e) =>
        {
            _value.Text = _format(e.NewValue);
            Changed?.Invoke(e.NewValue);
        };
        // Double-click resets to the value the slider started with, like Lightroom.
        var initial = value;
        _slider.MouseDoubleClick += (_, _) => _slider.Value = initial;
    }

    public double Value
    {
        get => _slider.Value;
        set => _slider.Value = value;
    }

    public Slider Inner => _slider;
}
