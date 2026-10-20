namespace Petal.Engines.Imaging;

/// <summary>
/// Writes go to a hidden temp file next to the destination and are moved into place only
/// when complete, so a cancelled or failed job never leaves a half-written result behind.
/// </summary>
internal sealed class AtomicFile : IDisposable
{
    private bool _committed;

    private AtomicFile(string finalPath, string tempPath)
    {
        FinalPath = finalPath;
        TempPath = tempPath;
    }

    public string FinalPath { get; }

    public string TempPath { get; }

    public static AtomicFile Create(string finalPath)
    {
        var full = Path.GetFullPath(finalPath);
        var dir = Path.GetDirectoryName(full) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.petal-tmp");
        return new AtomicFile(full, temp);
    }

    /// <summary>Moves the finished temp file to the destination. Never overwrites an existing file.</summary>
    public void Commit()
    {
        File.Move(TempPath, FinalPath, overwrite: false);
        _committed = true;
    }

    public void Dispose()
    {
        if (_committed)
            return;
        try
        {
            if (File.Exists(TempPath))
                File.Delete(TempPath);
        }
        catch (IOException)
        {
            // Best effort: a locked temp file is left for the OS to clean up.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> atomically.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using var file = Create(path);
        using (var fs = new FileStream(file.TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.Write(bytes);
        file.Commit();
    }

    /// <summary>A scratch file in the system temp folder, deleted on dispose.</summary>
    public static TempFile CreateScratch(string extension) => new(Path.Combine(ScratchDirectory, $"{Guid.NewGuid():N}.{extension.TrimStart('.')}"));

    private static string ScratchDirectory
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "Petal", "imaging");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

internal sealed class TempFile(string path) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
