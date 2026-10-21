using ImageMagick;
using Petal.Core.Formats;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

public sealed class PhotoshopTests(ITestOutputHelper output)
{
    private static readonly (byte, byte, byte) Blue = (0, 0, 255), Red = (255, 0, 0), Green = (0, 255, 0), White = (255, 255, 255);

    /// <summary>A blue background, a group "Shapes" with a half-opaque red box (named in Arabic) and a hidden green box, and a white corner on top.</summary>
    private static readonly TestLayer[] Layers =
    [
        new("Background", 0, 0, 64, 48, Blue),
        new("</Layer group>", 0, 0, 0, 0, White, Section: 3),
        new("مربع أحمر", 5, 6, 20, 10, Red, Opacity: 128),
        new("Hidden green", 40, 30, 8, 8, Green, Hidden: true),
        new("Shapes", 0, 0, 0, 0, White, Section: 1),
        new("Top", 0, 0, 4, 4, White),
    ];

    private static string Write(string name, bool withComposite, bool large = false)
    {
        var path = Path.Combine(Fixtures.NewDirectory(), name);
        File.WriteAllBytes(path, PsdWriter.Write(64, 48, Layers, withComposite ? (_, _) => (10, 20, 30, 255) : null, large));
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Layers_come_with_their_real_names_groups_and_visibility(bool large)
    {
        var psd = PsdFile.Read(Write(large ? "doc.psb" : "doc.psd", withComposite: true, large));
        Assert.Equal(large, psd.IsLarge);
        Assert.Equal((64, 48, 8, "RGB"), (psd.Width, psd.Height, psd.Depth, psd.ColorMode));
        Assert.True(psd.HasComposite);
        Assert.Equal(6, psd.Layers.Count);

        var red = psd.Layers[2];
        Assert.Equal("مربع أحمر", red.Name);
        Assert.Equal(["Shapes"], red.Group);
        Assert.Equal((PsdLayerKind.Pixels, false, (byte)128), (red.Kind, red.Hidden, red.Opacity));
        Assert.Equal((5, 6, 20, 10), (red.Left, red.Top, red.Width, red.Height));

        Assert.True(psd.Layers[3].Hidden);
        Assert.Equal(PsdLayerKind.Group, psd.Layers[4].Kind);
        Assert.Empty(psd.Layers[5].Group);
        // Frames are the layers with pixels, bottom first: Background, red, green, Top.
        Assert.Equal([0, -1, 1, 2, -1, 3], psd.Layers.Select(l => l.FrameIndex));
        Assert.Equal(4, psd.FrameCount);
    }

    [Fact]
    public void ImageMagick_returns_one_frame_per_layer_with_pixels()
    {
        var path = Write("doc.psd", withComposite: true);
        using var frames = PhotoshopTools.ReadLayers(path, F.Psd);
        Assert.Equal(1 + PsdFile.Read(path).FrameCount, frames.Count);
        Assert.Equal(64u, frames[0].Width);
    }

    [Fact]
    public void The_flat_copy_is_used_when_the_file_has_one()
    {
        using var image = ImageLoader.Load(Write("doc.psd", withComposite: true));
        Assert.Equal((64u, 48u), (image.Width, image.Height));
        var p = image.GetPixels().GetPixel(32, 24).ToColor()!;
        Assert.Equal((10, 20, 30), (p.R, p.G, p.B));
    }

    [Fact]
    public void Without_a_flat_copy_the_visible_layers_are_combined()
    {
        var path = Write("noflat.psd", withComposite: false);
        Assert.False(PsdFile.Read(path).HasComposite);
        using var image = ImageLoader.Load(path);
        using var pixels = image.GetPixels();
        IMagickColor<byte> at(int x, int y) => pixels.GetPixel(x, y).ToColor()!;
        Assert.Equal((0, 0, 255), (at(60, 2).R, at(60, 2).G, at(60, 2).B));          // background
        Assert.Equal((255, 255, 255), (at(1, 1).R, at(1, 1).G, at(1, 1).B));         // top layer
        Assert.InRange(at(10, 10).R, 120, 135);                                       // half-opaque red over blue
        Assert.InRange(at(10, 10).B, 120, 135);
        Assert.Equal((0, 0, 255), (at(44, 34).R, at(44, 34).G, at(44, 34).B));        // the hidden green stays hidden
    }

    [Fact]
    public async Task Layers_are_saved_one_file_each_named_from_the_top()
    {
        var path = Write("doc.psd", withComposite: true);
        var folder = Path.Combine(Fixtures.NewDirectory(), "Layers");
        var files = await PhotoshopTools.ExportLayersAsync(path, folder, new LayerExportOptions());
        Assert.Equal(["01 Top.png", "02 Shapes - مربع أحمر.png", "03 Background.png"], files.Select(Path.GetFileName));

        using var red = new MagickImage(files[1]);
        Assert.Equal((20u, 10u), (red.Width, red.Height));
        var p = red.GetPixels().GetPixel(3, 3).ToColor()!;
        Assert.Equal((255, 0, 0), (p.R, p.G, p.B));
        Assert.InRange(p.A, 125, 130); // the layer's 50 % opacity

        var all = await PhotoshopTools.ExportLayersAsync(path, Path.Combine(Fixtures.NewDirectory(), "All"), new LayerExportOptions(FullCanvas: true, IncludeHidden: true));
        Assert.Equal(4, all.Count);
        using var green = new MagickImage(all.Single(f => f.Contains("Hidden green")));
        Assert.Equal((64u, 48u), (green.Width, green.Height));
        var g = green.GetPixels();
        Assert.Equal(255, g.GetPixel(44, 34).ToColor()!.G);
        Assert.Equal(0, g.GetPixel(10, 10).ToColor()!.A); // transparent outside the layer
    }

    [Fact]
    public async Task Photoshop_history_is_removed_and_the_layers_stay()
    {
        // Photoshop lists every document ever pasted in; thousands of entries are common.
        var ids = string.Concat(Enumerable.Range(0, 3000).Select(i => $"<rdf:li>xmp.did:{Guid.NewGuid()}</rdf:li>"));
        var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                  "<rdf:Description rdf:about=\"\" xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:CreatorTool=\"Adobe Photoshop 25.0\">" +
                  "<photoshop:DocumentAncestors><rdf:Bag>" + ids + "</rdf:Bag></photoshop:DocumentAncestors></rdf:Description></rdf:RDF></x:xmpmeta>";
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "heavy.psd");
        File.WriteAllBytes(input, PsdWriter.Write(64, 48, Layers, (_, _) => (10, 20, 30, 255), xmp: xmp));
        Assert.Equal(3000, PhotoshopTools.HistoryEntries(PsdFile.Read(input)));

        var slim = Path.Combine(dir, "heavy Slim.psd");
        var (before, after, entries) = await PhotoshopTools.RemoveHistoryAsync(input, slim);
        output.WriteLine($"{before} → {after} bytes");
        Assert.Equal(3000, entries);
        Assert.True(after < before - 100_000, $"{before} → {after}");
        var again = PsdFile.Read(slim);
        Assert.Equal(0, PhotoshopTools.HistoryEntries(again));
        Assert.Equal(PsdFile.Read(input).Layers.Select(l => (l.Name, l.Kind, l.Hidden, l.FrameIndex)), again.Layers.Select(l => (l.Name, l.Kind, l.Hidden, l.FrameIndex)));
        Assert.Contains("Adobe Photoshop 25.0", System.Text.Encoding.UTF8.GetString(again.Xmp!));
        using var flat = ImageLoader.Load(slim);
        Assert.Equal(10, flat.GetPixels().GetPixel(5, 5).ToColor()!.R);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PhotoshopTools.RemoveHistoryAsync(slim, Path.Combine(dir, "again.psd")));
    }

    [Fact]
    public async Task A_copy_for_sharing_loses_place_device_author_and_history_but_not_layers()
    {
        var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                  "<rdf:Description rdf:about=\"\" xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\" xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" xmlns:tiff=\"http://ns.adobe.com/tiff/1.0/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"" +
                  " exif:GPSLatitude=\"41,0.5N\" exif:GPSLongitude=\"28,58.7E\" tiff:Make=\"Canon\" tiff:Model=\"EOS R6\">" +
                  "<dc:creator><rdf:Seq><rdf:li>Yusuf</rdf:li></rdf:Seq></dc:creator>" +
                  "<photoshop:DocumentAncestors><rdf:Bag><rdf:li>xmp.did:1</rdf:li><rdf:li>xmp.did:2</rdf:li></rdf:Bag></photoshop:DocumentAncestors>" +
                  "</rdf:Description></rdf:RDF></x:xmpmeta>";
        var folder = Path.Combine(Fixtures.NewDirectory(), "تصاميم");
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "بطاقة.psd");
        File.WriteAllBytes(input, PsdWriter.Write(64, 48, Layers, (_, _) => (10, 20, 30, 255), xmp: xmp));
        var output = Path.Combine(folder, "بطاقة Clean.psd");
        await PhotoshopTools.CleanForSharingAsync(input, output);
        var clean = PsdFile.Read(output);
        var text = clean.Xmp is null ? "" : System.Text.Encoding.UTF8.GetString(clean.Xmp);
        foreach (var gone in new[] { "GPSLatitude", "EOS R6", "Yusuf", "DocumentAncestors" })
            Assert.DoesNotContain(gone, text);
        Assert.Equal(PsdFile.Read(input).Layers.Select(l => (l.Name, l.FrameIndex)), clean.Layers.Select(l => (l.Name, l.FrameIndex)));
        using var frames = PhotoshopTools.ReadLayers(output, F.Psd);
        Assert.Equal(5, frames.Count);

        // And the history alone, in a folder with an Arabic name.
        var slim = Path.Combine(folder, "بطاقة Slim.psd");
        var (_, _, entries) = await PhotoshopTools.RemoveHistoryAsync(input, slim);
        Assert.Equal(2, entries);
        Assert.Equal(0, PhotoshopTools.HistoryEntries(PsdFile.Read(slim)));
    }

    [Fact]
    public void A_file_that_isnt_Photoshop_is_refused_clearly()
    {
        var path = Path.Combine(Fixtures.NewDirectory(), "._resource.psd");
        File.WriteAllBytes(path, [0x00, 0x05, 0x16, 0x07, 0, 2, 0, 0, 1, 2, 3, 4]);
        var ex = Assert.Throws<InvalidDataException>(() => PsdFile.Read(path));
        Assert.Equal("This isn't a Photoshop file.", ex.Message);
    }

    /// <summary>
    /// Real documents (copies) in the folder PETAL_ADOBE_SAMPLES: every layer is matched to a
    /// frame, the flat picture loads, and every visible layer is saved. Only structure is checked.
    /// </summary>
    [Fact]
    public async Task Real_Photoshop_files_open_and_export()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_ADOBE_SAMPLES");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_ADOBE_SAMPLES isn't set.");
            return;
        }
        // Only real Photoshop files (not macOS "._" leftovers that share the extension).
        var files = Directory.GetFiles(folder).Where(f => F.FromPath(f) is { } fmt && (fmt == F.Psd || fmt == F.Psb) && PsdFile.IsPsd(File.ReadAllBytes(f).AsSpan(0, 6))).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var psd = PsdFile.Read(file);
            var kinds = psd.Layers.GroupBy(l => l.Kind).ToDictionary(g => g.Key, g => g.Count());
            using (var frames = PhotoshopTools.ReadLayers(file, ImageFormats.Detect(file)))
                Assert.Equal(1 + psd.FrameCount, frames.Count);
            using (var flat = ImageLoader.Load(file))
                Assert.Equal(((uint)psd.Width, (uint)psd.Height), (flat.Width, flat.Height));
            var dir = Path.Combine(Fixtures.NewDirectory(), "layers");
            var written = await PhotoshopTools.ExportLayersAsync(file, dir, new LayerExportOptions());
            int visible = psd.Layers.Count(l => l.FrameIndex >= 0 && !l.Hidden);
            Assert.Equal(visible, written.Count);
            Assert.All(written, w => Assert.True(new FileInfo(w).Length > 0));
            output.WriteLine($"{Path.GetFileName(file)}: {psd.Width}x{psd.Height} {psd.ColorMode} {psd.Depth}-bit, " +
                $"{psd.Layers.Count} records ({string.Join(", ", kinds.Select(k => $"{k.Value} {k.Key}"))}), " +
                $"{written.Count} exported, {psd.Fonts.Count} fonts, {psd.PlacedFiles.Count} placed, history {PhotoshopTools.HistoryEntries(psd)}, {clock.ElapsedMilliseconds} ms");
        }
    }
}
