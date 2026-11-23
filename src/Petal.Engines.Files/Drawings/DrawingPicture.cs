using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Petal.Engines.Files.Drawings;

/// <summary>
/// A drawing put on paper: as a vector PDF or SVG (lines stay lines, text stays text you can search) or as a PNG,
/// scaled to fit a sheet whose longest side is 420 mm (A3), with line weights in millimetres as a plotter draws them,
/// dashes by each line type, hatches, fills, pictures, and text in its font (shaped, so Arabic joins), on white.
/// </summary>
internal sealed class DrawingPicture : IDisposable
{
    public enum Kind { Png, Pdf, Svg }

    /// <summary>The sheet: the drawing's longest side, and the white around it, in millimetres.</summary>
    private const double Longest = 400, Margin = 10;

    /// <summary>A PNG's longest side, in pixels.</summary>
    private const int PngLongest = 4000;

    /// <summary>The thinnest line, in millimetres (a weight of 0 is a plotter's thinnest).</summary>
    private const double Hairline = 0.05;

    private const float Reference = 100;

    private readonly Drawing _drawing;

    /// <summary>Text that needs shaping drawn as outlines (for an SVG).</summary>
    private bool _outlines;
    private readonly Dictionary<string, SKTypeface> _faces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SKTypeface, SKShaper> _shapers = [];
    private readonly Dictionary<Words, List<(string Text, SKTypeface Face, double X, double Y, double ScaleX, double ScaleY)>> _layout = [];

    public DrawingPicture(Drawing drawing) => _drawing = drawing;

    public void Dispose()
    {
        foreach (var shaper in _shapers.Values)
            shaper.Dispose();
        foreach (var face in _faces.Values.Distinct())
            face.Dispose();
    }

    public static void Write(Drawing drawing, string path, Kind kind)
    {
        using var picture = new DrawingPicture(drawing);
        picture.Save(path, kind);
    }

    // ───────────── text ─────────────

