using ImageMagick;

namespace Petal.Engines.Imaging;

/// <summary>Keeps metadata truthful after pixels change.</summary>
internal static class MetadataHygiene
{
    /// <summary>
    /// After a rotate, crop, resize or pixel edit: the EXIF thumbnail would show the old picture
    /// (a privacy leak after redaction) and the EXIF pixel dimensions are wrong.
    /// </summary>
    public static void AfterGeometryChange(MagickImage image)
    {
        var exif = image.GetExifProfile();
        if (exif is null)
            return;
        exif.RemoveThumbnail();
        if (exif.GetValue(ExifTag.PixelXDimension) is not null)
            exif.SetValue(ExifTag.PixelXDimension, new Number(image.Width));
        if (exif.GetValue(ExifTag.PixelYDimension) is not null)
            exif.SetValue(ExifTag.PixelYDimension, new Number(image.Height));
        if (exif.GetValue(ExifTag.Orientation) is not null)
            exif.SetValue(ExifTag.Orientation, (ushort)1);
        image.SetProfile(exif);
    }

    /// <summary>EXIF bytes ("Exif\0\0" + TIFF) for writers that embed EXIF themselves, with orientation 1.</summary>
    public static byte[]? ExifForEmbedding(MagickImage image)
    {
        var exif = image.GetExifProfile();
        if (exif is null)
            return null;
        if (exif.GetValue(ExifTag.Orientation) is not null)
            exif.SetValue(ExifTag.Orientation, (ushort)1);
        var bytes = exif.ToByteArray();
        if (bytes is null || bytes.Length < 8)
            return null;
        return bytes.AsSpan().StartsWith("Exif\0\0"u8) ? bytes : [.. "Exif\0\0"u8, .. bytes];
    }

    /// <summary>Removes EXIF/XMP/IPTC/comments; the ICC profile survives when <paramref name="keepColorProfile"/>.</summary>
    public static void Strip(MagickImage image, bool keepColorProfile)
    {
        var icc = keepColorProfile ? image.GetColorProfile() : null;
        image.Strip();
        if (icc is not null)
            image.SetProfile(icc);
        // Strip() leaves user comments in place for some coders.
        image.Comment = null;
    }

    /// <summary>Copies EXIF/XMP/IPTC/ICC from <paramref name="source"/>, fixing orientation and dimensions.</summary>
    public static void CopyProfiles(MagickImage source, MagickImage target, bool includeColorProfile)
    {
        foreach (var name in source.ProfileNames.ToList())
        {
            if (!includeColorProfile && name.Equals("icc", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name is "8bim" && source.GetProfile("iptc") is not null)
                continue;
            var profile = source.GetProfile(name);
            if (profile is not null)
                target.SetProfile(profile);
        }
        target.Orientation = Orientation.TopLeft;
        AfterGeometryChange(target);
    }
}
