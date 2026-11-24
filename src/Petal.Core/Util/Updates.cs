using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Petal.Core.Util;

/// <summary>How much an update matters, as its maker says in the signed feed.</summary>
public enum UpdateUrgency
{
    /// <summary>Offered quietly (the tray's dot); installed when the user likes.</summary>
    Optional,
    /// <summary>Offered with a card, and can be skipped. What feeds without an urgency (before 1.24) are.</summary>
    Recommended,
    /// <summary>Fixes something important: offered every time, and can't be skipped.</summary>
    Required,
}

/// <summary>
/// What an update feed (update.json, written by build.ps1) says about the newest release.
/// <see cref="Url"/> may be relative to the feed's own address.
/// </summary>
public sealed record UpdateManifest
{
    /// <summary>The urgency names a feed uses.</summary>
    public static readonly IReadOnlyList<string> UrgencyNames = ["optional", "recommended", "required"];

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = "";

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    /// <summary>"required", "recommended" or "optional"; signed (format 2). Absent in older feeds.</summary>
    [JsonPropertyName("urgency")]
    public string? Urgency { get; init; }

    /// <summary>What changed, one line each; signed (format 2). Absent in older feeds.</summary>
    [JsonPropertyName("changes")]
    public IReadOnlyList<string>? Changes { get; init; }

    /// <summary>The urgency; a feed that names none is recommended, as every update was before.</summary>
    [JsonIgnore]
    public UpdateUrgency Level => Urgency switch
    {
        "required" => UpdateUrgency.Required,
        "optional" => UpdateUrgency.Optional,
        _ => UpdateUrgency.Recommended,
    };

    /// <summary>
    /// A feed with an urgency or a list of changes carries a second signature, over them and the notes too: they
    /// can't be changed or added to an older feed. Taking them all out leaves a feed as before 1.24 (recommended,
    /// without the list), never one that claims more than its maker said.
    /// </summary>
    [JsonIgnore]
    public bool SignedWithDetails => Urgency is not null || Changes is not null;

    /// <summary>
    /// ECDSA P-256 over <see cref="Updates.SignedText"/>, made with the maker's private key. Every DropWheel
    /// checks this one; copies older than 1.24 check nothing else, so feeds keep it.
    /// </summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    /// <summary>The same key over <see cref="Updates.SignedDetails"/>: what a feed with an urgency or changes must carry.</summary>
    [JsonPropertyName("signature2")]
    public string? Signature2 { get; init; }

    /// <summary>Where the installer is, resolved against the feed.</summary>
    [JsonIgnore]
    public Uri? InstallerUri { get; init; }
}

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The feed wasn't there or couldn't be reached (as opposed to a feed that was there and wrong).</summary>
    public bool Unreachable { get; init; }
}

/// <summary>
/// Checks a feed for a newer Petal and fetches its installer. Feeds and installers must come over
/// HTTPS (or from a file path, for networks that share updates from a folder); a newer version is only
/// offered when the feed is signed with the maker's key, and an installer is only kept when its SHA-256
/// matches the signed feed. Someone who can change the feed can't offer an installer of their own.
/// </summary>
public static class Updates
{
    /// <summary>The public half of the key Petal's releases are signed with (SubjectPublicKeyInfo, base64).</summary>
    public const string PetalKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgqBPwVYZDvJdiK9lUlPOg/a3EtHwXwMlWUpDnQMyyeTL9kn/UqL3FFfTuhMMdquIbxACsSqO6rp3alMJA361aQ==";

    /// <summary>The key a feed must be signed with: Petal's, or a test's own.</summary>
    public static string TrustedKey { get; set; } = PetalKey;

    /// <summary>
    /// DropWheel's own feed: the update.json of the newest release on GitHub. A copy from DropWheel's own setup looks
    /// here when Settings name no other address.
    /// </summary>
    public const string DefaultFeed = "https://github.com/yahyaabohashem332/DropWheel/releases/latest/download/update.json";

