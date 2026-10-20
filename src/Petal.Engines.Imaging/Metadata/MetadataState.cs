using System.Globalization;
using ImageMagick;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging.Metadata;

/// <summary>An image's metadata blocks, decoded for display and editing.</summary>
internal sealed class MetadataState
{
    public FileFormat? Format { get; private init; }

    public IExifProfile? Exif { get; private set; }

    public bool ExifChanged { get; private set; }

    public XmpDocument? Xmp { get; private set; }

    /// <summary>XMP that isn't well-formed RDF: shown as one entry, kept or removed whole.</summary>
    public byte[]? RawXmp { get; private set; }

    public bool XmpChanged { get; private set; }

    public IIptcProfile? Iptc { get; private set; }

    public bool IptcChanged { get; private set; }

    public IColorProfile? Icc { get; private set; }

    public bool IccRemoved { get; private set; }

    /// <summary>Comments and PNG text chunks as (keyword, text); JPEG/GIF comments use the key "comment".</summary>
    public List<(string Key, string Value)> Texts { get; } = [];

    public bool TextsChanged { get; private set; }

    public Orientation Orientation { get; private init; }

    public int Width { get; private init; }

    public int Height { get; private init; }

    private string ColorSpaceName { get; init; } = "";

    private uint Depth { get; init; }

    private bool HasAlpha { get; init; }

    private Density Density { get; init; } = new(0);

    /// <summary>TIFF EXIF tags, which ImageMagick exposes only as properties (read-only here).</summary>
    private List<(string Key, string Value)> TiffTags { get; } = [];

    public static MetadataState Load(byte[] bytes, FileFormat? format)
    {
        using var image = new MagickImage();
        var settings = format is not null && format != F.Svg ? new MagickReadSettings { Format = ImageFormats.ToMagick(format) } : new MagickReadSettings();
        if (format == F.Gif || format == F.Tiff || format == F.Webp)
        {
            settings.FrameIndex = 0;
            settings.FrameCount = 1;
        }
        // Ping skips profiles for PNG (XMP), WEBP, GIF and BMP; it is complete (and much
        // cheaper) only for JPEG, HEIC and AVIF.
        if (format == F.Jpg || format == F.Heic || format == F.Avif)
            image.Ping(bytes, settings);
        else
            image.Read(bytes, settings);

        var state = new MetadataState
        {
            Format = format,
            Exif = image.GetExifProfile(),
            Iptc = image.GetIptcProfile(),
            Icc = image.GetColorProfile(),
            Orientation = image.Orientation,
            Width = (int)image.Width,
            Height = (int)image.Height,
            ColorSpaceName = image.ColorSpace.ToString(),
            Depth = image.Depth,
            HasAlpha = image.HasAlpha,
            Density = image.Density,
        };
        if (image.GetXmpProfile()?.ToByteArray() is { Length: > 0 } xmp)
        {
            state.Xmp = XmpDocument.Parse(xmp);
            if (state.Xmp is null)
                state.RawXmp = xmp;
        }

        if (format == F.Png)
        {
            foreach (var chunk in PngFile.Parse(bytes).Chunks)
            {
                if (PngFile.TextKeyword(chunk) is { } key && IsUserText(key) && PngFile.TextValue(chunk) is { } value)
                    state.Texts.Add((key, value));
            }
        }
        else if (format == F.Jpg)
        {
            foreach (var segment in JpegFile.Parse(bytes).Segments.Where(s => s.IsComment))
                state.Texts.Add(("comment", System.Text.Encoding.UTF8.GetString(segment.Payload).TrimEnd('\0')));
        }
        else if (!string.IsNullOrWhiteSpace(image.Comment))
        {
            state.Texts.Add(("comment", image.Comment));
        }

        if (format == F.Tiff && state.Exif is null && TiffExif.Extract(bytes) is { } tiffExif)
            state.Exif = new ExifProfile(tiffExif);
        if (format == F.Tiff && state.Exif is null)
        {
            foreach (var name in image.AttributeNames.Where(n => n.StartsWith("exif:", StringComparison.Ordinal)))
                state.TiffTags.Add((name[5..], image.GetAttribute(name) ?? ""));
        }
        return state;
    }

