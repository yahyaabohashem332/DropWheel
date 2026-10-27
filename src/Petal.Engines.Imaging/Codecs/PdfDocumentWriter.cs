using System.Globalization;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace Petal.Engines.Imaging;

/// <summary>One image ready to embed in a PDF page.</summary>
internal sealed class PdfImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required bool Gray { get; init; }

    /// <summary>JPEG bytes (DCTDecode) when true, PNG-predicted zlib data (FlateDecode) when false.</summary>
    public required bool IsJpeg { get; init; }

    public required byte[] Data { get; init; }

    public byte[]? Icc { get; init; }

    public double DpiX { get; init; } = 72;

    public double DpiY { get; init; } = 72;

    /// <summary>Encodes a decoded (already upright, opaque) image.</summary>
    public static PdfImage FromMagick(MagickImage image, bool preferJpeg, int jpegQuality, bool keepIcc)
    {
        bool gray = image.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray || image.ColorType is ColorType.Grayscale;
        var icc = keepIcc ? image.GetColorProfile() : null;
        if (icc is not null && icc.ColorSpace != (gray ? ColorSpace.Gray : ColorSpace.sRGB))
            icc = null;
        var (dpiX, dpiY) = DpiOf(image.Density);
        int w = (int)image.Width, h = (int)image.Height;

        if (preferJpeg)
        {
            using var clone = (MagickImage)image.Clone();
            ImageWriter.FlattenOnWhite(clone);
            clone.Strip();
            if (gray)
                clone.ColorType = ColorType.Grayscale;
            clone.Quality = (uint)Math.Clamp(jpegQuality, 1, 100);
            var jpeg = clone.ToByteArray(MagickFormat.Jpeg);
            var sof = JpegFrameInfo.Read(jpeg) ?? throw new InvalidDataException("Encoder produced an invalid JPEG");
            return new PdfImage
            {
                Width = sof.Width, Height = sof.Height, Gray = sof.Components == 1, IsJpeg = true, Data = jpeg,
                Icc = icc?.ToByteArray(), DpiX = dpiX, DpiY = dpiY,
            };
        }

        byte[] pixels;
        using (var pc = image.GetPixels())
            pixels = (gray ? pc.ToByteArray("R") : pc.ToByteArray(PixelMapping.RGB)) ?? throw new InvalidOperationException("Could not read pixels");
        return new PdfImage
        {
            Width = w, Height = h, Gray = gray, IsJpeg = false,
            Data = PngPredictAndDeflate(pixels, w, h, gray ? 1 : 3),
            Icc = icc?.ToByteArray(), DpiX = dpiX, DpiY = dpiY,
        };
    }

    /// <summary>
    /// Embeds a JPEG file byte-for-byte (no generation loss) when PDF readers can display it
    /// as stored: 8-bit baseline/progressive, grey or RGB, upright.
    /// </summary>
    public static PdfImage? TryJpegPassthrough(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        var sof = JpegFrameInfo.Read(bytes);
        if (sof is null || sof.Precision != 8 || sof.Components is not (1 or 3) || sof.Process is not (0 or 1 or 2))
            return null;

        using var ping = new MagickImage();
        try
        {
            ping.Ping(bytes);
        }
        catch (MagickException)
        {
            return null;
        }
        if (ping.Orientation is not (Orientation.Undefined or Orientation.TopLeft))
            return null;
        var icc = ping.GetColorProfile();
        bool gray = sof.Components == 1;
        if (icc is not null && icc.ColorSpace != (gray ? ColorSpace.Gray : ColorSpace.sRGB))
            icc = null;
        var (dpiX, dpiY) = DpiOf(ping.Density);
        return new PdfImage
        {
            Width = sof.Width, Height = sof.Height, Gray = gray, IsJpeg = true, Data = bytes,
            Icc = icc?.ToByteArray(), DpiX = dpiX, DpiY = dpiY,
        };
    }

    /// <summary>Page size in points at the image's own resolution (72 dpi when unknown), at most 200 inches.</summary>
    public (double Width, double Height) NaturalPageSize()
    {
        double w = Width * 72.0 / DpiX, h = Height * 72.0 / DpiY;
        double scale = Math.Min(1, PdfDocumentWriter.MaxPageSide / Math.Max(w, h));
        return (Math.Max(1, w * scale), Math.Max(1, h * scale));
    }

    private static (double, double) DpiOf(Density density)
    {
        var d = density.Units == DensityUnit.PixelsPerCentimeter ? density.ChangeUnits(DensityUnit.PixelsPerInch) : density;
        // Missing or aspect-ratio-only resolutions (JFIF 1:1) mean "unknown": use 72 dpi, one pixel per point.
        double x = d.X >= 10 ? d.X : 72, y = d.Y >= 10 ? d.Y : x;
        return (x, y);
    }

    private static byte[] PngPredictAndDeflate(byte[] pixels, int width, int height, int bpp)
    {
        int rowBytes = width * bpp;
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var filtered = new byte[rowBytes + 1];
            for (int y = 0; y < height; y++)
            {
                var row = pixels.AsSpan(y * rowBytes, rowBytes);
                var prev = y > 0 ? pixels.AsSpan((y - 1) * rowBytes, rowBytes) : default;
                filtered[0] = 4; // Paeth
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0;
                    int b = y > 0 ? prev[i] : 0;
                    int c = i >= bpp && y > 0 ? prev[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    filtered[i + 1] = (byte)(row[i] - pred);
                }
                z.Write(filtered);
            }
        }
        return output.ToArray();
    }
}

