using ImageMagick;
using ImageMagick.Drawing;

namespace Petal.Engines.Imaging;

/// <summary>What a country asks of a photo: its size, how tall the head is in it (crown to chin), and its background.</summary>
/// <param name="Id">Stable, for remembering the choice.</param>
/// <param name="Country">In English; the interface translates it.</param>
/// <param name="Document">What it is for, when the country asks differently for different papers; otherwise empty.</param>
/// <param name="Background">The colour asked for, as RGB.</param>
/// <param name="HeadStated">The office says how tall the head should be; otherwise it is the usual size for such a photo.</param>
/// <param name="WithoutHair">The office measures the head from the hairline, not over the hair.</param>
public sealed record IdPhotoSpec(string Id, string Country, string Document, double WidthMm, double HeightMm, double HeadMinMm, double HeadMaxMm, uint Background = 0xFFFFFF, bool HeadStated = true, bool WithoutHair = false)
{
    /// <summary>How tall the head is meant to be, in the middle of what is allowed.</summary>
    public double HeadMm => (HeadMinMm + HeadMaxMm) / 2;
}

/// <summary>A sheet to print photos on.</summary>
public sealed record IdPhotoPaper(string Id, string Name, double WidthMm, double HeightMm)
{
    public static readonly IdPhotoPaper Print4By6 = new("4x6", "4×6 in", 152.4, 101.6);
    public static readonly IdPhotoPaper A4 = new("a4", "A4", 210, 297);
    public static readonly IReadOnlyList<IdPhotoPaper> All = [Print4By6, A4];
}

/// <summary>What was found in a photo: where the person's head is.</summary>
public enum IdPhotoProblem
{
    /// <summary>No face was found: the head has to be placed by hand.</summary>
    NoFace,

    /// <summary>More than one face: the largest was taken.</summary>
    SeveralFaces,

    /// <summary>The photo has fewer pixels than the print wants.</summary>
    LowResolution,

    /// <summary>The photo stops above where the crop goes on to: the shoulders are cut, or a side is empty.</summary>
    TooClose,
}

/// <summary>Where the person's head is in the photo, in its pixels.</summary>
/// <param name="Hairline">Where the forehead begins; the crown when that isn't known.</param>
public readonly record struct IdPhotoHead(double CentreX, double Crown, double Chin, double Hairline = -1)
{
    public double Height => Chin - Crown;

    /// <summary>The top of the head as an office measures it.</summary>
    public double Top(IdPhotoSpec spec) => spec.WithoutHair && Hairline >= 0 ? Hairline : Crown;

    public double HeightFor(IdPhotoSpec spec) => Chin - Top(spec);
}

