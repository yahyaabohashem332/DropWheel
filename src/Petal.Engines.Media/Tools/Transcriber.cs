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

    /// <summary>
    /// Whether a model can put speech into English itself. large-v3 turbo was trained to write what is said only:
    /// asked to translate, it writes the language spoken or nonsense (as do the distilled models).
    /// </summary>
    public static bool CanTranslate(string modelPath)
    {
        var name = Path.GetFileName(modelPath).ToLowerInvariant();
        return !name.Contains("turbo") && !name.Contains("distil");
    }

    /// <summary>A file's sound as Whisper listens to it: 16 kHz mono.</summary>
    public static async Task<float[]> ReadSpeechAsync(string input, CancellationToken cancellationToken = default)
    {
        var pcm = await MediaPreview.DecodePcmAsync(input, new PcmDecodeOptions(16000, 1), cancellationToken).ConfigureAwait(false);
        if (pcm.Samples.Length < 16000 / 2)
            throw new MediaException("This file has no sound to transcribe.");
        return pcm.Samples;
    }

    /// <summary>The language spoken in the first half minute ("ar", "en"…), or null when Whisper can't tell.</summary>
    public static async Task<string?> DetectLanguageAsync(float[] samples, string modelPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The speech model isn't downloaded yet.", modelPath);
        return await Task.Run(() =>
        {
            using var factory = WhisperFactory.FromPath(modelPath);
            using var processor = factory.CreateBuilder().WithLanguage("auto").Build();
            var start = samples.AsSpan(0, Math.Min(samples.Length, 30 * 16000));
            var (language, probability) = processor.DetectLanguageWithProbability(start);
            return string.IsNullOrEmpty(language) || probability < 0.2f ? null : language;
        }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Transcript> TranscribeAsync(string input, TranscribeOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException("The speech model isn't downloaded yet.", options.ModelPath);
        var samples = await ReadSpeechAsync(input, cancellationToken).ConfigureAwait(false);
        progress?.Report(0.05);
        return await TranscribeAsync(samples, options, progress is null ? null : new Scaled(progress, 0.05, 1), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Speech already read (16 kHz mono) written down, or put into English.</summary>
    public static async Task<Transcript> TranscribeAsync(float[] samples, TranscribeOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException("The speech model isn't downloaded yet.", options.ModelPath);
        if (options.TranslateToEnglish && !CanTranslate(options.ModelPath))
            throw new InvalidOperationException("This speech model can't translate; another one puts the words into English.");

        return await Task.Run(async () =>
        {
            using var factory = WhisperFactory.FromPath(options.ModelPath);
            var builder = factory.CreateBuilder()
                .WithLanguage(string.IsNullOrEmpty(options.Language) ? "auto" : options.Language)
                .WithProgressHandler(percent => progress?.Report(Math.Clamp(percent, 0, 100) / 100.0));
            if (options.TranslateToEnglish)
                builder = builder.WithTranslate();
            await using var processor = builder.Build();
            var segments = new List<TranscriptSegment>();
            string language = options.Language;
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
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

    /// <summary>Progress of a part of the work, as a share of the whole.</summary>
    private sealed class Scaled(IProgress<double> inner, double from, double to) : IProgress<double>
    {
        public void Report(double value) => inner.Report(from + (to - from) * value);
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
