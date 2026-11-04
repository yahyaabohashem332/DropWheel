using System.IO;
using System.Windows.Media;
using Petal.Core.Jobs;
using Petal.Engines.Files.Fonts;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>Fonts between TTF, OTF, WOFF and WOFF2: read back by Windows' own font engine, every letter compared with the original.</summary>
public sealed class FontConverterTests : IDisposable
{
    private const string Arial = @"C:\Windows\Fonts\arial.ttf";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Petal", "font-" + Guid.NewGuid().ToString("N"));

    public FontConverterTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    /// <summary>The glyph outlines of some letters as Windows reads the font: bounds in font units, with the advance.</summary>
    private static List<(string Bounds, double Advance)> Letters(string path, string text)
    {
        var result = new List<(string, double)>();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var face = new GlyphTypeface(new Uri(path));
                foreach (char c in text)
                {
                    var glyph = face.CharacterToGlyphMap[c];
                    var outline = face.GetGlyphOutline(glyph, 2048, 2048);
                    var r = outline.Bounds;
                    result.Add((r.IsEmpty ? "empty" : $"{r.Left:0},{r.Top:0},{r.Right:0},{r.Bottom:0}", face.AdvanceWidths[glyph]));
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new InvalidOperationException(path, error);
        return result;
    }

    private static void CloseEnough(List<(string Bounds, double Advance)> a, List<(string Bounds, double Advance)> b, int slack)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Advance, b[i].Advance, 4);
            if (a[i].Bounds == b[i].Bounds)
                continue;
            var x = a[i].Bounds.Split(',').Select(int.Parse).ToArray();
            var y = b[i].Bounds.Split(',').Select(int.Parse).ToArray();
            for (int k = 0; k < 4; k++)
                Assert.True(Math.Abs(x[k] - y[k]) <= slack, $"Letter {i}: {a[i].Bounds} against {b[i].Bounds}");
        }
    }

    private const string Text = "AaBbgjQ&@%0123 éçşğ ءابتثجحخسعلمنهوي";

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Otf_made_from_a_ttf_draws_the_same_letters()
    {
        if (!File.Exists(Arial))
            return;
        var source = File.ReadAllBytes(Arial);
        var otf = Write("a.otf", FontConverter.Convert(source, F.Otf));
        Assert.Equal((uint)SfntFont.Cff, SfntFont.Read(File.ReadAllBytes(otf)).Flavor);
        Assert.Contains("CFF ", SfntFont.Read(File.ReadAllBytes(otf)).Tables.Keys);
        Assert.DoesNotContain("glyf", SfntFont.Read(File.ReadAllBytes(otf)).Tables.Keys);
        CloseEnough(Letters(Arial, Text), Letters(otf, Text), 2);
    }

    [Fact]
    public void Ttf_made_from_that_otf_draws_the_same_letters_again()
    {
        if (!File.Exists(Arial))
            return;
        var source = File.ReadAllBytes(Arial);
        var ttf = Write("b.ttf", FontConverter.Convert(FontConverter.Convert(source, F.Otf), F.Ttf));
        var font = SfntFont.Read(File.ReadAllBytes(ttf));
        Assert.Equal(SfntFont.TrueType, font.Flavor);
        Assert.Contains("glyf", font.Tables.Keys);
        Assert.DoesNotContain("CFF ", font.Tables.Keys);
        CloseEnough(Letters(Arial, Text), Letters(ttf, Text), 3);
    }

    [Fact]
    public void Shaping_and_names_come_through()
    {
        if (!File.Exists(Arial))
            return;
        var source = SfntFont.Read(File.ReadAllBytes(Arial));
        var otf = SfntFont.Read(FontConverter.Convert(File.ReadAllBytes(Arial), F.Otf));
        foreach (var tag in new[] { "GSUB", "GPOS", "GDEF", "cmap", "name", "OS/2", "hhea", "hmtx" })
            if (source.Tables.TryGetValue(tag, out var table))
                Assert.Equal(table, otf.Tables[tag]);
    }

    [Theory]
    [InlineData("woff")]
    [InlineData("woff2")]
    public void A_web_font_gives_back_exactly_the_font_it_was_made_from(string kind)
    {
        if (!File.Exists(Arial))
            return;
        var bytes = File.ReadAllBytes(Arial);
        var source = SfntFont.Read(bytes);
        var packed = FontConverter.Convert(bytes, F.FromId(kind)!);
        Assert.True(packed.Length < bytes.Length / 1.3, $"{packed.Length} of {bytes.Length}");
        var back = SfntFont.Read(packed);
        foreach (var (tag, table) in source.Tables)
        {
            if (tag is "glyf" or "loca" or "DSIG")
                continue;
            Assert.Equal(table, back.Tables[tag]);
        }
        var ttf = Write($"{kind}.ttf", FontConverter.Convert(packed, F.Ttf));
        CloseEnough(Letters(Arial, Text), Letters(ttf, Text), 0);
    }

    [Fact]
    public void Woff2_keeps_every_points_and_every_hint_of_the_outlines()
    {
        if (!File.Exists(Arial))
            return;
        var bytes = File.ReadAllBytes(Arial);
        var source = SfntFont.Read(bytes);
        var back = SfntFont.Read(FontConverter.Convert(bytes, F.Woff2));
        var before = GlyfTable.Read(source);
        var after = GlyfTable.Read(back);
        Assert.Equal(before.Length, after.Length);
        for (int g = 0; g < before.Length; g++)
        {
            Assert.Equal(before[g].Count, after[g].Count);
            for (int c = 0; c < before[g].Count; c++)
                Assert.Equal(before[g][c], after[g][c]);
        }
    }

    [Fact]
    public void An_otf_as_woff2_stays_an_otf()
    {
        if (!File.Exists(Arial))
            return;
        var otf = FontConverter.Convert(File.ReadAllBytes(Arial), F.Otf);
        var back = SfntFont.Read(FontConverter.Convert(otf, F.Woff2));
        Assert.Equal((uint)SfntFont.Cff, back.Flavor);
        Assert.Equal(SfntFont.Read(otf).Table("CFF "), back.Table("CFF "));
    }

    [Fact]
    public void A_cubic_becomes_quadratics_that_stay_close()
    {
        var path = new List<PathStep> { new('M', 0, 0, 0, 0, 0, 0), new('C', 0, 500, 500, 1000, 1000, 1000), new('L', 0, 0, 0, 0, 1000, 0), new('L', 0, 0, 0, 0, 0, 0) };
        var contour = Assert.Single(FontConverter.Quadratic(path, 0.5));
        Assert.True(contour.Count > 6);
        // Direction turned round, the first point of the path present and not repeated.
        Assert.Single(contour, p => p.X == 0 && p.Y == 0);
        Assert.Contains(contour, p => !p.On);
    }

    [Fact]
    public void A_quadratic_becomes_a_cubic_on_the_very_same_curve()
    {
        var contour = new List<OutlinePoint> { new(0, 0, true), new(300, 600, false), new(600, 0, true) };
        var path = FontConverter.Cubic([contour]);
        var curve = path.Single(s => s.Kind == 'C');
        // At its middle, both pass through the same point.
        double Mid(double a, double b, double c, double d) => (a + 3 * b + 3 * c + d) / 8;
        Assert.Equal(300, Mid(curve.C1X == 0 ? 0 : 600, curve.C1X, curve.C2X, curve.X == 0 ? 0 : 600), 3);
    }

    [Fact]
    public void What_isnt_a_font_says_so()
    {
        Assert.Throws<InvalidDataException>(() => FontConverter.Convert("this is not a font at all"u8.ToArray(), F.Otf));
        var collection = new byte[32];
        "ttcf"u8.CopyTo(collection);
        Assert.Contains("collection", Assert.Throws<InvalidDataException>(() => FontConverter.Convert(collection, F.Woff2)).Message);
    }

    [Fact]
    public async Task A_conversion_writes_its_file()
    {
        if (!File.Exists(Arial))
            return;
        var converter = new FontConverter();
        Assert.True(converter.CanConvert(F.Ttf, F.Woff2));
        Assert.False(converter.CanConvert(F.Ttf, F.Ttf));
        Assert.False(converter.CanConvert(F.Ttf, F.Pdf));
        var output = Path.Combine(_folder, "out.woff2");
        var result = await converter.ConvertAsync(new ConversionJob(Arial, F.Ttf, F.Woff2, output, ConversionSettings.Default), null, default);
        Assert.Equal(output, result.Primary);
        Assert.True(new FileInfo(output).Length > 1000);
    }

    /// <summary>For checking in a browser, whose strict font checker has the last word: PETAL_FONTS is a folder to put them in.</summary>
    [Fact]
    public void Fonts_for_a_browser_to_read()
    {
        if (Environment.GetEnvironmentVariable("PETAL_FONTS") is not { Length: > 0 } folder || !File.Exists(Arial))
            return;
        Directory.CreateDirectory(folder);
        var bytes = File.ReadAllBytes(Arial);
        var otf = FontConverter.Convert(bytes, F.Otf);
        var ttf = FontConverter.Convert(otf, F.Ttf);
        File.WriteAllBytes(Path.Combine(folder, "from-ttf.otf"), otf);
        File.WriteAllBytes(Path.Combine(folder, "from-otf.ttf"), ttf);
        File.WriteAllBytes(Path.Combine(folder, "ttf.woff"), FontConverter.Convert(bytes, F.Woff));
        File.WriteAllBytes(Path.Combine(folder, "ttf.woff2"), FontConverter.Convert(bytes, F.Woff2));
        File.WriteAllBytes(Path.Combine(folder, "otf.woff2"), FontConverter.Convert(otf, F.Woff2));
        File.WriteAllBytes(Path.Combine(folder, "converted.woff2"), FontConverter.Convert(ttf, F.Woff2));
    }
}
