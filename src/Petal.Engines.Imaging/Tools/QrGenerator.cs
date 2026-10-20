using System.Globalization;
using System.Text;
using ImageMagick;
using ZXing;
using ZXing.QrCode.Internal;

namespace Petal.Engines.Imaging;

/// <summary>How much of a QR code can be damaged and still read; more redundancy makes it denser.</summary>
public enum QrRedundancy
{
    Low,
    Medium,
    Quartile,
    High,
}

/// <summary>Makes QR codes locally, as module grids, PNG or SVG.</summary>
public static class QrGenerator
{
    /// <summary>The modules (true = dark), without the quiet zone. Text beyond ASCII is stored as UTF-8.</summary>
    public static bool[,] Encode(string text, QrRedundancy redundancy = QrRedundancy.Medium)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Type something to put in the QR code.", nameof(text));
        var hints = new Dictionary<EncodeHintType, object>();
        if (text.Any(c => c > 0x7F))
            hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
        var level = redundancy switch
        {
            QrRedundancy.Low => ErrorCorrectionLevel.L,
            QrRedundancy.Quartile => ErrorCorrectionLevel.Q,
            QrRedundancy.High => ErrorCorrectionLevel.H,
            _ => ErrorCorrectionLevel.M,
        };
        QRCode code;
        try
        {
            code = ZXing.QrCode.Internal.Encoder.encode(text, level, hints);
        }
        catch (WriterException)
        {
            throw new ArgumentException("This is too much text for a QR code.", nameof(text));
        }
        var matrix = code.Matrix;
        var modules = new bool[matrix.Width, matrix.Height];
        for (int y = 0; y < matrix.Height; y++)
            for (int x = 0; x < matrix.Width; x++)
                modules[x, y] = matrix[x, y] == 1;
        return modules;
    }

    /// <summary>
    /// A Wi-Fi network as phones read it ("WIFI:T:WPA;S:…;P:…;;"), with special characters escaped.
    /// </summary>
    public static string WifiPayload(string network, string password, bool hidden = false)
    {
        static string Escape(string s) => new StringBuilder(s).Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:").Replace("\"", "\\\"").ToString();
        var security = password.Length == 0 ? "nopass" : "WPA";
        return $"WIFI:T:{security};S:{Escape(network)};{(password.Length > 0 ? $"P:{Escape(password)};" : "")}{(hidden ? "H:true;" : "")};";
    }

    /// <summary>A PNG with <paramref name="quietZone"/> light modules around the code.</summary>
    public static byte[] ToPng(bool[,] modules, int pixelsPerModule, MagickColor dark, MagickColor light, int quietZone = 4)
    {
        int n = modules.GetLength(0), size = (n + quietZone * 2) * pixelsPerModule;
        var pixels = new byte[size * size * 4];
        byte[] d = [dark.R, dark.G, dark.B, dark.A], l = [light.R, light.G, light.B, light.A];
        for (int y = 0; y < size; y++)
        {
            int my = y / pixelsPerModule - quietZone;
            for (int x = 0; x < size; x++)
            {
                int mx = x / pixelsPerModule - quietZone;
                bool on = mx >= 0 && my >= 0 && mx < n && my < n && modules[mx, my];
                Buffer.BlockCopy(on ? d : l, 0, pixels, (y * size + x) * 4, 4);
            }
        }
        using var image = new MagickImage(pixels, new PixelReadSettings((uint)size, (uint)size, StorageType.Char, PixelMapping.RGBA));
        image.Density = new Density(300, DensityUnit.PixelsPerInch);
        using var stream = new MemoryStream();
        image.Write(stream, MagickFormat.Png32);
        return stream.ToArray();
    }

    /// <summary>A crisp, scalable SVG: one path for all dark modules.</summary>
    public static string ToSvg(bool[,] modules, string darkHex, string lightHex, int quietZone = 4)
    {
        int n = modules.GetLength(0), size = n + quietZone * 2;
        var path = new StringBuilder();
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                if (!modules[x, y])
                    continue;
                // Runs of dark modules become one rectangle.
                int run = 1;
                while (x + run < n && modules[x + run, y])
                    run++;
                path.Append(CultureInfo.InvariantCulture, $"M{x + quietZone},{y + quietZone}h{run}v1h-{run}z");
                x += run - 1;
            }
        }
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\">" +
               $"<rect width=\"{size}\" height=\"{size}\" fill=\"{lightHex}\"/><path fill=\"{darkHex}\" d=\"{path}\"/></svg>";
    }
}
