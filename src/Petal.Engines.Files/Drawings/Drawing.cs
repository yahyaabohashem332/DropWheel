using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using SkiaSharp;

namespace Petal.Engines.Files.Drawings;

/// <summary>A point of the drawing, in its own units, in double precision (survey drawings sit millions of units out).</summary>
internal readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>A 2D affine map: x' = A·x + C·y + E, y' = B·x + D·y + F.</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public Pt Map(Pt p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    /// <summary>A direction: the map without its move.</summary>
    public Pt Turn(Pt v) => new(A * v.X + C * v.Y, B * v.X + D * v.Y);

    /// <summary>This map after <paramref name="first"/>: (this ∘ first)(p) = this(first(p)).</summary>
    public Affine After(Affine first) => new(
        A * first.A + C * first.B, B * first.A + D * first.B,
        A * first.C + C * first.D, B * first.C + D * first.D,
        A * first.E + C * first.F + E, B * first.E + D * first.F + F);

    public static Affine Move(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Affine Rotate(double radians) => new(Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), 0, 0);

    public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>How much lengths grow, on average over the two axes.</summary>
    public double Size => Math.Sqrt(Math.Abs(A * D - B * C));
}

internal abstract record Mark(SKColor Color);

/// <summary>A line through points: its weight in millimetres on paper, its dashes in drawing units, its width (a wide polyline's) in drawing units.</summary>
internal sealed record Stroke(Pt[] Points, bool Closed, SKColor Color, double WeightMm, double[]? Dashes, double Width) : Mark(Color);

/// <summary>A filled area: rings filled even-odd, as hatches and solids are.</summary>
internal sealed record Area(List<Pt[]> Rings, SKColor Color) : Mark(Color);

/// <summary>
/// Text: its lines, where the text's own space (x along the line, y up, in drawing units) lands, its height (of a
/// capital letter), how it is anchored, and its font. <see cref="Fit"/> is the length a TEXT is stretched to
/// (aligned or fit), <see cref="Wrap"/> the width an MTEXT wraps at.
/// </summary>
internal sealed record Words(string[] Lines, Affine Place, double Height, int Horizontal, int Vertical, string? Font, SKColor Color,
    double LineSpacing = 1, double Wrap = 0, double Fit = 0, bool KeepHeight = false) : Mark(Color);

/// <summary>A picture placed on the drawing: the unit square of the picture (0–1, y up) to the drawing.</summary>
internal sealed record Picture(byte[] Bytes, Affine Place) : Mark(SKColors.White);

/// <summary>
/// A DXF or DWG drawing read as what is to be drawn: the model space (or, when that is empty, the paper space), in
/// the order drawn, with every block put in its place, colours and line weights resolved through layers and blocks,
/// and layers that are off, frozen or not for plotting left out.
/// </summary>
internal sealed class Drawing
{
    public List<Mark> Marks { get; } = [];
    public string Name { get; init; } = "";

    /// <summary>Points round a full circle, and along each arc of a polyline or a hatch's edge.</summary>
    private const int Round = 96, Bulge = 48;

    private readonly CadDocument _doc;
    private readonly string _folder;
    private readonly double _lineTypeScale;
    private readonly Dictionary<string, byte[]?> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private int _marks;

    /// <summary>Beyond this many marks the rest is left out: a drawing that large would take minutes to draw.</summary>
    private const int MostMarks = 3_000_000;

    private Drawing(CadDocument doc, string folder)
    {
        _doc = doc;
        _folder = folder;
        _lineTypeScale = doc.Header.LineTypeScale is > 0 and var s ? s : 1;
    }

    public static CadDocument Load(string path, bool dwg)
    {
        try
        {
            return dwg ? DwgReader.Read(path, (NotificationEventHandler?)null) : DxfReader.Read(path, (NotificationEventHandler?)null);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException || ex is not IOException and not UnauthorizedAccessException and not OutOfMemoryException)
        {
            throw new InvalidDataException(dwg
                ? "DropWheel can't read this DWG file. It reads DWG from R14 (1997) on, and not files that are damaged or locked with a password."
                : "DropWheel can't read this DXF file: it may be damaged.", ex);
        }
    }

