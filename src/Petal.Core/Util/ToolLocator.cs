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

    /// <summary>
    /// Ghostscript's console program, which full-quality EPS (and pre-2000 Illustrator) files need.
    /// It isn't bundled (its licence is the AGPL); Petal uses it when it is installed.
    /// </summary>
    public static string? Ghostscript => Find("gswin64c.exe", GhostscriptCandidates());

    private static string[] GhostscriptCandidates()
    {
        var found = new List<string>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var gs = Path.Combine(root, "gs");
            if (root.Length == 0 || !Directory.Exists(gs))
                continue;
            // gs10.04.0 and the like: newest first.
            foreach (var dir in Directory.GetDirectories(gs, "gs*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(Path.Combine(dir, "bin", "gswin64c.exe"));
                found.Add(Path.Combine(dir, "bin", "gswin32c.exe"));
            }
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GPL Ghostscript");
                foreach (var version in (key?.GetSubKeyNames() ?? []).OrderByDescending(v => v))
                {
                    using var sub = key!.OpenSubKey(version);
                    if (sub?.GetValue("GS_DLL") is string dll)
                        found.Add(Path.Combine(Path.GetDirectoryName(dll)!, "gswin64c.exe"));
                }
            }
            catch
            {
                // No access to the registry: the folders above are enough.
            }
        }
        return [.. found];
    }

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
