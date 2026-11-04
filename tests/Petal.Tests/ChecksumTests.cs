using System.IO;
using System.Text;
using Petal.Engines.Files.Metadata;

namespace Petal.Tests;

/// <summary>Checking a file: its value is right, and the published one is found however it was written.</summary>
public sealed class ChecksumTests : IDisposable
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string AbcMd5 = "900150983cd24fb0d6963f7d28e17f72";
    private const string AbcSha1 = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string AbcSha512 = "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Petal", "sum-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public ChecksumTests()
    {
        Directory.CreateDirectory(_folder);
        _file = Path.Combine(_folder, "setup.exe");
        File.WriteAllBytes(_file, Encoding.ASCII.GetBytes("abc"));
    }

    public void Dispose() => Directory.Delete(_folder, true);

    [Theory]
    [InlineData("SHA-256", AbcSha256)]
    [InlineData("MD5", AbcMd5)]
    [InlineData("SHA-1", AbcSha1)]
    [InlineData("SHA-512", AbcSha512)]
    public async Task The_value_is_the_standard_one(string algorithm, string expected) =>
        Assert.Equal(expected, await FileChecksum.ComputeAsync(_file, algorithm));

    [Theory]
    [InlineData(AbcSha256, "SHA-256")]
    [InlineData(AbcMd5, "MD5")]
    [InlineData(AbcSha1, "SHA-1")]
    [InlineData(AbcSha512, "SHA-512")]
    public void A_value_alone_says_its_kind_by_its_length(string text, string kind)
    {
        var found = FileChecksum.Find(text, "setup.exe");
        Assert.Equal(kind, found?.Algorithm);
        Assert.Equal(text, found?.Hex);
    }

    [Theory]
    [InlineData(AbcSha256 + "  setup.exe")]
    [InlineData(AbcSha256 + " *setup.exe")]
    [InlineData("SHA256 (setup.exe) = " + AbcSha256)]
    [InlineData("setup.exe: " + AbcSha256)]
    [InlineData("SHA-256: " + AbcSha256)]
    [InlineData("  BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD  ")]
    public void However_it_is_written_it_is_found(string text) =>
        Assert.Equal(AbcSha256, FileChecksum.Find(text, "setup.exe")?.Hex);

    [Fact]
    public void In_a_list_the_line_naming_the_file_is_the_one_meant()
    {
        var other = new string('1', 64);
        var list = $"{other}  other.zip\n{AbcSha256}  setup.exe\n{new string('2', 64)}  third.msi\n";
        Assert.Equal(AbcSha256, FileChecksum.Find(list, "setup.exe")?.Hex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("12345")]
    [InlineData("abcdefabcdefabcdefabcdefabcdefabcdefab")] // 38 characters: no kind of checksum.
    public void What_isnt_a_checksum_isnt_taken_for_one(string text) =>
        Assert.Null(FileChecksum.Find(text, "setup.exe"));

    [Fact]
    public void A_line_about_another_file_says_nothing_about_this_one()
    {
        Assert.Null(FileChecksum.Find(AbcSha256 + "  other.zip", "setup.exe"));
        var list = string.Join('\n', Enumerable.Range(1, 5).Select(i => new string((char)('0' + i), 64) + $"  file{i}.zip"));
        Assert.Null(FileChecksum.Find(list, "setup.exe"));
    }

    [Theory]
    [InlineData("setup.exe.sha256", AbcSha256 + "  setup.exe")]
    [InlineData("setup.exe.sha256", AbcSha256)]
    [InlineData("SHA256SUMS", "1111111111111111111111111111111111111111111111111111111111111111  a.zip\n" + AbcSha256 + "  setup.exe")]
    [InlineData("checksums.txt", "SHA256 (setup.exe) = " + AbcSha256)]
    public void A_checksum_file_beside_it_is_found(string name, string text)
    {
        File.WriteAllText(Path.Combine(_folder, name), text);
        var found = FileChecksum.FindBeside(_file);
        Assert.Equal(AbcSha256, found?.Hex);
        Assert.Equal(name, found?.Source);
    }

    [Fact]
    public void With_nothing_beside_it_nothing_is_found() =>
        Assert.Null(FileChecksum.FindBeside(_file));

    [Fact]
    public void Old_kinds_are_said_to_be_weak()
    {
        Assert.True(FileChecksum.IsWeak("MD5"));
        Assert.True(FileChecksum.IsWeak("SHA-1"));
        Assert.False(FileChecksum.IsWeak("SHA-256"));
    }

    [Fact]
    public async Task A_large_file_says_how_far_it_has_got_and_can_be_stopped()
    {
        var large = Path.Combine(_folder, "large.bin");
        File.WriteAllBytes(large, new byte[5 << 20]);
        double last = 0;
        await FileChecksum.ComputeAsync(large, progress: new SyncProgress(f => last = f));
        Assert.Equal(1, last, 3);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileChecksum.ComputeAsync(large, cancellationToken: stop.Token));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