    public static Drawing Read(CadDocument doc, string path)
    {
        var drawing = new Drawing(doc, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".") { Name = Path.GetFileNameWithoutExtension(path) };
        var top = new Context(Affine.Identity, SKColors.Black, 25, null, null, 0);
        drawing.Walk(Sorted(doc.ModelSpace), top);
        if (drawing.Marks.Count == 0 && doc.PaperSpace is { } paper)
            drawing.Walk(Sorted(paper).Where(e => e is not Viewport), top);
        if (drawing.Marks.Count == 0)
            throw new InvalidDataException("This drawing has nothing DropWheel can draw: it may hold only 3D solids, or nothing at all.");
        return drawing;
    }

    private static IEnumerable<Entity> Sorted(BlockRecord block)
    {
        try
        {
            return block.GetSortedEntities().ToList();
        }
        catch (Exception)
        {
            return block.Entities;
        }
    }

    /// <summary>What an entity inside a block takes from the insert: its place, and what "by block" and layer 0 mean there.</summary>
    private sealed record Context(Affine World, SKColor Color, double WeightHundredths, LineType? LineType, Layer? Layer, int Depth);

    private void Walk(IEnumerable<Entity> entities, Context ctx)
    {
        foreach (var entity in entities)
        {
            if (_marks > MostMarks)
                return;
            try
            {
                Draw(entity, ctx);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // One entity ACadSharp can't work out is left out; the rest of the drawing still comes.
            }
        }
    }

    // ───────────── what an entity looks like ─────────────

    /// <summary>The layer an entity is drawn on: inside a block, layer 0 means the insert's layer.</summary>
    private static Layer? LayerOf(Entity e, Context ctx) =>
        e.Layer is { } layer && layer.Name == "0" && ctx.Layer is not null ? ctx.Layer : e.Layer;

    private static bool Shown(Layer? layer) =>
        layer is null || (layer.IsOn && !layer.Flags.HasFlag(LayerFlags.Frozen) && layer.PlotFlag
            && !string.Equals(layer.Name, "Defpoints", StringComparison.OrdinalIgnoreCase));

    private static SKColor ColorOf(Color color, Layer? layer, Context ctx)
    {
        if (color.IsByLayer)
            color = layer?.Color ?? new Color(7);
        if (color.IsByBlock)
            return ctx.Color;
        if (color.IsTrueColor)
            return new SKColor(color.R, color.G, color.B);
        var index = Math.Abs(color.Index);
        // White on the screen is black on paper.
        if (index is 7 or 0 or 255)
            return SKColors.Black;
        var rgb = color.GetRgb();
        return rgb.Length >= 3 ? new SKColor(rgb[0], rgb[1], rgb[2]) : SKColors.Black;
    }

    private static double WeightOf(LineWeightType weight, Layer? layer, Context ctx)
    {
        if (weight == LineWeightType.ByLayer)
            weight = layer?.LineWeight ?? LineWeightType.Default;
        return weight switch
        {
            LineWeightType.ByBlock => ctx.WeightHundredths,
            LineWeightType.ByLayer or LineWeightType.Default or LineWeightType.ByDIPs => 25,
            _ => (int)weight,
        };
    }

    private LineType? LineTypeOf(Entity e, Layer? layer, Context ctx)
    {
        var type = e.LineType;
        if (type is null || string.Equals(type.Name, "ByLayer", StringComparison.OrdinalIgnoreCase))
            type = layer?.LineType;
        if (type is not null && string.Equals(type.Name, "ByBlock", StringComparison.OrdinalIgnoreCase))
            type = ctx.LineType;
        return type;
    }

    /// <summary>A line type's dashes in drawing units (on, off, on, off…), or null for a continuous line.</summary>
    private double[]? DashesOf(LineType? type, double scale)
    {
        if (type is null || type.PatternLength <= 0)
            return null;
        var dashes = new List<double>();
        foreach (var segment in type.Segments)
        {
            var length = Math.Abs(segment.Length) * scale;
            var on = segment.Length >= 0;
            if (dashes.Count % 2 == (on ? 0 : 1))
                dashes.Add(length);
            else if (dashes.Count > 0)
                dashes[^1] += length;
            else
                dashes.AddRange([0, length]);
        }
        if (dashes.Count < 2)
            return null;
        if (dashes.Count % 2 == 1)
        {
            // An odd pattern ends on a dash, which runs into the first.
            dashes[0] += dashes[^1];
            dashes.RemoveAt(dashes.Count - 1);
        }
        return dashes.Sum() > 0 ? [.. dashes] : null;
    }

