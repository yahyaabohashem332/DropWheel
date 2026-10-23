using System.Globalization;
using System.Text;
using Petal.Core.Text;
using Petal.Engines.Media;

namespace Petal.Recipes;

/// <summary>
/// Notes from a recorded meeting: the transcript with times, and (with the AI model) a summary, the
/// decisions and the action items. A long meeting is summarised part by part and the parts then together,
/// since the model reads a few pages at a time. Everything stays on this PC.
/// </summary>
public static class MeetingNotes
{
    /// <summary>The transcript as lines with their time: "[12:05] We start with the budget."; a long pause starts a paragraph.</summary>
    public static string TranscriptText(Transcript transcript)
    {
        var sb = new StringBuilder();
        TimeSpan? last = null;
        foreach (var s in transcript.Segments.Where(s => !string.IsNullOrWhiteSpace(s.Text) && !LiveTranscriber.IsNoise(s.Text.Trim())))
        {
            if (last is { } end && s.Start - end >= TimeSpan.FromSeconds(3))
                sb.AppendLine();
            sb.Append('[').Append(Stamp(s.Start)).Append("] ").AppendLine(s.Text.Trim());
            last = s.End;
        }
        return sb.ToString().TrimEnd();
    }

    public static string Stamp(TimeSpan t) => t.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes:00}:{t.Seconds:00}");

    /// <summary>Pieces of at most <paramref name="size"/> characters, cut between lines.</summary>
    public static IReadOnlyList<string> Chunks(string text, int size)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > size)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            // A line longer than a piece is cut where it must be.
            var rest = line;
            while (rest.Length > size)
            {
                chunks.Add(rest[..size]);
                rest = rest[size..];
            }
            current.Append(rest).Append('\n');
        }
        if (current.ToString().Trim().Length > 0)
            chunks.Add(current.ToString().Trim());
        return chunks;
    }

    private static string Language(bool arabic) => arabic ? "Arabic" : "the language of the transcript";

    /// <summary>
    /// The summary, decisions and action items, written by the AI model. Parts of a long meeting are first
    /// boiled down to notes, then the notes are written up together.
    /// </summary>
    public static async Task<string> SummarizeAsync(string transcript, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        bool arabic = KeyboardLayout.LooksArabicLanguage(transcript);
        var parts = Chunks(transcript, AskPetal.MaxWritingText - 500);
        string material;
        if (parts.Count == 1)
        {
            material = parts[0];
        }
        else
        {
            var notes = new List<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                notes.Add(await AskPetal.WriteAsync(
                    $"This is part {i + 1} of {parts.Count} of a meeting transcript. Write short notes of what was discussed, any decisions, and any tasks with who will do them, in {Language(arabic)}. Reply with the notes only.",
                    parts[i], 500, cancellationToken).ConfigureAwait(false));
                progress?.Report((i + 1.0) / (parts.Count + 1));
            }
            material = string.Join("\n\n", notes);
            if (material.Length > AskPetal.MaxWritingText)
                material = material[..AskPetal.MaxWritingText];
        }
        var headings = arabic ? ("الملخص", "القرارات", "المهام") : ("Summary", "Decisions", "Action items");
        var result = await AskPetal.WriteAsync(
            $"From this meeting, write three sections in {Language(arabic)} with these exact headings, each on its own line starting with \"## \": " +
            $"\"## {headings.Item1}\" (3 to 5 sentences), \"## {headings.Item2}\" (bullet points starting with \"- \", or \"- None\"), " +
            $"\"## {headings.Item3}\" (bullet points \"- \" with who does what and when, if said, or \"- None\"). Reply with the three sections only.",
            material, 700, cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        return result;
    }

    /// <summary>The notes file: when and how long, the summary when there is one, then the transcript.</summary>
    public static string Markdown(DateTime started, TimeSpan length, string? summary, string transcript, bool arabic)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(arabic ? "ملاحظات الاجتماع" : "Meeting notes");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{started:yyyy-MM-dd HH:mm} · {Stamp(length)}"));
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine(summary.Trim());
            sb.AppendLine();
        }
        sb.Append("## ").AppendLine(arabic ? "النص الكامل" : "Transcript");
        sb.AppendLine();
        sb.AppendLine(transcript);
        return sb.ToString();
    }
}
