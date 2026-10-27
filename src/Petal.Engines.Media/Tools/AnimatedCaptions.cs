using System.Globalization;
using System.Text;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media;

/// <summary>How the words appear.</summary>
public enum CaptionStyle
{
    /// <summary>A short line at a time, the word being said lit up and a little larger.</summary>
    WordByWord,
    /// <summary>One big word at a time, popping in.</summary>
    OneWord,
    /// <summary>Plain lines, as ordinary subtitles but bolder.</summary>
    Lines,
}

public enum CaptionPlace
{
    Bottom,
    Middle,
    Top,
    /// <summary>A third of the way up: clear of the buttons and the description short-video apps lay over the bottom.</summary>
    Lower,
}

/// <param name="Size">Letter height as a share of the picture's height (0.04 small … 0.09 large).</param>
/// <param name="Highlight">The colour of the word being said, "#RRGGBB".</param>
/// <param name="MostWords">Words in a line before a new one starts.</param>
public sealed record AnimatedCaptionOptions(CaptionStyle Style = CaptionStyle.WordByWord, CaptionPlace Place = CaptionPlace.Bottom, double Size = 0.06,
    string Highlight = "#FFD60A", int MostWords = 5) : MediaToolOptions;

/// <summary>A word and when it is said.</summary>
public sealed record CaptionWord(string Text, TimeSpan Start, TimeSpan End);

