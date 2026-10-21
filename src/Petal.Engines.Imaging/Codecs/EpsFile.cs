using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using Petal.Core.Util;

namespace Petal.Engines.Imaging;

/// <summary>
/// Encapsulated PostScript (and pre-2000 Illustrator files, which are PostScript too). Drawing
/// PostScript needs Ghostscript; without it Petal can still show the picture most EPS files carry
/// for that purpose: a TIFF in the DOS EPS header (Illustrator, Photoshop), or an EPSI bitmap.
/// </summary>
public static partial class EpsFile
{
    public static bool IsDosEps(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 0xC5 && head[1] == 0xD0 && head[2] == 0xD3 && head[3] == 0xC6;

    public static bool IsPostScript(ReadOnlySpan<byte> head) => head.StartsWith("%!PS"u8) || head.StartsWith("%!"u8);

    /// <summary>True when Ghostscript is installed, so EPS files can be drawn at full quality.</summary>
    public static bool CanRender => Ghostscript.IsAvailable;

    /// <summary>The PostScript part: all of a plain EPS, or the section a DOS EPS header points to.</summary>
    public static (long Offset, long Length) PostScript(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[30];
        int n = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (n >= 12 && IsDosEps(header))
            return (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]), BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
        return (0, stream.Length);
    }

    /// <summary>The preview picture stored in the file, or null when it has none.</summary>
    public static MagickImage? LoadPreview(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (IsDosEps(bytes))
        {
            uint tiffOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)), tiffLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
            if (tiffLength > 0 && tiffOffset + (long)tiffLength <= bytes.Length)
            {
                var image = new MagickImage(bytes.AsSpan((int)tiffOffset, (int)tiffLength), new MagickReadSettings { Format = MagickFormat.Tiff, FrameIndex = 0, FrameCount = 1 });
                image.ResetPage();
                return image;
            }
        }
        var (offset, length) = PostScript(path);
        var text = Encoding.Latin1.GetString(bytes, (int)offset, (int)Math.Min(length, bytes.Length - offset));
        return Epsi(text);
    }

    /// <summary>An EPSI preview: "%%BeginPreview: width height depth lines", then hex rows commented with "%".</summary>
    public static MagickImage? Epsi(string postScript)
    {
        var m = BeginPreview().Match(postScript);
        if (!m.Success)
            return null;
        int width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int depth = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (width <= 0 || height <= 0 || width > 20000 || height > 20000 || depth is not (1 or 2 or 4 or 8))
            return null;
        int end = postScript.IndexOf("%%EndPreview", m.Index, StringComparison.Ordinal);
        if (end < 0)
            return null;
        var hex = new StringBuilder();
        foreach (var line in postScript[(m.Index + m.Length)..end].Split('\n'))
        {
            var row = line.Trim().TrimStart('%');
            foreach (char c in row)
                if (Uri.IsHexDigit(c))
                    hex.Append(c);
        }
        var data = Convert.FromHexString(hex.Length % 2 == 0 ? hex.ToString() : hex.ToString()[..^1]);
        int rowBytes = (width * depth + 7) / 8;
        if (data.Length < rowBytes * height)
            return null;
        // Each row is padded to whole bytes; 0 is black and the largest value white, as for the image operator.
        var rgb = new byte[width * height * 3];
        int max = (1 << depth) - 1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int bit = x * depth;
                int value = (data[y * rowBytes + bit / 8] >> (8 - depth - bit % 8)) & max;
                byte level = (byte)(value * 255 / max);
                int at = (y * width + x) * 3;
                rgb[at] = rgb[at + 1] = rgb[at + 2] = level;
            }
        }
        var image = new MagickImage();
        image.ReadPixels(rgb, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
        return image;
    }

    /// <summary>The drawing's size in points (1/72 inch), from %%HiResBoundingBox or %%BoundingBox.</summary>
    public static (double Width, double Height)? BoundingBox(string path)
    {
        var text = Head(path);
        var m = HiResBox().Match(text);
        if (!m.Success)
            m = Box().Match(text);
        if (!m.Success)
            return null;
        double v(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        return (Math.Abs(v(3) - v(1)), Math.Abs(v(4) - v(2)));
    }

    /// <summary>The program that wrote the file ("Adobe Illustrator(R) 24.0").</summary>
    public static string? Creator(string path)
    {
        var m = CreatorLine().Match(Head(path));
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string Head(string path)
    {
        var (offset, length) = PostScript(path);
        using var stream = File.OpenRead(path);
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(length, 256 * 1024)];
        int n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Encoding.Latin1.GetString(buffer, 0, n);
    }

    /// <summary>Draws the file at full quality with Ghostscript, as a PNG file.</summary>
    public static Task RenderAsync(string input, string png, int dpi, CancellationToken cancellationToken = default) =>
        Ghostscript.RenderPngAsync(input, png, dpi, cancellationToken);

    [GeneratedRegex(@"%%BeginPreview:\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)[^\n]*\n")]
    private static partial Regex BeginPreview();

    [GeneratedRegex(@"%%HiResBoundingBox:\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)")]
    private static partial Regex HiResBox();

    [GeneratedRegex(@"%%BoundingBox:\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)")]
    private static partial Regex Box();

    [GeneratedRegex(@"%%Creator:\s*([^\r\n]{1,120})")]
    private static partial Regex CreatorLine();
}
