using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Petal.Shell;

/// <summary>
/// The picture a design file carries for programs that can't draw it: a Photoshop file's merged image
/// (or its small thumbnail), the preview in an Illustrator, InDesign or EPS file's XMP, an EPS file's
/// TIFF or WMF, the merged image or thumbnail inside a Krita, OpenRaster or Sketch file. Read with
/// nothing but .NET Framework 4.8's own classes, from any seekable stream, at about the size asked for.
/// </summary>
public static class EmbeddedPreview
{
    /// <summary>XMP thumbnails are looked for in this much of the start and of the end of a file.</summary>
    private const int XmpWindow = 8 * 1024 * 1024;

    /// <summary>The preview, about <paramref name="size"/> pixels on its longer side; null when the file has none.</summary>
    public static Bitmap? Read(Stream stream, int size)
    {
        if (!stream.CanSeek || stream.Length < 8)
            return null;
        var head = new byte[32];
        stream.Position = 0;
        int n = ReadFully(stream, head, 0, head.Length);
        stream.Position = 0;
        Bitmap? found =
            n >= 4 && head[0] == '8' && head[1] == 'B' && head[2] == 'P' && head[3] == 'S' ? Psd(stream, size)
            : n >= 4 && head[0] == 0xC5 && head[1] == 0xD0 && head[2] == 0xD3 && head[3] == 0xC6 ? DosEps(stream) ?? Xmp(stream)
            : n >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4 ? Zip(stream)
            : Xmp(stream);
        return found is null ? null : Fit(found, size);
    }

    /// <summary>The picture scaled down (never up) to fit <paramref name="size"/>, keeping transparency.</summary>
    public static Bitmap Fit(Bitmap picture, int size)
    {
        int longest = Math.Max(picture.Width, picture.Height);
        if (size <= 0 || longest <= size)
            return picture;
        double scale = (double)size / longest;
        var result = new Bitmap(Math.Max(1, (int)Math.Round(picture.Width * scale)), Math.Max(1, (int)Math.Round(picture.Height * scale)), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(picture, new Rectangle(0, 0, result.Width, result.Height));
        }
        picture.Dispose();
        return result;
    }

    // ---------- Photoshop ----------