/// <summary>
/// Captions that move with the voice, burned into the picture, as short videos have them: each line
/// a few words, the word being said lit up. Words take their time from their sentence in proportion to
/// their length, which reads naturally in any language (and after translation too). Drawn by libass,
/// which shapes Arabic and lays it right to left.
/// </summary>
public static class AnimatedCaptions
{
    /// <summary>The sentences' words, each given a share of its sentence's time by its length.</summary>
    public static List<CaptionWord> Words(IReadOnlyList<TranscriptSegment> segments)
    {
        var words = new List<CaptionWord>();
        foreach (var segment in segments)
        {
            var parts = segment.Text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || segment.End <= segment.Start)
                continue;
            // A word's weight: its letters, and a little for the space after it.
            var weights = parts.Select(p => p.Count(char.IsLetterOrDigit) + 1.5).ToArray();
            double total = weights.Sum(), length = (segment.End - segment.Start).TotalSeconds, at = segment.Start.TotalSeconds;
            for (int i = 0; i < parts.Length; i++)
            {
                double share = length * weights[i] / total;
                words.Add(new CaptionWord(parts[i], TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(at + share)));
                at += share;
            }
        }
        return words;
    }

    /// <summary>
    /// Lines of at most <paramref name="mostWords"/> words and <paramref name="mostLetters"/> letters; a line also ends at
    /// the end of a sentence, or where the speaker pauses.
    /// </summary>
    public static List<List<CaptionWord>> Lines(IReadOnlyList<CaptionWord> words, int mostWords, int mostLetters = 28)
    {
        var lines = new List<List<CaptionWord>>();
        List<CaptionWord>? line = null;
        foreach (var word in words)
        {
            bool pause = line is { Count: > 0 } && word.Start - line[^1].End > TimeSpan.FromSeconds(0.6);
            if (line is null || pause || line.Count >= Math.Max(1, mostWords) || line.Sum(w => w.Text.Length + 1) + word.Text.Length > mostLetters)
            {
                line = [];
                lines.Add(line);
            }
            line.Add(word);
            if (word.Text.EndsWith('.') || word.Text.EndsWith('?') || word.Text.EndsWith('!') || word.Text.EndsWith('؟') || word.Text.EndsWith('،') && line.Count >= 3)
                line = null;
        }
        return lines;
    }

    /// <summary>The captions as an Advanced SubStation file, sized for a picture of this size.</summary>
    public static string Ass(IReadOnlyList<TranscriptSegment> segments, int width, int height, AnimatedCaptionOptions options) =>
        Ass(Words(segments), width, height, options);

    /// <summary>
    /// Words already timed (each word's own time, when it is known) as an Advanced SubStation file for a picture of this
    /// size; with a <paramref name="headline"/>, a title in a box of the lit word's colour at the top for the first
    /// <paramref name="headlineSeconds"/> seconds, as short videos open.
    /// </summary>
    public static string Ass(IReadOnlyList<CaptionWord> words, int width, int height, AnimatedCaptionOptions options, string? headline = null, double headlineSeconds = 4.5)
    {
        int size = Math.Max(12, (int)Math.Round(height * Math.Clamp(options.Size, 0.02, 0.15) * (options.Style == CaptionStyle.OneWord ? 1.5 : 1)));
        int alignment = options.Place switch { CaptionPlace.Top => 8, CaptionPlace.Middle => 5, _ => 2 };
        int margin = (int)Math.Round(height * options.Place switch { CaptionPlace.Middle => 0, CaptionPlace.Lower => 0.28, _ => 0.09 });
        // As many letters as fit across, at about half a letter's height each (never more than the 28 that read well).
        int letters = Math.Clamp((int)(width * 5 / 6.0 / (size * 0.56)), 8, 28);
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.Append("PlayResX: ").AppendLine(width.ToString(CultureInfo.InvariantCulture));
        sb.Append("PlayResY: ").AppendLine(height.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        // Encoding -1: each line takes its direction from its own letters, so Arabic reads right to left.
        sb.AppendLine(Invariant($"Style: Petal,Segoe UI,{size},&H00FFFFFF,&H00FFFFFF,&H00101010,&H80000000,-1,0,0,0,100,100,0,0,1,{Math.Max(2, size / 11)},{Math.Max(1, size / 22)},{alignment},{width / 12},{width / 12},{margin},-1"));
        int titleSize = Math.Max(12, (int)Math.Round(height * (height > width * 1.2 ? 0.032 : 0.05)));
        // An opaque box (border style 3) in the lit word's colour, its padding the "outline"; dark letters on it.
        sb.AppendLine(Invariant($"Style: Headline,Segoe UI,{titleSize},&H00141414,&H00141414,{Colour(options.Highlight).TrimEnd('&')},&H00000000,-1,0,0,0,100,100,0,0,3,{Math.Max(4, titleSize / 3)},0,8,{width / 10},{width / 10},{(int)Math.Round(height * (height > width * 1.2 ? 0.11 : 0.06))},-1"));
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        if (!string.IsNullOrWhiteSpace(headline) && headlineSeconds > 0)
            sb.Append("Dialogue: 1,").Append(Stamp(TimeSpan.Zero)).Append(',').Append(Stamp(TimeSpan.FromSeconds(headlineSeconds)))
              .Append(",Headline,,0,0,0,,").Append(@"{\fad(200,350)}").AppendLine(Escape(headline.Trim()));
        var lit = Colour(options.Highlight);
        switch (options.Style)
        {
            case CaptionStyle.OneWord:
                foreach (var word in words)
                    Event(sb, word.Start, word.End, @"{\fscx70\fscy70\t(0,90,\fscx100\fscy100)}" + Escape(word.Text));
                break;
            case CaptionStyle.Lines:
                foreach (var line in Lines(words, options.MostWords, letters))
                    Event(sb, line[0].Start, line[^1].End, string.Join(' ', line.Select(w => Escape(w.Text))));
                break;
            default:
                foreach (var line in Lines(words, options.MostWords, letters))
                {
                    for (int i = 0; i < line.Count; i++)
                    {
                        // The line stays up from its first word to its last; only the lit word changes.
                        var end = i + 1 < line.Count ? line[i + 1].Start : line[^1].End;
                        var text = string.Join(' ', line.Select((w, k) => k == i
                            ? $@"{{\c{lit}\fscx112\fscy112}}{Escape(w.Text)}{{\c&H00FFFFFF&\fscx100\fscy100}}"
                            : Escape(w.Text)));
                        Event(sb, line[i].Start, end, text);
                    }
                }
                break;
        }
        return sb.ToString();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static void Event(StringBuilder sb, TimeSpan start, TimeSpan end, string text) =>
        sb.Append("Dialogue: 0,").Append(Stamp(start)).Append(',').Append(Stamp(end)).Append(",Petal,,0,0,0,,").AppendLine(text);

    /// <summary>"0:01:02.35": hours, minutes, seconds and hundredths, as ASS wants them.</summary>
    internal static string Stamp(TimeSpan t)
    {
        t = t < TimeSpan.Zero ? TimeSpan.Zero : t;
        return Invariant($"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }

    /// <summary>"#FFD60A" as ASS writes colours: &amp;H00BBGGRR&amp;.</summary>
    internal static string Colour(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            h = "FFD60A";
        return $"&H00{h[4..6]}{h[2..4]}{h[0..2]}&".ToUpperInvariant();
    }

    /// <summary>A word as plain text in ASS: no braces or backslashes that would start a command.</summary>
    internal static string Escape(string text) => text.Replace("\\", "").Replace("{", "(").Replace("}", ")");

    /// <summary>Burns the captions into a copy of the video; the sound is kept as it is.</summary>
    public static async Task<string> BurnAsync(string video, IReadOnlyList<TranscriptSegment> segments, string output, AnimatedCaptionOptions options,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await ToolSupport.ProbeAsync(video, output, cancellationToken).ConfigureAwait(false);
        ToolSupport.RequireVideo(info);
        var target = Containers.ForOutput(output, video);
        var ass = Path.Combine(Path.GetTempPath(), $"petal-captions-{Guid.NewGuid():N}.ass");
        try
        {
            await File.WriteAllTextAsync(ass, Ass(segments, Math.Max(2, info.Width), Math.Max(2, info.Height), options), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var plan = ToolSupport.Plan(video, info, target, options.EffectiveSettings) with
            {
                VideoFilter = $"ass=filename={VideoTools.FilterPath(ass)}",
                Audio = ToolSupport.CopyAudioIfPossible(info, target),
            };
            await MediaEncoder.RunAsync(plan, output, new SafeProgress(progress), cancellationToken).ConfigureAwait(false);
            return output;
        }
        finally
        {
            try { File.Delete(ass); } catch (IOException) { }
        }
    }
}
