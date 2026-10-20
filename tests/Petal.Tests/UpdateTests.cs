using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Petal.Core.Util;

namespace Petal.Tests;

public class UpdateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-update-").FullName;
    private readonly HttpClient _http = new();
    private readonly byte[] _installer = Enumerable.Range(0, 50_000).Select(i => (byte)(i % 251)).ToArray();

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Feed(string version, string? sha = null, string url = "Petal-Setup.exe")
    {
        File.WriteAllBytes(Path.Combine(_dir, "Petal-Setup.exe"), _installer);
        var feed = Path.Combine(_dir, "update.json");
        File.WriteAllText(feed, JsonSerializer.Serialize(new
        {
            version,
            url,
            sha256 = sha ?? Convert.ToHexString(SHA256.HashData(_installer)),
            size = _installer.Length,
            notes = "Faster and friendlier.",
        }));
        return feed;
    }

    [Fact]
    public async Task Offers_only_a_newer_version()
    {
        var feed = Feed("1.2.0");
        var update = await Updates.CheckAsync(feed, new Version(1, 0, 0), _http);
        Assert.NotNull(update);
        Assert.Equal("1.2.0", update.Version);
        Assert.Equal("Faster and friendlier.", update.Notes);
        Assert.Equal(Path.Combine(_dir, "Petal-Setup.exe"), update.InstallerUri!.LocalPath);

        Assert.Null(await Updates.CheckAsync(feed, new Version(1, 2, 0, 0), _http));
        Assert.Null(await Updates.CheckAsync(feed, new Version(2, 0), _http));
    }

    [Fact]
    public async Task Keeps_the_installer_only_when_its_checksum_matches()
    {
        var downloads = Path.Combine(_dir, "downloads");
        var good = await Updates.CheckAsync(Feed("1.1.0"), new Version(1, 0, 0), _http);
        var progress = new List<double>();
        var path = await Updates.DownloadAsync(good!, downloads, _http, new SynchronousProgress(progress.Add));
        Assert.Equal(_installer, File.ReadAllBytes(path));
        Assert.Equal(1, progress[^1]);

        var tampered = await Updates.CheckAsync(Feed("1.1.1", sha: new string('a', 64)), new Version(1, 0, 0), _http);
        Directory.Delete(downloads, recursive: true);
        var error = await Assert.ThrowsAsync<UpdateException>(() => Updates.DownloadAsync(tampered!, downloads, _http));
        Assert.Contains("checksum", error.Message);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Theory]
    [InlineData("http://example.com/update.json", "https")]
    [InlineData("update.json", "isn't valid")]
    public async Task Refuses_feeds_that_are_not_safe(string feed, string message)
    {
        var error = await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(feed, new Version(1, 0), _http));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task Refuses_an_installer_over_plain_http_and_broken_feeds()
    {
        var insecure = Feed("9.0.0", url: "http://example.com/Petal-Setup.exe");
        Assert.Contains("secure", (await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(insecure, new Version(1, 0), _http))).Message);

        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ \"version\": \"x\" }");
        Assert.Contains("valid", (await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(Path.Combine(_dir, "broken.json"), new Version(1, 0), _http))).Message);
        Assert.Contains("reached", (await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(Path.Combine(_dir, "none.json"), new Version(1, 0), _http))).Message);
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
