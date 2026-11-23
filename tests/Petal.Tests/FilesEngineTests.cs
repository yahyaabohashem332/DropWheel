using System.IO.Compression;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Files.Archives;
using Petal.Engines.Files.Subtitles;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

public class ArchiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-archive-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeZip(string name, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, content) in files)
        {
            using var w = new StreamWriter(zip.CreateEntry(p).Open());
            w.Write(content);
        }
        return path;
    }

    [Theory]
    [InlineData("tar")]
    [InlineData("tgz")]
    public async Task Zip_round_trips_through_other_formats(string targetId)
    {
        var zip = MakeZip("project.zip", ("readme.txt", "hello"), ("src/main.c", "int main(){}"), ("src/lib/util.h", "#pragma once"));
        var target = F.FromId(targetId)!;
        var converter = new ArchiveConverter();
        var packed = OutputNaming.ForConversion(zip, target);
        await converter.ConvertAsync(new ConversionJob(zip, F.Zip, target, packed, ConversionSettings.Default), null, default);
        Assert.True(File.Exists(packed));

        var back = Path.Combine(_dir, "back.zip");
        await converter.ConvertAsync(new ConversionJob(packed, target, F.Zip, back, ConversionSettings.Default), null, default);
        using var archive = ZipFile.OpenRead(back);
        var names = archive.Entries.Select(e => e.FullName).Where(n => !n.EndsWith('/')).Order().ToList();
        Assert.Equal(["readme.txt", "src/lib/util.h", "src/main.c"], names);
        using var reader = new StreamReader(archive.GetEntry("src/main.c")!.Open());
        Assert.Equal("int main(){}", reader.ReadToEnd());
    }

    [Fact]
    public async Task Extract_flattens_a_single_top_level_folder()
    {
        var zip = MakeZip("site.zip", ("site/index.html", "<h1>hi</h1>"), ("site/css/app.css", "body{}"));
        var result = await ArchiveExtractor.ExtractAsync(zip, null, default);
        Assert.Equal(Path.Combine(_dir, "site"), result);
        Assert.True(File.Exists(Path.Combine(result, "css", "app.css")));
    }

    [Fact]
    public async Task Extract_uses_the_archive_name_for_loose_files()
    {
        var zip = MakeZip("photos.zip", ("a.txt", "a"), ("b.txt", "b"));
        var result = await ArchiveExtractor.ExtractAsync(zip, null, default);
        Assert.Equal(Path.Combine(_dir, "photos"), result);
        Assert.Equal(2, Directory.GetFiles(result).Length);
    }

    /// <summary>Files with text and with every byte value, in a folder and at the top.</summary>
    private static readonly (string Name, byte[] Data)[] Packed =
    [
        ("docs/readme.txt", System.Text.Encoding.UTF8.GetBytes("Petal archive, مرحبا")),
        ("docs/binary.bin", [.. Enumerable.Range(0, 256).Select(i => (byte)i)]),
        ("notes.txt", System.Text.Encoding.UTF8.GetBytes("Loose file at the top.")),
    ];

    // RAR archives WinRAR makes by default are not solid, which SharpCompress won't read in one pass.
    [Theory]
    [InlineData("rar")]
    [InlineData("7z")]
    public async Task Rar_and_7z_become_zip_with_every_file_whole(string fromId)
    {
        var from = F.FromId(fromId)!;
        var path = Path.Combine(_dir, "packed." + fromId);
        File.WriteAllBytes(path, fromId == "rar" ? Petal.Engines.Documents.Tests.BookSamples.Rar4(Packed) : Petal.Engines.Documents.Tests.BookSamples.SevenZip(Packed));
        var zip = OutputNaming.ForConversion(path, F.Zip);
        await new ArchiveConverter().ConvertAsync(new ConversionJob(path, from, F.Zip, zip, ConversionSettings.Default), null, default);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(Packed.Select(p => p.Name).Order(), archive.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).Order());
        foreach (var (name, data) in Packed)
        {
            using var stream = archive.GetEntry(name)!.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.Equal(data, copy.ToArray());
        }
    }

    [Fact]
    public async Task A_rar_is_extracted_into_its_folder()
    {
        var path = Path.Combine(_dir, "backup.rar");
        File.WriteAllBytes(path, Petal.Engines.Documents.Tests.BookSamples.Rar4(Packed));
        var result = await ArchiveExtractor.ExtractAsync(path, null, default);
        Assert.Equal(Path.Combine(_dir, "backup"), result);
        foreach (var (name, data) in Packed)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(result, name.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Extract_ignores_paths_that_escape_the_folder()
    {
        var zip = MakeZip("evil.zip", ("../../escaped.txt", "x"), ("ok.txt", "y"));
        var result = await ArchiveExtractor.ExtractAsync(zip, null, default);
        Assert.False(File.Exists(Path.Combine(_dir, "..", "escaped.txt")));
        Assert.True(File.Exists(Path.Combine(result, "ok.txt")));
    }
}

public class SubtitleTests
{
    private const string Srt = "1\r\n00:00:01,000 --> 00:00:04,200\r\nHello <i>world</i>\r\n\r\n2\r\n00:01:02,500 --> 00:01:05,000\r\nSecond line\r\ncontinues\r\n";

    [Fact]
    public void Srt_to_vtt_keeps_timing()
    {
        var cues = SubtitleConverter.Parse(Srt);
        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(62_500), cues[1].Start);
        var vtt = SubtitleConverter.WriteVtt(cues);
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:01:02.500 --> 00:01:05.000", vtt);
        var again = SubtitleConverter.Parse(vtt);
        Assert.Equal(cues.Select(c => (c.Start, c.End)), again.Select(c => (c.Start, c.End)));
    }

    [Fact]
    public void Vtt_with_settings_and_notes_parses()
    {
        const string vtt = "WEBVTT - Title\n\nNOTE a comment\n\nSTYLE\n::cue { color: red }\n\nintro\n00:05.000 --> 00:07.500 align:start\n<v Anna>Hi there</v>\n";
        var cues = SubtitleConverter.Parse(vtt);
        Assert.Single(cues);
        Assert.Equal("align:start", cues[0].Settings);
        Assert.Equal("Hi there\r\n\r\n", SubtitleConverter.WriteSrt(cues)[(SubtitleConverter.WriteSrt(cues).IndexOf("Hi"))..]);
    }

    [Fact]
    public void Text_export_strips_markup()
    {
        var text = SubtitleConverter.WriteText(SubtitleConverter.Parse(Srt));
        Assert.Equal($"Hello world{Environment.NewLine}Second line continues{Environment.NewLine}", text);
    }
}
