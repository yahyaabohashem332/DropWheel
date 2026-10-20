using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using ImageMagick;

namespace Petal.Engines.Imaging.Metadata;

/// <summary>Naming, display and editing rules for EXIF values.</summary>
internal static class ExifFields
{
    private const ushort Orientation = 0x0112, ExposureTime = 0x829A, FNumber = 0x829D, FocalLength = 0x920A,
        UserComment = 0x9286, GpsLatitude = 0x0002, GpsLongitude = 0x0004, GpsDestLatitude = 0x0014, GpsDestLongitude = 0x0016;

    /// <summary>Structural tags: shown, but changing them would misdescribe the pixels.</summary>
    private static readonly HashSet<ushort> Structural =
    [
        0x0100, 0x0101, 0x0102, 0x0103, 0x0106, 0x0111, 0x0115, 0x0116, 0x0117, 0x011A, 0x011B, 0x011C, 0x0128,
        0x0212, 0x0213, 0x0214, Orientation, 0x9000, 0x9101, 0x9102, 0xA000, 0xA001, 0xA002, 0xA003, 0x927C,
    ];

    private static readonly HashSet<ushort> XpTags = [0x9C9B, 0x9C9C, 0x9C9D, 0x9C9E, 0x9C9F];

    private static readonly Lazy<Dictionary<(ExifIfds, ushort), ExifTag>> KnownTags = new(() =>
        typeof(ExifTag).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(p => p.GetValue(null))
            .OfType<ExifTag>()
            .GroupBy(t => (t.Ifd, (ushort)t))
            .ToDictionary(g => g.Key, g => g.First()));

    public static string Id(ExifTag tag) => (tag.Ifd == ExifIfds.Gps ? "gps:" : "exif:") + ((ushort)tag).ToString("X4", CultureInfo.InvariantCulture);

