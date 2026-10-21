using Petal.Core.Jobs;

namespace Petal.Engines.Documents.Util;

/// <summary>
/// Every output is written to a temporary name beside its destination and moved into
/// place when complete, so a failed or cancelled job never leaves a half-written file.
/// </summary>
internal static class SafeOutput
{
    public const string TempSuffix = ".petal-tmp";

    public static string TempFor(string path) => path + TempSuffix;

    /// <summary>Throws when <paramref name="output"/> would overwrite <paramref name="input"/>.</summary>
    public static void EnsureNotInput(string input, string output)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output path must differ from the input path.", nameof(output));
    }

    public static void EnsureNotInput(IEnumerable<string> inputs, string output)
    {
        foreach (var input in inputs)
            EnsureNotInput(input, output);
    }

    /// <summary>Runs <paramref name="write"/> against a temporary path, then moves it to <paramref name="path"/>.</summary>
    public static void Write(string path, Action<string> write)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = TempFor(path);
        TryDelete(temp);
        try
        {
            write(temp);
            if (!File.Exists(temp))
                throw new IOException($"Nothing was written for {Path.GetFileName(path)}.");
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static async Task WriteAsync(string path, Func<string, Task> write)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = TempFor(path);
        TryDelete(temp);
        try
        {
            await write(temp).ConfigureAwait(false);
            if (!File.Exists(temp))
                throw new IOException($"Nothing was written for {Path.GetFileName(path)}.");
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static void WriteBytes(string path, byte[] bytes) => Write(path, t => File.WriteAllBytes(t, bytes));

    /// <summary>
    /// Writes one file per page. A single page goes straight to <paramref name="outputPath"/>;
    /// several pages go into a new folder named after the input ("report/report 1.png"…),
    /// which is built under a temporary name and renamed when every page is done.
    /// </summary>
    public static IReadOnlyList<string> WritePages(string inputPath, string outputPath, int pageCount, Action<int, string> writePage) =>
        WritePages(inputPath, outputPath, pageCount, paths =>
        {
            for (var i = 0; i < paths.Count; i++)
                writePage(i, paths[i]);
        });

    /// <summary>The same, with every page's path handed over at once, so pages can be written together.</summary>
    public static IReadOnlyList<string> WritePages(string inputPath, string outputPath, int pageCount, Action<IReadOnlyList<string>> writeAll)
    {
        if (pageCount <= 0)
            throw new InvalidOperationException("The document has no pages.");
        if (pageCount == 1)
        {
            Write(outputPath, t => writeAll([t]));
            return [outputPath];
        }
        var stem = Petal.Core.Formats.Formats.BaseName(inputPath);
        var ext = Path.GetExtension(outputPath);
        return WriteFolder(inputPath, outputPath, Enumerable.Range(1, pageCount).Select(n => $"{stem} {n}{ext}").ToList(), writeAll);
    }

    /// <summary>
    /// Writes several files into a new folder beside the output, named after the input
    /// ("report", then "report 2"…). The folder only appears once every file is complete.
    /// </summary>
    public static IReadOnlyList<string> WriteFolder(string inputPath, string outputPath, IReadOnlyList<string> fileNames, Action<int, string> write) =>
        WriteFolder(inputPath, outputPath, fileNames, paths =>
        {
            for (var i = 0; i < paths.Count; i++)
                write(i, paths[i]);
        });

    /// <summary>The same, with every file's path handed over at once.</summary>
    public static IReadOnlyList<string> WriteFolder(string inputPath, string outputPath, IReadOnlyList<string> fileNames, Action<IReadOnlyList<string>> writeAll)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? Environment.CurrentDirectory;
        var stem = Petal.Core.Formats.Formats.BaseName(inputPath);
        var folder = OutputNaming.Unique(dir, stem, null);
        var tempFolder = folder + TempSuffix;
        TryDeleteDirectory(tempFolder);
        Directory.CreateDirectory(tempFolder);
        try
        {
            writeAll(fileNames.Select(n => Path.Combine(tempFolder, n)).ToList());
            Directory.Move(tempFolder, folder);
            return fileNames.Select(n => Path.Combine(folder, n)).ToList();
        }
        finally
        {
            TryDeleteDirectory(tempFolder);
        }
    }

    /// <summary>Replaces characters Windows does not allow in file names.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "untitled" : clean;
    }

    /// <summary>A private scratch folder for intermediate files, deleted on dispose.</summary>
    public static ScratchFolder CreateScratch() => new();

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class ScratchFolder : IDisposable
{
    public ScratchFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Petal", "documents", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => SafeOutput.TryDeleteDirectory(Path);
}