    /// <summary>The feed to look at: the address set in Settings, or DropWheel's own when none is.</summary>
    public static string Feed(string? setting) => string.IsNullOrWhiteSpace(setting) ? DefaultFeed : setting.Trim();

    /// <summary>Most changes a feed may list, and the longest one.</summary>
    public const int MostChanges = 50, LongestChange = 400;

    /// <summary>What a feed's signature covers: the version, where the installer is, its checksum and size.</summary>
    public static string SignedText(UpdateManifest manifest) =>
        $"petal-update\n{manifest.Version}\n{manifest.Url}\n{manifest.Sha256.ToUpperInvariant()}\n{Size(manifest)}";

    /// <summary>
    /// What the second signature covers: the same, then the urgency, the number of changes and each change on its
    /// own line, and last the notes.
    /// </summary>
    public static string SignedDetails(UpdateManifest manifest)
    {
        var changes = manifest.Changes ?? [];
        string[] lines =
        [
            "petal-update-2", manifest.Version, manifest.Url, manifest.Sha256.ToUpperInvariant(), Size(manifest),
            manifest.Urgency ?? "", changes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            .. changes, manifest.Notes ?? "",
        ];
        return string.Join("\n", lines);
    }

    private static string Size(UpdateManifest manifest) => manifest.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Signs a feed, as build.ps1 does (for tests and tools): both signatures when it has an urgency or changes.</summary>
    public static UpdateManifest Signed(UpdateManifest manifest, ECDsa key) => manifest with
    {
        Signature = SignText(SignedText(manifest), key),
        Signature2 = manifest.SignedWithDetails ? SignText(SignedDetails(manifest), key) : null,
    };

    /// <summary>The first signature alone (for tests and tools).</summary>
    public static string Sign(UpdateManifest manifest, ECDsa key) => SignText(SignedText(manifest), key);

    private static string SignText(string text, ECDsa key) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>
    /// True when the feed carries good signatures from <see cref="TrustedKey"/>: the first always, and the second
    /// too when it has an urgency or changes.
    /// </summary>
    public static bool IsSigned(UpdateManifest manifest) =>
        Verifies(SignedText(manifest), manifest.Signature)
        && (!manifest.SignedWithDetails || Verifies(SignedDetails(manifest), manifest.Signature2));

