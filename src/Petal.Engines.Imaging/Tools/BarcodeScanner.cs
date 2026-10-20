using ImageMagick;
using ZXing;
using ZXing.Common;

namespace Petal.Engines.Imaging;

/// <summary>A point in image pixels.</summary>
public readonly record struct PixelPoint(float X, float Y);

/// <summary>A decoded QR code or barcode.</summary>
/// <param name="Format">Symbology, e.g. "QR_CODE", "EAN_13", "CODE_128".</param>
/// <param name="Points">Finder/end points in upright image pixels.</param>
public sealed record BarcodeResult(string Text, string Format, IReadOnlyList<PixelPoint> Points);

/// <summary>Reads QR codes and 1D/2D barcodes from images (ZXing.Net).</summary>
public static class BarcodeScanner
{
    private const int FastPassMaxSize = 2000;

    private static readonly IList<BarcodeFormat> Formats =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417,
        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.ITF, BarcodeFormat.CODABAR,
    ];

    /// <summary>Every distinct code found in the image, in reading order.</summary>
    public static Task<IReadOnlyList<BarcodeResult>> ScanAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<BarcodeResult>>(() =>
        {
            using var image = ImageLoader.Load(path);
            int longEdge = (int)Math.Max(image.Width, image.Height);

            // A downscaled pass is fast and often more robust on noisy photos; small codes need full resolution.
            if (longEdge > FastPassMaxSize)
            {
                using var small = (MagickImage)image.Clone();
                small.Resize(new MagickGeometry(FastPassMaxSize, FastPassMaxSize));
                var quick = Scan(ImagePreview.ToBgra(small));
                if (quick.Count > 0)
                {
                    double s = (double)longEdge / Math.Max(small.Width, small.Height);
                    return quick.Select(r => r with { Points = r.Points.Select(p => new PixelPoint((float)(p.X * s), (float)(p.Y * s))).ToList() }).ToList();
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Scan(ImagePreview.ToBgra(image));
        }, cancellationToken);

    /// <summary>Scans a BGRA buffer the UI already has.</summary>
    public static IReadOnlyList<BarcodeResult> Scan(BgraBitmap bitmap)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = Formats },
        };
        var source = new RGBLuminanceSource(bitmap.Pixels, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var results = reader.DecodeMultiple(source) ?? [];
        return results
            .Where(r => !string.IsNullOrEmpty(r.Text))
            .GroupBy(r => (r.Text, r.BarcodeFormat))
            .Select(g => g.First())
            .Select(r => new BarcodeResult(
                r.Text,
                r.BarcodeFormat.ToString(),
                (r.ResultPoints ?? []).Where(p => p is not null).Select(p => new PixelPoint(p.X, p.Y)).ToList()))
            .OrderBy(r => r.Points.Count > 0 ? r.Points.Min(p => p.Y) : 0)
            .ThenBy(r => r.Points.Count > 0 ? r.Points.Min(p => p.X) : 0)
            .ToList();
    }
}