    private SKTypeface Face(string? font)
    {
        var key = font ?? "";
        if (_faces.TryGetValue(key, out var known))
            return known;
        SKTypeface? face = null;
        var name = font?.Trim() ?? "";
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is ".ttf" or ".otf" or ".ttc")
        {
            foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Fonts), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts") })
            {
                var file = Path.Combine(folder, Path.GetFileName(name));
                if (File.Exists(file) && SKTypeface.FromFile(file) is { } fromFile)
                {
                    face = fromFile;
                    break;
                }
            }
            name = Path.GetFileNameWithoutExtension(name);
        }
        // SHX fonts are the CAD program's own strokes; a plain sans font stands in for them.
        if (face is null && extension is not ".shx" && name.Length > 0 && SKTypeface.FromFamilyName(name) is { } family
            && string.Equals(family.FamilyName, name, StringComparison.OrdinalIgnoreCase))
            face = family;
        face ??= SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        _faces[key] = face;
        return face;
    }

    /// <summary>The font that has the line's letters: the style's own, else one Windows has for its first missing letter.</summary>
    private SKTypeface FaceFor(string text, SKTypeface face)
    {
        using var font = new SKFont(face, Reference);
        if (font.ContainsGlyphs(text))
            return face;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || font.ContainsGlyphs(rune.ToString()))
                continue;
            var key = "fallback:" + rune.Value;
            if (!_faces.TryGetValue(key, out var other))
            {
                other = SKFontManager.Default.MatchCharacter(face.FamilyName, face.FontStyle, null, rune.Value) ?? face;
                _faces[key] = other;
            }
            return other;
        }
        return face;
    }

    private SKShaper Shaper(SKTypeface face)
    {
        if (!_shapers.TryGetValue(face, out var shaper))
            _shapers[face] = shaper = new SKShaper(face);
        return shaper;
    }

    private static double CapRatio(SKTypeface face)
    {
        using var font = new SKFont(face, Reference);
        var cap = font.Metrics.CapHeight;
        return cap > 1 ? cap / Reference : 0.716;
    }

    private double Width(string text, SKTypeface face)
    {
        using var font = new SKFont(face, Reference);
        return Shaper(face).Shape(text, font).Width;
    }

    /// <summary>
    /// Each line of a text where it goes in the text's own space: its start and baseline, and how much its letters are
    /// stretched, measured in the text's own font.
    /// </summary>
    private List<(string Text, SKTypeface Face, double X, double Y, double ScaleX, double ScaleY)> Layout(Words words)
    {
        if (_layout.TryGetValue(words, out var known))
            return known;
        var styleFace = Face(words.Font);
        // From the font's units at the reference size to the drawing's: a capital letter is the text's height.
        var k = words.Height / (CapRatio(styleFace) * Reference);
        var lines = new List<(string Text, SKTypeface Face, double Width)>();
        foreach (var raw in words.Lines)
        {
            var line = raw.Replace('\t', ' ');
            if (words.Wrap > 0)
            {
                var current = "";
                foreach (var word in line.Split(' '))
                {
                    var tryLine = current.Length == 0 ? word : current + " " + word;
                    var face = FaceFor(tryLine, styleFace);
                    if (current.Length > 0 && Width(tryLine, face) * k > words.Wrap * 1.001)
                    {
                        var f = FaceFor(current, styleFace);
                        lines.Add((current, f, Width(current, f) * k));
                        current = word;
                    }
                    else
                        current = tryLine;
                }
                var last = FaceFor(current, styleFace);
                lines.Add((current, last, Width(current, last) * k));
            }
            else
            {
                var face = FaceFor(line, styleFace);
                lines.Add((line, face, Width(line, face) * k));
            }
        }

        var result = new List<(string, SKTypeface, double, double, double, double)>();
        var pitch = words.Height * 5 / 3 * words.LineSpacing;
        var total = words.Height + (lines.Count - 1) * pitch;
        var firstBaseline = words.Vertical switch
        {
            0 => 0.0,
            1 => lines.Count == 1 ? words.Height * 0.3 : total - words.Height,
            2 => total / 2 - words.Height,
            _ => -words.Height,
        };
        for (var i = 0; i < lines.Count; i++)
        {
            var (text, face, width) = lines[i];
            double sx = k, sy = k;
            if (words.Fit > 0 && width > 0)
            {
                // An aligned TEXT grows or shrinks whole between its two points; a fit one only along the line.
                sx = k * words.Fit / width;
                if (!words.KeepHeight)
                    sy = sx;
                width = words.Fit;
            }
            var x = words.Fit > 0 ? 0 : words.Horizontal switch { 1 => -width / 2, 2 => -width, _ => 0 };
            result.Add((text, face, x, firstBaseline - i * pitch, sx, sy));
        }
        _layout[words] = result;
        return result;
    }

    // ───────────── extent ─────────────

    private (Pt Min, Pt Max) Extent()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Add(Pt p, double grow = 0)
        {
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y))
                return;
            minX = Math.Min(minX, p.X - grow); maxX = Math.Max(maxX, p.X + grow);
            minY = Math.Min(minY, p.Y - grow); maxY = Math.Max(maxY, p.Y + grow);
        }
        foreach (var mark in _drawing.Marks)
        {
            switch (mark)
            {
                case Stroke s:
                    foreach (var p in s.Points) Add(p, s.Width / 2);
                    break;
                case Area a:
                    foreach (var ring in a.Rings) foreach (var p in ring) Add(p);
                    break;
                case Picture pic:
                    foreach (var corner in new Pt[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) }) Add(pic.Place.Map(corner));
                    break;
                case Words w:
                    foreach (var (text, face, x, y, sx, sy) in Layout(w))
                    {
                        var width = Width(text, face) * sx;
                        var up = w.Height;
                        foreach (var corner in new Pt[] { new(x, y - up * 0.3), new(x + width, y - up * 0.3), new(x + width, y + up * 1.1), new(x, y + up * 1.1) })
                            Add(w.Place.Map(corner));
                    }
                    break;
            }
        }
        if (minX > maxX)
            throw new InvalidDataException("This drawing has nothing DropWheel can draw.");
        // A drawing of one straight line has no height: give it some room.
        var size = Math.Max(maxX - minX, maxY - minY);
        if (size <= 0) size = 1;
        if (maxX - minX < size * 1e-3) { minX -= size * 5e-4; maxX += size * 5e-4; }
        if (maxY - minY < size * 1e-3) { minY -= size * 5e-4; maxY += size * 5e-4; }
        return (new Pt(minX, minY), new Pt(maxX, maxY));
    }

    // ───────────── paper ─────────────

    private void Save(string path, Kind kind)
    {
        var (min, max) = Extent();
        var w = max.X - min.X;
        var h = max.Y - min.Y;
        // Millimetres on paper per drawing unit, and device units per millimetre.
        var mmPerUnit = Longest / Math.Max(w, h);
        var unitsPerMm = kind switch
        {
            Kind.Pdf => 72 / 25.4,
            Kind.Svg => 96 / 25.4,
            _ => PngLongest / (Longest + 2 * Margin),
        };
        var pageW = (w * mmPerUnit + 2 * Margin) * unitsPerMm;
        var pageH = (h * mmPerUnit + 2 * Margin) * unitsPerMm;
        var a = mmPerUnit * unitsPerMm;
        var device = new Affine(a, 0, 0, -a, Margin * unitsPerMm - min.X * a, Margin * unitsPerMm + max.Y * a);
        var paint = new Paper(device, unitsPerMm, a);

        using var output = File.Create(path);
        switch (kind)
        {
            case Kind.Pdf:
            {
                var metadata = new SKDocumentPdfMetadata { Title = _drawing.Name, Creator = "DropWheel", Producer = "DropWheel", RasterDpi = 300 };
                using var document = SKDocument.CreatePdf(output, metadata);
                var canvas = document.BeginPage((float)pageW, (float)pageH);
                Draw(canvas, paint);
                document.EndPage();
                document.Close();
                break;
            }
            case Kind.Svg:
            {
                _outlines = true;
                using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, (float)pageW, (float)pageH), output))
                    Draw(canvas, paint);
                break;
            }
            default:
            {
                var width = Math.Max(16, (int)Math.Ceiling(pageW));
                var height = Math.Max(16, (int)Math.Ceiling(pageH));
                using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                    ?? throw new InvalidOperationException("There isn't memory enough for a picture this size.");
                surface.Canvas.Clear(SKColors.White);
                Draw(surface.Canvas, paint);
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                data.SaveTo(output);
                break;
            }
        }
    }

    private sealed record Paper(Affine Device, double UnitsPerMm, double UnitsPerDrawingUnit);

    private static SKMatrix Matrix(Affine m) => new()
    {
        ScaleX = (float)m.A, SkewX = (float)m.C, TransX = (float)m.E,
        SkewY = (float)m.B, ScaleY = (float)m.D, TransY = (float)m.F,
        Persp2 = 1,
    };

    private void Draw(SKCanvas canvas, Paper paper)
    {
        var bitmaps = new Dictionary<byte[], SKBitmap?>();
        try
        {
            foreach (var mark in _drawing.Marks)
            {
                switch (mark)
                {
                    case Stroke s:
                        DrawStroke(canvas, paper, s);
                        break;
                    case Area area:
                    {
                        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                        foreach (var ring in area.Rings)
                        {
                            var p = paper.Device.Map(ring[0]);
                            path.MoveTo((float)p.X, (float)p.Y);
                            for (var i = 1; i < ring.Length; i++)
                            {
                                p = paper.Device.Map(ring[i]);
                                path.LineTo((float)p.X, (float)p.Y);
                            }
                            path.Close();
                        }
                        using var fill = new SKPaint { Color = area.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
                        canvas.DrawPath(path, fill);
                        break;
                    }
                    case Picture picture:
                    {
                        if (!bitmaps.TryGetValue(picture.Bytes, out var bitmap))
                            bitmaps[picture.Bytes] = bitmap = SKBitmap.Decode(picture.Bytes);
                        if (bitmap is null)
                            break;
                        // The picture's pixels (y down) onto its unit square (y up).
                        var place = paper.Device.After(picture.Place).After(new Affine(1.0 / bitmap.Width, 0, 0, -1.0 / bitmap.Height, 0, 1));
                        canvas.Save();
                        canvas.SetMatrix(Matrix(place));
                        using var image = SKImage.FromBitmap(bitmap);
                        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                        canvas.Restore();
                        break;
                    }
                    case Words words:
                        DrawWords(canvas, paper, words);
                        break;
                }
            }
        }
        finally
        {
            foreach (var bitmap in bitmaps.Values)
                bitmap?.Dispose();
        }
    }

    private static void DrawStroke(SKCanvas canvas, Paper paper, Stroke s)
    {
        var weight = Math.Max(s.WeightMm, Hairline) * paper.UnitsPerMm;
        var width = Math.Max(weight, s.Width * paper.UnitsPerDrawingUnit);
        using var stroke = new SKPaint
        {
            Color = s.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)width,
            StrokeJoin = SKStrokeJoin.Round, StrokeCap = s.Width > 0 ? SKStrokeCap.Butt : SKStrokeCap.Round,
        };
        if (s.Points.Length == 1)
        {
            var p = paper.Device.Map(s.Points[0]);
            using var dot = new SKPaint { Color = s.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawCircle((float)p.X, (float)p.Y, (float)Math.Max(weight, paper.UnitsPerMm * 0.2) / 2, dot);
            return;
        }
        if (s.Dashes is { } dashes)
        {
            var device = dashes.Select(d => d * paper.UnitsPerDrawingUnit).ToArray();
            // Dashes too fine to see on paper (under a millimetre a pattern) are drawn as a plain line.
            if (device.Sum() >= paper.UnitsPerMm * 1.0)
            {
                var intervals = device.Select(d => (float)Math.Max(d, 0.01)).ToArray();
                stroke.PathEffect = SKPathEffect.CreateDash(intervals, 0);
                stroke.StrokeCap = device.Where((_, i) => i % 2 == 0).Any(d => d < weight) ? SKStrokeCap.Round : SKStrokeCap.Butt;
            }
        }
        using var path = new SKPath();
        var first = paper.Device.Map(s.Points[0]);
        path.MoveTo((float)first.X, (float)first.Y);
        for (var i = 1; i < s.Points.Length; i++)
        {
            var p = paper.Device.Map(s.Points[i]);
            path.LineTo((float)p.X, (float)p.Y);
        }
        if (s.Closed)
            path.Close();
        canvas.DrawPath(path, stroke);
    }

    private void DrawWords(SKCanvas canvas, Paper paper, Words words)
    {
        using var paint = new SKPaint { Color = words.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        foreach (var (text, face, x, y, sx, sy) in Layout(words))
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            // The text's space to the page, then the font's (y down, at the reference size) to the text's.
            var place = paper.Device.After(words.Place).After(new Affine(sx, 0, 0, -sy, x, y));
            using var font = new SKFont(face, Reference) { Subpixel = true, Edging = SKFontEdging.Antialias };
            canvas.Save();
            canvas.SetMatrix(Matrix(place));
            if (_outlines && NeedsShaping(text))
            {
                // An SVG places each letter of its text on its own, which keeps Arabic from joining: such text goes as
                // the shapes of its joined letters.
                var shaped = Shaper(face).Shape(text, font);
                using var path = new SKPath();
                for (var i = 0; i < shaped.Codepoints.Length; i++)
                {
                    using var glyph = font.GetGlyphPath((ushort)shaped.Codepoints[i]);
                    if (glyph is not null)
                        path.AddPath(glyph, shaped.Points[i].X, shaped.Points[i].Y);
                }
                canvas.DrawPath(path, paint);
            }
            else
                canvas.DrawShapedText(Shaper(face), text, 0, 0, SKTextAlign.Left, font, paint);
            canvas.Restore();
        }
    }

    /// <summary>Whether a line has letters that join or reorder (Arabic and its scripts, Hebrew, the Indic scripts, Thai).</summary>
    internal static bool NeedsShaping(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '\u0590' and <= '\u08FF' or >= '\u0900' and <= '\u0E7F' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF')
                return true;
        }
        return false;
    }
}
