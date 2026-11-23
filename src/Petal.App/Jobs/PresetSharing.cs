using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.Core.Formats;
using Petal.Core.Jobs;

namespace Petal.App.Jobs;

/// <summary>
/// Favourites passed between people and PCs: saved as a .petalpreset file, or copied as one line of text
/// to paste in a chat. What comes in is checked before anything changes: each favourite gets a new id, its
/// numbers are kept within what DropWheel offers, a folder this PC doesn't have is left out (and said so),
/// and one DropWheel can't save is not added. Favourites are only settings, never anything that runs.
/// </summary>
public static class PresetSharing
{
    public const string Extension = ".petalpreset";
    public const string CodePrefix = "PETAL-PRESET:";

    /// <summary>Most favourites one file or line may hold.</summary>
    public const int MostInFile = 24;

    private const int MostBytes = 256 * 1024;

    private sealed record Envelope(int PetalPreset, List<Favourite> Favourites);

    /// <summary>The favourites as a file's text.</summary>
    public static string ToJson(IEnumerable<Favourite> favourites) =>
        JsonSerializer.Serialize(new Envelope(1, favourites.Select(Plain).ToList()), SettingsStore.Options);

    /// <summary>The favourites as one line: "PETAL-PRESET:" and the compressed file.</summary>
    public static string ToCode(IEnumerable<Favourite> favourites)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Envelope(1, favourites.Select(Plain).ToList()),
            new JsonSerializerOptions(SettingsStore.Options) { WriteIndented = false }));
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize))
            gzip.Write(json);
        return CodePrefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>What a shared favourite would do here.</summary>
    public enum Verdict
    {
        /// <summary>Added as it is.</summary>
        Add,
        /// <summary>Added, but its folder isn't on this PC, so results go where Settings says.</summary>
        AddWithoutFolder,
        /// <summary>The user has one with the same settings already.</summary>
        AlreadyThere,
        /// <summary>DropWheel can't save to its format.</summary>
        CantSave,
    }

    /// <summary>One favourite from a shared file, as it would be added, and what adding it does.</summary>
    public sealed record Incoming(Favourite Favourite, Verdict Verdict, string? Folder);

    /// <summary>The favourites in a file's text or a code; null with a reason when it holds none.</summary>
    public static (IReadOnlyList<Incoming>? Favourites, string? Problem) Read(string text, IReadOnlyList<Favourite> mine)
    {
        text = text.Trim();
        try
        {
            if (text.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var b64 = text[CodePrefix.Length..].Trim().Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
                var inner = new byte[MostBytes + 1];
                int read = 0, n;
                while (read < inner.Length && (n = gzip.Read(inner, read, inner.Length - read)) > 0)
                    read += n;
                if (read > MostBytes)
                    return (null, "This favourite is too large.");
                text = Encoding.UTF8.GetString(inner, 0, read);
            }
            if (text.Length > MostBytes)
                return (null, "This favourite is too large.");
            var envelope = JsonSerializer.Deserialize<Envelope>(text, SettingsStore.Options);
            if (envelope is not { PetalPreset: 1, Favourites: { Count: > 0 } list })
                return (null, "This isn't a DropWheel favourite.");
            var incoming = new List<Incoming>();
            foreach (var shared in list.Where(f => f is not null).Take(MostInFile))
            {
                var (fresh, folder) = Fresh(shared);
                var verdict = fresh.Target is not { } target || !FavouriteText.Targets.Contains(target) ? Verdict.CantSave
                    : mine.Concat(incoming.Where(i => i.Verdict is Verdict.Add or Verdict.AddWithoutFolder).Select(i => i.Favourite)).Any(m => Same(m, fresh)) ? Verdict.AlreadyThere
                    : folder is not null ? Verdict.AddWithoutFolder
                    : Verdict.Add;
                incoming.Add(new(fresh, verdict, folder));
            }
            return incoming.Count > 0 ? (incoming, null) : (null, "This isn't a DropWheel favourite.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException or NotSupportedException)
        {
            return (null, "This isn't a DropWheel favourite.");
        }
    }

    /// <summary>
    /// A shared favourite as it would be added: a new id, a name of at most 24 letters, sizes and qualities
    /// within what DropWheel offers, a tidy name pattern. Its folder is kept only when this PC has it;
    /// otherwise it is returned apart, to say so.
    /// </summary>
    internal static (Favourite Favourite, string? MissingFolder) Fresh(Favourite shared)
    {
        var name = (shared.Name ?? "").Trim();
        var fresh = new Favourite
        {
            Name = name.Length > 24 ? name[..24].Trim() : name,
            Format = (shared.Format ?? "").Trim().ToLowerInvariant(),
            LongestSide = Math.Clamp(shared.LongestSide, 0, 16384),
            Quality = Math.Clamp(shared.Quality, 0, 512),
            Dpi = shared.Dpi == 0 ? 0 : Math.Clamp(shared.Dpi, 36, 1200),
            RemoveMetadata = shared.RemoveMetadata,
            NamePattern = PresetNaming.Tidy(shared.NamePattern),
        };
        string? missing = null;
        var folder = shared.Folder?.Trim();
        if (!string.IsNullOrEmpty(folder))
        {
            if (Path.IsPathFullyQualified(folder) && Directory.Exists(folder))
                fresh.Folder = folder;
            else
                missing = folder;
        }
        return (fresh, missing);
    }

    /// <summary>Two favourites that do the same thing, whatever their names.</summary>
    public static bool Same(Favourite a, Favourite b) =>
        a.Format == b.Format && a.LongestSide == b.LongestSide && a.Quality == b.Quality && a.Dpi == b.Dpi
        && a.RemoveMetadata == b.RemoveMetadata && (a.NamePattern ?? "") == (b.NamePattern ?? "")
        && string.Equals(a.Folder ?? "", b.Folder ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>A copy to share: nothing but the settings (the id stays on this PC).</summary>
    private static Favourite Plain(Favourite f) => new()
    {
        Id = Guid.Empty,
        Name = f.Name,
        Format = f.Format,
        LongestSide = f.LongestSide,
        Quality = f.Quality,
        Dpi = f.Dpi,
        RemoveMetadata = f.RemoveMetadata,
        Folder = string.IsNullOrWhiteSpace(f.Folder) ? null : f.Folder,
        NamePattern = string.IsNullOrWhiteSpace(f.NamePattern) ? null : f.NamePattern,
    };

    /// <summary>
    /// The gallery's own folder: .petalpreset files put here (by hand, or saved from Settings) are offered in
    /// the gallery beside the ready-made favourites. Nothing is fetched from anywhere.
    /// </summary>
    public static string GalleryFolder => Path.Combine(SettingsStore.Directory, "presets");

    /// <summary>The favourites in the gallery folder's files, each with the file it came from.</summary>
    public static IReadOnlyList<(string File, Favourite Favourite)> FromGalleryFolder()
    {
        var found = new List<(string, Favourite)>();
        if (!Directory.Exists(GalleryFolder))
            return found;
        foreach (var file in Directory.EnumerateFiles(GalleryFolder, "*" + Extension).Order(StringComparer.CurrentCultureIgnoreCase).Take(200))
        {
            try
            {
                if (new FileInfo(file).Length > MostBytes)
                    continue;
                var (list, _) = Read(File.ReadAllText(file), []);
                foreach (var item in list ?? [])
                    if (item.Verdict != Verdict.CantSave)
                        found.Add((file, item.Favourite));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return found;
    }

    /// <summary>A ready-made favourite in the gallery: what it is for, its icon, and the favourite itself.</summary>
    public sealed record ReadyMade(string Description, string Icon, Favourite Favourite);

    /// <summary>Ready-made favourites for common jobs. Their names and descriptions are translated where shown.</summary>
    public static IReadOnlyList<ReadyMade> Gallery { get; } =
    [
        new("Pictures for a website or a blog.", "globe",
            new Favourite { Name = "Web", Format = "webp", LongestSide = 1600, Quality = 80, RemoveMetadata = true }),
        new("Photos small enough to send, without the camera and the place they were taken.", "mail",
            new Favourite { Name = "E-mail", Format = "jpg", LongestSide = 1280, Quality = 80, RemoveMetadata = true }),
        new("Pictures for social networks.", "share",
            new Favourite { Name = "Social", Format = "jpg", LongestSide = 2048, Quality = 90 }),
        new("PDF pages and documents as pictures for printing.", "fileImage",
            new Favourite { Name = "Print", Format = "png", Dpi = 300 }),
        new("Each page or slide as a light picture for a chat.", "presentation",
            new Favourite { Name = "Slides", Format = "jpg", Dpi = 150, Quality = 85 }),
        new("Videos small enough to send in a chat.", "film",
            new Favourite { Name = "Small video", Format = "mp4", LongestSide = 1280, Quality = 24, RemoveMetadata = true }),
        new("Spoken audio, small and clear.", "mic",
            new Favourite { Name = "Podcast", Format = "mp3", Quality = 128 }),
        new("PDF copies named with today's date, such as “report 2026-11-23.pdf”.", "calendar",
            new Favourite { Name = "Dated PDF", Format = "pdf", NamePattern = $"{PresetNaming.Name} {PresetNaming.Date}" }),
    ];

    /// <summary>A ready-made favourite as it is added: its name in the user's language, a new id.</summary>
    public static Favourite Make(ReadyMade ready)
    {
        var copy = ready.Favourite.Copy();
        copy.Id = Guid.NewGuid();
        var name = L.T(ready.Favourite.Name);
        copy.Name = name.Length > 24 ? name[..24].Trim() : name;
        return copy;
    }

    /// <summary>Formats a favourite can't save to on this PC are never offered.</summary>
    internal static bool CanSave(Favourite f) => f.Target is { } t && FavouriteText.Targets.Contains(t);
}
