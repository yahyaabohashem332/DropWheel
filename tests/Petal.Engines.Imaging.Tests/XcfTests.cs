using ImageMagick;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

/// <summary>GIMP files as one picture, and the new formats told by their content.</summary>
public sealed class XcfTests
{
    [Fact]
    public async Task Layers_are_flattened_with_their_opacity_places_and_visibility()
    {
        var dir = Fixtures.NewDirectory();
        var xcf = Path.Combine(dir, "poster.xcf");
        File.WriteAllBytes(xcf, XcfWriter.Write(200, 100,
        [
            // Top: red at half opacity, partly off the right edge.
            new XcfLayer("Glow", 170, 20, 60, 40, (_, _) => (255, 0, 0, 255), Opacity: 128),
            new XcfLayer("Draft", 0, 0, 200, 100, (_, _) => (0, 255, 0, 255), Visible: false),
            // A layer with its own see-through pixels.
            new XcfLayer("Logo", 10, 10, 20, 20, (x, _) => (255, 255, 255, (byte)(x < 10 ? 255 : 0))),
            new XcfLayer("Sky", 0, 0, 200, 100, (_, _) => (0, 0, 255, 255)),
        ]));
        var png = Path.Combine(dir, "poster.png");
        await new ImageConverter().ConvertAsync(new ConversionJob(xcf, F.Xcf, F.Png, png, ConversionSettings.Default), null, CancellationToken.None);

        using var image = new MagickImage(png);
        Assert.Equal((200u, 100u), (image.Width, image.Height));
        Assert.Equal(new MagickColor(0, 0, 255), Fixtures.PixelAt(image, 100, 80));      // sky; the hidden green isn't there
        Assert.Equal(new MagickColor(255, 255, 255), Fixtures.PixelAt(image, 12, 15));   // the logo's left half
        Assert.Equal(new MagickColor(0, 0, 255), Fixtures.PixelAt(image, 25, 15));       // its see-through right half
        var glow = Fixtures.PixelAt(image, 185, 40);                                       // red at half over blue
        Assert.InRange((int)glow.R, 120, 135);
        Assert.InRange((int)glow.B, 120, 135);
        Assert.Equal(0, (int)glow.G);
    }

    [Fact]
    public void Layer_opacity_and_visibility_are_read_from_the_file()
    {
        var bytes = XcfWriter.Write(10, 10,
        [
            new XcfLayer("A", 0, 0, 10, 10, (_, _) => (1, 2, 3, 255), Opacity: 51),
            new XcfLayer("B", 0, 0, 10, 10, (_, _) => (1, 2, 3, 255), Visible: false),
        ]);
        var layers = XcfFile.Layers(bytes)!;
        Assert.Equal(2, layers.Count);
        Assert.Equal(0.2, layers[0].Opacity, 3);
        Assert.True(layers[0].Visible);
        Assert.False(layers[1].Visible);
        Assert.Null(XcfFile.Layers("not a gimp file at all, just text"u8.ToArray()));
    }

    [Theory]
    [InlineData("jxl")]
    [InlineData("jp2")]
    [InlineData("qoi")]
    [InlineData("dds")]
    public void The_new_formats_are_told_by_their_content(string id)
    {
        var dir = Fixtures.NewDirectory();
        using var image = Fixtures.Quadrants();
        var saved = Fixtures.Save(image, dir, F.FromId(id)!);
        // Named .bin, the bytes still say what it is.
        var renamed = Path.ChangeExtension(saved, ".bin");
        File.Move(saved, renamed);
        Assert.Equal(F.FromId(id), ImageFormats.Detect(renamed));
    }

    [Fact]
    public void A_gimp_file_is_told_by_its_content()
    {
        var path = Path.Combine(Fixtures.NewDirectory(), "layers.bin");
        File.WriteAllBytes(path, XcfWriter.Write(4, 4, [new XcfLayer("x", 0, 0, 4, 4, (_, _) => (0, 0, 0, 255))]));
        Assert.Equal(F.Xcf, ImageFormats.Detect(path));
    }
}