    /// <summary>PNG keywords that are text a person would edit (not ImageMagick's raw profile dumps or XMP).</summary>
    internal static bool IsUserText(string keyword) =>
        keyword != PngFile.XmpKeyword && !keyword.StartsWith("Raw profile type", StringComparison.OrdinalIgnoreCase);

    public List<MetadataEntry> Entries(string path, long fileSize)
    {
        var entries = new List<MetadataEntry>
        {
            File("name", "File Name", System.IO.Path.GetFileName(path)),
            File("size", "File Size", FormatSize(fileSize)),
            File("type", "File Type", Format?.Label ?? "Unknown"),
            File("width", "Image Width", Width.ToString(CultureInfo.InvariantCulture)),
            File("height", "Image Height", Height.ToString(CultureInfo.InvariantCulture)),
            File("colorspace", "Color Space", ColorSpaceName),
            File("depth", "Bit Depth", Depth.ToString(CultureInfo.InvariantCulture)),
            File("alpha", "Transparency", HasAlpha ? "Yes" : "No"),
        };
        if (Density.X >= 1)
            entries.Add(File("resolution", "Resolution", string.Create(CultureInfo.InvariantCulture,
                $"{Density.X:0.##} × {Density.Y:0.##} {(Density.Units == DensityUnit.PixelsPerCentimeter ? "dpcm" : "dpi")}")));

        if (Exif is not null)
        {
            foreach (var value in Exif.Values)
            {
                var name = value.Tag.ToString();
                entries.Add(new MetadataEntry(ExifFields.Id(value.Tag), ExifFields.Group(value.Tag), name, ExifFields.Humanize(name),
                    ExifFields.Format(value), ExifFields.IsEditable(value), ExifFields.IsRemovable(value)));
            }
        }
        foreach (var (key, value) in TiffTags)
            entries.Add(new MetadataEntry("tiff:" + key, key.StartsWith("GPS", StringComparison.Ordinal) ? "GPS" : "EXIF", key, ExifFields.Humanize(key), value, false, false));

        if (Xmp is not null)
        {
            foreach (var p in Xmp.Properties())
                entries.Add(new MetadataEntry(p.Id, "XMP", p.Key, ExifFields.Humanize(p.Local), p.Value, p.IsEditable, true));
        }
        else if (RawXmp is not null)
        {
            entries.Add(new MetadataEntry("xmp", "XMP", "XMP", "XMP Packet", $"({RawXmp.Length} bytes)", false, true));
        }

        if (Iptc is not null)
        {
            foreach (var group in Iptc.Values.GroupBy(v => v.Tag))
            {
                int index = 0;
                foreach (var value in group)
                {
                    bool binary = value.Tag is IptcTag.RecordVersion or IptcTag.Unknown;
                    entries.Add(new MetadataEntry($"iptc:{value.Tag}:{index++}", "IPTC", value.Tag.ToString(), ExifFields.Humanize(value.Tag.ToString()),
                        binary ? $"({value.Length} bytes)" : value.Value, !binary, true));
                }
            }
        }

        if (Icc is not null)
        {
            entries.Add(new MetadataEntry(MetadataIds.ColorProfile, "ICC Profile", "Profile", "Profile Description", Icc.Description ?? "Embedded profile", false, true));
            if (!string.IsNullOrWhiteSpace(Icc.Manufacturer))
                entries.Add(Info("icc:manufacturer", "ICC Profile", "Manufacturer", Icc.Manufacturer));
            if (!string.IsNullOrWhiteSpace(Icc.Model))
                entries.Add(Info("icc:model", "ICC Profile", "Model", Icc.Model));
            if (!string.IsNullOrWhiteSpace(Icc.Copyright))
                entries.Add(Info("icc:copyright", "ICC Profile", "Copyright", Icc.Copyright));
            entries.Add(Info("icc:colorspace", "ICC Profile", "Color Space", Icc.ColorSpace.ToString()));
        }

        foreach (var (key, value) in Texts)
            entries.Add(new MetadataEntry(TextId(key), "Text", key, key == "comment" ? "Comment" : ExifFields.Humanize(key), value, true, true));

        // Duplicate keys (e.g. two COM segments) get distinct ids.
        return entries.GroupBy(e => e.Id).SelectMany(g => g.Select((e, i) => i == 0 ? e : e with { Id = $"{e.Id}#{i}" })).ToList();

        static MetadataEntry File(string id, string name, string value) => new("file:" + id, "File", name, name, value, false, false);
        static MetadataEntry Info(string id, string group, string name, string value) => new(id, group, name, name, value, false, false);
    }

