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
        var panel = new UniformGrid { Rows = 1 };
        var group = Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var button = new RadioButton
            {
                Content = label,
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
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
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
