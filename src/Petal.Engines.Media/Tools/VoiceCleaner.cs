using System.Globalization;
using Petal.Core.Models;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <param name="MaxReductionDb">How far noise may be pushed down: about 12 is gentle, 100 removes all it can.</param>
public sealed record CleanVoiceOptions(double MaxReductionDb = 100) : MediaToolOptions;

/// <summary>
/// Remove Noise: DeepFilterNet 3 (run on this PC) takes hum, hiss, traffic and room noise out of
/// speech, in recordings and in videos (whose picture is copied as it is).
/// </summary>
public static class VoiceCleaner
{
    public static IReadOnlyList<ModelInfo> Models { get; } = [ModelCatalog.DeepFilter];

    public static bool IsReady => ModelStore.IsAvailable(ModelCatalog.DeepFilter);

    public static async Task<string> CleanAsync(string input, string output, CleanVoiceOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new CleanVoiceOptions();
        var program = ModelStore.Find(ModelCatalog.DeepFilter) ?? throw new MediaException("The noise removal model isn't downloaded yet.");
        var info = await ToolSupport.ProbeAsync(input, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireAudio(info);
        var safe = new SafeProgress(progress);
        using var scratch = new ScratchFolder();

        // The model works at 48 kHz, on up to two channels.
        var voice = scratch.File("voice.wav");
        int channels = Math.Clamp(info.Audio!.Channels, 1, 2);
        await Ffmpeg.RunAsync(["-i", input, "-map", $"0:{info.Audio.Index}", "-vn", "-sn", "-dn",
            "-ac", channels.ToString(CultureInfo.InvariantCulture), "-ar", "48000", "-c:a", "pcm_s16le", "-f", "wav", voice],
            info.DurationSeconds, safe.Slice(0, 0.15), cancellationToken).ConfigureAwait(false);

        var cleaned = scratch.File("clean");
        Directory.CreateDirectory(cleaned);
        // -D keeps the cleaned sound exactly in step with the picture.
        var result = await FfmpegProcess.RunAsync(program,
            ["-D", "-a", Math.Clamp(options.MaxReductionDb, 3, 100).ToString("0", CultureInfo.InvariantCulture), "-o", cleaned, voice],
            null, cancellationToken).ConfigureAwait(false);
        var clean = Path.Combine(cleaned, "voice.wav");
        if (!result.Succeeded || !File.Exists(clean))
            throw new MediaException("The noise removal didn't finish: " + result.StandardError.Trim().Split('\n').LastOrDefault());
        safe.Report(0.7);

        var target = Containers.ForOutput(output, input);
        var plan = ToolSupport.Plan(input, info, target, options.EffectiveSettings) with
        {
            Video = info.HasVideo ? ToolSupport.CopyVideoIfPossible(info, target) : StreamMode.Copy,
            Audio = StreamMode.Encode,
            ReplacementAudio = clean,
            AllAudioTracks = false,
            Duration = info.DurationSeconds,
        };
        await MediaEncoder.RunAsync(plan, output, safe.Slice(0.7, 0.3), cancellationToken).ConfigureAwait(false);
        safe.Report(1);
        return output;
    }
}