    public MetadataSummary Summary()
    {
        GpsLocation? location = Exif is null ? null : ExifFields.ReadLocation(Exif);
        location ??= XmpLocation();
        string? Exif_(ExifTag<string> tag) => Clean(Exif?.GetValue(tag)?.Value);
        string? Xmp_(string ns, string local) => Clean(Xmp?.Find(ns, local)?.Value);
        string? Iptc_(IptcTag tag) => Clean(Iptc?.GetValue(tag)?.Value);

        const string Tiff = "http://ns.adobe.com/tiff/1.0/", XmpBasic = "http://ns.adobe.com/xap/1.0/", Aux = "http://ns.adobe.com/exif/1.0/aux/",
            ExifEx = "http://cipa.jp/exif/1.0/", Photoshop = "http://ns.adobe.com/photoshop/1.0/";

        string? xpAuthor = null;
        if (Exif?.GetValue(ExifTag.XPAuthor)?.Value is { } xp)
            xpAuthor = Clean(System.Text.Encoding.Unicode.GetString(xp));

        return new MetadataSummary(
            location,
            Exif_(ExifTag.Make) ?? Xmp_(Tiff, "Make"),
            Exif_(ExifTag.Model) ?? Xmp_(Tiff, "Model"),
            Exif_(ExifTag.LensModel) ?? Xmp_(ExifEx, "LensModel") ?? Xmp_(Aux, "Lens"),
            Exif_(ExifTag.Artist) ?? xpAuthor ?? Xmp_(XmpDocument.DublinCore, "creator") ?? Iptc_(IptcTag.Byline),
            Exif_(ExifTag.DateTimeOriginal) ?? Xmp_(XmpDocument.ExifNs, "DateTimeOriginal") ?? Xmp_(Photoshop, "DateCreated") ?? Xmp_(XmpBasic, "CreateDate"),
            Exif_(ExifTag.Software) ?? Xmp_(XmpBasic, "CreatorTool"));
    }

    public void Apply(MetadataChange change)
    {
        switch (change)
        {
            case MetadataChange.Set set:
                SetEntry(set.EntryId, set.Value);
                break;
            case MetadataChange.Remove remove:
                RemoveEntry(remove.EntryId);
                break;
            case MetadataChange.SetLocation location:
                SetLocation(location.Location);
                break;
            case MetadataChange.SetAuthor author:
                SetAuthor(author.Author);
                break;
        }
    }

