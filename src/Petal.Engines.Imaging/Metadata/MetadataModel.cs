using Petal.Core.Formats;

namespace Petal.Engines.Imaging;

/// <summary>One metadata field as shown in the Metadata window ("Key: value").</summary>
/// <param name="Id">Stable identifier used to edit or remove the field, e.g. "exif:010F", "xmp:http://purl.org/dc/elements/1.1/#creator".</param>
/// <param name="Group">"File", "EXIF", "GPS", "IPTC", "XMP", "ICC Profile" or "Text".</param>
/// <param name="Key">Technical name, e.g. "Make", "dc:creator", "Byline".</param>
/// <param name="Name">Human-readable label, e.g. "Date Time Original".</param>
/// <param name="Value">Display value.</param>
/// <param name="IsEditable">The value can be changed with <see cref="MetadataChange.Set"/>.</param>
/// <param name="IsRemovable">The field can be deleted with <see cref="MetadataChange.Remove"/>.</param>
public sealed record MetadataEntry(string Id, string Group, string Key, string Name, string Value, bool IsEditable, bool IsRemovable);

/// <summary>A position in decimal degrees (south and west negative); altitude in metres.</summary>
public sealed record GpsLocation(double Latitude, double Longitude, double? Altitude = null);

/// <summary>The fields Petal highlights for photos: location, camera, author.</summary>
public sealed record MetadataSummary(
    GpsLocation? Location,
    string? CameraMake,
    string? CameraModel,
    string? Lens,
    string? Author,
    string? DateTaken,
    string? Software)
{
    /// <summary>"Apple iPhone 15 Pro" style label (make omitted when the model already starts with it).</summary>
    public string? Camera => (CameraMake, CameraModel) switch
    {
        (null or "", null or "") => null,
        (null or "", var m) => m,
        (var mk, null or "") => mk,
        var (mk, m) => m!.StartsWith(mk!, StringComparison.OrdinalIgnoreCase) ? m : $"{mk} {m}",
    };

    public bool HasLocation => Location is not null;
}

/// <param name="IsLossless">Edits rewrite only the metadata (JPEG, PNG, WEBP). Other formats are re-encoded,
/// which also drops auxiliary images such as HDR gain maps and depth maps.</param>
public sealed record MetadataReport(
    string Path,
    FileFormat? Format,
    IReadOnlyList<MetadataEntry> Entries,
    MetadataSummary Summary,
    bool IsLossless)
{
    public IReadOnlyList<string> Groups => Entries.Select(e => e.Group).Distinct().ToList();

    /// <summary>Case-insensitive search over group, key, name and value (the window's search box).</summary>
    public IEnumerable<MetadataEntry> Search(string? query) => string.IsNullOrWhiteSpace(query)
        ? Entries
        : Entries.Where(e =>
            e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.Group.Contains(query, StringComparison.OrdinalIgnoreCase));

    public MetadataEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);
}

/// <summary>An edit to apply with <see cref="ImageMetadata.ApplyAsync"/>.</summary>
public abstract record MetadataChange
{
    private MetadataChange()
    {
    }

    /// <summary>
    /// Sets a field. <see cref="MetadataEntry.Id"/> of an existing entry, or one of
    /// <see cref="MetadataIds"/> to add a common EXIF tag that isn't there yet.
    /// </summary>
    public sealed record Set(string EntryId, string Value) : MetadataChange;

    /// <summary>Deletes a field.</summary>
    public sealed record Remove(string EntryId) : MetadataChange;

    /// <summary>Writes GPS coordinates (EXIF and, if present, XMP); null removes every location field.</summary>
    public sealed record SetLocation(GpsLocation? Location) : MetadataChange;

    /// <summary>Sets the author in EXIF Artist plus existing XMP dc:creator / IPTC By-line; null removes them.</summary>
    public sealed record SetAuthor(string? Author) : MetadataChange;
}

/// <summary>Ids of common fields, usable with <see cref="MetadataChange.Set"/> even when absent.</summary>
public static class MetadataIds
{
    public const string Make = "exif:010F";
    public const string Model = "exif:0110";
    public const string Software = "exif:0131";
    public const string Artist = "exif:013B";
    public const string Copyright = "exif:8298";
    public const string ImageDescription = "exif:010E";
    public const string DateTimeOriginal = "exif:9003";
    public const string LensModel = "exif:A434";
    public const string Comment = "text:comment";
    public const string ColorProfile = "icc";
}
