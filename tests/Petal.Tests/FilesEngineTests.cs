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
