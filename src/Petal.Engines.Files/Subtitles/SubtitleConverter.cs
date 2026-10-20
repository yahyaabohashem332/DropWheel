using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Files.Subtitles;

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text, string? Settings = null);

/// <summary>SRT ↔ VTT keeping cue timing, and either one → plain text.</summary>
public sealed partial class SubtitleConverter : IConverter
{
    public string Name => "Subtitles";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        from.Family == FormatFamily.Subtitle && (to.Family == FormatFamily.Subtitle || to == F.Txt) && from != to;

    public async Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(job.InputPath, cancellationToken);
        var cues = Parse(text);
        progress?.Report(0.5);
        var output = job.To.Id switch
        {
            "srt" => WriteSrt(cues),
            "vtt" => WriteVtt(cues),
            _ => WriteText(cues),
        };
        var temp = job.OutputPath + ".petal-tmp";
        await File.WriteAllTextAsync(temp, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        File.Move(temp, job.OutputPath);
        progress?.Report(1);
        return JobResult.Single(job.OutputPath);
    }

    /// <summary>Parses SRT or WebVTT (detected from content), tolerating missing indices and odd line endings.</summary>
    public static List<SubtitleCue> Parse(string text)
    {
        var cues = new List<SubtitleCue>();
        var blocks = BlankLines().Split(text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('﻿', '\n', ' '));
        foreach (var raw in blocks)
        {
            var lines = raw.Split('\n').ToList();
            int timing = lines.FindIndex(l => l.Contains("-->"));
            if (timing < 0)
                continue; // WEBVTT header, NOTE, STYLE and REGION blocks.
            var match = TimingLine().Match(lines[timing]);
            if (!match.Success)
                continue;
            var start = ParseTime(match.Groups["start"].Value);
            var end = ParseTime(match.Groups["end"].Value);
            var settings = match.Groups["settings"].Value.Trim();
            var body = string.Join("\n", lines.Skip(timing + 1)).TrimEnd();
            cues.Add(new SubtitleCue(start, end, body, settings.Length > 0 ? settings : null));
        }
        return cues;
    }

    public static string WriteSrt(IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
        {
            sb.Append(i + 1).Append("\r\n");
            sb.Append(Format(cues[i].Start, ',')).Append(" --> ").Append(Format(cues[i].End, ',')).Append("\r\n");
            sb.Append(VoiceTags().Replace(cues[i].Text, "").Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    public static string WriteVtt(IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
        {
            sb.Append(Format(cue.Start, '.')).Append(" --> ").Append(Format(cue.End, '.'));
            if (cue.Settings is not null)
                sb.Append(' ').Append(cue.Settings);
            sb.Append('\n').Append(cue.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Just the words, one cue per line, without markup.</summary>
    public static string WriteText(IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        foreach (var cue in cues)
        {
            var plain = Tags().Replace(cue.Text, "").Replace("\n", " ").Trim();
            plain = System.Net.WebUtility.HtmlDecode(plain);
            if (plain.Length > 0)
                sb.AppendLine(plain);
        }
        return sb.ToString();
    }

    private static TimeSpan ParseTime(string value)
    {
        value = value.Replace(',', '.');
        var parts = value.Split(':');
        int hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        int minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        double seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        return new TimeSpan(0, hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
    }

    private static string Format(TimeSpan t, char separator) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{separator}{t.Milliseconds:000}";

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"^\s*(?<start>(\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*(?<end>(\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})(?<settings>.*)$")]
    private static partial Regex TimingLine();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    // SRT players don't understand WebVTT voice and class spans.
    [GeneratedRegex(@"</?(v|c|lang|ruby|rt)(\.[^ >]*)?( [^>]*)?>")]
    private static partial Regex VoiceTags();
}
