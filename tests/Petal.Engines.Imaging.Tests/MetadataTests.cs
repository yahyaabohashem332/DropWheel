using System.Text;
using ImageMagick;
using Petal.Engines.Imaging.Metadata;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Tests;

public sealed class MetadataTests
{
    private const string Xmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
          <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/"
                xmlns:exif="http://ns.adobe.com/exif/1.0/" xmp:CreatorTool="Petal Test Suite">
              <dc:creator><rdf:Seq><rdf:li>Original Author</rdf:li></rdf:Seq></dc:creator>
              <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Sunset</rdf:li></rdf:Alt></dc:title>
              <dc:subject><rdf:Bag><rdf:li>beach</rdf:li><rdf:li>sea</rdf:li></rdf:Bag></dc:subject>
              <exif:GPSLatitude>48,51.492000N</exif:GPSLatitude>
              <exif:GPSLongitude>2,17.669400E</exif:GPSLongitude>
            </rdf:Description>
          </rdf:RDF>
        </x:xmpmeta>
        """;

    /// <summary>A JPEG with every kind of metadata, plus junk after EOI like an MPF gain map.</summary>
    private static string TaggedJpeg(string dir, bool rotated = false)
    {
        var path = Path.Combine(dir, rotated ? "tagged-rotated.jpg" : "tagged.jpg");
        using var image = Fixtures.Photo(160, 120);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "PetalCam");
        exif.SetValue(ExifTag.Model, "PetalCam Model 7");
        exif.SetValue(ExifTag.Artist, "Original Author");
        exif.SetValue(ExifTag.DateTimeOriginal, "2024:05:06 07:08:09");
        exif.SetValue(ExifTag.ExposureTime, new Rational(1, 125));
        exif.SetValue(ExifTag.FNumber, new Rational(28, 10));
        exif.SetValue(ExifTag.MakerNote, new byte[64]);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(48, 1), new Rational(51, 1), new Rational(2952, 100)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(2, 1), new Rational(17, 1), new Rational(4016, 100)]);
        exif.SetValue(ExifTag.GPSAltitudeRef, (byte)0);
        exif.SetValue(ExifTag.GPSAltitude, new Rational(35, 1));
        if (rotated)
        {
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            image.Orientation = Orientation.RightTop;
        }
        image.SetProfile(exif);
        image.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes(Xmp)));
        var iptc = new IptcProfile();
        iptc.SetValue(IptcTag.Byline, "Original Author");
        iptc.SetValue(IptcTag.City, "Paris");
        iptc.SetValue(IptcTag.Keyword, "beach");
        image.SetProfile(iptc);
        image.SetProfile(ColorProfiles.AdobeRGB1998);
        image.Comment = "secret comment";
        image.Write(path, MagickFormat.Jpeg);
        File.AppendAllText(path, "MPF-TRAILER-GAIN-MAP");
        return path;
    }

    [Fact]
    public async Task ReadsEveryGroupAndTheSummary()
    {
        var dir = Fixtures.NewDirectory();
        var report = await ImageMetadata.ReadAsync(TaggedJpeg(dir));

        Assert.True(report.IsLossless);
        foreach (var group in new[] { "File", "EXIF", "GPS", "XMP", "IPTC", "ICC Profile", "Text" })
            Assert.Contains(group, report.Groups);

        var make = report.Find(MetadataIds.Make)!;
        Assert.Equal("PetalCam", make.Value);
        Assert.True(make.IsEditable && make.IsRemovable);
        Assert.Equal("1/125 s", report.Entries.Single(e => e.Key == "ExposureTime").Value);
        Assert.Equal("f/2.8", report.Entries.Single(e => e.Key == "FNumber").Value);
        Assert.False(report.Entries.Single(e => e.Key == "MakerNote").IsEditable);
        Assert.Equal("Date Time Original", report.Find(MetadataIds.DateTimeOriginal)!.Name);
        Assert.Contains(report.Entries, e => e.Group == "XMP" && e.Key == "dc:creator" && e.Value == "Original Author" && e.IsEditable);
        Assert.Contains(report.Entries, e => e.Group == "XMP" && e.Key == "dc:subject" && e.Value == "beach; sea");
        Assert.Contains(report.Entries, e => e.Group == "IPTC" && e.Key == "City" && e.Value == "Paris");
        Assert.Contains(report.Entries, e => e.Id == MetadataIds.Comment && e.Value == "secret comment");
        Assert.Contains("Adobe", report.Find(MetadataIds.ColorProfile)!.Value);
        Assert.All(report.Entries.Where(e => e.Group == "File"), e => Assert.False(e.IsEditable || e.IsRemovable));

        var s = report.Summary;
        Assert.NotNull(s.Location);
        Assert.Equal(48.8582, s.Location!.Latitude, 3);
        Assert.Equal(2.2945, s.Location.Longitude, 3);
        Assert.Equal(35, s.Location.Altitude!.Value, 1);
        Assert.Equal("PetalCam Model 7", s.Camera);
        Assert.Equal("Original Author", s.Author);
        Assert.Equal("2024:05:06 07:08:09", s.DateTaken);
        Assert.NotEmpty(report.Search("paris"));
    }

    [Fact]
    public async Task EditingJpegKeepsCompressedPixels()
    {
        var dir = Fixtures.NewDirectory();
        var input = TaggedJpeg(dir);
        var output = Path.Combine(dir, "edited.jpg");
        await ImageMetadata.ApplyAsync(input, output,
        [
            new MetadataChange.Set(MetadataIds.Make, "Other Brand"),
            new MetadataChange.Set(MetadataIds.Copyright, "© 2026 Petal"),
            new MetadataChange.Remove(MetadataIds.Model),
            new MetadataChange.Set("xmp:http://purl.org/dc/elements/1.1/#title", "Sunrise"),
            new MetadataChange.Set("iptc:City:0", "Lyon"),
            new MetadataChange.Set(MetadataIds.Comment, "new comment"),
        ]);

        Assert.Equal(JpegFile.Parse(File.ReadAllBytes(input)).ScanData, JpegFile.Parse(File.ReadAllBytes(output)).ScanData);
        using var result = new MagickImage(output);
        var exif = result.GetExifProfile()!;
        Assert.Equal("Other Brand", exif.GetValue(ExifTag.Make)?.Value);
        Assert.Equal("© 2026 Petal", exif.GetValue(ExifTag.Copyright)?.Value);
        Assert.Null(exif.GetValue(ExifTag.Model));
        Assert.Equal("Original Author", exif.GetValue(ExifTag.Artist)?.Value);
        Assert.Equal("Lyon", result.GetIptcProfile()!.GetValue(IptcTag.City)!.Value);
        Assert.Equal("new comment", result.Comment);

        var report = await ImageMetadata.ReadAsync(output);
        Assert.Equal("Sunrise", report.Entries.Single(e => e.Key == "dc:title").Value);
        Assert.Contains("Adobe", report.Find(MetadataIds.ColorProfile)!.Value);
    }

    [Fact]
    public async Task LocationAndAuthorEdits()
    {
        var dir = Fixtures.NewDirectory();
        var input = TaggedJpeg(dir);
        var moved = Path.Combine(dir, "moved.jpg");
        await ImageMetadata.ApplyAsync(input, moved,
        [
            new MetadataChange.SetLocation(new GpsLocation(-33.8568, 151.2153, 12)),
            new MetadataChange.SetAuthor("Yahya"),
        ]);
        var report = await ImageMetadata.ReadAsync(moved);
        Assert.Equal(-33.8568, report.Summary.Location!.Latitude, 4);
        Assert.Equal(151.2153, report.Summary.Location.Longitude, 4);
        Assert.Equal("Yahya", report.Summary.Author);
        Assert.Contains(report.Entries, e => e.Key == "dc:creator" && e.Value == "Yahya");
        Assert.Contains(report.Entries, e => e.Key == "Byline" && e.Value == "Yahya");
        Assert.Contains(report.Entries, e => e.Key == "exif:GPSLatitude" && e.Value.EndsWith('S'));

        var hidden = Path.Combine(dir, "hidden.jpg");
        await ImageMetadata.ApplyAsync(moved, hidden, [new MetadataChange.SetLocation(null), new MetadataChange.SetAuthor(null)]);
        var cleared = await ImageMetadata.ReadAsync(hidden);
        Assert.Null(cleared.Summary.Location);
        Assert.DoesNotContain(cleared.Entries, e => e.Group == "GPS");
        Assert.DoesNotContain(cleared.Entries, e => e.Key.Contains("GPS", StringComparison.Ordinal));
        Assert.Null(cleared.Summary.Author);
        Assert.Equal("PetalCam", cleared.Find(MetadataIds.Make)!.Value);
    }

    [Fact]
    public async Task RemoveAllStripsEverythingButColourAndOrientation()
    {
        var dir = Fixtures.NewDirectory();
        var input = TaggedJpeg(dir, rotated: true);
        var output = Path.Combine(dir, "clean.jpg");
        await ImageMetadata.RemoveAllAsync(input, output);

        var bytes = File.ReadAllBytes(output);
        Assert.Equal(JpegFile.Parse(File.ReadAllBytes(input)).ScanData, JpegFile.Parse(bytes).ScanData);
        Assert.DoesNotContain("MPF-TRAILER", Encoding.Latin1.GetString(bytes));
        Assert.DoesNotContain("secret comment", Encoding.Latin1.GetString(bytes));
        Assert.DoesNotContain("PetalCam", Encoding.Latin1.GetString(bytes));

        var report = await ImageMetadata.ReadAsync(output);
        Assert.DoesNotContain(report.Entries, e => e.Group is "GPS" or "XMP" or "IPTC" or "Text");
        var exif = Assert.Single(report.Entries, e => e.Group == "EXIF");
        Assert.Equal("Orientation", exif.Key);
        Assert.False(exif.IsRemovable);
        Assert.Contains("Adobe", report.Find(MetadataIds.ColorProfile)!.Value);

        using var image = new MagickImage(output);
        Assert.Equal(Orientation.RightTop, image.Orientation);
    }

    [Fact]
    public async Task RejectsInvalidEdits()
    {
        var dir = Fixtures.NewDirectory();
        var input = TaggedJpeg(dir, rotated: true);
        var output = Path.Combine(dir, "x.jpg");
        await Assert.ThrowsAsync<ArgumentException>(() => ImageMetadata.RemoveTagAsync(input, output, "exif:0112"));
        await Assert.ThrowsAsync<ArgumentException>(() => ImageMetadata.ApplyAsync(input, output, [new MetadataChange.Set("file:name", "x")]));
        await Assert.ThrowsAsync<ArgumentException>(() => ImageMetadata.ApplyAsync(input, output, [new MetadataChange.Set("exif:829D", "fast")]));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task PngEditsAndRemoveAllKeepPixels()
    {
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "tagged.png");
        using (var image = Fixtures.Photo(120, 90))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "PngCam");
            image.SetProfile(exif);
            image.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes(Xmp)));
            image.SetAttribute("Software", "Petal Tests");
            image.Comment = "png comment";
            image.Write(input, MagickFormat.Png);
        }
        var report = await ImageMetadata.ReadAsync(input);
        Assert.Equal("PngCam", report.Find(MetadataIds.Make)!.Value);
        Assert.Contains(report.Entries, e => e.Group == "Text" && e.Key == "Software" && e.Value == "Petal Tests");
        Assert.Contains(report.Entries, e => e.Key == "dc:title");

        var edited = Path.Combine(dir, "edited.png");
        await ImageMetadata.ApplyAsync(input, edited,
        [
            new MetadataChange.Set(MetadataIds.Make, "PngCam 2"),
            new MetadataChange.Set("text:Software", "Petal ✓"),
            new MetadataChange.Remove("xmp:http://purl.org/dc/elements/1.1/#title"),
        ]);
        Assert.Equal(Idat(input), Idat(edited));
        var after = await ImageMetadata.ReadAsync(edited);
        Assert.Equal("PngCam 2", after.Find(MetadataIds.Make)!.Value);
        Assert.Contains(after.Entries, e => e.Key == "Software" && e.Value == "Petal ✓");
        Assert.DoesNotContain(after.Entries, e => e.Key == "dc:title");
        Assert.Contains(after.Entries, e => e.Key == "dc:creator");

        var clean = Path.Combine(dir, "clean.png");
        await ImageMetadata.RemoveAllAsync(input, clean);
        Assert.Equal(Idat(input), Idat(clean));
        var stripped = await ImageMetadata.ReadAsync(clean);
        Assert.DoesNotContain(stripped.Entries, e => e.Group is "EXIF" or "XMP" or "Text");
    }

    [Fact]
    public async Task WebpEditsWithoutReencoding()
    {
        var dir = Fixtures.NewDirectory();
        string input;
        using (var image = Fixtures.Photo(120, 90))
            input = Fixtures.Save(image, dir, F.Webp); // a simple (non-extended) WebP with no metadata
        var tagged = Path.Combine(dir, "tagged.webp");
        await ImageMetadata.ApplyAsync(input, tagged, [new MetadataChange.Set(MetadataIds.Make, "WebpCam"), new MetadataChange.SetAuthor("Yahya")]);

        var original = WebpFile.Parse(File.ReadAllBytes(input));
        var result = WebpFile.Parse(File.ReadAllBytes(tagged));
        Assert.Equal(original.Chunks.First(c => c.FourCC.StartsWith("VP8")).Data, result.Chunks.First(c => c.FourCC is "VP8 " or "VP8L").Data);
        using (var image = new MagickImage(tagged))
        {
            Assert.Equal("WebpCam", image.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
            Assert.Equal(120u, image.Width);
        }

        var clean = Path.Combine(dir, "clean.webp");
        await ImageMetadata.RemoveAllAsync(tagged, clean);
        Assert.DoesNotContain((await ImageMetadata.ReadAsync(clean)).Entries, e => e.Group == "EXIF");
    }

    [Theory]
    [InlineData("heic")]
    [InlineData("tiff")]
    [InlineData("avif")]
    public async Task ReencodedFormatsSupportEditsAndRemoveAll(string id)
    {
        var dir = Fixtures.NewDirectory();
        var format = F.FromId(id)!;
        string input;
        using (var image = Fixtures.Quadrants())
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "ReCam");
            image.SetProfile(exif);
            image.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes(Xmp)));
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            input = Fixtures.Save(image, dir, format);
        }
        var report = await ImageMetadata.ReadAsync(input);
        Assert.False(report.IsLossless);
        Assert.Contains(report.Entries, e => e.Key == "dc:creator");

        Assert.Equal("ReCam", report.Find(MetadataIds.Make)?.Value);

        var edited = Path.Combine(dir, "edited." + format.WriteExtension);
        await ImageMetadata.ApplyAsync(input, edited, [new MetadataChange.SetAuthor("Yahya"), new MetadataChange.Set(MetadataIds.Make, "ReCam 2")]);
        var after = await ImageMetadata.ReadAsync(edited);
        Assert.Contains(after.Entries, e => e.Key == "dc:creator" && e.Value == "Yahya");
        Assert.Equal("ReCam 2", after.Find(MetadataIds.Make)?.Value);
        Assert.Equal("Yahya", after.Find(MetadataIds.Artist)?.Value);

        var clean = Path.Combine(dir, "clean." + format.WriteExtension);
        await ImageMetadata.RemoveAllAsync(input, clean);
        var stripped = await ImageMetadata.ReadAsync(clean);
        Assert.DoesNotContain(stripped.Entries, e => e.Group is "EXIF" or "XMP");
        Assert.Contains("Adobe", stripped.Find(MetadataIds.ColorProfile)?.Value ?? "");
        using var decoded = new MagickImage(clean);
        Fixtures.AssertQuadrants(decoded, 24, id);
    }

    [Fact]
    public async Task BatchRemoveAll()
    {
        var dir = Fixtures.NewDirectory();
        var a = TaggedJpeg(dir);
        var b = TaggedJpeg(Fixtures.NewDirectory(), rotated: true);
        var results = await ImageMetadata.RemoveAllBatchAsync([new ImageJobItem(a, Path.Combine(dir, "a.jpg")), new ImageJobItem(b, Path.Combine(dir, "b.jpg"))]);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error));
        Assert.All(results, r => Assert.DoesNotContain("PetalCam", Encoding.Latin1.GetString(File.ReadAllBytes(r.OutputPath))));
    }

    [Fact]
    public async Task AnimatedGifKeepsFramesWhenStripped()
    {
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "anim.gif");
        using (var frames = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Blue, MagickColors.Lime })
            {
                var frame = new MagickImage(color, 40, 30) { AnimationDelay = 20 };
                frame.Comment = "gif comment";
                frames.Add(frame);
            }
            frames.Write(input, MagickFormat.Gif);
        }
        var report = await ImageMetadata.ReadAsync(input);
        Assert.Contains(report.Entries, e => e.Id == MetadataIds.Comment && e.Value == "gif comment");

        var edited = Path.Combine(dir, "edited.gif");
        await ImageMetadata.ApplyAsync(input, edited, [new MetadataChange.Set(MetadataIds.Comment, "new")]);
        Assert.Equal("new", (await ImageMetadata.ReadAsync(edited)).Find(MetadataIds.Comment)?.Value);

        var clean = Path.Combine(dir, "clean.gif");
        await ImageMetadata.RemoveAllAsync(input, clean);
        using var result = new MagickImageCollection(clean);
        Assert.Equal(3, result.Count);
        Assert.DoesNotContain((await ImageMetadata.ReadAsync(clean)).Entries, e => e.Group == "Text");
    }

    [Fact]
    public async Task OversizedExifDropsItsThumbnailToFitAJpeg()
    {
        var dir = Fixtures.NewDirectory();
        var input = Path.Combine(dir, "bigthumb.jpg");
        using (var photo = Fixtures.Photo(400, 300))
        {
            // A large thumbnail: fine as stored, too big once a long description is added.
            using var thumb = Fixtures.Photo(300, 225);
            byte[] data;
            uint quality = 100;
            do
            {
                thumb.Quality = quality;
                data = thumb.ToByteArray(MagickFormat.Jpeg);
                quality -= 4;
            }
            while (data.Length > 50_000);
            Assert.True(data.Length > 30_000, $"thumbnail {data.Length} bytes");
            photo.SetProfile(Fixtures.ExifWithThumbnail(data, "ThumbCam"));
            photo.Write(input, MagickFormat.Jpeg);
        }
        var output = Path.Combine(dir, "described.jpg");
        await ImageMetadata.ApplyAsync(input, output, [new MetadataChange.Set(MetadataIds.ImageDescription, new string('x', 40_000))]);
        using var result = new MagickImage(output);
        Assert.Equal(40_000, result.GetExifProfile()!.GetValue(ExifTag.ImageDescription)!.Value.Length);
        Assert.Equal(0u, result.GetExifProfile()!.ThumbnailLength);
        Assert.Equal("ThumbCam", result.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);
    }

    private static byte[] Idat(string path) =>
        PngFile.Parse(File.ReadAllBytes(path)).Chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
}
