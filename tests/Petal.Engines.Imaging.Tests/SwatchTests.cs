using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ImageMagick;

namespace Petal.Engines.Imaging.Tests;

public sealed class SwatchTests
{
    private static void U16(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        s.Write(b);
    }

    private static void U32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void F32(Stream s, float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, v);
        s.Write(b);
    }

    private static byte[] Name(string name)
    {
        var o = new MemoryStream();
        U16(o, name.Length + 1);
        o.Write(Encoding.BigEndianUnicode.GetBytes(name + "\0"));
        return o.ToArray();
    }

    private static void Block(Stream o, int type, byte[] body)
    {
        U16(o, type);
        U32(o, (uint)body.Length);
        o.Write(body);
    }

    private static byte[] Colour(string name, string model, float[] values, int type)
    {
        var o = new MemoryStream();
        o.Write(Name(name));
        o.Write(Encoding.ASCII.GetBytes(model));
        foreach (var v in values)
            F32(o, v);
        U16(o, type);
        return o.ToArray();
    }

    /// <summary>An ASE file laid out by hand from Adobe's format: a group, then loose colours.</summary>
    private static string HandMadeAse()
    {
        var o = new MemoryStream();
        o.Write("ASEF"u8);
        U16(o, 1);
        U16(o, 0);
        U32(o, 6);
        Block(o, 0xC001, Name("Brand"));
        Block(o, 0x0001, Colour("Petal Violet", "RGB ", [0.5f, 0.3f, 1f], 0));
        Block(o, 0x0001, Colour("Ink Red", "CMYK", [0f, 1f, 1f, 0f], 1));
        Block(o, 0xC002, []);
        Block(o, 0x0001, Colour("Paper", "Gray", [1f], 2));
        Block(o, 0x0001, Colour("لون", "LAB ", [0.5f, 20f, -30f], 2));
        var path = Path.Combine(Fixtures.NewDirectory(), "brand.ase");
        File.WriteAllBytes(path, o.ToArray());
        return path;
    }

    [Fact]
    public void An_ASE_file_is_read_with_groups_models_and_spot_colours()
    {
        var swatches = Swatches.Read(HandMadeAse());
        Assert.Equal(["Petal Violet", "Ink Red", "Paper", "لون"], swatches.Select(s => s.Name));
        Assert.Equal(["RGB", "CMYK", "Gray", "Lab"], swatches.Select(s => s.Model));
        Assert.Equal(["Brand", "Brand", null, null], swatches.Select(s => s.Group));
        Assert.Equal([false, true, false, false], swatches.Select(s => s.Spot));
        Assert.Equal("#804DFF", swatches[0].Hex); // 0.5 is 128
        Assert.Equal("#FF0000", swatches[1].Hex);
        Assert.Equal("#FFFFFF", swatches[2].Hex);
        Assert.Equal(50, swatches[3].Values[0], 3); // Lab lightness is stored as 0–1
    }

    [Fact]
    public void Lab_colours_turn_into_the_right_screen_colours()
    {
        // L 50 grey is sRGB 119; sRGB red is Lab (D50) 54.29, 80.80, 69.89.
        Assert.Equal((119, 119, 119), Expand(Swatches.ToRgb("Lab", [50, 0, 0])));
        var red = Swatches.ToRgb("Lab", [54.29f, 80.80f, 69.89f]);
        Assert.InRange((int)red.R, 252, 255);
        Assert.InRange((int)red.G, 0, 4);
        Assert.InRange((int)red.B, 0, 4);
    }

    private static (int, int, int) Expand((byte R, byte G, byte B) c) => (c.R, c.G, c.B);

    [Fact]
    public void Swatches_survive_ASE_and_ACO_round_trips()
    {
        var original = Swatches.Read(HandMadeAse());
        var dir = Fixtures.NewDirectory();
        var ase = Path.Combine(dir, "again.ase");
        Swatches.WriteAse(original, ase);
        var back = Swatches.Read(ase);
        Assert.Equal(original.Select(s => (s.Name, s.Model, s.Group, s.Spot, s.Hex)), back.Select(s => (s.Name, s.Model, s.Group, s.Spot, s.Hex)));

        var aco = Path.Combine(dir, "photoshop.aco");
        Swatches.WriteAco(original, aco);
        var fromAco = Swatches.Read(aco);
        // Photoshop swatches have no groups or spot flag, but keep names, models and colours.
        Assert.Equal(original.Select(s => (s.Name, s.Model, s.Hex)), fromAco.Select(s => (s.Name, s.Model, s.Hex)));
    }

    [Fact]
    public void A_Photoshop_ACO_file_takes_the_names_from_its_second_part()
    {
        var o = new MemoryStream();
        U16(o, 1); U16(o, 1);
        U16(o, 0); U16(o, 65535); U16(o, 32768); U16(o, 0); U16(o, 0);
        U16(o, 2); U16(o, 1);
        U16(o, 0); U16(o, 65535); U16(o, 32768); U16(o, 0); U16(o, 0);
        U32(o, 7); o.Write(Encoding.BigEndianUnicode.GetBytes("Orange\0"));
        var path = Path.Combine(Fixtures.NewDirectory(), "one.aco");
        File.WriteAllBytes(path, o.ToArray());
        var swatch = Assert.Single(Swatches.Read(path));
        Assert.Equal(("Orange", "#FF8000"), (swatch.Name, swatch.Hex));
    }

    [Fact]
    public void Swatches_export_for_code_and_other_apps()
    {
        var swatches = Swatches.Read(HandMadeAse());
        var css = Swatches.ToCss(swatches);
        Assert.Contains("--petal-violet: #804dff;", css);
        Assert.Contains("--paper: #ffffff;", css);
        Assert.Contains("--لون:", css);
        using var json = JsonDocument.Parse(Swatches.ToJson(swatches));
        Assert.Equal("Ink Red", json.RootElement[1].GetProperty("name").GetString());
        Assert.True(json.RootElement[1].GetProperty("spot").GetBoolean());
        var gpl = Swatches.ToGpl(swatches, "Brand");
        Assert.StartsWith("GIMP Palette\nName: Brand\n", gpl);
        Assert.Contains("128  77 255\tPetal Violet", gpl);
    }

    [Fact]
    public void Swatches_become_a_picture_of_named_chips()
    {
        var swatches = Swatches.Read(HandMadeAse());
        var path = Path.Combine(Fixtures.NewDirectory(), "sheet.png");
        Swatches.WriteSheet(swatches, path);
        using var sheet = new MagickImage(path);
        Assert.Equal((16u + 4 * 176, 16u + 1 * 230), (sheet.Width, sheet.Height));
        var chip = sheet.GetPixels().GetPixel(16 + 80, 16 + 80).ToColor()!;
        Assert.Equal((128, 77, 255), ((int)chip.R, (int)chip.G, (int)chip.B));
    }

    [Fact]
    public void The_main_colours_of_a_picture_come_most_used_first()
    {
        using var image = new MagickImage(MagickColors.White, 100, 100);
        Fixtures.Fill(image, 0, 0, 60, 100, new MagickColor(200, 30, 40));
        Fixtures.Fill(image, 60, 0, 30, 100, new MagickColor(20, 60, 180));
        var swatches = Swatches.Extract(image, 6);
        Assert.Equal(3, swatches.Count); // near-identical clusters merge
        Assert.Equal(["#C81E28", "#143CB4", "#FFFFFF"], swatches.Select(s => s.Hex));
    }
}