    private void SetEntry(string id, string value)
    {
        id = BaseId(id, out int occurrence);
        if (ExifFields.TryParseId(id, out var ifd, out var number))
        {
            Exif ??= new ExifProfile();
            var existing = ExifFields.Find(Exif, ifd, number);
            if (existing is not null)
            {
                if (!ExifFields.IsEditable(existing))
                    throw new ArgumentException($"{ExifFields.Humanize(existing.Tag.ToString())} can't be edited");
                ExifFields.SetValue(existing, value);
            }
            else if (!ExifFields.TryAdd(Exif, ifd, number, value))
            {
                throw new ArgumentException($"Unknown or non-text EXIF tag {id}");
            }
            ExifChanged = true;
        }
        else if (TryParseXmpId(id, out var ns, out var local))
        {
            Xmp ??= XmpDocument.CreateEmpty();
            var property = Xmp.Find(ns, local);
            if (property is not null && !property.IsEditable)
                throw new ArgumentException($"{property.Key} is a structure and can only be removed");
            if (property is null)
                Xmp.SetSimple(ns, "ns" + Math.Abs(ns.GetHashCode() % 1000), local, value);
            else
                Xmp.Set(ns, local, value);
            RawXmp = null;
            XmpChanged = true;
        }
        else if (TryParseIptcId(id, out var tag, out int index))
        {
            var target = Iptc?.Values.Where(v => v.Tag == tag).ElementAtOrDefault(index)
                ?? throw new ArgumentException($"No IPTC field {id}");
            target.Value = value;
            IptcChanged = true;
        }
        else if (id.StartsWith("text:", StringComparison.Ordinal))
        {
            var key = id[5..];
            var matches = Texts.Select((t, i) => (t, i)).Where(x => x.t.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > occurrence)
                Texts[matches[occurrence].i] = (matches[occurrence].t.Key, value);
            else
                Texts.Add((key, value));
            TextsChanged = true;
        }
        else
        {
            throw new ArgumentException($"{id} can't be edited");
        }
    }

    private void RemoveEntry(string id)
    {
        id = BaseId(id, out int occurrence);
        if (ExifFields.TryParseId(id, out var ifd, out var number))
        {
            var existing = Exif is null ? null : ExifFields.Find(Exif, ifd, number);
            if (existing is null)
                return;
            if (!ExifFields.IsRemovable(existing))
                throw new ArgumentException($"{ExifFields.Humanize(existing.Tag.ToString())} keeps the photo upright and can't be removed");
            Exif!.RemoveValue(existing.Tag);
            ExifChanged = true;
        }
        else if (id == "xmp")
        {
            Xmp = null;
            RawXmp = null;
            XmpChanged = true;
        }
        else if (TryParseXmpId(id, out var ns, out var local))
        {
            if (Xmp?.Remove(ns, local) == true)
                XmpChanged = true;
        }
        else if (TryParseIptcId(id, out var tag, out int index))
        {
            var target = Iptc?.Values.Where(v => v.Tag == tag).ElementAtOrDefault(index);
            if (target is null)
                return;
            Iptc!.RemoveValue(tag, target.Value);
            IptcChanged = true;
        }
        else if (id == MetadataIds.ColorProfile)
        {
            if (Icc is null)
                return;
            Icc = null;
            IccRemoved = true;
        }
        else if (id.StartsWith("text:", StringComparison.Ordinal))
        {
            var key = id[5..];
            var matches = Texts.Select((t, i) => (t, i)).Where(x => x.t.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > occurrence)
            {
                Texts.RemoveAt(matches[occurrence].i);
                TextsChanged = true;
            }
        }
        else
        {
            throw new ArgumentException($"{id} can't be removed");
        }
    }

    private void SetLocation(GpsLocation? location)
    {
        if (location is null)
        {
            if (Exif is not null && Exif.Values.Any(v => v.Tag.Ifd == ExifIfds.Gps))
            {
                ExifFields.RemoveLocation(Exif);
                ExifChanged = true;
            }
            if (Xmp is not null && Xmp.Properties().Any(IsXmpGps))
            {
                Xmp.RemoveWhere(IsXmpGps);
                XmpChanged = true;
            }
            return;
        }

        Exif ??= new ExifProfile();
        ExifFields.WriteLocation(Exif, location);
        ExifChanged = true;
        if (Xmp is not null && Xmp.Properties().Any(IsXmpGps))
        {
            Xmp.RemoveWhere(IsXmpGps);
            Xmp.SetSimple(XmpDocument.ExifNs, "exif", "GPSLatitude", XmpCoordinate(location.Latitude, 'N', 'S'));
            Xmp.SetSimple(XmpDocument.ExifNs, "exif", "GPSLongitude", XmpCoordinate(location.Longitude, 'E', 'W'));
            XmpChanged = true;
        }
    }

