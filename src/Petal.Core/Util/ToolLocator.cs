namespace Petal.Core.Util;

/// <summary>
/// Finds the helper programs Petal drives. Bundled copies in the app's "tools" folder win,
/// then the PATH, then a few well-known install locations.
/// </summary>
public static class ToolLocator
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    public static string? Ffmpeg => Find("ffmpeg.exe");
    public static string? Ffprobe => Find("ffprobe.exe");
    public static string? ExifTool => Find("exiftool.exe");

    /// <summary>LibreOffice's soffice.exe, used for slides and high-fidelity office conversions.</summary>
    public static string? LibreOffice => Find("soffice.exe",
        @"%ProgramFiles%\LibreOffice\program\soffice.exe",
        @"%ProgramFiles(x86)%\LibreOffice\program\soffice.exe",
        LibreOfficeFromRegistry() ?? "");

    /// <summary>LibreOffice records its install folder under UNO\InstallPath, even for custom locations.</summary>
    private static string? LibreOfficeFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\LibreOffice\UNO\InstallPath");
                if (key?.GetValue(null) is string dir && dir.Length > 0)
                    return Path.Combine(dir, "soffice.exe");
            }
        }
        catch
        {
            // No access to the registry: fall back to the usual folders.
        }
        return null;
    }

    public static string BundledToolsDirectory => Path.Combine(AppContext.BaseDirectory, "tools");

    public static string? Find(string exeName, params string[] extraCandidates)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(exeName, out var cached))
                return cached;
            var found = Search(exeName, extraCandidates);
            Cache[exeName] = found;
            return found;
        }
    }

    public static void Reset()
    {
        lock (Gate)
            Cache.Clear();
    }

    private static string? Search(string exeName, string[] extraCandidates)
    {
        var bundled = Path.Combine(BundledToolsDirectory, exeName);
        if (File.Exists(bundled))
            return bundled;

        var nested = Directory.Exists(BundledToolsDirectory)
            ? Directory.EnumerateFiles(BundledToolsDirectory, exeName, SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (nested is not null)
            return nested;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        foreach (var raw in extraCandidates)
        {
            var candidate = Environment.ExpandEnvironmentVariables(raw);
            if (File.Exists(candidate))
                return candidate;
        }

        // winget installs FFmpeg under the user's package folder.
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            foreach (var pkg in Directory.EnumerateDirectories(winget))
            {
                var hit = Directory.EnumerateFiles(pkg, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null)
                    return hit;
            }
        }
        return null;
    }
}