    private void Line(IEnumerable<Pt> points, bool closed, Entity e, Context ctx, double width = 0)
    {
        var layer = LayerOf(e, ctx);
        var pts = points.Select(ctx.World.Map).ToArray();
        if (pts.Length == 0)
            return;
        var scale = _lineTypeScale * (e.LineTypeScale > 0 ? e.LineTypeScale : 1) * ctx.World.Size;
        Marks.Add(new Stroke(pts, closed, ColorOf(e.Color, layer, ctx), WeightOf(e.LineWeight, layer, ctx) / 100.0,
            DashesOf(LineTypeOf(e, layer, ctx), scale), width * ctx.World.Size));
        _marks += pts.Length;
    }

    private void Fill(IEnumerable<IEnumerable<Pt>> rings, Entity e, Context ctx, SKColor? color = null)
    {
        var list = rings.Select(r => r.Select(ctx.World.Map).ToArray()).Where(r => r.Length >= 3).ToList();
        if (list.Count == 0)
            return;
        Marks.Add(new Area(list, color ?? ColorOf(e.Color, LayerOf(e, ctx), ctx)));
        _marks += list.Sum(r => r.Length);
    }

    // ───────────── planes ─────────────

    /// <summary>
    /// The map from an entity's own plane (its object coordinate system, by DXF's arbitrary axis rule) to the
    /// drawing's, looked at from above: the identity for an entity drawn flat, a mirror for one whose plane faces down.
    /// </summary>
    private static Affine Plane(XYZ normal)
    {
        var n = Normalize(normal);
        if (Math.Abs(n.X) < 1e-9 && Math.Abs(n.Y) < 1e-9)
            return n.Z >= 0 ? Affine.Identity : new Affine(-1, 0, 0, 1, 0, 0);
        var ax = Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64 ? Cross(new XYZ(0, 1, 0), n) : Cross(new XYZ(0, 0, 1), n);
        ax = Normalize(ax);
        var ay = Normalize(Cross(n, ax));
        return new Affine(ax.X, ax.Y, ay.X, ay.Y, 0, 0);
    }

