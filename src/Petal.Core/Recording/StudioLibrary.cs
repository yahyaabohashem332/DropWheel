namespace Petal.Core.Recording;

/// <summary>The studio's projects on this PC, each in its own folder; the newest are kept, so a recording can be opened again.</summary>
public static class StudioLibrary
{
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "studio");

    /// <summary>How many projects are kept; older ones are removed as new ones come.</summary>
    public const int Kept = 12;

    /// <summary>A new, empty folder for a project.</summary>
    public static string NewFolder()
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var folder = Path.Combine(Folder, stamp);
        for (int i = 2; Directory.Exists(folder); i++)
            folder = Path.Combine(Folder, $"{stamp} ({i})");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Every project that can be read, newest first.</summary>
    public static List<StudioProject> All()
    {
        if (!Directory.Exists(Folder))
            return [];
        return Directory.EnumerateDirectories(Folder)
            .Select(StudioProject.Load)
            .OfType<StudioProject>()
            .OrderByDescending(p => p.Made)
            .ToList();
    }

    /// <summary>The project a video was saved from (or that was made from it), if it is still here.</summary>
    public static StudioProject? ForVideo(string path)
    {
        var full = Path.GetFullPath(path);
        return All().FirstOrDefault(p =>
            p.Exported is { } exported && string.Equals(Path.GetFullPath(exported), full, StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(p.Video) && string.Equals(Path.GetFullPath(p.Video), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Removes the oldest projects beyond <see cref="Kept"/>, and folders left without a project, but never <paramref name="keep"/>.</summary>
    public static void Prune(string? keep = null)
    {
        if (!Directory.Exists(Folder))
            return;
        var projects = All();
        var stale = projects.Skip(Kept).Select(p => p.Folder).ToList();
        foreach (var folder in Directory.EnumerateDirectories(Folder))
        {
            // A folder without a project an hour after it was made was left by a recording that didn't finish.
            if (!File.Exists(Path.Combine(folder, StudioProject.FileName)) && Directory.GetLastWriteTime(folder) < DateTime.Now.AddHours(-1))
                stale.Add(folder);
        }
        foreach (var folder in stale)
        {
            if (keep is not null && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