    private void SetAuthor(string? author)
    {
        author = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        if (author is null)
        {
            if (Exif is not null)
            {
                foreach (var tag in new ExifTag[] { ExifTag.Artist, ExifTag.XPAuthor })
                    if (Exif.Values.Any(v => v.Tag == tag))
                    {
                        Exif.RemoveValue(tag);
                        ExifChanged = true;
                    }
            }
        }
        else
        {
            Exif ??= new ExifProfile();
            Exif.SetValue(ExifTag.Artist, author);
            if (Exif.GetValue(ExifTag.XPAuthor) is not null)
                Exif.SetValue(ExifTag.XPAuthor, System.Text.Encoding.Unicode.GetBytes(author + "\0"));
            ExifChanged = true;
        }

        if (Xmp is not null)
        {
            if (author is null)
                XmpChanged |= Xmp.Remove(XmpDocument.DublinCore, "creator");
            else
            {
                Xmp.SetList(XmpDocument.DublinCore, "dc", "creator", [author]);
                XmpChanged = true;
            }
        }

        if (Iptc is not null)
        {
            if (author is null)
                IptcChanged |= Iptc.RemoveValue(IptcTag.Byline);
            else
            {
                Iptc.SetValue(IptcTag.Byline, author);
                IptcChanged = true;
            }
        }
    }

    /// <summary>EXIF bytes with the "Exif\0\0" header, or null when nothing is left.</summary>
    public byte[]? ExifBytes()
    {
        if (Exif is null || !Exif.Values.Any())
            return null;
        var bytes = Exif.ToByteArray();
        if (bytes is null || bytes.Length < 8)
            return null;
        return bytes.AsSpan().StartsWith(JpegFile.ExifHeader) ? bytes : [.. JpegFile.ExifHeader, .. bytes];
    }

    public byte[]? XmpBytes() =>
        Xmp is not null ? (Xmp.Properties().Count > 0 ? Xmp.ToBytes() : null) : RawXmp;

    public byte[]? IptcBytes() =>
        Iptc is null || !Iptc.Values.Any() ? null : Iptc.ToByteArray();

    /// <summary>Applies pending changes to a decoded image for formats that are re-encoded.</summary>
    public void ApplyTo(IMagickImage<byte> image)
    {
        if (ExifChanged)
        {
            if (Exif is null || !Exif.Values.Any())
                image.RemoveProfile("exif");
            else
                image.SetProfile(Exif);
        }
        if (XmpChanged)
        {
            var xmp = XmpBytes();
            if (xmp is null)
                image.RemoveProfile("xmp");
            else
                image.SetProfile(new XmpProfile(xmp));
        }
        if (IptcChanged)
        {
            // The Photoshop block holds its own IPTC copy; drop it so the old values can't come back.
            image.RemoveProfile("8bim");
            if (Iptc is null || !Iptc.Values.Any())
                image.RemoveProfile("iptc");
            else
                image.SetProfile(Iptc);
        }
        if (IccRemoved)
            image.RemoveProfile("icc");
        if (TextsChanged)
        {
            var comment = Texts.FirstOrDefault(t => t.Key.Equals("comment", StringComparison.OrdinalIgnoreCase)).Value;
            if (comment is null)
                image.RemoveAttribute("comment");
            else
                image.Comment = comment;
        }
    }

