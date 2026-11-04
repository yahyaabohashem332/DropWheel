using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Petal.Engines.Files.Metadata;

/// <summary>A checksum someone published: which kind, and its value in lower-case hexadecimal.</summary>
public sealed record PublishedChecksum(string Algorithm, string Hex, string? Source = null);

/// <summary>
/// Checks that a file is the one its publisher made: its checksum, and the published one to set beside it, found in
/// what was copied or in a checksum file next to it, in any of the forms those are written in.
/// </summary>
public static partial class FileChecksum
{
    public const string Sha256 = "SHA-256";

    /// <summary>The kinds, by how long their hexadecimal is.</summary>
    public static string? AlgorithmOf(int hexLength) => hexLength switch
    {
        32 => "MD5",
        40 => "SHA-1",
        64 => Sha256,
        96 => "SHA-384",
        128 => "SHA-512",
        _ => null,
    };

    /// <summary>MD5 and SHA-1 catch a damaged download, but someone can make a different file with the same value.</summary>
    public static bool IsWeak(string algorithm) => algorithm is "MD5" or "SHA-1";

    private static IncrementalHash Hasher(string algorithm) => IncrementalHash.CreateHash(algorithm switch
    {
        "MD5" => HashAlgorithmName.MD5,
        "SHA-1" => HashAlgorithmName.SHA1,
        "SHA-384" => HashAlgorithmName.SHA384,
        "SHA-512" => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256,
    });

    /// <summary>A file's checksum in lower-case hexadecimal.</summary>
    public static async Task<string> ComputeAsync(string path, string algorithm = Sha256, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var hasher = Hasher(algorithm);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        long done = 0, total = Math.Max(1, stream.Length);
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            done += read;
            progress?.Report((double)done / total);
        }
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32,128}(?![0-9a-fA-F])")]
    private static partial Regex Hex();

    /// <summary>
    /// The published checksum in a text: a bare value, or a list ("hash  name", "hash *name", "SHA256 (name) = hash",
    /// "name: hash"), where the line that names the file is the one meant. A list that doesn't name the file says nothing about it.
    /// </summary>
    public static PublishedChecksum? Find(string text, string fileName)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        PublishedChecksum? Of(Match m) => AlgorithmOf(m.Length) is { } kind ? new PublishedChecksum(kind, m.Value.ToLowerInvariant()) : null;
        foreach (var line in lines)
            if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase) && Hex().Match(line) is { Success: true } named && Of(named) is { } found)
                return found;
        var all = Hex().Matches(text).Where(m => AlgorithmOf(m.Length) is not null).ToList();
        if (all.Count == 1 && lines.Length <= 3 && Of(all[0]) is { } only)
        {
            // A value on its own, or with a word or two about it. A line naming some other file is about that file.
            var line = lines.First(l => l.Contains(all[0].Value, StringComparison.Ordinal));
            var rest = line.Replace(all[0].Value, "", StringComparison.Ordinal).Trim(' ', '*', '\t', ':', '=');
            bool another = rest.Contains('.', StringComparison.Ordinal) && rest.Length > 0;
            return another ? null : only;
        }
        return null;
    }

    private static readonly string[] Listings = ["SHA256SUMS", "SHA256SUMS.txt", "sha256sum.txt", "sha256sums.txt", "checksums.txt", "CHECKSUMS", "checksum.txt", "SHA512SUMS", "MD5SUMS", "md5sum.txt", "hashes.txt"];

    /// <summary>The published checksum in a file beside this one: "name.sha256" and the like, or a list for the whole folder.</summary>
    public static PublishedChecksum? FindBeside(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
            return null;
        var name = Path.GetFileName(path);
        var candidates = new[] { ".sha256", ".sha256sum", ".sha512", ".sha1", ".md5", ".sha256.txt", ".checksum" }.Select(e => path + e)
            .Concat(Listings.Select(l => Path.Combine(folder, l)));
        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate) || new FileInfo(candidate).Length > 4_000_000)
                    continue;
                if (Find(File.ReadAllText(candidate), name) is { } found)
                    return found with { Source = Path.GetFileName(candidate) };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }
}
