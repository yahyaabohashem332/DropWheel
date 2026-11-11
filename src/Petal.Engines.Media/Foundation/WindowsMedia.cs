using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media.Foundation;

/// <summary>An FFmpeg command made ready to run, with the Windows bridges it reads from; dispose it once FFmpeg has exited.</summary>
public sealed class WindowsMediaCommand : IAsyncDisposable
{
    private readonly PreparedCommand _prepared;

    internal WindowsMediaCommand(PreparedCommand prepared) => _prepared = prepared;

    public IReadOnlyList<string> Arguments => _prepared.Arguments;

    /// <summary>Stops the bridges, then throws if decoding through Windows went wrong (FFmpeg's output is then short).</summary>
    public async Task FinishAsync()
    {
        await _prepared.DisposeAsync().ConfigureAwait(false);
        if (_prepared.Failure is { } failure)
            throw failure;
    }

    public ValueTask DisposeAsync() => _prepared.DisposeAsync();
}

/// <summary>
/// What Windows decodes for DropWheel. Its FFmpeg carries no decoder for formats still under patent (H.264, HEVC,
/// AAC, WMV, WMA and others); for those, the pictures and sound come from Windows' own decoders.
/// </summary>
public static partial class WindowsMedia
{
    internal const string MissingText =
        "This edition of Windows has no Media Foundation, which DropWheel needs for H.264, HEVC and AAC. Add the Media Feature Pack in Settings › System › Optional features.";

    internal const string HevcText = "Windows can't read this file's HEVC video. The HEVC Video Extensions from the Microsoft Store add it.";

    internal const string VideoText = "Windows can't decode this file's video, and DropWheel's FFmpeg doesn't carry its format.";

    internal const string SoundText = "Windows can't decode this file's sound, and DropWheel's FFmpeg doesn't carry its format.";

    internal const string WmvText = "DropWheel reads WMV and WMA files but doesn't write them, as their encoders aren't free to ship. Choose MP4 or M4A instead.";

    /// <summary>Every user-facing text of this part, for the translation tests.</summary>
    public static IReadOnlyList<string> Texts { get; } = [MissingText, HevcText, VideoText, SoundText, WmvText];

    internal static string CantRead(string path, MediaStreamInfo? stream) => stream switch
    {
        { Kind: MediaStreamKind.Video, Codec: "hevc" } => HevcText,
        { Kind: MediaStreamKind.Audio } => SoundText,
        _ => VideoText,
    };

    /// <summary>Gets an FFmpeg command ready: what DropWheel's FFmpeg can't decode comes from Windows instead.</summary>
    public static async Task<WindowsMediaCommand> PrepareAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        new(await FfmpegCommand.PrepareAsync(arguments, cancellationToken).ConfigureAwait(false));

    private static readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlySet<string>>>> Decoders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The decoders the FFmpeg in use has, by name.</summary>
    internal static Task<IReadOnlySet<string>> FfmpegDecodersAsync(CancellationToken cancellationToken)
    {
        var exe = Ffmpeg.Exe;
        return Decoders.GetOrAdd(exe, path => new Lazy<Task<IReadOnlySet<string>>>(() => ListAsync(path))).Value.WaitAsync(cancellationToken);

        static async Task<IReadOnlySet<string>> ListAsync(string path)
        {
            var result = await FfmpegProcess.RunAsync(path, ["-hide_banner", "-nostdin", "-decoders"], null, CancellationToken.None).ConfigureAwait(false);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in DecoderLine().Matches(result.StandardOutput))
                names.Add(m.Groups[1].Value);
            if (names.Count == 0)
                throw new MediaException("FFmpeg was not found.");
            return names;
        }
    }

    private static readonly AsyncLocal<IReadOnlySet<string>?> Forced = new();

    /// <summary>For tests: these codecs go through Windows, as if FFmpeg had no decoder for them, until disposed.</summary>
    internal static IDisposable ForceWindows(params string[] codecs)
    {
        var before = Forced.Value;
        Forced.Value = codecs.ToHashSet(StringComparer.Ordinal);
        return new Restore(() => Forced.Value = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Whether FFmpeg decodes a codec itself, by its own decoder or the library that does it for it.</summary>
    internal static bool CanDecode(string codec, IReadOnlySet<string> decoders) => Forced.Value?.Contains(codec) != true && codec switch
    {
        "av1" => decoders.Contains("libdav1d"),
        "vp8" => decoders.Contains("vp8") || decoders.Contains("libvpx"),
        "vp9" => decoders.Contains("vp9") || decoders.Contains("libvpx-vp9"),
        "opus" => decoders.Contains("opus") || decoders.Contains("libopus"),
        _ => decoders.Contains(codec),
    };

    /// <summary>A line of "ffmpeg -decoders": " V....D h264   H.264 / AVC…".</summary>
    [GeneratedRegex(@"^\s*[VASDT.][F.][S.][X.][B.][D.]\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex DecoderLine();
}