    /// <summary>EXIF holding nothing but the orientation, so "Remove All" keeps photos upright.</summary>
    public byte[]? OrientationOnlyExif()
    {
        if (Orientation is Orientation.Undefined or Orientation.TopLeft)
            return null;
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)Orientation);
        var bytes = exif.ToByteArray();
        if (bytes is null)
            return null;
        return bytes.AsSpan().StartsWith(JpegFile.ExifHeader) ? bytes : [.. JpegFile.ExifHeader, .. bytes];
    }

    internal static string TextId(string key) => "text:" + (key.Equals("comment", StringComparison.OrdinalIgnoreCase) ? "comment" : key);

    private static string BaseId(string id, out int occurrence)
    {
        occurrence = 0;
        int hash = id.LastIndexOf('#');
        if (hash > 0 && !id.StartsWith("xmp:", StringComparison.Ordinal) && int.TryParse(id[(hash + 1)..], out occurrence))
            return id[..hash];
        if (hash > 0 && id.StartsWith("xmp:", StringComparison.Ordinal))
        {
            // xmp ids already contain one '#' (namespace#local); a second one is an occurrence suffix.
            int first = id.IndexOf('#');
            if (first != hash && int.TryParse(id[(hash + 1)..], out occurrence))
                return id[..hash];
        }
        occurrence = 0;
        return id;
    }

    private static bool TryParseXmpId(string id, out string ns, out string local)
    {
        ns = local = "";
        if (!id.StartsWith("xmp:", StringComparison.Ordinal))
            return false;
        int hash = id.LastIndexOf('#');
        if (hash <= 4)
            return false;
        ns = id[4..hash];
        local = id[(hash + 1)..];
        return local.Length > 0;
    }

    private static bool TryParseIptcId(string id, out IptcTag tag, out int index)
    {
        tag = IptcTag.Unknown;
        index = 0;
        var parts = id.Split(':');
        return parts.Length == 3 && parts[0] == "iptc" && Enum.TryParse(parts[1], out tag) && int.TryParse(parts[2], out index);
    }

    private static bool IsXmpGps(XmpProperty p) => p.Namespace == XmpDocument.ExifNs && p.Local.StartsWith("GPS", StringComparison.Ordinal);

    private GpsLocation? XmpLocation()
    {
        var lat = Xmp?.Find(XmpDocument.ExifNs, "GPSLatitude")?.Value;
        var lon = Xmp?.Find(XmpDocument.ExifNs, "GPSLongitude")?.Value;
        return lat is not null && lon is not null && ParseXmpCoordinate(lat) is double a && ParseXmpCoordinate(lon) is double b
            ? new GpsLocation(a, b)
            : null;
    }

    /// <summary>XMP writes coordinates as "DDD,MM.mmmmk" or "DDD,MM,SSk".</summary>
    private static double? ParseXmpCoordinate(string text)
    {
        text = text.Trim();
        if (text.Length < 2)
            return null;
        char hemisphere = char.ToUpperInvariant(text[^1]);
        var parts = text[..^1].Split(',');
        var numbers = parts.Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        if (numbers.Length < 2 || numbers.Any(double.IsNaN))
            return null;
        double value = numbers[0] + numbers[1] / 60 + (numbers.Length > 2 ? numbers[2] / 3600 : 0);
        return hemisphere is 'S' or 'W' ? -value : value;
    }

    private static string XmpCoordinate(double value, char positive, char negative)
    {
        double abs = Math.Abs(value);
        int degrees = (int)Math.Floor(abs);
        double minutes = (abs - degrees) * 60;
        return string.Create(CultureInfo.InvariantCulture, $"{degrees},{minutes:0.000000}{(value < 0 ? negative : positive)}");
    }

    private static string? Clean(string? value)
    {
        value = value?.Trim('\0', ' ');
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1000 => $"{bytes} bytes",
        < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1000.0:0.#} KB"),
        < 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000.0:0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000.0:0.##} GB"),
    };
}
