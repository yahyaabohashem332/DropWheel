using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;
using Petal.Engines.Files.Archives;

namespace Petal.Tests;

public class ZipCreatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-zip-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Make(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Packs_files_and_folders_with_their_names()
    {
        var note = Make("ملاحظة.txt", "مرحبا");
        Make(Path.Combine("Photos", "trip", "a.jpg"), "jpeg bytes");
        Directory.CreateDirectory(Path.Combine(_dir, "Photos", "empty"));
        var output = Path.Combine(_dir, "out.zip");

        await ZipCreator.CreateAsync([note, Path.Combine(_dir, "Photos")], output);

        using var zip = System.IO.Compression.ZipFile.OpenRead(output);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("ملاحظة.txt", names);
        Assert.Contains("Photos/trip/a.jpg", names);
        Assert.Contains("Photos/empty/", names);
        using var reader = new StreamReader(zip.GetEntry("ملاحظة.txt")!.Open());
        Assert.Equal("مرحبا", reader.ReadToEnd());
        Assert.False(File.Exists(output + ".petal-tmp"));
    }

    [Fact]
    public async Task A_password_encrypts_every_file_with_aes()
    {
        var secret = Make("secret.txt", "top secret");
        var output = Path.Combine(_dir, "locked.zip");
        await ZipCreator.CreateAsync([secret], output, new ZipOptions { Password = "كلمة-سر 1" });

        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(output) { Password = "كلمة-سر 1" })
        {
            var entry = zip.GetEntry("secret.txt");
            Assert.True(entry.IsCrypted);
            Assert.Equal(256, entry.AESKeySize);
            using var reader = new StreamReader(zip.GetInputStream(entry));
            Assert.Equal("top secret", reader.ReadToEnd());
        }
        using (var wrong = new ICSharpCode.SharpZipLib.Zip.ZipFile(output) { Password = "nope" })
        {
            Assert.ThrowsAny<Exception>(() =>
            {
                using var reader = new StreamReader(wrong.GetInputStream(wrong.GetEntry("secret.txt")));
                reader.ReadToEnd();
            });
        }
    }

    [Fact]
    public void Default_name_follows_the_first_item_and_never_overwrites()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "Holiday")).FullName;
        var file = Make("report.docx", "x");
        Assert.Equal(Path.Combine(_dir, "Holiday.zip"), ZipCreator.DefaultOutput([folder, file]));
        Assert.Equal(Path.Combine(_dir, "report.zip"), ZipCreator.DefaultOutput([file]));
        File.WriteAllText(Path.Combine(_dir, "report.zip"), "");
        Assert.Equal(Path.Combine(_dir, "report 2.zip"), ZipCreator.DefaultOutput([file]));
    }

    [Fact]
    public async Task Cancelling_leaves_nothing_behind()
    {
        var big = Path.Combine(_dir, "big.bin");
        File.WriteAllBytes(big, new byte[8 << 20]);
        var output = Path.Combine(_dir, "cancelled.zip");
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress(p => { if (p > 0.1) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ZipCreator.CreateAsync([big], output, new ZipOptions(), progress, cts.Token));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".petal-tmp"));
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
