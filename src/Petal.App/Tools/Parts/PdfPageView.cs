using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Petal.App.Tools.Parts;

/// <summary>
/// One page in the PDF editor: white until its picture arrives, then its picture, the form's fields on it, the items the person
/// added drawn over those, and any text box being typed in above everything. Laid out left to right in every language. Its parts
/// are made when it first comes into view, so a long PDF costs little until then.
/// </summary>
public sealed class PdfPageView : Grid
{
    private readonly Func<int, DrawingContext, double, bool> _draw;
    private readonly Func<int, Point, bool> _wants;

    public PdfPageView(int index, double widthInPoints, double heightInPoints, Func<int, DrawingContext, double, bool> draw, Func<int, Point, bool> wants)
    {
        Index = index;
        WidthInPoints = widthInPoints;
        HeightInPoints = heightInPoints;
        _draw = draw;
        _wants = wants;
        Background = Brushes.White;
        FlowDirection = FlowDirection.LeftToRight;
        Margin = new Thickness(0, 0, 0, 14);
        ClipToBounds = true;
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Direction = 270, Opacity = 0.25 };
    }

    public int Index { get; }

    public double WidthInPoints { get; }

    public double HeightInPoints { get; }

    public double Zoom { get; private set; } = 1;

    public bool IsBuilt { get; private set; }

    public Image Picture { get; private set; } = null!;

    public Canvas Fields { get; private set; } = null!;

    public PdfPageLayer Layer { get; private set; } = null!;

    public Canvas Top { get; private set; } = null!;

    /// <summary>The width in pixels of the picture it is showing or waiting for.</summary>
    public int PictureWidth { get; set; }

    public bool FieldsBuilt { get; set; }

    public void SetZoom(double zoom)
    {
        Zoom = zoom;
        Width = Math.Max(1, WidthInPoints * zoom);
        Height = Math.Max(1, HeightInPoints * zoom);
        if (IsBuilt)
            Layer.InvalidateVisual();
    }

    public void Build()
    {
        if (IsBuilt)
            return;
        IsBuilt = true;
        Picture = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(Picture, BitmapScalingMode.HighQuality);
        Fields = new Canvas();
        Layer = new PdfPageLayer(this, _draw, _wants);
        Top = new Canvas();
        Children.Add(Picture);
        Children.Add(Fields);
        Children.Add(Layer);
        Children.Add(Top);
    }

    /// <summary>A point on the view in points on the page as displayed.</summary>
    public Point ToPage(Point p) => new(p.X / Zoom, p.Y / Zoom);
}

/// <summary>Draws a page's items and takes the mouse over them; where it wants nothing, a click goes through to the field under it.</summary>
public sealed class PdfPageLayer(PdfPageView page, Func<int, DrawingContext, double, bool> draw, Func<int, Point, bool> wants) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc) => draw(page.Index, dc, page.Zoom);

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        wants(page.Index, page.ToPage(hitTestParameters.HitPoint)) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
}
