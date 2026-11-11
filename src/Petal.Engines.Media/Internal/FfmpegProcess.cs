using System.Diagnostics;
using System.Text;
using Petal.Core.Util;

namespace Petal.Engines.Media.Internal;

/// <summary>
/// Starts FFmpeg/FFprobe without a console window. Unlike <see cref="ProcessRunner"/> it waits for the
/// process to really exit after a cancellation kill, so partial output files can be deleted right away.
/// </summary>
internal static class FfmpegProcess
{
    private const int MaxCapturedChars = 256 * 1024;

    /// <summary>
    /// Runs a tool and collects its output. An FFmpeg command whose inputs hold formats DropWheel's FFmpeg can't decode
    /// reads them through Windows (<see cref="FfmpegCommand"/>); a failure there is thrown, as the output would be short.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string exe,
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFfmpeg(exe))
            return await RunCoreAsync(exe, arguments, onStdoutLine, cancellationToken).ConfigureAwait(false);
        var prepared = await FfmpegCommand.PrepareAsync(arguments, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await RunCoreAsync(exe, prepared.Arguments, onStdoutLine, cancellationToken).ConfigureAwait(false);
            await prepared.DisposeAsync().ConfigureAwait(false);
            if (prepared.Failure is { } failure)
                throw failure;
            return result;
        }
        finally
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static bool IsFfmpeg(string exe) => string.Equals(Path.GetFileNameWithoutExtension(exe), "ffmpeg", StringComparison.OrdinalIgnoreCase);

    private static async Task<ProcessResult> RunCoreAsync(
        string exe,
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        using var process = Create(exe, arguments);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (onStdoutLine is not null)
                onStdoutLine(e.Data);
            else
                Append(stdout, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                Append(stderr, e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {exe}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (cancellationToken.Register(() => Kill(process)))
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        // The parameterless overload also waits for the redirected streams to drain.
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();

        string Text(StringBuilder sb)
        {
            lock (sb)
                return sb.ToString();
        }
        return new ProcessResult(process.ExitCode, Text(stdout), Text(stderr));
    }

    /// <summary>Runs a tool and hands its binary standard output to <paramref name="readStdout"/> as it arrives.</summary>
    public static async Task RunReadingStdoutAsync(
        string exe,
        IReadOnlyList<string> arguments,
        Func<Stream, CancellationToken, Task> readStdout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFfmpeg(exe))
        {
            await RunReadingStdoutCoreAsync(exe, arguments, readStdout, cancellationToken).ConfigureAwait(false);
            return;
        }
        var prepared = await FfmpegCommand.PrepareAsync(arguments, cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await RunReadingStdoutCoreAsync(exe, prepared.Arguments, readStdout, cancellationToken).ConfigureAwait(false);
            }
            catch (ExternalToolException)
            {
                await prepared.DisposeAsync().ConfigureAwait(false);
                if (prepared.Failure is { } cause)
                    throw cause;
                throw;
            }
            await prepared.DisposeAsync().ConfigureAwait(false);
            if (prepared.Failure is { } failure)
                throw failure;
        }
        finally
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunReadingStdoutCoreAsync(
        string exe,
        IReadOnlyList<string> arguments,
        Func<Stream, CancellationToken, Task> readStdout,
        CancellationToken cancellationToken)
    {
        using var process = Create(exe, arguments, binaryStdout: true);
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                Append(stderr, e.Data);
        };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {exe}");
        process.BeginErrorReadLine();

        using (cancellationToken.Register(() => Kill(process)))
        {
            try
            {
                await readStdout(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (!process.HasExited)
                {
                    Kill(process);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string err;
            lock (stderr)
                err = stderr.ToString();
            throw new ExternalToolException(Path.GetFileNameWithoutExtension(exe), new ProcessResult(process.ExitCode, "", err));
        }
    }

    private static Process Create(string exe, IReadOnlyList<string> arguments, bool binaryStdout = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!binaryStdout)
            psi.StandardOutputEncoding = Encoding.UTF8;
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        return new Process { StartInfo = psi, EnableRaisingEvents = true };
    }

    private static void Append(StringBuilder sb, string line)
    {
        lock (sb)
        {
            // Keep the tail: errors are printed last.
            if (sb.Length > MaxCapturedChars)
                sb.Remove(0, sb.Length - MaxCapturedChars / 2);
            sb.AppendLine(line);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Not started or already gone.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exiting at the same moment.
        }
    }
}
