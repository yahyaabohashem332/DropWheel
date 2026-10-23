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
    // Feeds here are signed with a key of the test's own, trusted only while it runs.
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public UpdateTests() => Updates.TrustedKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        Updates.TrustedKey = Updates.PetalKey;
        _key.Dispose();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Feed(string version, string? sha = null, string url = "Petal-Setup.exe", ECDsa? signer = null, bool signed = true)
    {
        File.WriteAllBytes(Path.Combine(_dir, "Petal-Setup.exe"), _installer);
        var feed = Path.Combine(_dir, "update.json");
        var manifest = new UpdateManifest
        {
            Version = version,
            Url = url,
            Sha256 = sha ?? Convert.ToHexString(SHA256.HashData(_installer)),
            Size = _installer.Length,
            Notes = "Faster and friendlier.",
        };
        if (signed)
            manifest = manifest with { Signature = Updates.Sign(manifest, signer ?? _key) };
        File.WriteAllText(feed, JsonSerializer.Serialize(manifest));
        return feed;
    }

    [Fact]
    public async Task Offers_only_an_update_signed_by_the_maker()
    {
        Assert.NotNull(await Updates.CheckAsync(Feed("1.2.0"), new Version(1, 0, 0), _http));
        var unsigned = await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(Feed("1.2.0", signed: false), new Version(1, 0, 0), _http));
        Assert.Contains("signed", unsigned.Message);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(Feed("1.2.0", signer: stranger), new Version(1, 0, 0), _http));
        // A signed feed changed afterwards (another installer, same signature) is refused too.
        var feed = Feed("1.2.0");
        File.WriteAllText(feed, File.ReadAllText(feed).Replace("Petal-Setup.exe", "Other.exe"));
        await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(feed, new Version(1, 0, 0), _http));
        // Nothing newer: no signature needed to say so.
        Assert.Null(await Updates.CheckAsync(Feed("1.0.0", signed: false), new Version(1, 0, 0), _http));
    }

    [Fact]
    public void The_build_signs_what_the_app_checks()
    {
        // build.ps1 signs this exact text; the two must not drift apart.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "scripts", "build.ps1")))
            root = root.Parent;
        Assert.NotNull(root);
        var script = File.ReadAllText(Path.Combine(root.FullName, "tools", "scripts", "build.ps1"));
        Assert.Contains("\"petal-update`n$($manifest.version)`n$($manifest.url)`n$($manifest.sha256.ToUpperInvariant())`n$($manifest.size)\"", script);
        var text = Updates.SignedText(new UpdateManifest { Version = "1.5.0", Url = "Petal-Setup-1.5.0.exe", Sha256 = "ab", Size = 12 });
        Assert.Equal("petal-update\n1.5.0\nPetal-Setup-1.5.0.exe\nAB\n12", text);
        Assert.False(string.IsNullOrWhiteSpace(Updates.PetalKey));
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

    [Fact]
    public void An_arm64_pc_looks_for_the_arm64_feed_first()
    {
        Assert.Equal("https://example.com/petal/update-arm64.json", Updates.ArmFeed("https://example.com/petal/update.json", System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Null(Updates.ArmFeed("https://example.com/petal/update.json", System.Runtime.InteropServices.Architecture.X64));
        // A feed that isn't a .json file is used as it is.
        Assert.Null(Updates.ArmFeed(@"\\server\share\petal", System.Runtime.InteropServices.Architecture.Arm64));
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
