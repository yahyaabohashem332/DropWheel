using Petal.Core.Util;

namespace Petal.Engines.Media.Internal;

/// <summary>Runs FFmpeg with progress reporting and writes results through a temp file.</summary>
internal static class Ffmpeg
{
    public static string Exe => ToolLocator.Ffmpeg ?? throw new MediaException("FFmpeg was not found. Install FFmpeg or place ffmpeg.exe in DropWheel's tools folder.");

    public static string ProbeExe => ToolLocator.Ffprobe ?? throw new MediaException("FFprobe was not found. Install FFmpeg or place ffprobe.exe in DropWheel's tools folder.");

    public static bool IsAvailable => ToolLocator.Ffmpeg is not null && ToolLocator.Ffprobe is not null;

    /// <summary>
    /// Runs FFmpeg. <paramref name="args"/> are everything after the global options (inputs, filters, outputs).
    /// Progress is <c>out_time / durationSeconds</c>; pass 0 to only report completion.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        IEnumerable<string> args,
        double durationSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string logLevel = "error")
    {
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", logLevel, "-nostats" };
        ProgressParser? parser = null;
        if (progress is not null)
        {
            all.AddRange(["-progress", "pipe:1", "-stats_period", "0.25"]);
            parser = new ProgressParser(progress, durationSeconds);
        }
        all.AddRange(args);

        var result = await FfmpegProcess.RunAsync(Exe, all, parser is null ? null : parser.OnLine, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ExternalToolException("ffmpeg", result);
        progress?.Report(1);
        return result;
    }

    /// <summary>Encodes to a temp file beside <paramref name="outputPath"/> and moves it into place when FFmpeg succeeds.</summary>
    public static async Task<ProcessResult> EncodeAsync(
        IReadOnlyList<string> args,
        string outputPath,
        string muxer,
        double durationSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        string logLevel = "error")
    {
        var temp = TempOutput.PathFor(outputPath);
        try
        {
            var result = await RunAsync([.. args, "-f", muxer, temp], durationSeconds, progress, cancellationToken, logLevel).ConfigureAwait(false);
            TempOutput.Commit(temp, outputPath);
            return result;
        }
        catch
        {
            TempOutput.Discard(temp);
            throw;
        }
    }

    /// <summary>Parses FFmpeg's <c>-progress</c> key=value stream.</summary>
    private sealed class ProgressParser(IProgress<double> progress, double durationSeconds)
    {
        public void OnLine(string line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                if (durationSeconds <= 0)
                    return;
                var us = Inv.ParseLong(line.AsSpan(12).ToString(), -1);
                if (us >= 0)
                    progress.Report(Math.Clamp(us / 1_000_000.0 / durationSeconds, 0, 0.999));
            }
            else if (line == "progress=end")
            {
                progress.Report(1);
            }
        }
    }
}

/// <summary>Results are written to "name.ext.petal-tmp" and renamed at the end, so a failed or cancelled job leaves nothing behind.</summary>
internal static class TempOutput
{
    public const string Suffix = ".petal-tmp";

    public static string PathFor(string outputPath)
    {
        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return full + Suffix;
    }

    public static void Commit(string tempPath, string outputPath)
    {
        var info = new FileInfo(tempPath);
        if (!info.Exists || info.Length == 0)
            throw new MediaException("FFmpeg did not produce any output.");
        File.Move(tempPath, Path.GetFullPath(outputPath), overwrite: false);
    }

    public static void Discard(string tempPath)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The killed process may still hold the handle for a moment.
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Refuses to write over any of the inputs.</summary>
    public static void EnsureNotInput(string outputPath, params IEnumerable<string> inputs)
    {
        var output = Path.GetFullPath(outputPath);
        foreach (var input in inputs)
        {
            if (string.Equals(output, Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output path must differ from the input file.", nameof(outputPath));
        }
    }
}

/// <summary>A scratch folder under %TEMP% for palettes and two-pass logs, deleted on dispose.</summary>
internal sealed class ScratchFolder : IDisposable
{
    public ScratchFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Petal", "media-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort; %TEMP% gets cleaned eventually.
        }
    }
}

/// <summary>Never goes backwards, never repeats, and maps sub-steps onto a slice of the whole.</summary>
internal sealed class SafeProgress(IProgress<double>? inner) : IProgress<double>
{
    private readonly Lock _gate = new();
    private double _last = -1;

    public void Report(double value)
    {
        if (inner is null || double.IsNaN(value))
            return;
        value = Math.Clamp(value, 0, 1);
        lock (_gate)
        {
            if (value <= _last)
                return;
            _last = value;
        }
        inner.Report(value);
    }

    public IProgress<double> Slice(double start, double span) => new SliceProgress(this, start, span);

    private sealed class SliceProgress(SafeProgress owner, double start, double span) : IProgress<double>
    {
        public void Report(double value) => owner.Report(start + Math.Clamp(value, 0, 1) * span);
    }
}