    public static bool TryParseId(string id, out ExifIfds ifd, out ushort number)
    {
        ifd = ExifIfds.Ifd0;
        number = 0;
        string hex;
        if (id.StartsWith("exif:", StringComparison.Ordinal))
            hex = id[5..];
        else if (id.StartsWith("gps:", StringComparison.Ordinal))
        {
            hex = id[4..];
            ifd = ExifIfds.Gps;
        }
        else
            return false;
        return ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number);
    }

    public static IExifValue? Find(IExifProfile exif, ExifIfds ifd, ushort number) =>
        exif.Values.FirstOrDefault(v => (ushort)v.Tag == number && (ifd == ExifIfds.Gps) == (v.Tag.Ifd == ExifIfds.Gps));

    public static string Group(ExifTag tag) => tag.Ifd == ExifIfds.Gps ? "GPS" : "EXIF";

    public static bool IsEditable(IExifValue value)
    {
        ushort n = (ushort)value.Tag;
        if (value.Tag.Ifd == ExifIfds.Gps)
            return !value.IsArray && value.DataType is ExifDataType.String or ExifDataType.Rational;
        if (Structural.Contains(n))
            return false;
        if (XpTags.Contains(n) || n == UserComment)
            return true;
        if (value.IsArray)
            return false;
        return value.DataType is ExifDataType.String or ExifDataType.Short or ExifDataType.Long or ExifDataType.SignedShort
            or ExifDataType.SignedLong or ExifDataType.Rational or ExifDataType.SignedRational or ExifDataType.Byte;
    }

    public static bool IsRemovable(IExifValue value) => !((ushort)value.Tag == Orientation && value.Tag.Ifd != ExifIfds.Gps);

    public static string Format(IExifValue value)
    {
        var raw = value.GetValue();
        ushort n = (ushort)value.Tag;
        bool gps = value.Tag.Ifd == ExifIfds.Gps;

        if (!gps && n == Orientation && raw is ushort o)
            return OrientationName(o);
        if (gps && n is GpsLatitude or GpsLongitude or GpsDestLatitude or GpsDestLongitude && raw is Rational[] { Length: 3 } dms)
            return string.Create(CultureInfo.InvariantCulture, $"{dms[0].ToDouble():0}° {dms[1].ToDouble():0}' {dms[2].ToDouble():0.##}\"");
        if (!gps && n == ExposureTime && raw is Rational t && t.Denominator != 0)
        {
            double seconds = t.ToDouble();
            return seconds is > 0 and < 1 ? $"1/{Math.Round(1 / seconds).ToString(CultureInfo.InvariantCulture)} s" : $"{Num(seconds)} s";
        }
        if (!gps && n == FNumber && raw is Rational f)
            return "f/" + Num(f.ToDouble());
        if (!gps && n == FocalLength && raw is Rational fl)
            return Num(fl.ToDouble()) + " mm";
        if (!gps && XpTags.Contains(n) && raw is byte[] xp)
            return Encoding.Unicode.GetString(xp).TrimEnd('\0');
        if (!gps && n == UserComment && raw is byte[] uc)
            return DecodeUserComment(uc);

        return raw switch
        {
            null => "",
            string s => s.TrimEnd('\0').Trim(),
            byte[] b => FormatBytes(b),
            Rational r => FormatRational(r.Numerator, r.Denominator),
            SignedRational r => r.Denominator == 0 ? "0" : Num(r.ToDouble()),
            Rational[] rs => string.Join(", ", rs.Select(r => FormatRational(r.Numerator, r.Denominator))),
            SignedRational[] rs => string.Join(", ", rs.Select(r => r.Denominator == 0 ? "0" : Num(r.ToDouble()))),
            IFormattable fmt when raw is not IEnumerable => fmt.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable items => string.Join(", ", items.Cast<object>().Select(i => Convert.ToString(i, CultureInfo.InvariantCulture))),
            _ => raw.ToString() ?? "",
        };
    }

    /// <summary>Parses <paramref name="text"/> into the value's own type and stores it.</summary>
    public static void SetValue(IExifValue value, string text)
    {
        ushort n = (ushort)value.Tag;
        bool gps = value.Tag.Ifd == ExifIfds.Gps;
        object parsed;
        if (!gps && XpTags.Contains(n))
            parsed = Encoding.Unicode.GetBytes(text + "\0");
        else if (!gps && n == UserComment)
            parsed = EncodeUserComment(text);
        else
        {
            var current = value.GetValue();
            parsed = current switch
            {
                string => text,
                ushort => ushort.Parse(text, CultureInfo.InvariantCulture),
                uint => uint.Parse(text, CultureInfo.InvariantCulture),
                short => short.Parse(text, CultureInfo.InvariantCulture),
                int => int.Parse(text, CultureInfo.InvariantCulture),
                byte => byte.Parse(text, CultureInfo.InvariantCulture),
                Number => new Number(uint.Parse(text, CultureInfo.InvariantCulture)),
                Rational => ParseRational(text),
                SignedRational => ParseSignedRational(text),
                _ => throw new ArgumentException($"{value.Tag} can't be edited"),
            };
        }
        if (!value.SetValue(parsed))
            throw new ArgumentException($"“{text}” is not a valid value for {Humanize(value.Tag.ToString())}");
    }

    /// <summary>Adds a known text tag that isn't in the profile yet.</summary>
    public static bool TryAdd(IExifProfile exif, ExifIfds ifd, ushort number, string text)
    {
        if (!KnownTags.Value.TryGetValue((ifd == ExifIfds.Gps ? ExifIfds.Gps : TagIfd(number), number), out var tag) &&
            !KnownTags.Value.TryGetValue((ExifIfds.Exif, number), out tag) &&
            !KnownTags.Value.TryGetValue((ExifIfds.Ifd0, number), out tag))
            return false;
        if (tag is ExifTag<string> stringTag)
        {
            exif.SetValue(stringTag, text);
            return true;
        }
        if (tag is ExifTag<byte[]> bytesTag && XpTags.Contains(number))
        {
            exif.SetValue(bytesTag, Encoding.Unicode.GetBytes(text + "\0"));
            return true;
        }
        return false;
    }

    private static ExifIfds TagIfd(ushort number) =>
        KnownTags.Value.Keys.FirstOrDefault(k => k.Item2 == number && k.Item1 != ExifIfds.Gps).Item1;

    public static GpsLocation? ReadLocation(IExifProfile exif)
    {
        var lat = exif.GetValue(ExifTag.GPSLatitude)?.Value;
        var lon = exif.GetValue(ExifTag.GPSLongitude)?.Value;
        if (lat is not { Length: 3 } || lon is not { Length: 3 })
            return null;
        double latitude = ToDegrees(lat), longitude = ToDegrees(lon);
        if (double.IsNaN(latitude) || double.IsNaN(longitude))
            return null;
        if (exif.GetValue(ExifTag.GPSLatitudeRef)?.Value?.Trim().StartsWith('S') == true)
            latitude = -latitude;
        if (exif.GetValue(ExifTag.GPSLongitudeRef)?.Value?.Trim().StartsWith('W') == true)
            longitude = -longitude;
        double? altitude = null;
        if (exif.GetValue(ExifTag.GPSAltitude)?.Value is Rational alt && alt.Denominator != 0)
            altitude = alt.ToDouble() * (exif.GetValue(ExifTag.GPSAltitudeRef)?.Value == 1 ? -1 : 1);
        return new GpsLocation(latitude, longitude, altitude);
    }

    public static void RemoveLocation(IExifProfile exif)
    {
        foreach (var value in exif.Values.Where(v => v.Tag.Ifd == ExifIfds.Gps).ToList())
            exif.RemoveValue(value.Tag);
    }

    public static void WriteLocation(IExifProfile exif, GpsLocation location)
    {
        if (location.Latitude is < -90 or > 90 || location.Longitude is < -180 or > 180)
            throw new ArgumentException("Latitude must be within ±90° and longitude within ±180°.");
        RemoveLocation(exif);
        exif.SetValue(ExifTag.GPSVersionID, [2, 3, 0, 0]);
        exif.SetValue(ExifTag.GPSLatitudeRef, location.Latitude < 0 ? "S" : "N");
        exif.SetValue(ExifTag.GPSLatitude, ToDms(Math.Abs(location.Latitude)));
        exif.SetValue(ExifTag.GPSLongitudeRef, location.Longitude < 0 ? "W" : "E");
        exif.SetValue(ExifTag.GPSLongitude, ToDms(Math.Abs(location.Longitude)));
        if (location.Altitude is double altitude)
        {
            exif.SetValue(ExifTag.GPSAltitudeRef, (byte)(altitude < 0 ? 1 : 0));
            exif.SetValue(ExifTag.GPSAltitude, new Rational((uint)Math.Round(Math.Abs(altitude) * 100), 100));
        }
    }

    /// <summary>"DateTimeOriginal" → "Date Time Original", "GPSLatitude" → "GPS Latitude".</summary>
    public static string Humanize(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) ||
                (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    public static string OrientationName(int value) => value switch
    {
        1 => "Horizontal (normal)",
        2 => "Mirror horizontal",
        3 => "Rotate 180",
        4 => "Mirror vertical",
        5 => "Mirror horizontal and rotate 270 CW",
        6 => "Rotate 90 CW",
        7 => "Mirror horizontal and rotate 90 CW",
        8 => "Rotate 270 CW",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static double ToDegrees(Rational[] dms)
    {
        if (dms.Any(r => r.Denominator == 0))
            return double.NaN;
        return dms[0].ToDouble() + dms[1].ToDouble() / 60 + dms[2].ToDouble() / 3600;
    }

    private static Rational[] ToDms(double degrees)
    {
        uint d = (uint)Math.Floor(degrees);
        double minutesFull = (degrees - d) * 60;
        uint m = (uint)Math.Floor(minutesFull);
        double seconds = (minutesFull - m) * 60;
        return [new Rational(d, 1), new Rational(m, 1), new Rational((uint)Math.Round(seconds * 10000), 10000)];
    }

    private static Rational ParseRational(string text)
    {
        text = text.Trim();
        var slash = text.Split('/');
        if (slash.Length == 2 && uint.TryParse(slash[0], CultureInfo.InvariantCulture, out var a) && uint.TryParse(slash[1], CultureInfo.InvariantCulture, out var b) && b != 0)
            return new Rational(a, b);
        if (double.TryParse(text.TrimEnd('s', 'm', ' ').Replace("f/", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0)
            return Rational.FromDouble(d, bestPrecision: true);
        throw new ArgumentException($"“{text}” is not a number");
    }

    private static SignedRational ParseSignedRational(string text)
    {
        text = text.Trim();
        var slash = text.Split('/');
        if (slash.Length == 2 && int.TryParse(slash[0], CultureInfo.InvariantCulture, out var a) && int.TryParse(slash[1], CultureInfo.InvariantCulture, out var b) && b != 0)
            return new SignedRational(a, b);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return SignedRational.FromDouble(d, bestPrecision: true);
        throw new ArgumentException($"“{text}” is not a number");
    }

    private static string FormatRational(uint numerator, uint denominator) =>
        denominator == 0 ? "0" : denominator == 1 ? numerator.ToString(CultureInfo.InvariantCulture) : Num((double)numerator / denominator);

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FormatBytes(byte[] bytes)
    {
        var trimmed = bytes.AsSpan().TrimEnd((byte)0);
        if (trimmed.Length > 0 && trimmed.Length <= 256 && trimmed.ToArray().All(b => b is >= 0x20 and < 0x7F))
            return Encoding.ASCII.GetString(trimmed);
        return bytes.Length <= 8 ? string.Join(" ", bytes.Select(b => b.ToString(CultureInfo.InvariantCulture))) : $"({bytes.Length} bytes of binary data)";
    }

    private static string DecodeUserComment(byte[] data)
    {
        if (data.Length < 8)
            return Encoding.ASCII.GetString(data).TrimEnd('\0', ' ');
        var header = Encoding.ASCII.GetString(data, 0, 8);
        var body = data.AsSpan(8);
        string text = header.StartsWith("UNICODE", StringComparison.Ordinal) ? DecodeUtf16(body)
            : header.StartsWith("ASCII", StringComparison.Ordinal) || header.All(c => c == '\0') ? Encoding.UTF8.GetString(body)
            : Encoding.Latin1.GetString(body);
        return text.TrimEnd('\0', ' ');
    }

    // EXIF doesn't say which byte order UNICODE comments use; guess from where the zero bytes are.
    private static string DecodeUtf16(ReadOnlySpan<byte> body)
    {
        int evenZeros = 0, oddZeros = 0;
        for (int i = 0; i < body.Length; i++)
            if (body[i] == 0) { if (i % 2 == 0) evenZeros++; else oddZeros++; }
        return (evenZeros > oddZeros ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(body);
    }

    private static byte[] EncodeUserComment(string text) => text.All(c => c < 0x80)
        ? [.. "ASCII\0\0\0"u8, .. Encoding.ASCII.GetBytes(text)]
        : [.. "UNICODE\0"u8, .. Encoding.Unicode.GetBytes(text)];
}
