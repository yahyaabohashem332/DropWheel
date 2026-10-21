using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ImageMagick;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents;
using Petal.Engines.Imaging;

namespace Petal.Recipes;

/// <summary>Where the organizer puts a file.</summary>
public enum FileCategory
{
    Screenshots,
    Invoices,
    Pictures,
    Videos,
    Music,
    Documents,
    Archives,
    Installers,
}

public sealed record OrganizeOptions
{
    /// <summary>Pictures, videos, music, documents, archives and installers each to their own folder.</summary>
    public bool ByKind { get; init; } = true;

    /// <summary>Screen captures to a folder of their own, apart from photos.</summary>
    public bool Screenshots { get; init; } = true;

    /// <summary>Invoices and receipts (read from the PDF, or with OCR from a photo) to a folder of their own.</summary>
    public bool Invoices { get; init; } = true;

    /// <summary>Photos renamed after the moment they were taken: "2024-07-18 14.32.05.jpg".</summary>
    public bool RenameByDate { get; init; }
}

/// <summary>
/// Sorts arriving files (a Downloads folder, say) into folders by what they are: screenshots,
/// invoices and receipts by their content, then by kind. Folder names follow whichever already
/// exists, English or Arabic, or are made in the given language.
/// </summary>
public static partial class Organizer
{
    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller" };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso" };

    /// <summary>Folder names for each category: English, Arabic.</summary>
    public static IReadOnlyList<string> Names(FileCategory category) => category switch
    {
        FileCategory.Screenshots => ["Screenshots", "لقطات الشاشة"],
        FileCategory.Invoices => ["Invoices", "الفواتير"],
        FileCategory.Pictures => ["Pictures", "الصور"],
        FileCategory.Videos => ["Videos", "الفيديو"],
        FileCategory.Music => ["Music", "الصوت"],
        FileCategory.Documents => ["Documents", "المستندات"],
        FileCategory.Archives => ["Archives", "الأرشيفات"],
        _ => ["Installers", "برامج التثبيت"],
    };

    [GeneratedRegex(@"screen ?shot|screen capture|^capture|snip|لقطه شاشه|لقطه الشاشه|سكرين", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotName();

    [GeneratedRegex(@"invoice|receipt|bill to|amount due|subtotal|sub-total|grand total|total due|\bvat\b|tax invoice|payment|فاتوره|ايصال|المجموع|الاجمالي|ضريبه|المبلغ المستحق|رقم الفاتوره|سند قبض")]
    private static partial Regex InvoiceWord();

    [GeneratedRegex(@"(?:\d[\d,.]*\s?(?:\$|€|£|usd|eur|sar|aed|egp|try|ريال|دينار|درهم|جنيه|ليره|ل\.س|tl)|(?:\$|€|£)\s?\d)")]
    private static partial Regex Amount();

    /// <summary>The usual screen sizes: a picture this big, saved as PNG, is very likely a screenshot.</summary>
    private static readonly HashSet<(uint, uint)> ScreenSizes =
    [
        (1920, 1080), (2560, 1440), (3840, 2160), (1366, 768), (1536, 864), (1440, 900), (1600, 900), (1280, 720), (1280, 800),
        (2880, 1800), (3024, 1964), (3456, 2234), (2560, 1600), (1920, 1200), (3440, 1440), (2736, 1824), (2880, 1920),
        (1080, 2400), (1080, 2340), (1170, 2532), (1179, 2556), (1284, 2778), (1290, 2796), (1125, 2436), (1242, 2688), (1440, 3200), (1080, 1920),
    ];

    /// <summary>Which folder a file belongs in; null leaves it where it is.</summary>
    public static async Task<FileCategory?> CategoryAsync(string path, OrganizeOptions options, ConverterRegistry converters, ConversionSettings settings, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path);
        var format = Formats.FromPath(path);
        bool picture = format?.Family is FormatFamily.Image or FormatFamily.Vector;
        if (options.Screenshots && picture && IsScreenshot(path, format!))
            return FileCategory.Screenshots;
        if (options.Invoices && (format == Formats.Pdf || format?.Family == FormatFamily.Image) && await IsInvoiceAsync(path, format!, converters, settings, cancellationToken))
            return FileCategory.Invoices;
        if (!options.ByKind)
            return null;
        if (InstallerExtensions.Contains(extension))
            return FileCategory.Installers;
        if (ArchiveExtensions.Contains(extension))
            return FileCategory.Archives;
        return format?.Family switch
        {
            FormatFamily.Image or FormatFamily.Vector => FileCategory.Pictures,
            FormatFamily.Video => FileCategory.Videos,
            FormatFamily.Audio => FileCategory.Music,
            FormatFamily.Pdf or FormatFamily.Document or FormatFamily.Text or FormatFamily.Ebook or FormatFamily.Slides or FormatFamily.Spreadsheet => FileCategory.Documents,
            FormatFamily.Archive => FileCategory.Archives,
            _ => null,
        };
    }

    internal static bool IsScreenshot(string path, FileFormat format)
    {
        if (ScreenshotName().IsMatch(AskPetal.Fold(Path.GetFileNameWithoutExtension(path))))
            return true;
        if (format != Formats.Png)
            return false;
        try
        {
            var info = new MagickImageInfo(path);
            return ScreenSizes.Contains((info.Width, info.Height));
        }
        catch (MagickException)
        {
            return false;
        }
    }

    /// <summary>Two or more invoice words, or one with an amount of money.</summary>
    internal static bool LooksLikeInvoice(string text)
    {
        var folded = AskPetal.Fold(text);
        int words = InvoiceWord().Matches(folded).Select(m => m.Value).Distinct().Count();
        return words >= 2 || words == 1 && Amount().IsMatch(folded);
    }

    private static async Task<bool> IsInvoiceAsync(string path, FileFormat format, ConverterRegistry converters, ConversionSettings settings, CancellationToken ct)
    {
        try
        {
            string text;
            if (format == Formats.Pdf)
            {
                if (new FileInfo(path).Length > 20_000_000)
                    return false;
                text = await DocumentText.ReadAsync(path, converters, settings, null, ct);
            }
            else
            {
                if (!DocumentEngine.IsOcrAvailable || new FileInfo(path).Length > 15_000_000)
                    return false;
                text = await ImageRedactor.ReadTextAsync(path, ct);
            }
            return LooksLikeInvoice(text.Length > 20_000 ? text[..20_000] : text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>"2024-07-18 14.32.05" from when a photo was taken, or null without that date.</summary>
    public static async Task<string?> DateNameAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Formats.FromPath(path)?.Family != FormatFamily.Image)
            return null;
        try
        {
            var report = await ImageMetadata.ReadAsync(path, cancellationToken);
            if (report.Summary.DateTaken is not { Length: > 0 } taken)
                return null;
            string[] patterns = ["yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy-MM-ddTHH:mm:sszzz"];
            if (DateTime.TryParseExact(taken.Trim()[..Math.Min(taken.Trim().Length, 25)], patterns, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
                || DateTime.TryParse(taken, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date))
                return date.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The category's folder inside <paramref name="root"/>: one that exists already (any language), else a new one named in <paramref name="arabic"/> or English.</summary>
    public static string FolderFor(string root, FileCategory category, bool arabic)
    {
        var names = Names(category);
        foreach (var name in names)
        {
            var existing = Path.Combine(root, name);
            if (Directory.Exists(existing))
                return existing;
        }
        return Path.Combine(root, arabic ? names[1] : names[0]);
    }

    /// <summary>The folder names the organizer may make, so a watcher can leave them alone.</summary>
    public static IEnumerable<string> AllNames() => Enum.GetValues<FileCategory>().SelectMany(Names);

    /// <summary>
    /// Moves the file into its folder (renamed by date when asked), never over another file.
    /// Returns the new path, or null when it stays where it is.
    /// </summary>
    public static async Task<string?> OrganizeAsync(string path, string root, OrganizeOptions options, bool arabic,
        ConverterRegistry converters, ConversionSettings settings, CancellationToken cancellationToken = default)
    {
        var category = await CategoryAsync(path, options, converters, settings, cancellationToken);
        string? stem = options.RenameByDate ? await DateNameAsync(path, cancellationToken) : null;
        if (category is null && stem is null)
            return null;
        var folder = category is { } c ? FolderFor(root, c, arabic) : Path.GetDirectoryName(path)!;
        var current = Path.GetFileNameWithoutExtension(path);
        bool sameFolder = string.Equals(Path.GetFullPath(folder), Path.GetFullPath(Path.GetDirectoryName(path)!), StringComparison.OrdinalIgnoreCase);
        // Already named after its date ("… 2" included): nothing to do, or it would be renamed for ever.
        if (sameFolder && (stem is null || current == stem || Regex.IsMatch(current, "^" + Regex.Escape(stem) + @" \d+$")))
            return null;
        Directory.CreateDirectory(folder);
        var target = OutputNaming.Unique(folder, stem ?? current, OutputNaming.ExtensionOf(path));
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            return null;
        File.Move(path, target);
        return target;
    }
}