/// <summary>Size and coding process from a JPEG's SOF marker.</summary>
internal sealed record JpegFrameInfo(int Width, int Height, int Components, int Precision, int Process)
{
    public static JpegFrameInfo? Read(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return null;
        int i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF) return null;
            byte marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or (>= 0xD0 and <= 0xD7) or 0x01) { i += 2; continue; }
            int len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 4 + 6 > jpeg.Length) return null;
                int precision = jpeg[i + 4];
                int height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                int width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                int comps = jpeg[i + 9];
                return new JpegFrameInfo(width, height, comps, precision, marker - 0xC0);
            }
            if (marker == 0xDA || marker == 0xD9) return null;
            i += 2 + len;
        }
        return null;
    }
}

/// <summary>
/// Writes a PDF made of full-page images, one object at a time. PDF 1.7 structure:
/// catalog (1), page tree (2), info (3), then page, content and image objects per page.
/// </summary>
internal sealed class PdfDocumentWriter
{
    /// <summary>Largest page side most readers accept: 200 inches.</summary>
    public const double MaxPageSide = 14400;

    private readonly Stream _output;
    private readonly List<long> _offsets = [0, 0, 0, 0]; // index = object number
    private readonly List<int> _pages = [];
    private bool _finished;

    public PdfDocumentWriter(Stream output)
    {
        _output = output;
        WriteRaw("%PDF-1.7\n%âãÏÓ\n"u8.ToArray());
    }

    private long _written;

    private long Position => _written;

    public int PageCount => _pages.Count;

