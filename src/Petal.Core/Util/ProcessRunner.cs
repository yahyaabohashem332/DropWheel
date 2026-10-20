using System.Diagnostics;
using System.Text;

namespace Petal.Core.Util;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class ExternalToolException(string tool, ProcessResult result)
    : Exception($"{tool} failed with exit code {result.ExitCode}: {Tail(result.StandardError)}")
{
    public ProcessResult Result { get; } = result;

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(4));
    }
}

/// <summary>Runs a command-line tool without a console window, streaming its output.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        string? workingDirectory = null,
        Stream? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onStderrLine?.Invoke(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {fileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (standardInput is not null)
        {
            await standardInput.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        // WaitForExitAsync returns before the async readers drain; this overload waits for them.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs a tool and captures its binary standard output (e.g. raw PCM or PNG frames).</summary>
    public static async Task<byte[]> RunForBytesAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var buffer = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new ExternalToolException(Path.GetFileNameWithoutExtension(fileName), new ProcessResult(process.ExitCode, "", error));
        return buffer.ToArray();
    }
}
