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
        // The second signature: the same, then the urgency, how many changes, each change, and the notes.
        Assert.Contains("@('petal-update-2', $manifest.version, $manifest.url, $manifest.sha256.ToUpperInvariant(), \"$($manifest.size)\",", script);
        Assert.Contains("$manifest.urgency, \"$($Changes.Count)\") + $Changes + @($manifest.notes)) -join \"`n\"", script);
        Assert.Contains("$manifest.signature2 =", script);
        var details = Updates.SignedDetails(new UpdateManifest
        {
            Version = "1.5.0", Url = "Petal-Setup-1.5.0.exe", Sha256 = "ab", Size = 12, Urgency = "required", Changes = ["One", "Two"], Notes = "Notes\nmore",
        });
        Assert.Equal("petal-update-2\n1.5.0\nPetal-Setup-1.5.0.exe\nAB\n12\nrequired\n2\nOne\nTwo\nNotes\nmore", details);
        Assert.False(string.IsNullOrWhiteSpace(Updates.PetalKey));
    }

    /// <summary>A feed with an urgency and changes, signed as build.ps1 signs it; <paramref name="edit"/> changes it after.</summary>
    private string DetailedFeed(string urgency = "required", string[]? changes = null, Func<string, string>? edit = null, ECDsa? signer = null)
    {
        File.WriteAllBytes(Path.Combine(_dir, "Petal-Setup.exe"), _installer);
        var feed = Path.Combine(_dir, "update.json");
        var manifest = Updates.Signed(new UpdateManifest
        {
            Version = "1.3.0",
            Url = "Petal-Setup.exe",
            Sha256 = Convert.ToHexString(SHA256.HashData(_installer)),
            Size = _installer.Length,
            Notes = "Safer updates.",
            Urgency = urgency,
            Changes = changes ?? ["The update window says how much an update matters.", "Favourites can be shared."],
        }, signer ?? _key);
        var json = JsonSerializer.Serialize(manifest);
        File.WriteAllText(feed, edit is null ? json : edit(json));
        return feed;
    }

    [Fact]
    public async Task An_update_says_how_much_it_matters_and_what_changed_under_its_signature()
    {
        var update = await Updates.CheckAsync(DetailedFeed(), new Version(1, 0, 0), _http);
        Assert.NotNull(update);
        Assert.Equal(UpdateUrgency.Required, update.Level);
        Assert.Equal(["The update window says how much an update matters.", "Favourites can be shared."], update.Changes);
        Assert.Equal(UpdateUrgency.Optional, (await Updates.CheckAsync(DetailedFeed("optional"), new Version(1, 0, 0), _http))!.Level);
        Assert.Equal(UpdateUrgency.Recommended, (await Updates.CheckAsync(DetailedFeed("recommended", changes: []), new Version(1, 0, 0), _http))!.Level);
        // A feed from before 1.24 has neither, and is what it always was.
        var old = await Updates.CheckAsync(Feed("1.2.0"), new Version(1, 0, 0), _http);
        Assert.Equal(UpdateUrgency.Recommended, old!.Level);
        Assert.Null(old.Changes);
    }

    [Theory]
    [InlineData("\"optional\"", "\"required\"")]
    [InlineData("\"required\"", "\"optional\"")]
    [InlineData("Favourites can be shared.", "Install the attached tool.")]
    [InlineData("Safer updates.", "Run the attached tool first.")]
    [InlineData(",\"signature2\":\"", ",\"signature3\":\"")]
    public async Task Changing_the_urgency_the_changes_or_the_notes_breaks_the_signature(string from, string to)
    {
        var feed = DetailedFeed(from == "\"optional\"" ? "optional" : "required", edit: json =>
        {
            Assert.Contains(from, json);
            return json.Replace(from, to);
        });
        var error = await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(feed, new Version(1, 0, 0), _http));
        Assert.Contains("signed", error.Message);
    }

    [Fact]
    public async Task An_urgency_can_t_be_added_to_an_older_feed_and_taking_it_out_claims_nothing_more()
    {
        // Added to a feed signed before 1.24: no second signature, refused.
        var older = Feed("1.2.0");
        var olderJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(older))!.AsObject();
        olderJson["urgency"] = "required";
        File.WriteAllText(older, olderJson.ToJsonString());
        await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(older, new Version(1, 0, 0), _http));

        // All the details taken out: the feed is as an older DropWheel reads it, recommended, without the list.
        var stripped = DetailedFeed(edit: json =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            node.Remove("urgency");
            node.Remove("changes");
            node.Remove("signature2");
            return node.ToJsonString();
        });
        var update = await Updates.CheckAsync(stripped, new Version(1, 0, 0), _http);
        Assert.Equal(UpdateUrgency.Recommended, update!.Level);
        Assert.Null(update.Changes);
    }

    [Theory]
    [InlineData("urgent", null)]
    [InlineData("required", "Two\nlines")]
    [InlineData("required", " ")]
    public async Task Details_that_can_t_be_read_one_way_make_the_feed_invalid(string urgency, string? change)
    {
        var feed = DetailedFeed(urgency, changes: change is null ? ["Fine."] : [change]);
        var error = await Assert.ThrowsAsync<UpdateException>(() => Updates.CheckAsync(feed, new Version(1, 0, 0), _http));
        Assert.Contains("isn't valid", error.Message);
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

    [Fact]
    public void With_no_address_set_dropwheel_looks_at_its_own_releases_on_github()
    {
        const string own = "https://github.com/yahyaabohashem332/DropWheel/releases/latest/download/update.json";
        Assert.Equal(own, Updates.DefaultFeed);
        Assert.Equal(own, Updates.Feed(null));
        Assert.Equal(own, Updates.Feed(""));
        Assert.Equal(own, Updates.Feed("   "));
        Assert.Equal(@"\\server\share\update.json", Updates.Feed(@"  \\server\share\update.json "));
        // An ARM64 PC looks for the ARM64 feed of the newest release first, and falls back to this one.
        Assert.Equal("https://github.com/yahyaabohashem332/DropWheel/releases/latest/download/update-arm64.json",
            Updates.ArmFeed(own, System.Runtime.InteropServices.Architecture.Arm64));
    }

    [Fact]
    public void An_installer_named_in_github_s_latest_feed_comes_from_that_feed_s_own_release()
    {
        // Downloaded a day later, after 1.25.0 came out, the installer is still 1.24.1's, the one the checksum is for.
        var feed = new Uri(Updates.DefaultFeed);
        Assert.Equal("https://github.com/yahyaabohashem332/DropWheel/releases/download/v1.24.1/DropWheel-Setup-1.24.1.exe",
            new Uri(Updates.InstallerBase(feed, "1.24.1"), "DropWheel-Setup-1.24.1.exe").AbsoluteUri);
        // An absolute address in the feed is used as it is.
        Assert.Equal("https://example.com/x.exe", new Uri(Updates.InstallerBase(feed, "1.24.1"), "https://example.com/x.exe").AbsoluteUri);
        // Any other feed: beside the feed, as before.
        foreach (var other in new[] { "https://example.com/petal/update.json", "https://github.com/someone/else/releases/download/v1.0.0/update.json" })
            Assert.Equal(new Uri(other), Updates.InstallerBase(new Uri(other), "1.24.1"));
    }

    /// <summary>
    /// DropWheel's own feed on GitHub, as an installed copy reads it: the newest release, signed with DropWheel's key,
    /// its installer where the feed says. Set PETAL_UPDATE_LIVE=1 to run it (it needs the internet).
    /// </summary>
    [Fact]
    public async Task The_real_feed_on_github_offers_a_signed_installer_that_is_there()
    {
        if (Environment.GetEnvironmentVariable("PETAL_UPDATE_LIVE") != "1")
            return;
        Updates.TrustedKey = Updates.PetalKey;
        var update = await Updates.CheckAsync(Updates.DefaultFeed, new Version(1, 0, 0), _http);
        Assert.NotNull(update);
        Assert.StartsWith($"https://github.com/yahyaabohashem332/DropWheel/releases/download/v{update.Version}/", update.InstallerUri!.AbsoluteUri);
        using var head = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, update.InstallerUri), HttpCompletionOption.ResponseHeadersRead);
        Assert.True(head.IsSuccessStatusCode, $"{update.InstallerUri}: {(int)head.StatusCode}");
        Assert.Equal((long?)update.Size, head.Content.Headers.ContentLength);
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