    /// <summary>Adds a page of <paramref name="pageWidth"/> × <paramref name="pageHeight"/> points with the image drawn in the given box (PDF origin is bottom-left).</summary>
    public void AddPage(PdfImage image, double pageWidth, double pageHeight, double x, double y, double drawWidth, double drawHeight)
    {
        int iccObj = 0;
        if (image.Icc is { Length: > 0 } icc)
        {
            iccObj = Reserve();
            var data = Deflate(icc);
            WriteStreamObject(iccObj, $"<< /N {(image.Gray ? 1 : 3)} /Alternate /{(image.Gray ? "DeviceGray" : "DeviceRGB")} /Filter /FlateDecode /Length {data.Length} >>", data);
        }

        int imageObj = Reserve();
        var colorSpace = iccObj > 0 ? $"[/ICCBased {iccObj} 0 R]" : image.Gray ? "/DeviceGray" : "/DeviceRGB";
        var filter = image.IsJpeg
            ? "/Filter /DCTDecode"
            : $"/Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors {(image.Gray ? 1 : 3)} /BitsPerComponent 8 /Columns {image.Width} >>";
        WriteStreamObject(imageObj,
            $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace {colorSpace} /BitsPerComponent 8 {filter} /Length {image.Data.Length} >>",
            image.Data);

        int contentObj = Reserve();
        var content = Encoding.ASCII.GetBytes($"q {N(drawWidth)} 0 0 {N(drawHeight)} {N(x)} {N(y)} cm /Im0 Do Q\n");
        WriteStreamObject(contentObj, $"<< /Length {content.Length} >>", content);

        int pageObj = Reserve();
        WriteObject(pageObj,
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(pageWidth)} {N(pageHeight)}] /Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>");
        _pages.Add(pageObj);
    }

    /// <summary>Adds a page exactly the size of the image.</summary>
    public void AddFittedPage(PdfImage image)
    {
        var (w, h) = image.NaturalPageSize();
        AddPage(image, w, h, 0, 0, w, h);
    }

    public void Finish(string? title = null)
    {
        if (_finished)
            return;
        if (_pages.Count == 0)
            throw new InvalidOperationException("A PDF needs at least one page");
        _finished = true;

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, $"<< /Type /Pages /Kids [{string.Join(" ", _pages.Select(p => $"{p} 0 R"))}] /Count {_pages.Count} >>");
        var date = DateTimeOffset.Now;
        var offset = date.Offset;
        var stamp = $"D:{date:yyyyMMddHHmmss}{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
        var info = $"<< /Producer (DropWheel) /CreationDate ({stamp})";
        if (!string.IsNullOrEmpty(title))
            info += $" /Title {PdfText(title)}";
        WriteObject(3, info + " >>");

        long xref = Position;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {_offsets.Count}\n");
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i < _offsets.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{_offsets[i]:0000000000} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_offsets.Count} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        WriteRaw(Encoding.ASCII.GetBytes(sb.ToString()));
        _output.Flush();
    }

    /// <summary>Image → single-page PDF used by conversions.</summary>
    public static void WriteSinglePage(MagickImage image, Stream output, ImageWriteOptions options)
    {
        var pdfImage = (options.PdfJpegSource is { } jpeg ? PdfImage.TryJpegPassthrough(jpeg) : null)
            ?? PdfImage.FromMagick(image, options.PdfPreferJpeg, options.Quality, options.KeepColorProfile);
        var writer = new PdfDocumentWriter(output);
        writer.AddFittedPage(pdfImage);
        writer.Finish();
    }

    private int Reserve()
    {
        _offsets.Add(0);
        return _offsets.Count - 1;
    }

    private void WriteObject(int number, string body)
    {
        _offsets[number] = Position;
        WriteRaw(Encoding.ASCII.GetBytes($"{number} 0 obj\n{body}\nendobj\n"));
    }

    private void WriteStreamObject(int number, string dictionary, byte[] data)
    {
        _offsets[number] = Position;
        WriteRaw(Encoding.ASCII.GetBytes($"{number} 0 obj\n{dictionary}\nstream\n"));
        WriteRaw(data);
        WriteRaw("\nendstream\nendobj\n"u8.ToArray());
    }

    private void WriteRaw(byte[] bytes)
    {
        _output.Write(bytes);
        _written += bytes.Length;
    }

    private static string N(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    /// <summary>A PDF text string: UTF-16BE with BOM so any title survives.</summary>
    private static string PdfText(string text)
    {
        var bytes = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text));
        return "<" + Convert.ToHexString(bytes.ToArray()) + ">";
    }
}