    private static bool Verifies(string text, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(TrustedKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// The feed for this PC's processor: on ARM64 an "update.json" feed has an "update-arm64.json" beside it for the
    /// ARM64 build. Other feeds are used as they are.
    /// </summary>
    public static string? ArmFeed(string feed, Architecture processor) =>
        processor == Architecture.Arm64 && feed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? feed[..^5] + "-arm64.json" : null;

    /// <summary>The newer release the feed offers, or null when this one is current.</summary>
    public static async Task<UpdateManifest?> CheckAsync(string feed, Version current, HttpClient http, CancellationToken cancellationToken = default)
    {
        // An ARM64 PC takes the ARM64 build when the feed has one; the x64 build also runs there, so it is the fallback.
        if (ArmFeed(feed, RuntimeInformation.ProcessArchitecture) is { } arm)
        {
            try
            {
                return await CheckOneAsync(arm, current, http, cancellationToken).ConfigureAwait(false);
            }
            catch (UpdateException ex) when (ex.Unreachable)
            {
                // No ARM64 feed beside this one: the x64 build, which Windows also runs on ARM64. A feed that is
                // there but unsigned or broken is never passed over for another.
            }
        }
        return await CheckOneAsync(feed, current, http, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<UpdateManifest?> CheckOneAsync(string feed, Version current, HttpClient http, CancellationToken cancellationToken)
    {
        var feedUri = Address(feed);
        string json;
        try
        {
            json = feedUri.IsFile
                ? await File.ReadAllTextAsync(feedUri.LocalPath, cancellationToken).ConfigureAwait(false)
                : await http.GetStringAsync(feedUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("The update feed couldn't be reached.", ex) { Unreachable = true };
        }

        UpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json) ?? throw new JsonException("empty");
        }
        catch (JsonException ex)
        {
            throw new UpdateException("The update feed isn't valid.", ex);
        }
        if (!System.Version.TryParse(manifest.Version, out var offered) || manifest.Url.Length == 0
            || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit) || !DetailsAreValid(manifest))
            throw new UpdateException("The update feed isn't valid.");

        if (Normalize(offered) <= Normalize(current))
            return null;
        if (!IsSigned(manifest))
            throw new UpdateException("The update isn't signed by DropWheel's maker, so it isn't offered.");
        var installer = Uri.TryCreate(InstallerBase(feedUri, manifest.Version), manifest.Url, out var resolved) ? resolved : null;
        if (installer is null || !(installer.Scheme == Uri.UriSchemeHttps || installer.IsFile))
            throw new UpdateException("The update feed points to an address that isn't secure.");
        return manifest with { InstallerUri = installer };
    }

    /// <summary>
    /// A feed with details names one of the three urgencies, lists at most <see cref="MostChanges"/> changes of one
    /// line each, none empty: so the signed text can only be read one way.
    /// </summary>
    public static bool DetailsAreValid(UpdateManifest manifest) =>
        !manifest.SignedWithDetails
        || manifest.Urgency is { } u && UpdateManifest.UrgencyNames.Contains(u)
           && (manifest.Changes ?? []).Count <= MostChanges
           && (manifest.Changes ?? []).All(c => !string.IsNullOrWhiteSpace(c) && c.Length <= LongestChange && c.IndexOfAny(['\r', '\n']) < 0);

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/> and checks it. Returns its path; a
    /// file that doesn't match the feed's checksum is deleted and an error raised.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateManifest manifest, string folder, HttpClient http, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = manifest.InstallerUri ?? throw new UpdateException("Check for the update first.");
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(source.LocalPath);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = $"DropWheel-Setup-{manifest.Version}.exe";
        var final = Path.Combine(folder, name);
        var partial = final + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                Stream input;
                long total;
                HttpResponseMessage? response = null;
                if (source.IsFile)
                {
                    input = new FileStream(source.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                    total = input.Length;
                }
                else
                {
                    response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    total = response.Content.Headers.ContentLength ?? manifest.Size;
                }
                using (response)
                await using (input)
                {
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        done += read;
                        if (total > 0)
                            progress?.Report(Math.Min(1, (double)done / total));
                    }
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The downloaded update didn't match its checksum, so it was thrown away.");
            File.Move(partial, final, overwrite: true);
            progress?.Report(1);
            return final;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("The update couldn't be downloaded.", ex);
        }
        finally
        {
            try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>An https address, or a local or network file path.</summary>
    /// <summary>
    /// What an installer named in the feed is found beside. GitHub's "latest" address follows the newest release, so
    /// an installer named in a feed read from it is taken from that feed's own release (tagged v and its version):
    /// one downloaded after a newer release came out is still there, and still the one the feed's checksum is for.
    /// </summary>
    public static Uri InstallerBase(Uri feed, string version)
    {
        const string latest = "/releases/latest/download/";
        int at = feed.AbsolutePath.IndexOf(latest, StringComparison.Ordinal);
        if (!feed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || at < 0)
            return feed;
        return new UriBuilder(feed) { Path = feed.AbsolutePath[..at] + $"/releases/download/v{version}/" + feed.AbsolutePath[(at + latest.Length)..], Query = "" }.Uri;
    }

    private static Uri Address(string feed)
    {
        feed = feed.Trim();
        if (Uri.TryCreate(feed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsFile))
            return uri;
        if (Uri.TryCreate(feed, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttp)
            throw new UpdateException("Update feeds must use https://.");
        if (Path.IsPathFullyQualified(feed))
            return new Uri(Path.GetFullPath(feed));
        throw new UpdateException("The update address isn't valid.");
    }

    /// <summary>1.2 and 1.2.0.0 are the same release.</summary>
    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
