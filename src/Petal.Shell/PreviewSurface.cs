using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

namespace Petal.Shell;

/// <summary>What the preview pane shows, in the colours File Explorer gives it (light or dark).</summary>
internal abstract class PreviewSurface : UserControl
{
    protected PreviewSurface(Color background, Color text)
    {
        BackColor = background;
        ForeColor = text;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9f);
    }

    /// <summary>The surface for a file's bytes, told by what they hold.</summary>
    public static PreviewSurface For(byte[] bytes, Color background, Color text)
    {
        try
        {
            if (bytes.Length > 8)
            {
                if (PdfPages.Looks(bytes, Math.Min(bytes.Length, 1024)) && PdfPages.Render(bytes, 0, 64, out int pages) is { } first)
                {
                    first.Dispose();
                    return new PagesSurface(Math.Min(pages, 500), (page, width) => PdfPages.Render(bytes, page, width), background, text);
                }
                if (Mesh.LooksLikeModel(bytes, Math.Min(bytes.Length, 1024), bytes.Length) && Mesh.Read(bytes) is { } mesh)
                    return new ModelSurface(mesh, background, text);
                if (Comic(bytes) is { Count: > 1 } comic)
                    return new PagesSurface(comic.Count, (page, width) => Picture(comic[page], width), background, text);
                using var stream = new MemoryStream(bytes);
                var picture = Previews.Read(stream, 2000);
                var layers = bytes[0] == '8' && bytes[1] == 'B' && bytes[2] == 'P' && bytes[3] == 'S' ? PsdLayers.Read(new MemoryStream(bytes)) : [];
                if (picture is not null)
                    return new PictureSurface(picture, layers, background, text);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OutOfMemoryException)
        {
        }
        return new NothingSurface(background, text);
    }

    /// <summary>Waits until whatever is drawn after the surface is made has been drawn (for a test's picture).</summary>
    public virtual void Settle()
    {
    }

    /// <summary>A comic book's pages, in their names' order; null when the bytes aren't one.</summary>
    private static List<byte[]>? Comic(byte[] bytes)
    {
        if (bytes[0] != 'P' || bytes[1] != 'K')
            return null;
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        bool Picture(string name) => Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp";
        if (zip.Entries.Any(e => e.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase)) || zip.Entries.Count(e => e.Length > 0 && !Picture(e.FullName)) > 3)
            return null;
        return zip.Entries.Where(e => e.Length > 0 && Picture(e.FullName) && !e.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).OrderBy(e => Number(e.FullName)).Take(500)
            .Select(e =>
            {
                using var inner = e.Open();
                using var copy = new MemoryStream();
                inner.CopyTo(copy);
                return copy.ToArray();
            }).ToList();
    }

    private static long Number(string name)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(name).Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length is > 0 and < 18 ? long.Parse(digits) : long.MaxValue;
    }

    private static Bitmap? Picture(byte[] bytes, int width) => EmbeddedPreview.Decode(bytes) is { } picture ? EmbeddedPreview.Fit(picture, width) : null;
}

/// <summary>Nothing to show: the pane says so quietly.</summary>
internal sealed class NothingSurface : PreviewSurface
{
    public NothingSurface(Color background, Color text) : base(background, text)
    {
        Controls.Add(new Label { Text = "—", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = text });
    }
}

/// <summary>A picture fitted to the pane, and a Photoshop file's layers beside it (hidden ones greyed, groups indented).</summary>
internal sealed class PictureSurface : PreviewSurface
{
    private readonly Bitmap _picture;
    private readonly List<PsdLayer> _layers;
    private readonly Panel _canvas = new() { Dock = DockStyle.Fill };

