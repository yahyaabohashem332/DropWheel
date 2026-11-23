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

    /// <summary>Files in folders, with a name in Arabic, words and every byte value, and an empty file.</summary>
    private static readonly (string Name, byte[] Data)[] Mixed =
    [
        ("project/readme.txt", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Petal archive line. ", 4000)))),
        ("project/data/binary.bin", [.. Enumerable.Range(0, 70000).Select(i => (byte)(i * 7 + i / 256))]),
        ("project/docs/مرحبا.txt", System.Text.Encoding.UTF8.GetBytes("مرحبا بالعالم")),
        ("project/empty.txt", []),
    ];

    private string MixedZip(string name, bool arabic = true)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in Mixed.Where(m => arabic || m.Name.All(c => c < 128)))
        {
            using var s = zip.CreateEntry(entry).Open();
            s.Write(data);
        }
        zip.CreateEntry("project/nothing yet/");
        return path;
    }

    private static Dictionary<string, byte[]> Files(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            return copy.ToArray();
        });
    }

    // 1.24: 7Z, TAR.XZ and TAR.BZ2 written, and ISO and CAB made from a ZIP; each read back as written.
    [Theory]
    [InlineData("7z")]
    [InlineData("txz")]
    [InlineData("tbz2")]
    [InlineData("iso")]
    [InlineData("cab")]
    public async Task Zip_becomes_each_new_kind_and_back_byte_for_byte(string targetId)
    {
        var zip = MixedZip("mixed.zip");
        var target = F.FromId(targetId)!;
        var converter = new ArchiveConverter();
        Assert.True(ConversionMatrix.CanConvert(F.Zip, target));
        var packed = OutputNaming.ForConversion(zip, target);
        await converter.ConvertAsync(new ConversionJob(zip, F.Zip, target, packed, ConversionSettings.Default), null, default);
        Assert.Equal(target, F.FromPath(packed));

        var back = Path.Combine(_dir, "back-" + targetId + ".zip");
        await converter.ConvertAsync(new ConversionJob(packed, target, F.Zip, back, ConversionSettings.Default), null, default);
        var expected = Files(zip);
        var actual = Files(back);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (name, data) in expected)
            Assert.Equal(data, actual[name]);
    }

    /// <summary>Windows' own tar (libarchive) reads what DropWheel writes, as any other program would.</summary>
    [Theory]
    [InlineData("7z")]
    [InlineData("txz")]
    [InlineData("tbz2")]
    [InlineData("iso")]
    [InlineData("cab")]
    public async Task Windows_tar_reads_what_is_written(string targetId)
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar))
            return;
        // Windows' tar.exe can't name files outside the system's code page, so this sample keeps to ASCII.
        var zip = MixedZip("ascii.zip", arabic: false);
        var target = F.FromId(targetId)!;
        var packed = OutputNaming.ForConversion(zip, target);
        await new ArchiveConverter().ConvertAsync(new ConversionJob(zip, F.Zip, target, packed, ConversionSettings.Default), null, default);
        var into = Directory.CreateDirectory(Path.Combine(_dir, "tar-" + targetId)).FullName;
        var result = await Petal.Core.Util.ProcessRunner.RunAsync(tar, ["-x", "-f", Path.GetFileName(packed), "-C", "tar-" + targetId], workingDirectory: _dir);
        Assert.True(result.ExitCode == 0, result.StandardError);
        foreach (var (name, data) in Mixed.Where(m => m.Name.All(c => c < 128)))
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(into, name.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task A_cabinet_Windows_made_with_LZX_is_read()
    {
        var makecab = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "makecab.exe");
        if (!File.Exists(makecab))
            return;
        var source = Directory.CreateDirectory(Path.Combine(_dir, "cabsource")).FullName;
        var words = string.Concat(Enumerable.Repeat("LZX keeps the history of the whole folder. ", 3000));
        File.WriteAllText(Path.Combine(source, "one.txt"), words);
        File.WriteAllBytes(Path.Combine(source, "two.bin"), [.. Enumerable.Range(0, 100000).Select(i => (byte)(i % 251))]);
        File.WriteAllLines(Path.Combine(_dir, "files.ddf"),
        [
            ".OPTION EXPLICIT", ".Set CabinetNameTemplate=windows.cab", $".Set DiskDirectoryTemplate=\"{_dir}\"", ".Set Cabinet=on",
            ".Set Compress=on", ".Set CompressionType=LZX", ".Set MaxDiskSize=0",
            $"\"{Path.Combine(source, "one.txt")}\" \"text\\one.txt\"", $"\"{Path.Combine(source, "two.bin")}\" \"two.bin\"",
        ]);
        var made = await Petal.Core.Util.ProcessRunner.RunAsync(makecab, ["/F", "files.ddf"], workingDirectory: _dir);
        Assert.Equal(0, made.ExitCode);
        var cab = Path.Combine(_dir, "windows.cab");
        var zip = Path.Combine(_dir, "from-cab.zip");
        await new ArchiveConverter().ConvertAsync(new ConversionJob(cab, F.Cab, F.Zip, zip, ConversionSettings.Default), null, default);
        var files = Files(zip);
        Assert.Equal(["text/one.txt", "two.bin"], files.Keys.Order());
        Assert.Equal(words, System.Text.Encoding.UTF8.GetString(files["text/one.txt"]));
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, "two.bin")), files["two.bin"]);
    }

    [Fact]
    public async Task A_tar_xz_is_extracted_beside_itself()
    {
        var zip = MixedZip("site.zip");
        var txz = Path.Combine(_dir, "site.tar.xz");
        await new ArchiveConverter().ConvertAsync(new ConversionJob(zip, F.Zip, F.Txz, txz, ConversionSettings.Default), null, default);
        var result = await ArchiveExtractor.ExtractAsync(txz, null, default);
        Assert.Equal(Path.Combine(_dir, "project"), result);
        Assert.Equal("مرحبا بالعالم", File.ReadAllText(Path.Combine(result, "docs", "مرحبا.txt")));
        Assert.True(Directory.Exists(Path.Combine(result, "nothing yet")));
    }

    [Fact]
    public void Compressed_tar_names_keep_both_extensions()
    {
        Assert.Equal(F.Txz, F.FromPath(@"C:\a\linux.tar.xz"));
        Assert.Equal(F.Tbz2, F.FromPath(@"C:\a\linux.tar.bz2"));
        Assert.Equal("linux", F.BaseName(@"C:\a\linux.tar.bz2"));
        Assert.Equal("linux", F.BaseName(@"C:\a\linux.tar.xz"));
        Assert.Equal("tar.bz2", OutputNaming.ExtensionOf(@"C:\a\linux.tar.bz2"));
        Assert.EndsWith(".tar.xz", OutputNaming.ForConversion(Path.Combine(Path.GetTempPath(), "linux.zip"), F.Txz), StringComparison.Ordinal);
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
