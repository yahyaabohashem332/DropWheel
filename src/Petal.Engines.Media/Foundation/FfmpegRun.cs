using System.Diagnostics;

namespace Petal.Engines.Media.Foundation;

/// <summary>
/// An FFmpeg process its caller starts and talks to (writing frames in, or reading them out), whose inputs in formats
/// DropWheel's FFmpeg doesn't decode are read through Windows. Call <see cref="FinishAsync"/> once FFmpeg has exited.
/// </summary>
public sealed class FfmpegRun : IAsyncDisposable
{
    private readonly WindowsMediaCommand _command;

    private FfmpegRun(Process process, WindowsMediaCommand command)
    {
        Process = process;
        _command = command;
    }

    public Process Process { get; }

    /// <summary>Starts FFmpeg with the start info's arguments, made ready for Windows to decode what FFmpeg can't.</summary>
    public static async Task<FfmpegRun> StartAsync(ProcessStartInfo start, CancellationToken cancellationToken = default)
    {
        var command = await WindowsMedia.PrepareAsync(start.ArgumentList.ToList(), cancellationToken).ConfigureAwait(false);
        try
        {
            start.ArgumentList.Clear();
            foreach (var argument in command.Arguments)
                start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new MediaException("FFmpeg didn't start.");
            return new FfmpegRun(process, command);
        }
        catch
        {
            await command.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>After FFmpeg has exited: throws if decoding through Windows went wrong, as FFmpeg's output is then short.</summary>
    public Task FinishAsync() => _command.FinishAsync();

    public async ValueTask DisposeAsync()
    {
        await _command.DisposeAsync().ConfigureAwait(false);
        Process.Dispose();
    }
}