    public PictureSurface(Bitmap picture, List<PsdLayer> layers, Color background, Color text) : base(background, text)
    {
        _picture = picture;
        _layers = layers;
        _canvas.Paint += (_, e) => Draw(e.Graphics);
        _canvas.Resize += (_, _) => _canvas.Invalidate();
        typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(_canvas, true);
        Controls.Add(_canvas);
        if (layers.Count > 0)
        {
            var list = new ListBox
            {
                Dock = DockStyle.Right,
                Width = 200,
                BorderStyle = BorderStyle.None,
                BackColor = Blend(background, text, 0.05),
                ForeColor = text,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 22,
                IntegralHeight = false,
                SelectionMode = SelectionMode.None,
            };
            foreach (var layer in layers)
                list.Items.Add(layer);
            list.DrawItem += (_, e) =>
            {
                if (e.Index < 0)
                    return;
                var layer = (PsdLayer)list.Items[e.Index];
                e.Graphics.FillRectangle(new SolidBrush(list.BackColor), e.Bounds);
                var colour = layer.Visible ? text : Blend(text, background, 0.55);
                var bounds = new Rectangle(e.Bounds.X + 10 + layer.Depth * 14, e.Bounds.Y, e.Bounds.Width - 12 - layer.Depth * 14, e.Bounds.Height);
                var font = layer.Group ? new Font(Font, FontStyle.Bold) : Font;
                TextRenderer.DrawText(e.Graphics, (layer.Group ? "▸ " : "") + layer.Name, font, bounds, colour, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            Controls.Add(list);
        }
    }

    private void Draw(Graphics g)
    {
        g.Clear(BackColor);
        var area = new Rectangle(12, 12, Math.Max(1, _canvas.Width - 24), Math.Max(1, _canvas.Height - 24));
        double scale = Math.Min(1.0, Math.Min((double)area.Width / _picture.Width, (double)area.Height / _picture.Height));
        int w = (int)(_picture.Width * scale), h = (int)(_picture.Height * scale);
        var place = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        // A soft checkerboard under a picture with clear parts.
        using (var light = new SolidBrush(Blend(BackColor, Color.Gray, 0.12)))
        using (var dark = new SolidBrush(Blend(BackColor, Color.Gray, 0.25)))
        {
            g.SetClip(place);
            for (int y = place.Top; y < place.Bottom; y += 8)
                for (int x = place.Left; x < place.Right; x += 8)
                    g.FillRectangle(((x - place.Left) / 8 + (y - place.Top) / 8) % 2 == 0 ? light : dark, x, y, 8, 8);
            g.ResetClip();
        }
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(_picture, place);
    }

    internal static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _picture.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Pages one under another, as wide as the pane, drawn as they come into view (and let go of when far), a PDF's by
/// PDFium, a comic book's from its pictures.
/// </summary>
internal sealed class PagesSurface : PreviewSurface
{
    private const int Gap = 10;
    private readonly int _count;
    private readonly Func<int, int, Bitmap?> _render;
    private readonly Dictionary<int, Bitmap> _drawn = [];
    private readonly Dictionary<int, double> _ratio = [];
    private readonly VScrollBar _bar = new() { Dock = DockStyle.Right };
    private int _width = -1;

    public PagesSurface(int count, Func<int, int, Bitmap?> render, Color background, Color text) : base(background, text)
    {
        _count = count;
        _render = render;
        BackColor = PictureSurface.Blend(background, Color.Gray, 0.18);
        Controls.Add(_bar);
        _bar.Scroll += (_, _) => Invalidate();
        Resize += (_, _) => Lay();
        MouseWheel += (_, e) =>
        {
            _bar.Value = Math.Max(_bar.Minimum, Math.Min(_bar.Maximum - _bar.LargeChange + 1, _bar.Value - e.Delta));
            Invalidate();
        };
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    private int PageWidth => Math.Max(50, Width - _bar.Width - 2 * Gap);

    /// <summary>A page's height for its width: its own proportion once drawn, the first page's before.</summary>
    private int HeightOf(int page) => (int)(PageWidth * (_ratio.TryGetValue(page, out var r) ? r : _ratio.TryGetValue(0, out var first) ? first : 1.414));

    private void Lay()
    {
        if (PageWidth != _width)
        {
            foreach (var picture in _drawn.Values)
                picture.Dispose();
            _drawn.Clear();
            _width = PageWidth;
        }
        int total = Gap + Enumerable.Range(0, _count).Sum(p => HeightOf(p) + Gap);
        _bar.Minimum = 0;
        _bar.LargeChange = Math.Max(1, Height);
        _bar.SmallChange = 40;
        _bar.Maximum = Math.Max(0, total);
        _bar.Enabled = total > Height;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int top = Gap - _bar.Value;
        for (int page = 0; page < _count; page++)
        {
            int height = HeightOf(page);
            bool near = top + height > -Height && top < 2 * Height;
            if (near && !_drawn.ContainsKey(page) && _render(page, PageWidth) is { } picture)
            {
                _drawn[page] = picture;
                double ratio = (double)picture.Height / picture.Width;
                if (!_ratio.TryGetValue(page, out var old) || Math.Abs(old - ratio) > 0.001)
                {
                    _ratio[page] = ratio;
                    BeginInvoke(new Action(Lay));
                }
            }
            else if (!near && _drawn.TryGetValue(page, out var far))
            {
                far.Dispose();
                _drawn.Remove(page);
            }
            if (top + height > 0 && top < Height)
            {
                var place = new Rectangle(Gap, top, PageWidth, height);
                if (_drawn.TryGetValue(page, out var shown))
                    g.DrawImage(shown, place);
                else
                    g.FillRectangle(Brushes.White, place);
            }
            top += height + Gap;
        }
    }

    public override void Settle()
    {
        Lay();
        Refresh();
        Application.DoEvents();
        Lay();
        Refresh();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var picture in _drawn.Values)
                picture.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A 3D model to turn: dragging with the mouse turns it about its middle and tilts it.</summary>
internal sealed class ModelSurface : PreviewSurface
{
    private readonly Mesh _mesh;
    private double _yaw = -35, _pitch = 25;
    private Point? _from;
    private Bitmap? _shown;

    public ModelSurface(Mesh mesh, Color background, Color text) : base(background, text)
    {
        _mesh = mesh;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Resize += (_, _) => Redraw(full: true);
        MouseDown += (_, e) => _from = e.Location;
        MouseMove += (_, e) =>
        {
            if (_from is not { } from || e.Button != MouseButtons.Left)
                return;
            _yaw += (e.X - from.X) * 0.6;
            _pitch = Math.Max(-89, Math.Min(89, _pitch + (e.Y - from.Y) * 0.6));
            _from = e.Location;
            // Quicker while turning; sharp again once let go.
            Redraw(full: false);
        };
        MouseUp += (_, _) =>
        {
            _from = null;
            Redraw(full: true);
        };
        Cursor = Cursors.SizeAll;
    }

    private void Redraw(bool full)
    {
        int size = Math.Max(16, Math.Min(Width, Height) - 24);
        _shown?.Dispose();
        _shown = _mesh.Render(full ? size : Math.Max(16, size / 2), _yaw, _pitch);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_shown is null)
            return;
        int size = Math.Max(16, Math.Min(Width, Height) - 24);
        double scale = (double)size / Math.Max(_shown.Width, _shown.Height);
        int w = (int)(_shown.Width * scale), h = (int)(_shown.Height * scale);
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(_shown, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));
        TextRenderer.DrawText(e.Graphics, _mesh.TriangleCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " △", Font, new Point(10, Height - 22), PictureSurface.Blend(ForeColor, BackColor, 0.4));
    }

    public override void Settle() => Redraw(full: true);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _shown?.Dispose();
        base.Dispose(disposing);
    }
}
