using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Petal.App.Tools.Parts;

/// <summary>One page in the organised document: which source page, and how it's turned.</summary>
public sealed record PageSlot(int SourceIndex, int Rotation);

/// <summary>
/// Grid of page cards for Organize PDF: drag to reorder, rotate, duplicate or remove pages.
/// Thumbnails are loaded lazily through <see cref="ThumbnailLoader"/>.
/// </summary>
public sealed class PageGrid : WrapPanel
{
    private readonly ObservableCollection<PageSlot> _pages = [];
    private readonly Dictionary<int, ImageSource> _thumbnails = [];
    private int _dragFrom = -1;

    public event Action? Changed;

    public PageGrid()
    {
        _pages.CollectionChanged += (_, _) =>
        {
            Rebuild();
            Changed?.Invoke();
        };
    }

    public Func<int, Task<ImageSource?>>? ThumbnailLoader { get; set; }

    public IReadOnlyList<PageSlot> Pages => _pages;

    public bool IsOriginalOrder(int count) =>
        _pages.Count == count && _pages.Select((p, i) => p.SourceIndex == i && p.Rotation == 0).All(x => x);

    public void Load(int pageCount)
    {
        _pages.Clear();
        for (int i = 0; i < pageCount; i++)
            _pages.Add(new PageSlot(i, 0));
    }

    private void Rebuild()
    {
        Children.Clear();
        for (int i = 0; i < _pages.Count; i++)
            Children.Add(Card(i));
    }

    private UIElement Card(int index)
    {
        var slot = _pages[index];
        var image = new Image { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(slot.Rotation) };
        if (_thumbnails.TryGetValue(slot.SourceIndex, out var cached))
            image.Source = cached;
        else
            _ = LoadAsync(slot.SourceIndex, image);

        var page = new Border
        {
            Width = 116,
            Height = 150,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Child = image,
            Padding = new Thickness(4),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.18 },
        };

        var badge = new Border
        {
            MinWidth = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-6, -6, 0, 0),
            Padding = new Thickness(6, 0, 6, 0),
            Child = new TextBlock { Text = (index + 1).ToString(CultureInfo.CurrentCulture), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        badge.SetResourceReference(Border.BackgroundProperty, "Petal.Accent");

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -8, -8, 0), Visibility = Visibility.Hidden };
        actions.Children.Add(Action("rotateCw", "Rotate", () => _pages[index] = slot with { Rotation = (slot.Rotation + 90) % 360 }));
        actions.Children.Add(Action("duplicate", "Duplicate", () => _pages.Insert(index + 1, slot)));
        actions.Children.Add(Action("trash", "Remove page", () => { if (_pages.Count > 1) _pages.RemoveAt(index); }));

        var root = new Grid { Margin = new Thickness(10), AllowDrop = true, Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
        root.Children.Add(page);
        root.Children.Add(badge);
        root.Children.Add(actions);
        root.MouseEnter += (_, _) => actions.Visibility = Visibility.Visible;
        root.MouseLeave += (_, _) => actions.Visibility = Visibility.Hidden;
        root.PreviewMouseLeftButtonDown += (_, _) => _dragFrom = index;
        root.MouseMove += (_, e) =>
        {
            if (_dragFrom == index && e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not Button)
                DragDrop.DoDragDrop(root, new DataObject("PetalPage", index), DragDropEffects.Move);
        };
        root.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent("PetalPage") ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        root.Drop += (_, e) =>
        {
            if (e.Data.GetData("PetalPage") is int from && from != index)
                _pages.Move(from, index);
            _dragFrom = -1;
        };
        return root;
    }

    private static Button Action(string icon, string tip, Action click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("Petal.FilledIconButton"),
            Width = 26,
            Height = 26,
            Margin = new Thickness(3, 0, 0, 0),
            ToolTip = tip,
            Content = ToolWindow.ThemedIcon(icon, 13, "Petal.Text", 2.2),
        };
        b.Click += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return b;
    }

    private async Task LoadAsync(int sourceIndex, Image target)
    {
        if (ThumbnailLoader is null) return;
        try
        {
            var image = await ThumbnailLoader(sourceIndex);
            if (image is null) return;
            _thumbnails[sourceIndex] = image;
            target.Source = image;
        }
        catch
        {
            // Leave the card blank.
        }
    }
}
