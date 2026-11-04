using System.IO.Compression;
using System.Text;
using Petal.Core.Jobs;
using Petal.Engines.Documents.IWork;
using Xunit;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.Tests;

/// <summary>Apple's documents, built here to their published structure, as no real one can be kept in the tests.</summary>
public sealed class IWorkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Petal", "iwork-" + Guid.NewGuid().ToString("N"));

    public IWorkTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return [.. bytes];
    }

    private static byte[] Bytes(int field, byte[] data) => [.. Varint((ulong)(field << 3 | 2)), .. Varint((ulong)data.Length), .. data];

    private static byte[] Number(int field, ulong value) => [.. Varint((ulong)(field << 3)), .. Varint(value)];

    /// <summary>One archive: its header, with its id and its one message's type and length, then the message.</summary>
    private static byte[] Archive(ulong id, int type, byte[] message)
    {
        byte[] info = [.. Number(1, (ulong)type), .. Number(2, 1), .. Number(3, (ulong)message.Length)];
        byte[] header = [.. Number(1, id), .. Bytes(2, info)];
        return [.. Varint((ulong)header.Length), .. header, .. message];
    }

    private static byte[] Text(string text) => Bytes(3, Encoding.UTF8.GetBytes(text));

    /// <summary>Snappy of literals only, as valid as any.</summary>
    private static byte[] Iwa(byte[] data)
    {
        var o = new MemoryStream();
        var body = new MemoryStream();
        body.Write(Varint((ulong)data.Length));
        int at = 0;
        while (at < data.Length)
        {
            int run = Math.Min(60, data.Length - at);
            body.WriteByte((byte)((run - 1) << 2));
            body.Write(data, at, run);
            at += run;
        }
        o.WriteByte(0);
        o.WriteByte((byte)body.Length);
        o.WriteByte((byte)(body.Length >> 8));
        o.WriteByte((byte)(body.Length >> 16));
        body.WriteTo(o);
        return o.ToArray();
    }

    private string Pages(string name, Action<ZipArchive> fill)
    {
        var path = Path.Combine(_folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        fill(zip);
        return path;
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(data);
    }

    [Fact]
    public void The_words_are_read_in_order_with_their_marks_gone()
    {
        var data = new List<byte>();
        data.AddRange(Archive(200, 2001, Text("Second paragraph" + (char)0xFFFC)));
        data.AddRange(Archive(100, 2001, Text("مرحبا بالعالم" + (char)0x2029 + "First line")));
        data.AddRange(Archive(300, 9999, Text("Not text at all")));
        var path = Pages("a.pages", z => Add(z, "Index/Document.iwa", Iwa([.. data])));
        var text = IWorkFile.Text(path);
        Assert.Equal("مرحبا بالعالم\nFirst line\n\nSecond paragraph", text);
    }

    [Fact]
    public void Words_inside_an_inner_zip_are_read_too()
    {
        var inner = new MemoryStream();
        using (var zip = new ZipArchive(inner, ZipArchiveMode.Create, true))
            Add(zip, "Index/Document.iwa", Iwa(Archive(1, 2001, Text("Inside"))));
        var path = Pages("b.key", z => Add(z, "Index.zip", inner.ToArray()));
        Assert.Equal("Inside", IWorkFile.Text(path));
    }

    [Fact]
    public void Numbers_cells_strings_are_read()
    {
        byte[] entry = [.. Number(1, 1), .. Text("Budget")];
        var path = Pages("c.numbers", z => Add(z, "Index/Tables/DataList.iwa", Iwa(Archive(5, 6005, Bytes(3, entry)))));
        Assert.Equal("Budget", IWorkFile.Text(path));
    }

    [Fact]
    public void Snappy_copies_are_undone()
    {
        // "abcd" then a copy of 4 from 4 back, length 8 in all.
        byte[] packed = [8, 3 << 2, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (0 << 2) | 1 | (0 << 5), 4];
        var output = new MemoryStream();
        IWorkFile.Snappy(packed, 0, packed.Length, output);
        Assert.Equal("abcdabcd", Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public void Damage_is_said_not_crashed_on()
    {
        Assert.Throws<InvalidDataException>(() => IWorkFile.Unpack([9, 0, 0, 0]));
        Assert.Throws<InvalidDataException>(() => IWorkFile.Unpack([0, 200, 0, 0, 1]));
        var notZip = Path.Combine(_folder, "x.pages");
        File.WriteAllText(notZip, "not a zip");
        Assert.Throws<InvalidDataException>(() => IWorkFile.Text(notZip));
    }

    [Fact]
    public async Task A_pdf_inside_is_given_as_it_is()
    {
        byte[] pdf = "%PDF-1.4 whole document"u8.ToArray();
        var path = Pages("d.pages", z => Add(z, "QuickLook/Preview.pdf", pdf));
        var output = Path.Combine(_folder, "d.pdf");
        await new IWorkConverter().ConvertAsync(new ConversionJob(path, F.Pages, F.Pdf, output, ConversionSettings.Default), null, default);
        Assert.Equal(pdf, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task Without_a_pdf_the_words_become_one()
    {
        var path = Pages("e.pages", z =>
        {
            Add(z, "preview.jpg", [0xFF, 0xD8, 0xFF]);
            Add(z, "Index/Document.iwa", Iwa(Archive(1, 2001, Text("The whole text of the document."))));
        });
        var output = Path.Combine(_folder, "e.pdf");
        await new IWorkConverter().ConvertAsync(new ConversionJob(path, F.Pages, F.Pdf, output, ConversionSettings.Default), null, default);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Contains("wholetext", pdf.GetPage(1).Text.Replace(" ", ""));
    }

    [Fact]
    public async Task The_first_pages_picture_and_the_text_can_be_had()
    {
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xD9];
        var path = Pages("f.key", z =>
        {
            Add(z, "preview.jpg", jpg);
            Add(z, "Index/Document.iwa", Iwa(Archive(1, 2001, Text("Slide title"))));
        });
        var converter = new IWorkConverter();
        var picture = Path.Combine(_folder, "f.jpg");
        await converter.ConvertAsync(new ConversionJob(path, F.Keynote, F.Jpg, picture, ConversionSettings.Default), null, default);
        Assert.Equal(jpg, File.ReadAllBytes(picture));
        var text = Path.Combine(_folder, "f.txt");
        await converter.ConvertAsync(new ConversionJob(path, F.Keynote, F.Txt, text, ConversionSettings.Default), null, default);
        Assert.Equal("Slide title", File.ReadAllText(text));
    }

    [Fact]
    public async Task A_file_with_neither_says_so()
    {
        var path = Pages("g.pages", z => Add(z, "Metadata/x", [1]));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new IWorkConverter().ConvertAsync(new ConversionJob(path, F.Pages, F.Txt, Path.Combine(_folder, "g.txt"), ConversionSettings.Default), null, default));
        Assert.Contains("Export it", error.Message);
    }

    [Fact]
    public void It_says_what_it_can_convert()
    {
        var converter = new IWorkConverter();
        Assert.True(converter.CanConvert(F.Pages, F.Pdf));
        Assert.True(converter.CanConvert(F.Numbers, F.Txt));
        Assert.True(converter.CanConvert(F.Keynote, F.Jpg));
        Assert.False(converter.CanConvert(F.Pages, F.Docx));
        Assert.False(converter.CanConvert(F.Docx, F.Pdf));
    }
}