/// <summary>The photo cut to size: where in the picture the print's rectangle lies, in its pixels.</summary>
public readonly record struct IdPhotoCrop(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>A hand's correction of where the head was put.</summary>
/// <param name="Zoom">1 as worked out; above it the head is larger.</param>
/// <param name="Across">Fractions of the print's width to move the person to the right.</param>
/// <param name="Down">Fractions of the print's height to move the person down.</param>
public readonly record struct IdPhotoAdjust(double Zoom = 1, double Across = 0, double Down = 0);

/// <summary>A portrait ready to be cut to any country's size.</summary>
public sealed class IdPhotoSource : IDisposable
{
    public required MagickImage Image { get; init; }

    /// <summary>White where the person is; null when the model wasn't used.</summary>
    public MagickImage? Mask { get; init; }

    public required IdPhotoHead Head { get; init; }

    public required int Faces { get; init; }

    public int Width => (int)Image.Width;

    public int Height => (int)Image.Height;

    public void Dispose()
    {
        Image.Dispose();
        Mask?.Dispose();
    }
}

/// <summary>
/// Photos for passports, visas and residence permits: the person is found, cut from the background, placed so the head
/// is the size that country asks for, and the photo is printed at its exact size, alone and in rows on a sheet.
/// </summary>
public static class IdPhoto
{
    public const int Dpi = 300;

    /// <summary>The gap between photos on a sheet, for the scissors.</summary>
    private const double GapMm = 2;

    /// <summary>Photo labs print to the edge; this much keeps the cuts off it.</summary>
    private const double MarginMm = 1.5;

    /// <summary>How much of a print's spare height goes above the head: the rest of it is shoulders.</summary>
    private const double AboveHead = 0.38;

    public static int Pixels(double mm) => (int)Math.Round(mm / 25.4 * Dpi);

    /// <summary>Finds the person in a photo. With the background model here the top of the hair is seen; without it, it is estimated from the face.</summary>
    public static Task<IdPhotoSource> PrepareAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var image = ImageLoader.Load(path);
            image.AutoOrient();
            image.ColorSpace = ColorSpace.sRGB;
            MagickImage? mask = null;
            try
            {
                using var small = (MagickImage)image.Clone();
                small.Alpha(AlphaAction.Remove);
                double scale = 1;
                if (Math.Max(small.Width, small.Height) > 1600)
                {
                    scale = 1600.0 / Math.Max(small.Width, small.Height);
                    small.Resize((uint)(small.Width * scale), (uint)(small.Height * scale));
                }
                var bgra = small.GetPixels().ToByteArray(PixelMapping.BGRA) ?? throw new InvalidOperationException("The picture has no pixels.");
                var faces = await ImageRedactor.DetectFacesAsync(new BgraBitmap((int)small.Width, (int)small.Height, bgra), cancellationToken);
                if (BackgroundRemover.IsReady)
                    mask = BackgroundRemover.Mask(image, cancellationToken);
                var head = FindHead(faces.Select(f => new PixelRect((int)(f.X / scale), (int)(f.Y / scale), (int)(f.Width / scale), (int)(f.Height / scale))).ToList(),
                    (int)image.Width, (int)image.Height, mask?.GetPixels().ToByteArray("R"));
                return new IdPhotoSource { Image = image, Mask = mask, Head = head, Faces = faces.Count };
            }
            catch
            {
                image.Dispose();
                mask?.Dispose();
                throw;
            }
        }, cancellationToken);

    /// <summary>
    /// Where the head is: across and down to the chin from the largest face; up to the crown from where the person's
    /// outline begins above the face, as the face detector stops at the forehead and says nothing of hair.
    /// </summary>
    /// <param name="faces">As the redactor gives them: 15 % larger than the face on each side.</param>
    internal static IdPhotoHead FindHead(IReadOnlyList<PixelRect> faces, int width, int height, byte[]? mask)
    {
        if (faces.Count == 0)
            return new IdPhotoHead(width / 2.0, height * 0.15, height * 0.65);
        var face = faces.MaxBy(f => f.Width * f.Height);
        // Back to the box the detector gave.
        double w = face.Width / 1.3, h = face.Height / 1.3;
        double left = face.X + face.Width * 0.5 - w / 2, top = face.Y + face.Height * 0.5 - h / 2;
        double centre = left + w / 2;
        double chin = top + h * 1.02;
        double crown = top - h * 0.28;
        if (mask is not null && mask.Length >= width * height)
        {
            int from = Math.Clamp((int)(centre - w * 0.3), 0, width - 1), to = Math.Clamp((int)(centre + w * 0.3), 0, width - 1);
            int limit = Math.Clamp((int)top, 0, height - 1);
            int need = Math.Max(1, (to - from) / 6);
            for (int y = 0; y <= limit; y++)
            {
                int solid = 0;
                for (int x = from; x <= to; x++)
                    if (mask[y * width + x] >= 128)
                        solid++;
                if (solid >= need)
                {
                    // Hair higher than this much above a face is a hat or a raised hand, not a crown.
                    crown = Math.Max(y, top - h * 0.75);
                    break;
                }
            }
        }
        return new IdPhotoHead(centre, Math.Max(0, crown), Math.Min(height, chin), Math.Max(crown, Math.Max(0, top - h * 0.12)));
    }

    /// <summary>The rectangle of the picture that goes into the print, with the head the size it should be and in the middle.</summary>
    public static IdPhotoCrop Layout(IdPhotoHead head, IdPhotoSpec spec, IdPhotoAdjust adjust = default)
    {
        double zoom = adjust.Zoom <= 0 ? 1 : adjust.Zoom;
        double headMm = spec.HeadMm * zoom;
        double perMm = head.HeightFor(spec) / headMm;
        double width = spec.WidthMm * perMm, height = spec.HeightMm * perMm;
        double above = (spec.HeightMm - headMm) * AboveHead * perMm;
        return new IdPhotoCrop(head.CentreX - width / 2 - adjust.Across * width, head.Top(spec) - above - adjust.Down * height, width, height);
    }

    /// <summary>How tall the head comes out in the print, in millimetres.</summary>
    public static double HeadMmIn(IdPhotoHead head, IdPhotoCrop crop, IdPhotoSpec spec) => head.HeightFor(spec) / crop.Height * spec.HeightMm;

    /// <summary>What is wrong with the result, if anything.</summary>
    public static IReadOnlyList<IdPhotoProblem> Check(IdPhotoSource source, IdPhotoCrop crop, IdPhotoSpec spec)
    {
        var problems = new List<IdPhotoProblem>();
        if (source.Faces == 0)
            problems.Add(IdPhotoProblem.NoFace);
        else if (source.Faces > 1)
            problems.Add(IdPhotoProblem.SeveralFaces);
        if (crop.Width < Pixels(spec.WidthMm) * 0.6)
            problems.Add(IdPhotoProblem.LowResolution);
        if (crop.Bottom > source.Height + 1 || crop.X < -crop.Width * 0.12 || crop.Right > source.Width + crop.Width * 0.12)
            problems.Add(IdPhotoProblem.TooClose);
        return problems;
    }

    /// <summary>The print at its exact size, on the background asked for.</summary>
    public static MagickImage Render(IdPhotoSource source, IdPhotoCrop crop, IdPhotoSpec spec, uint background)
    {
        int width = Pixels(spec.WidthMm), height = Pixels(spec.HeightMm);
        var canvas = new MagickImage(new MagickColor((byte)(background >> 16), (byte)(background >> 8), (byte)background), (uint)width, (uint)height);
        double scale = width / crop.Width;
        // The part of the picture the rectangle holds; beyond the picture is the background.
        int x0 = Math.Clamp((int)Math.Floor(crop.X), 0, source.Width), y0 = Math.Clamp((int)Math.Floor(crop.Y), 0, source.Height);
        int x1 = Math.Clamp((int)Math.Ceiling(crop.Right), 0, source.Width), y1 = Math.Clamp((int)Math.Ceiling(crop.Bottom), 0, source.Height);
        if (x1 > x0 && y1 > y0)
        {
            using var part = (MagickImage)source.Image.Clone();
            part.Alpha(AlphaAction.Remove);
            if (source.Mask is { } mask)
            {
                part.Alpha(AlphaAction.Set);
                part.Composite(mask, CompositeOperator.CopyAlpha);
            }
            part.Crop(new MagickGeometry(x0, y0, (uint)(x1 - x0), (uint)(y1 - y0)));
            part.ResetPage();
            part.FilterType = FilterType.Lanczos;
            int w = Math.Max(1, (int)Math.Round((x1 - x0) * scale)), h = Math.Max(1, (int)Math.Round((y1 - y0) * scale));
            part.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
            canvas.Composite(part, (int)Math.Round((x0 - crop.X) * scale), (int)Math.Round((y0 - crop.Y) * scale), CompositeOperator.Over);
        }
        canvas.Density = new Density(Dpi, DensityUnit.PixelsPerInch);
        return canvas;
    }

    /// <summary>How many prints fit on a sheet, and which way round the sheet is: the way that holds more.</summary>
    public static (int Across, int Down, bool Turned) Fit(IdPhotoSpec spec, IdPhotoPaper paper)
    {
        static int Count(double room, double size) => Math.Max(0, (int)Math.Floor((room - 2 * MarginMm + GapMm) / (size + GapMm)));
        int a = Count(paper.WidthMm, spec.WidthMm), b = Count(paper.HeightMm, spec.HeightMm);
        int c = Count(paper.HeightMm, spec.WidthMm), d = Count(paper.WidthMm, spec.HeightMm);
        return c * d > a * b ? (c, d, true) : (a, b, false);
    }

    /// <summary>A sheet of prints, in the middle of the paper, with thin marks to cut along.</summary>
    public static MagickImage Sheet(MagickImage print, IdPhotoSpec spec, IdPhotoPaper paper)
    {
        var (across, down, turned) = Fit(spec, paper);
        int width = Pixels(turned ? paper.HeightMm : paper.WidthMm), height = Pixels(turned ? paper.WidthMm : paper.HeightMm);
        var sheet = new MagickImage(MagickColors.White, (uint)width, (uint)height);
        int gap = Pixels(GapMm);
        int w = (int)print.Width, h = (int)print.Height;
        int left = (width - (across * w + (across - 1) * gap)) / 2, top = (height - (down * h + (down - 1) * gap)) / 2;
        var drawables = new Drawables().StrokeColor(new MagickColor(170, 170, 170)).StrokeWidth(1).FillOpacity(new Percentage(0));
        for (int row = 0; row < down; row++)
            for (int column = 0; column < across; column++)
            {
                int x = left + column * (w + gap), y = top + row * (h + gap);
                sheet.Composite(print, x, y, CompositeOperator.Over);
                drawables.Rectangle(x - 1, y - 1, x + w, y + h);
            }
        if (across > 0 && down > 0)
            drawables.Draw(sheet);
        sheet.Density = new Density(Dpi, DensityUnit.PixelsPerInch);
        return sheet;
    }

    /// <summary>Saves a print or a sheet as a JPEG at its printing resolution.</summary>
    public static async Task SaveAsync(MagickImage image, string path, CancellationToken cancellationToken = default)
    {
        using var flat = (MagickImage)image.Clone();
        flat.Quality = 95;
        flat.Density = new Density(Dpi, DensityUnit.PixelsPerInch);
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        await Task.Run(() => flat.Write(path, MagickFormat.Jpeg), cancellationToken);
    }
}
