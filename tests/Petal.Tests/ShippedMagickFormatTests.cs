using ImageMagick;
using Petal.Core.Jobs;
using Petal.Engines.Imaging;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>
/// The picture formats of 1.24 with the Magick.Native DropWheel ships (built without the patented codecs), which
/// these tests load, unlike the imaging tests' own project: JPEG XL, TGA, DDS, JPEG 2000, QOI and PCX both ways,
/// and a GIMP file read.
/// </summary>
public sealed class ShippedMagickFormatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-magick-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_loaded_magick_native_is_the_one_dropwheel_ships()
    {
        _ = MagickNET.Version;
        var native = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .Single(m => m.ModuleName.StartsWith("Magick.Native", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar, native.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("jxl")]
    [InlineData("tga")]
    [InlineData("dds")]
    [InlineData("jp2")]
    [InlineData("qoi")]
    [InlineData("pcx")]
    public async Task Each_new_format_is_written_from_png_and_read_back(string id)
    {
        var format = F.FromId(id)!;
        var png = Path.Combine(_dir, $"{id}.png");
        using (var image = new MagickImage(new MagickColor(220, 40, 40), 64, 48))
        {
            image.Draw(new ImageMagick.Drawing.Drawables().FillColor(new MagickColor(40, 70, 200)).Rectangle(32, 0, 64, 48));
            image.Write(png);
        }
        var output = Path.Combine(_dir, $"{id}.{format.WriteExtension}");
        var converter = new ImageConverter();
        Assert.True(converter.CanConvert(F.Png, format));
        await converter.ConvertAsync(new ConversionJob(png, F.Png, format, output, ConversionSettings.Default), null, CancellationToken.None);
        var back = Path.Combine(_dir, $"{id} back.png");
        await converter.ConvertAsync(new ConversionJob(output, format, F.Png, back, ConversionSettings.Default), null, CancellationToken.None);
        using var read = new MagickImage(back);
        Assert.Equal((64u, 48u), (read.Width, read.Height));
        using var pixels = read.GetPixels();
        var left = pixels.GetPixel(8, 24).ToColor()!;
        var right = pixels.GetPixel(56, 24).ToColor()!;
        Assert.InRange((int)left.R, 205, 235);
        Assert.InRange((int)right.B, 185, 215);
    }

    [Fact]
    public async Task A_gimp_file_becomes_a_png()
    {
        // GIMP 2's layout: one RGB layer of 8×8, its tiles RLE-compressed.
        var xcf = Path.Combine(_dir, "art.xcf");
        var bytes = new List<byte>();
        void U32(int v) => bytes.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        void Patch(int at, int v) { bytes[at] = (byte)(v >> 24); bytes[at + 1] = (byte)(v >> 16); bytes[at + 2] = (byte)(v >> 8); bytes[at + 3] = (byte)v; }
        bytes.AddRange("gimp xcf file\0"u8.ToArray());
        U32(8); U32(8); U32(0);
        U32(17); U32(1); bytes.Add(1); // RLE
        U32(0); U32(0);
        int layerPointer = bytes.Count;
        U32(0); U32(0); U32(0);
        Patch(layerPointer, bytes.Count);
        U32(8); U32(8); U32(1);
        U32(2); bytes.AddRange("L\0"u8.ToArray());
        U32(8); U32(4); U32(1); // visible, as GIMP always writes
        U32(0); U32(0);
        int hierarchy = bytes.Count;
        U32(0); U32(0);
        Patch(hierarchy, bytes.Count);
        U32(8); U32(8); U32(4);
        int level = bytes.Count;
        U32(0); U32(0);
        Patch(level, bytes.Count);
        U32(8); U32(8);
        int tile = bytes.Count;
        U32(0); U32(0);
        Patch(tile, bytes.Count);
        foreach (var value in new byte[] { 0, 160, 0, 255 }) // a green, opaque: each channel as one run of 64
        {
            bytes.Add(63);
            bytes.Add(value);
        }
        File.WriteAllBytes(xcf, bytes.ToArray());
        var png = Path.Combine(_dir, "art.png");
        await new ImageConverter().ConvertAsync(new ConversionJob(xcf, F.Xcf, F.Png, png, ConversionSettings.Default), null, CancellationToken.None);
        using var read = new MagickImage(png);
        using var pixels = read.GetPixels();
        Assert.Equal(new MagickColor(0, 160, 0), pixels.GetPixel(4, 4).ToColor());
    }
}
