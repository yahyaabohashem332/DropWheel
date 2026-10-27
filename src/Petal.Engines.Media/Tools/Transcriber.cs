using System.Globalization;
using System.Text;
using Petal.Core.Util;
using Petal.Engines.Media.Internal;
using Whisper.net;

namespace Petal.Engines.Media;

/// <param name="ModelPath">A Whisper ggml model file.</param>
/// <param name="Language">"auto" to detect, or a code such as "ar" or "en".</param>
/// <param name="TranslateToEnglish">Write the English translation instead of the original words.</param>
/// <param name="WordTimes">Also when each word is said, aligned to the sound (see <see cref="TranscriptSegment.Words"/>).</param>
public sealed record TranscribeOptions(string ModelPath, string Language = "auto", bool TranslateToEnglish = false, bool WordTimes = false);

/// <summary>A word and when it is said, in seconds from the start of the recording.</summary>
public sealed record SpokenWord(string Text, double Start, double End);

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text)
{
    /// <summary>The segment's words with their own times, when they were asked for; otherwise none.</summary>
    public IReadOnlyList<SpokenWord> Words { get; init; } = [];
}

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
        if (options.WordTimes && !options.TranslateToEnglish)
            return await TranscribeWordsAsync(samples, options, null, progress, cancellationToken).ConfigureAwait(false);

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

    /// <summary>The longest stretch of sound Whisper is given at once when the words' times are wanted.</summary>
    private const double LongestPiece = 28;

    /// <summary>
    /// What was said with when each word is said, aligned to the sound. Whisper's alignment (DTW) stops without a word
    /// partway through a long recording, so the sound is cut into pieces of under half a minute at its pauses, each
    /// written down with what was said just before it as context; quiet between the pieces is never read (which is
    /// where Whisper makes words up). A piece the alignment fails on is written again with Whisper's own times. The
    /// language is told from the first piece with speech, unless it was given, and kept for the rest.
    /// </summary>
    /// <param name="levels">The sound's levels, when already measured.</param>
    public static async Task<Transcript> TranscribeWordsAsync(float[] samples, TranscribeOptions options, SoundLevels? levels = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException("The speech model isn't downloaded yet.", options.ModelPath);
        levels ??= SoundLevels.Measure(samples);
        var pieces = Pieces(levels, samples.Length / 16000.0);
        return await Task.Run(async () =>
        {
            var heads = StreamingTranscriber.Heads(Path.GetFileName(options.ModelPath));
            using var aligned = heads is { } preset
                ? WhisperFactory.FromPath(options.ModelPath, new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = preset })
                : WhisperFactory.FromPath(options.ModelPath);
            WhisperFactory? plain = null;
            try
            {
                string language = string.IsNullOrEmpty(options.Language) ? "auto" : options.Language;
                var segments = new List<TranscriptSegment>();
                var recent = new List<string>();
                for (int p = 0; p < pieces.Count; p++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (from, to) = pieces[p];
                    var sound = samples.AsSpan((int)(from * 16000), Math.Min(samples.Length, (int)(to * 16000)) - (int)(from * 16000)).ToArray();
                    var context = string.Join(' ', recent);
                    var found = await PieceAsync(aligned, heads is not null, sound, from, language, context, cancellationToken).ConfigureAwait(false);
                    if (found.Segments.Count == 0 && heads is not null)
                    {
                        plain ??= WhisperFactory.FromPath(options.ModelPath);
                        found = await PieceAsync(plain, false, sound, from, language, context, cancellationToken).ConfigureAwait(false);
                    }
                    if (language == "auto" && found.Language is { Length: > 0 } heard && found.Segments.Count > 0)
                        language = heard;
                    foreach (var segment in found.Segments)
                    {
                        segments.Add(segment);
                        recent.Add(segment.Text);
                        if (recent.Count > 3)
                            recent.RemoveAt(0);
                    }
                    progress?.Report((p + 1.0) / pieces.Count);
                }
                progress?.Report(1);
                return new Transcript(segments, language);
            }
            finally
            {
                plain?.Dispose();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One piece of sound written down, its times moved to where the piece is in the recording.</summary>
    private static async Task<(List<TranscriptSegment> Segments, string? Language)> PieceAsync(WhisperFactory factory, bool aligned, float[] sound, double offset,
        string language, string context, CancellationToken cancellationToken)
    {
        var builder = factory.CreateBuilder().WithLanguage(language).WithTokenTimestamps().WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8));
        if (context.Length > 0)
            builder = builder.WithPrompt(context.Length > 200 ? context[^200..] : context);
        await using var processor = builder.Build();
        var segments = new List<TranscriptSegment>();
        string? heard = null;
        double length = sound.Length / 16000.0;
        await foreach (var segment in processor.ProcessAsync(sound, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(segment.Language))
                heard = segment.Language;
            var text = segment.Text.Trim();
            if (text.Length == 0 || LiveTranscriber.IsNoise(text) || MadeUp(text) || segment.NoSpeechProbability > 0.7 && segment.Probability < 0.4)
                continue;
            var words = TokenWords.Spelled(text, TokenWords.Of(segment, aligned))
                .Select(w => new SpokenWord(w.Text, offset + Math.Clamp(w.Start, 0, length), offset + Math.Clamp(w.End, 0, length))).ToList();
            segments.Add(new TranscriptSegment(segment.Start + TimeSpan.FromSeconds(offset), segment.End + TimeSpan.FromSeconds(offset), text) { Words = words });
        }
        return (segments, heard);
    }

    /// <summary>What Whisper writes when it hears music or nothing and makes words up, from the videos it learnt from.</summary>
    internal static bool MadeUp(string text)
    {
        var key = new string(text.Where(c => char.IsLetter(c) || c == ' ').ToArray()).Trim().ToLowerInvariant();
        return key is "thank you for watching" or "thanks for watching" or "thank you so much for watching" or "please subscribe"
                   or "subscribe to the channel" or "see you in the next video"
               || key.Contains("نانسي قنقر") || key.Contains("اشتركوا في القناة") || key.Contains("شكرا للمشاهدة") || key.Contains("لا تنسوا الاشتراك");
    }

    /// <summary>
    /// The recording cut into pieces of at most <see cref="LongestPiece"/> seconds for Whisper, at the longest pause
    /// in each piece's last part; quiet longer than a second between them is left out.
    /// </summary>
    internal static List<(double Start, double End)> Pieces(SoundLevels levels, double length)
    {
        var pieces = new List<(double Start, double End)>();
        double t = 0;
        while (t < length - 0.3)
        {
            while (t < length && levels.QuietShare(t, Math.Min(length, t + 1)) > 0.97)
                t += 0.5;
            if (t >= length - 0.3)
                break;
            double start = Math.Max(0, t - 0.25);
            if (length - start <= LongestPiece)
            {
                pieces.Add((start, length));
                break;
            }
            double end = levels.LongestPauseBetween(start + LongestPiece * 0.45, start + LongestPiece);
            pieces.Add((start, end));
            t = end;
        }
        return pieces;
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