    private static XYZ Cross(XYZ a, XYZ b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static XYZ Normalize(XYZ v)
    {
        var l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return l > 0 ? new XYZ(v.X / l, v.Y / l, v.Z / l) : new XYZ(0, 0, 1);
    }

    /// <summary>The plane's map for an OCS point taken with z: the elevation moves it along the normal, which seen from above is nothing more.</summary>
    private static Pt Flat(XYZ p) => new(p.X, p.Y);

    // ───────────── each kind of entity ─────────────

    private void Draw(Entity e, Context ctx)
    {
        if (e.IsInvisible)
            return;
        var layer = LayerOf(e, ctx);
        if (!Shown(layer))
            return;
        switch (e)
        {
            case Line line:
                Line([Flat(line.StartPoint), Flat(line.EndPoint)], false, e, ctx);
                break;
            case Arc or Circle or Ellipse:
                var curve = e switch
                {
                    Arc a => a.PolygonalVertexes(Math.Max(8, (int)(Round * Math.Abs(a.Sweep) / (2 * Math.PI)))),
                    Circle c => c.PolygonalVertexes(Round),
                    Ellipse el => el.PolygonalVertexes(Round),
                    _ => [],
                };
                Line(curve.Select(Flat), e is Circle and not Arc || e is Ellipse { IsFullEllipse: true }, e, ctx);
                break;
            case LwPolyline lw:
            {
                var plane = Plane(lw.Normal);
                var width = lw.ConstantWidth > 0 ? lw.ConstantWidth : lw.Vertices.Count > 0 && lw.Vertices.All(v => v.StartWidth == lw.Vertices[0].StartWidth && v.EndWidth == v.StartWidth) ? lw.Vertices[0].StartWidth : 0;
                Line(lw.GetPoints<XYZ>(Bulge).Select(p => plane.Map(Flat(p))), lw.IsClosed, e, ctx, width);
                break;
            }
            case Polyline2D p2:
            {
                var plane = Plane(p2.Normal);
                var width = p2.StartWidth == p2.EndWidth ? p2.StartWidth : 0;
                Line(p2.GetPoints<XYZ>(Bulge).Select(p => plane.Map(Flat(p))), p2.IsClosed, e, ctx, width);
                break;
            }
            case Polyline3D p3:
                Line(p3.Vertices.Select(v => Flat(v.Location)), p3.IsClosed, e, ctx);
                break;
            case Spline spline:
            {
                List<XYZ> points;
                if (!spline.TryPolygonalVertexes(256, out points!) || points.Count < 2)
                    points = spline.FitPoints.Count >= 2 ? spline.FitPoints : spline.ControlPoints;
                Line(points.Select(Flat), spline.IsClosed, e, ctx);
                break;
            }
            case Solid solid:
            {
                // Corners 1, 2, 4, 3: the third and fourth go round the other way.
                var plane = Plane(solid.Normal);
                Fill([[plane.Map(Flat(solid.FirstCorner)), plane.Map(Flat(solid.SecondCorner)), plane.Map(Flat(solid.FourthCorner)), plane.Map(Flat(solid.ThirdCorner))]], e, ctx);
                break;
            }
            case Face3D face:
                Line([Flat(face.FirstCorner), Flat(face.SecondCorner), Flat(face.ThirdCorner), Flat(face.FourthCorner)], true, e, ctx);
                break;
            case Point point when _doc.Header.PointDisplayMode != 1:
            {
                var at = ctx.World.Map(Flat(point.Location));
                Marks.Add(new Stroke([at], false, ColorOf(e.Color, layer, ctx), WeightOf(e.LineWeight, layer, ctx) / 100.0, null, 0));
                _marks++;
                break;
            }
            case Hatch hatch:
                DrawHatch(hatch, ctx);
                break;
            case Wipeout wipeout:
                Fill([Clip(wipeout)], e, ctx, SKColors.White);
                break;
            case RasterImage image:
                DrawImage(image, ctx);
                break;
            case AttributeDefinition:
                // A block's template for its attributes: what is shown is each insert's own attribute.
                break;
            case TextEntity text:
                DrawText(text, ctx);
                break;
            case MText mtext:
                DrawMText(mtext, ctx);
                break;
            case Dimension dimension:
                DrawDimension(dimension, ctx);
                break;
            case Leader leader:
                DrawLeader(leader, ctx);
                break;
            case MultiLeader multi:
                DrawMultiLeader(multi, ctx);
                break;
            case MLine mline:
                DrawMLine(mline, ctx);
                break;
            case Insert insert:
                DrawInsert(insert, ctx);
                break;
        }
    }

    private void DrawInsert(Insert insert, Context ctx)
    {
        if (ctx.Depth > 24 || insert.Block is not { } block)
            return;
        var layer = LayerOf(insert, ctx);
        var plane = Plane(insert.Normal);
        var basePoint = block.BlockEntity?.BasePoint ?? XYZ.Zero;
        var color = ColorOf(insert.Color, layer, ctx);
        var weight = WeightOf(insert.LineWeight, layer, ctx);
        var lineType = LineTypeOf(insert, layer, ctx);
        var rows = Math.Max((int)insert.RowCount, 1);
        var columns = Math.Max((int)insert.ColumnCount, 1);
        var entities = Sorted(block).ToList();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                // Where the block's own origin goes: the insert point (in the insert's plane), turned and scaled.
                var place = plane
                    .After(Affine.Move(insert.InsertPoint.X, insert.InsertPoint.Y))
                    .After(Affine.Rotate(insert.Rotation))
                    .After(Affine.Move(c * insert.ColumnSpacing, r * insert.RowSpacing))
                    .After(Affine.Scale(insert.XScale, insert.YScale))
                    .After(Affine.Move(-basePoint.X, -basePoint.Y));
                Walk(entities, new Context(ctx.World.After(place), color, weight, lineType, layer, ctx.Depth + 1));
            }
        }
        // Attributes are where the insert put them already.
        foreach (var attribute in insert.Attributes)
        {
            if (!attribute.IsInvisible && !attribute.Flags.HasFlag(AttributeFlags.Hidden))
                Draw(attribute, ctx);
        }
    }

    private void DrawDimension(Dimension dimension, Context ctx)
    {
        var block = dimension.Block;
        if (block is null || !block.Entities.Any())
        {
            try
            {
                dimension.UpdateBlock();
                block = dimension.Block;
            }
            catch (Exception)
            {
                return;
            }
        }
        if (block is null)
            return;
        var layer = LayerOf(dimension, ctx);
        Walk(Sorted(block), ctx with
        {
            Color = ColorOf(dimension.Color, layer, ctx),
            WeightHundredths = WeightOf(dimension.LineWeight, layer, ctx),
            Layer = layer,
            Depth = ctx.Depth + 1,
        });
    }

    private void DrawHatch(Hatch hatch, Context ctx)
    {
        var plane = Plane(hatch.Normal);
        IEnumerable<Pt> Ring(Hatch.BoundaryPath path) => path.GetPoints(Bulge).Select(p => plane.Map(Flat(p)));
        if (hatch.IsSolid || hatch.GradientColor is { Enabled: true })
        {
            SKColor? color = null;
            if (hatch.GradientColor is { Enabled: true, Colors.Count: > 0 } gradient)
            {
                var first = gradient.Colors[0].Color;
                color = new SKColor(first.R, first.G, first.B);
            }
            Fill(hatch.Paths.Select(Ring), hatch, ctx, color);
            return;
        }
        var count = 0;
        foreach (var line in hatch.ExplodePattern().OfType<Line>())
        {
            if (++count > 200_000)
                break;
            Line([plane.Map(Flat(line.StartPoint)), plane.Map(Flat(line.EndPoint))], false, hatch, ctx);
        }
    }

    private static IEnumerable<Pt> Clip(CadWipeoutBase image)
    {
        // The clip boundary is in pixels, from the picture's corner; two corners make a rectangle.
        var u = new Pt(image.UVector.X, image.UVector.Y);
        var v = new Pt(image.VVector.X, image.VVector.Y);
        var origin = Flat(image.InsertPoint);
        var corners = image.ClipBoundaryVertices.Count switch
        {
            0 or 1 => [new XY(-0.5, -0.5), new XY(image.Size.X - 0.5, -0.5), new XY(image.Size.X - 0.5, image.Size.Y - 0.5), new XY(-0.5, image.Size.Y - 0.5)],
            2 => [image.ClipBoundaryVertices[0], new XY(image.ClipBoundaryVertices[1].X, image.ClipBoundaryVertices[0].Y), image.ClipBoundaryVertices[1], new XY(image.ClipBoundaryVertices[0].X, image.ClipBoundaryVertices[1].Y)],
            _ => image.ClipBoundaryVertices,
        };
        // Pixel rows count down from the top; the picture's V runs up from its bottom.
        return corners.Select(c => origin + u * (c.X + 0.5) + v * (image.Size.Y - (c.Y + 0.5)));
    }

    private void DrawImage(RasterImage image, Context ctx)
    {
        if (image.Definition?.FileName is not { Length: > 0 } name)
            return;
        if (!_pictures.TryGetValue(name, out var bytes))
        {
            bytes = null;
            foreach (var candidate in new[] { name, Path.Combine(_folder, name), Path.Combine(_folder, Path.GetFileName(name)) })
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        bytes = File.ReadAllBytes(candidate);
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
            _pictures[name] = bytes;
        }
        if (bytes is null)
            return;
        var u = new Pt(image.UVector.X, image.UVector.Y) * image.Size.X;
        var v = new Pt(image.VVector.X, image.VVector.Y) * image.Size.Y;
        var origin = Flat(image.InsertPoint) - new Pt(image.UVector.X, image.UVector.Y) * 0.5 - new Pt(image.VVector.X, image.VVector.Y) * 0.5;
        Marks.Add(new Picture(bytes, ctx.World.After(new Affine(u.X, u.Y, v.X, v.Y, origin.X, origin.Y))));
    }

    // ───────────── text ─────────────

    /// <summary>A TEXT's codes: %%c, %%d and %%p as their signs, %%nnn as a character, the over- and underline switches left out.</summary>
    internal static string Plain(string value)
    {
        if (!value.Contains("%%", StringComparison.Ordinal))
            return value;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && value[i + 1] == '%')
            {
                var code = char.ToLowerInvariant(value[i + 2]);
                switch (code)
                {
                    case 'c': sb.Append('Ø'); i += 2; continue;
                    case 'd': sb.Append('°'); i += 2; continue;
                    case 'p': sb.Append('±'); i += 2; continue;
                    case 'u' or 'o' or 'k': i += 2; continue;
                    case '%': sb.Append('%'); i += 2; continue;
                }
                if (i + 4 < value.Length && int.TryParse(value.AsSpan(i + 2, 3), out var n))
                {
                    sb.Append((char)n);
                    i += 4;
                    continue;
                }
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    private static string? FontOf(TextStyle? style) =>
        style is null ? null : !string.IsNullOrWhiteSpace(style.Filename) ? style.Filename : style.Name;

    private void DrawText(TextEntity text, Context ctx)
    {
        var value = Plain(text.Value ?? "");
        if (value.Length == 0 || text.Height <= 0 && (text.Style?.Height ?? 0) <= 0)
            return;
        var layer = LayerOf(text, ctx);
        var plane = Plane(text.Normal);
        var height = text.Height > 0 ? text.Height : text.Style!.Height;
        var h = text.HorizontalAlignment;
        var v = text.VerticalAlignment;
        var aligned = h is TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit;
        var anchor = h == TextHorizontalAlignment.Left && v == TextVerticalAlignmentType.Baseline || aligned ? text.InsertPoint : text.AlignmentPoint;
        var rotation = text.Rotation;
        var fit = 0.0;
        if (aligned)
        {
            var d = new Pt(text.AlignmentPoint.X - text.InsertPoint.X, text.AlignmentPoint.Y - text.InsertPoint.Y);
            if (d.Length > 0)
            {
                rotation = Math.Atan2(d.Y, d.X);
                fit = d.Length;
            }
        }
        var horizontal = h switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Middle => 1,
            TextHorizontalAlignment.Right => 2,
            _ => 0,
        };
        var vertical = h == TextHorizontalAlignment.Middle ? 2 : (int)v;
        var mirror = Affine.Scale(text.Mirror.HasFlag(TextMirrorFlag.Backward) ? -1 : 1, text.Mirror.HasFlag(TextMirrorFlag.UpsideDown) ? -1 : 1);
        var width = text.WidthFactor > 0 ? text.WidthFactor : 1;
        var oblique = Math.Tan(text.ObliqueAngle);
        var local = Affine.Move(anchor.X, anchor.Y).After(Affine.Rotate(rotation)).After(mirror).After(new Affine(width, 0, oblique, 1, 0, 0));
        Marks.Add(new Words([value], ctx.World.After(plane).After(local), height, horizontal, vertical, FontOf(text.Style), ColorOf(text.Color, layer, ctx),
            Fit: fit / Math.Max(width, 1e-9), KeepHeight: h == TextHorizontalAlignment.Fit));
        _marks++;
    }

    private void DrawMText(MText mtext, Context ctx)
    {
        string[] lines;
        try
        {
            lines = mtext.GetPlainTextLines();
        }
        catch (Exception)
        {
            lines = (mtext.Value ?? "").Replace("\\P", "\n", StringComparison.Ordinal).Split('\n');
        }
        lines = [.. lines.Select(Plain)];
        if (lines.All(string.IsNullOrWhiteSpace) || mtext.Height <= 0)
            return;
        var layer = LayerOf(mtext, ctx);
        var rotation = mtext.Rotation;
        if (rotation == 0 && (mtext.AlignmentPoint.X != 0 || mtext.AlignmentPoint.Y != 0))
            rotation = Math.Atan2(mtext.AlignmentPoint.Y, mtext.AlignmentPoint.X);
        var attach = Math.Clamp((int)mtext.AttachmentPoint, 1, 9) - 1;
        var place = Affine.Move(mtext.InsertPoint.X, mtext.InsertPoint.Y).After(Affine.Rotate(rotation));
        Marks.Add(new Words(lines, ctx.World.After(place), mtext.Height, attach % 3, 3 - attach / 3, FontOf(mtext.Style), ColorOf(mtext.Color, layer, ctx),
            LineSpacing: mtext.LineSpacing > 0 ? mtext.LineSpacing : 1, Wrap: mtext.RectangleWidth > 0 ? mtext.RectangleWidth : 0));
        _marks++;
    }

    // ───────────── leaders and multilines ─────────────

    private void Arrow(Pt tip, Pt from, double size, Entity e, Context ctx, SKColor? color = null)
    {
        var d = tip - from;
        if (size <= 0 || d.Length == 0)
            return;
        var dir = d * (1 / d.Length);
        var side = new Pt(-dir.Y, dir.X) * (size / 6);
        var back = tip - dir * size;
        Fill([[tip, back + side, back - side]], e, ctx, color);
    }

    private void DrawLeader(Leader leader, Context ctx)
    {
        var points = leader.Vertices.Select(Flat).ToList();
        if (points.Count < 2)
            return;
        Line(points, false, leader, ctx);
        if (leader.ArrowHeadEnabled && leader.Style is { } style)
            Arrow(points[0], points[1], style.ArrowSize * (style.ScaleFactor > 0 ? style.ScaleFactor : 1), leader, ctx);
    }

    private void DrawMultiLeader(MultiLeader multi, Context ctx)
    {
        if (multi.ContextData is not { } data)
            return;
        var layer = LayerOf(multi, ctx);
        foreach (var root in data.LeaderRoots)
        {
            foreach (var line in root.Lines)
            {
                var points = line.Points.Select(Flat).Append(Flat(root.ConnectionPoint)).ToList();
                if (points.Count < 2)
                    continue;
                Line(points, false, multi, ctx);
                Arrow(points[0], points[1], line.ArrowheadSize > 0 ? line.ArrowheadSize : data.ArrowheadSize, multi, ctx);
            }
            if (root.LandingDistance > 0 && root.Direction.X is var dx && root.Direction.Y is var dy && (dx != 0 || dy != 0))
                Line([Flat(root.ConnectionPoint), Flat(root.ConnectionPoint) + new Pt(dx, dy) * root.LandingDistance], false, multi, ctx);
        }
        if (!string.IsNullOrWhiteSpace(data.TextLabel) && data.TextHeight > 0)
        {
            var label = new MText { Value = data.TextLabel };
            string[] lines;
            try { lines = label.GetPlainTextLines(); }
            catch (Exception) { lines = data.TextLabel.Split("\\P"); }
            var place = Affine.Move(data.TextLocation.X, data.TextLocation.Y).After(Affine.Rotate(data.TextRotation));
            var color = data.TextColor.IsByBlock || data.TextColor.IsByLayer ? ColorOf(multi.Color, layer, ctx) : ColorOf(data.TextColor, layer, ctx);
            Marks.Add(new Words([.. lines.Select(Plain)], ctx.World.After(place), data.TextHeight, 0, 3, null, color));
            _marks++;
        }
    }

    private void DrawMLine(MLine mline, Context ctx)
    {
        if (mline.Vertices.Count < 2)
            return;
        var elements = mline.Vertices.Max(v => v.Segments.Count);
        for (var i = 0; i < elements; i++)
        {
            var points = mline.Vertices
                .Where(v => i < v.Segments.Count && v.Segments[i].Parameters.Count > 0)
                .Select(v => Flat(v.Position) + new Pt(v.Miter.X, v.Miter.Y) * v.Segments[i].Parameters[0])
                .ToList();
            if (points.Count >= 2)
                Line(points, mline.Flags.HasFlag(MLineFlags.Closed), mline, ctx);
        }
    }
}
