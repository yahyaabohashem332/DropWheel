using System.Globalization;
using System.Text;
using Petal.Core.Util;
using Petal.Engines.Media.Internal;
using Whisper.net;

namespace Petal.Engines.Media;

/// <param name="ModelPath">A Whisper ggml model file.</param>
/// <param name="Language">"auto" to detect, or a code such as "ar" or "en".</param>
/// <param name="TranslateToEnglish">Write the English translation instead of the original words.</param>
public sealed record TranscribeOptions(string ModelPath, string Language = "auto", bool TranslateToEnglish = false);

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>What was said, with timings, and the language Whisper heard.</summary>
public sealed record Transcript(IReadOnlyList<TranscriptSegment> Segments, string Language)
{
    public bool IsEmpty => Segments.All(s => string.IsNullOrWhiteSpace(s.Text));

    /// <summary>SubRip subtitles, which every player and editor reads.</summary>
    public string ToSrt()
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var s in Speech())
        {
            sb.Append(++n).Append('\n');
            sb.Append(Stamp(s.Start, ',')).Append(" --> ").Append(Stamp(s.End, ',')).Append('\n');
            sb.Append(s.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>WebVTT subtitles, for the web and HTML5 video.</summary>
    public string ToVtt()
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var s in Speech())
            sb.Append(Stamp(s.Start, '.')).Append(" --> ").Append(Stamp(s.End, '.')).Append('\n').Append(s.Text).Append("\n\n");
        return sb.ToString();
    }

    /// <summary>Plain text, one paragraph per pause.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        TimeSpan? last = null;
        foreach (var s in Speech())
        {
            // A pause of two seconds or more starts a new paragraph.
            if (last is { } end && s.Start - end >= TimeSpan.FromSeconds(2))
                sb.Append("\n\n");
            else if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(s.Text);
            last = s.End;
        }
        return sb.ToString().Trim() + "\n";
    }

    private IEnumerable<TranscriptSegment> Speech() => Segments.Where(s => !string.IsNullOrWhiteSpace(s.Text));

    private static string Stamp(TimeSpan t, char decimals) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{decimals}{t.Milliseconds:000}");
}

/// <summary>
/// Speech to text with Whisper, entirely on this PC: on the graphics card through Vulkan when it
/// can, otherwise on the processor.
/// </summary>
public static class Transcriber
{
    /// <summary>The languages Whisper knows, as codes ("ar", "en", …).</summary>
    public static IReadOnlyList<string> Languages { get; } =
    [
        "ar", "en", "fr", "de", "es", "it", "pt", "nl", "tr", "fa", "ur", "hi", "ru", "uk", "pl", "sv", "id", "ms", "zh", "ja", "ko", "he",
    ];

    public static async Task<Transcript> TranscribeAsync(string input, TranscribeOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException("The speech model isn't downloaded yet.", options.ModelPath);
        // Whisper listens to 16 kHz mono.
        var pcm = await MediaPreview.DecodePcmAsync(input, new PcmDecodeOptions(16000, 1), cancellationToken).ConfigureAwait(false);
        if (pcm.Samples.Length < 16000 / 2)
            throw new MediaException("This file has no sound to transcribe.");
        progress?.Report(0.05);

        return await Task.Run(async () =>
        {
            using var factory = WhisperFactory.FromPath(options.ModelPath);
            var builder = factory.CreateBuilder()
                .WithLanguage(string.IsNullOrEmpty(options.Language) ? "auto" : options.Language)
                .WithProgressHandler(percent => progress?.Report(0.05 + 0.95 * Math.Clamp(percent, 0, 100) / 100.0));
            if (options.TranslateToEnglish)
                builder = builder.WithTranslate();
            await using var processor = builder.Build();
            var segments = new List<TranscriptSegment>();
            string language = options.Language;
            await foreach (var segment in processor.ProcessAsync(pcm.Samples, cancellationToken).ConfigureAwait(false))
            {
                segments.Add(new TranscriptSegment(segment.Start, segment.End, segment.Text.Trim()));
                if (!string.IsNullOrEmpty(segment.Language))
                    language = segment.Language;
            }
            progress?.Report(1);
            return new Transcript(segments, options.TranslateToEnglish ? "en" : language);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts subtitles into a copy of the video. Burned in, they are part of the picture and show
    /// everywhere; otherwise they are a track viewers switch on, and the video isn't re-encoded.
    /// </summary>
    public static async Task<string> AddSubtitlesAsync(string video, string subtitles, string output, bool burnIn, string language = "und",
        Petal.Core.Jobs.ConversionSettings? settings = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(video, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var target = Containers.ForOutput(output, video);
        if (burnIn)
        {
            // libass draws the text with Windows' Segoe UI and shapes Arabic. Encoding=-1 lets each
            // line take its direction from its first letter, so an Arabic line reads right to left
            // (libass otherwise lays every line out left to right, like VSFilter did).
            var style = "FontName=Segoe UI,FontSize=20,Outline=1.6,Shadow=0.6,BorderStyle=1,MarginV=28,Encoding=-1";
            var plan = ToolSupport.Plan(video, info, target, settings ?? Petal.Core.Jobs.ConversionSettings.Default) with
            {
                VideoFilter = $"subtitles=filename={VideoTools.FilterPath(subtitles)}:charenc=UTF-8:force_style='{style}'",
                Audio = ToolSupport.CopyAudioIfPossible(info, target),
            };
            await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
            return output;
        }
        var codec = target.SubtitleCodec ?? throw new MediaException("This video format can't hold a subtitle track. Choose “Burned in”, or save as MP4 or MKV.");
        var args = new List<string>
        {
            "-i", video, "-i", subtitles,
            "-map", "0:v", "-map", "0:a?", "-map", "1:0",
            "-c:v", "copy", "-c:a", "copy", "-c:s", codec,
            "-metadata:s:s:0", $"language={Iso639_2(language)}",
            "-disposition:s:0", "default",
        };
        if (target.FastStart)
            args.AddRange(["-movflags", "+faststart"]);
        await Ffmpeg.EncodeAsync(args, output, target.Muxer, info.DurationSeconds, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Three-letter language codes for the subtitle track (what players show).</summary>
    internal static string Iso639_2(string code) => code switch
    {
        "ar" => "ara", "en" => "eng", "fr" => "fra", "de" => "deu", "es" => "spa", "it" => "ita", "pt" => "por", "nl" => "nld",
        "tr" => "tur", "fa" => "fas", "ur" => "urd", "hi" => "hin", "ru" => "rus", "uk" => "ukr", "pl" => "pol", "sv" => "swe",
        "id" => "ind", "ms" => "msa", "zh" => "zho", "ja" => "jpn", "ko" => "kor", "he" => "heb",
        _ => "und",
    };
}