    /// <summary>
    /// A Photoshop file: its merged image when it is 8-bit RGB, grey or CMYK (decoded a row at a time and
    /// sampled to the size asked for, so a large file costs little memory); otherwise the small JPEG
    /// thumbnail Photoshop keeps among the image resources.
    /// </summary>
    internal static Bitmap? Psd(Stream s, int size)
    {
        var header = new byte[26];
        if (ReadFully(s, header, 0, 26) < 26)
            return null;
        bool big = BigEndian16(header, 4) == 2;
        int channels = BigEndian16(header, 12);
        int height = BigEndian32(header, 14), width = BigEndian32(header, 18);
        int depth = BigEndian16(header, 22), mode = BigEndian16(header, 24);
        // Photoshop's own limits; anything beyond is a damaged or hostile file, read no further.
        if (width <= 0 || height <= 0 || channels <= 0 || width > 300_000 || height > 300_000 || channels > 56)
            return null;
        Skip(s, ReadBig32(s)); // colour mode data
        long resourcesLength = ReadBig32(s);
        long resourcesStart = s.Position;
        if (resourcesLength < 0 || resourcesStart + resourcesLength > s.Length)
            return null;
        // Small thumbnails come quicker from the one Photoshop keeps than from the whole picture.
        if (size <= 160)
        {
            try
            {
                if (PsdThumbnail(s, resourcesLength) is { } quick)
                    return quick;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or InvalidDataException or OverflowException or OutOfMemoryException)
            {
            }
        }
        try
        {
            if (depth == 8 && mode is 1 or 3 or 4 && channels >= (mode == 1 ? 1 : mode == 3 ? 3 : 4))
            {
                s.Position = resourcesStart + resourcesLength;
                long layers = big ? ReadBig64(s) : ReadBig32(s);
                Skip(s, layers);
                var merged = Merged(s, big, width, height, channels, mode, Math.Max(size, 256));
                if (merged is not null)
                    return merged;
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or InvalidDataException or OverflowException or OutOfMemoryException)
        {
            // A merged image that isn't there or isn't whole: the thumbnail below.
        }
        if (size <= 160)
            return null;
        try
        {
            s.Position = resourcesStart;
            return PsdThumbnail(s, resourcesLength);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or InvalidDataException or OverflowException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static Bitmap? Merged(Stream s, bool big, int width, int height, int channels, int mode, int size)
    {
        int compression = ReadBig16(s);
        if (compression is not (0 or 1))
            return null;
        int step = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)size));
        int outW = (width + step - 1) / step, outH = (height + step - 1) / step;
        int used = mode == 1 ? 1 : mode == 3 ? 3 : 4;
        var planes = new byte[used][];
        for (int c = 0; c < used; c++)
            planes[c] = new byte[outW * outH];

        int[]? counts = null;
        if (compression == 1)
        {
            // The row-length table must fit in what is left of the file.
            if ((long)channels * height * (big ? 4 : 2) > s.Length - s.Position)
                return null;
            counts = new int[channels * height];
            var table = new byte[(big ? 4 : 2) * counts.Length];
            if (ReadFully(s, table, 0, table.Length) < table.Length)
                return null;
            for (int i = 0; i < counts.Length; i++)
                counts[i] = big ? BigEndian32(table, i * 4) : BigEndian16(table, i * 2);
        }
        var row = new byte[width];
        var packed = new byte[Math.Max(16, width * 2)];
        for (int c = 0; c < used; c++)
        {
            for (int y = 0; y < height; y++)
            {
                if (compression == 1)
                {
                    int count = counts![c * height + y];
                    if (count > packed.Length)
                        packed = new byte[count];
                    if (ReadFully(s, packed, 0, count) < count)
                        throw new EndOfStreamException();
                    UnpackBits(packed, count, row);
                }
                else if (ReadFully(s, row, 0, width) < width)
                {
                    throw new EndOfStreamException();
                }
                if (y % step != 0)
                    continue;
                var plane = planes[c];
                int at = y / step * outW;
                for (int x = 0, ox = 0; x < width; x += step, ox++)
                    plane[at + ox] = row[x];
            }
        }

        var bitmap = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, outW, outH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var line = new byte[outW * 4];
            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    int i = y * outW + x;
                    byte r, g, b;
                    if (mode == 1)
                    {
                        r = g = b = planes[0][i];
                    }
                    else if (mode == 3)
                    {
                        r = planes[0][i];
                        g = planes[1][i];
                        b = planes[2][i];
                    }
                    else
                    {
                        // Photoshop keeps CMYK inverted: 255 is no ink.
                        int k = planes[3][i];
                        r = (byte)(planes[0][i] * k / 255);
                        g = (byte)(planes[1][i] * k / 255);
                        b = (byte)(planes[2][i] * k / 255);
                    }
                    line[x * 4] = b;
                    line[x * 4 + 1] = g;
                    line[x * 4 + 2] = r;
                    line[x * 4 + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(line, 0, data.Scan0 + y * data.Stride, line.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    /// <summary>PackBits, as Photoshop uses it: a count n, then n+1 literal bytes, or one byte repeated 1-n+1 times.</summary>
    internal static void UnpackBits(byte[] packed, int length, byte[] row)
    {
        int i = 0, o = 0;
        while (i < length && o < row.Length)
        {
            int n = (sbyte)packed[i++];
            if (n >= 0)
            {
                int take = Math.Min(n + 1, Math.Min(row.Length - o, length - i));
                Buffer.BlockCopy(packed, i, row, o, take);
                i += n + 1;
                o += take;
            }
            else if (n != -128)
            {
                if (i >= length)
                    break;
                byte value = packed[i++];
                int times = Math.Min(1 - n, row.Length - o);
                for (int k = 0; k < times; k++)
                    row[o++] = value;
            }
        }
    }

    /// <summary>The thumbnail resource (1036, or 1033 from Photoshop 4): a 28-byte header, then a JPEG.</summary>
    private static Bitmap? PsdThumbnail(Stream s, long length)
    {
        long end = s.Position + length;
        var sig = new byte[4];
        while (s.Position + 12 <= end)
        {
            if (ReadFully(s, sig, 0, 4) < 4 || Encoding.ASCII.GetString(sig) is not ("8BIM" or "MeSa" or "AgHg" or "PHUT" or "DCSR"))
                return null;
            int id = ReadBig16(s);
            int nameLength = s.ReadByte();
            Skip(s, (nameLength + 1) % 2 == 0 ? nameLength : nameLength + 1);
            long size = ReadBig32(s);
            long next = s.Position + size + (size % 2);
            if (size < 0 || next > end)
                return null;
            if (id is 1036 or 1033 && size > 28)
            {
                // A thumbnail is a few kilobytes; a larger claim is a damaged file.
                if (size > 16 * 1024 * 1024)
                    return null;
                Skip(s, 28);
                var jpeg = new byte[size - 28];
                ReadFully(s, jpeg, 0, jpeg.Length);
                var picture = Decode(jpeg);
                // Photoshop 4 stored the thumbnail as BGR: swap it back.
                if (picture is not null && id == 1033)
                    SwapRedBlue(picture);
                return picture;
            }
            s.Position = next;
        }
        return null;
    }

    // ---------- EPS ----------

    /// <summary>A DOS EPS header points at a TIFF or a Windows metafile drawn for programs that can't run PostScript.</summary>
    private static Bitmap? DosEps(Stream s)
    {
        var header = new byte[30];
        if (ReadFully(s, header, 0, 30) < 30)
            return null;
        long wmfAt = LittleEndian32(header, 12), wmfLength = LittleEndian32(header, 16);
        long tiffAt = LittleEndian32(header, 20), tiffLength = LittleEndian32(header, 24);
        foreach (var (at, length) in new[] { (tiffAt, tiffLength), (wmfAt, wmfLength) })
        {
            if (length <= 0 || at + length > s.Length || length > 256 * 1024 * 1024)
                continue;
            s.Position = at;
            var bytes = new byte[length];
            ReadFully(s, bytes, 0, bytes.Length);
            if (at == wmfAt)
            {
                var drawn = Metafile(bytes);
                if (drawn is not null)
                    return drawn;
            }
            else if (Decode(bytes) is { } tiff)
            {
                return tiff;
            }
        }
        return null;
    }

    private static Bitmap? Metafile(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var meta = new Metafile(stream);
            int w = Math.Max(1, Math.Min(2048, meta.Width)), h = Math.Max(1, Math.Min(2048, meta.Height));
            var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);
                g.DrawImage(meta, new Rectangle(0, 0, w, h));
            }
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    // ---------- XMP ----------

    /// <summary>
    /// The thumbnail in the XMP packet Illustrator, InDesign and Photoshop write: a base64 JPEG in
    /// xmpGImg:image (or xapGImg:image in older files). Looked for near the start and the end of the file.
    /// </summary>
    internal static Bitmap? Xmp(Stream s)
    {
        foreach (var (start, length) in Windows(s.Length))
        {
            s.Position = start;
            var bytes = new byte[length];
            int n = ReadFully(s, bytes, 0, bytes.Length);
            var text = Encoding.ASCII.GetString(bytes, 0, n);
            if (XmpThumbnail(text) is { } jpeg && Decode(jpeg) is { } picture)
                return picture;
        }
        return null;
    }

    private static IEnumerable<(long Start, int Length)> Windows(long length)
    {
        if (length <= 2L * XmpWindow)
        {
            yield return (0, (int)length);
            yield break;
        }
        yield return (0, XmpWindow);
        yield return (length - XmpWindow, XmpWindow);
    }

    /// <summary>The largest thumbnail's bytes in an XMP packet, as element or attribute; null when none.</summary>
    internal static byte[]? XmpThumbnail(string text)
    {
        byte[]? best = null;
        foreach (var tag in new[] { "xmpGImg:image", "xapGImg:image" })
        {
            int at = 0;
            while ((at = text.IndexOf(tag, at, StringComparison.Ordinal)) >= 0)
            {
                at += tag.Length;
                int start;
                char end;
                if (at < text.Length && text[at] == '>')
                {
                    start = at + 1;
                    end = '<';
                }
                else if (at + 1 < text.Length && text[at] == '=' && text[at + 1] is '"' or '\'')
                {
                    start = at + 2;
                    end = text[at + 1];
                }
                else
                {
                    continue;
                }
                int stop = text.IndexOf(end, start);
                if (stop < 0)
                    break;
                var base64 = text.Substring(start, stop - start).Replace("&#xA;", "").Replace("&#xa;", "").Replace("&#10;", "");
                base64 = new string(base64.Where(c => !char.IsWhiteSpace(c)).ToArray());
                try
                {
                    var bytes = Convert.FromBase64String(base64);
                    if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8 && (best is null || bytes.Length > best.Length))
                        best = bytes;
                }
                catch (FormatException)
                {
                }
                at = stop;
            }
        }
        return best;
    }

    // ---------- ZIP-based files ----------

    /// <summary>The names of the pictures ZIP-based design files keep for previews, best first.</summary>
    internal static readonly string[] ZipPictures =
    [
        "mergedimage.png", "preview.png", "Thumbnails/thumbnail.png", "previews/preview.png",
        "QuickLook/Preview.png", "QuickLook/Preview.jpg", "preview.jpg", "QuickLook/Thumbnail.png", "QuickLook/Thumbnail.jpg", "thumbnail.png", "Thumbnails/thumbnail.jpg",
        // Office's thumbnail (when it was saved with one), and a 3MF model's.
        "docProps/thumbnail.jpeg", "docProps/thumbnail.jpg", "docProps/thumbnail.png", "docProps/thumbnail.emf", "docProps/thumbnail.wmf", "Metadata/thumbnail.png",
    ];

    private static Bitmap? Zip(Stream s)
    {
        try
        {
            using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var name in ZipPictures)
            {
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length > 128 * 1024 * 1024)
                    continue;
                using var inner = entry.Open();
                using var copy = new MemoryStream();
                inner.CopyTo(copy);
                if (Decode(copy.ToArray()) is { } picture)
                    return picture;
            }
        }
        catch (InvalidDataException)
        {
        }
        return null;
    }

    // ---------- helpers ----------

    internal static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
            return new Bitmap(image);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static void SwapRedBlue(Bitmap picture)
    {
        for (int y = 0; y < picture.Height; y++)
            for (int x = 0; x < picture.Width; x++)
            {
                var c = picture.GetPixel(x, y);
                picture.SetPixel(x, y, Color.FromArgb(c.A, c.B, c.G, c.R));
            }
    }

    private static int ReadFully(Stream s, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, offset + total, count - total);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    private static void Skip(Stream s, long count)
    {
        if (count < 0 || s.Position + count > s.Length)
            throw new EndOfStreamException();
        s.Position += count;
    }

    private static int ReadBig16(Stream s)
    {
        var b = new byte[2];
        if (ReadFully(s, b, 0, 2) < 2) throw new EndOfStreamException();
        return BigEndian16(b, 0);
    }

    private static long ReadBig32(Stream s)
    {
        var b = new byte[4];
        if (ReadFully(s, b, 0, 4) < 4) throw new EndOfStreamException();
        return (uint)BigEndian32(b, 0);
    }

    private static long ReadBig64(Stream s)
    {
        var b = new byte[8];
        if (ReadFully(s, b, 0, 8) < 8) throw new EndOfStreamException();
        return ((long)(uint)BigEndian32(b, 0) << 32) | (uint)BigEndian32(b, 4);
    }

    private static int BigEndian16(byte[] b, int at) => b[at] << 8 | b[at + 1];

    private static int BigEndian32(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];

    private static long LittleEndian32(byte[] b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

}
