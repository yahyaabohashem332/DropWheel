using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Util;
using Petal.Engines.Media.Internal;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Media;

/// <summary>
/// Audio and video conversions (including GIF) through FFmpeg. Streams that are already valid for the
/// target container are copied instead of re-encoded ("smart remux").
/// </summary>
public sealed class MediaConverter : IConverter
{
    public string Name => "FFmpeg";

    public bool CanConvert(FileFormat from, FileFormat to)
    {
        if (from == to || !MediaCapabilities.IsAvailable || !Containers.Supports(to))
            return false;
        return from.Family switch
        {
            FormatFamily.Audio => to.Family == FormatFamily.Audio,
            FormatFamily.Video when from == F.Gif => to.Family == FormatFamily.Video,
            FormatFamily.Video => to.Family is FormatFamily.Video or FormatFamily.Audio,
            _ => false,
        };
    }

    public async Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        TempOutput.EnsureNotInput(job.OutputPath, job.InputPath);
        var safe = new SafeProgress(progress);
        var info = await MediaProbe.ProbeAsync(job.InputPath, cancellationToken).ConfigureAwait(false);
        var plan = Plan(job, info);

        try
        {
            await MediaEncoder.RunAsync(plan, job.OutputPath, safe, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalToolException) when (!cancellationToken.IsCancellationRequested &&
                                           (plan.Video == StreamMode.Copy || plan.Audio == StreamMode.Copy))
        {
            // Some streams that look compatible still refuse to mux (odd timestamps, exotic tags): re-encode them.
            var reencode = plan with
            {
                Video = plan.Video == StreamMode.Copy ? StreamMode.Encode : plan.Video,
                Audio = plan.Audio == StreamMode.Copy ? StreamMode.Encode : plan.Audio,
            };
            await MediaEncoder.RunAsync(reencode, job.OutputPath, safe, cancellationToken).ConfigureAwait(false);
        }

        safe.Report(1);
        return JobResult.Single(job.OutputPath);
    }

    /// <summary>Decides per stream whether to copy or encode, and with which filters.</summary>
    internal static EncodePlan Plan(ConversionJob job, MediaInfo info)
    {
        var target = Containers.For(job.To);
        var settings = job.Settings ?? ConversionSettings.Default;

        if (target.IsAudioOnly && !info.HasAudio)
            throw new MediaException($"\"{Path.GetFileName(job.InputPath)}\" has no audio track to convert.");
        if (!target.IsAudioOnly && !info.HasVideo)
            throw new MediaException($"\"{Path.GetFileName(job.InputPath)}\" has no video to convert.");

        var fromGif = info.Video?.Codec == "gif" || job.From == F.Gif;

        var videoMode = StreamMode.Drop;
        string? videoFilter = null;
        GifSettings? gif = null;
        if (!target.IsAudioOnly)
        {
            if (target.IsGif)
            {
                videoMode = StreamMode.Encode;
                var fps = Math.Clamp(settings.GifFps, 1, 50);
                if (info.FrameRate > 0)
                    fps = (int)Math.Min(fps, Math.Ceiling(info.FrameRate));
                gif = new GifSettings(fps, Math.Max(0, settings.GifMaxWidth));
            }
            else if (!fromGif && target.CopyVideo.Contains(info.Video!.Codec))
            {
                videoMode = StreamMode.Copy;
            }
            else
            {
                videoMode = StreamMode.Encode;
                if (fromGif)
                {
                    // GIF frame delays are in 1/100 s; give encoders (and AVI/WMV muxers) a constant rate.
                    var rate = info.FrameRate is > 1 and <= 60 ? Math.Round(info.FrameRate, 3) : 15;
                    videoFilter = $"fps={Inv.N(rate)}";
                }
            }
        }
        else if (info.CoverArt is not null && target.CoverArt)
        {
            videoMode = StreamMode.Copy;
        }

        var audioMode = StreamMode.Drop;
        if (info.HasAudio && !target.IsGif)
        {
            var allCopyable = (target.MultipleAudio ? info.AudioStreams : [info.Audio!]).All(a => target.CopyAudio.Contains(a.Codec));
            audioMode = allCopyable ? StreamMode.Copy : StreamMode.Encode;
        }

        return new EncodePlan
        {
            InputPath = job.InputPath,
            Source = info,
            Target = target,
            Video = videoMode,
            Audio = audioMode,
            Subtitles = true,
            VideoFilter = videoFilter,
            Quality = new VideoQuality(settings.VideoCrf),
            AudioKbps = settings.AudioBitrateKbps,
            Hardware = settings.HardwareVideoEncoding,
            Duration = info.DurationSeconds,
            Gif = gif,
        };
    }
}
