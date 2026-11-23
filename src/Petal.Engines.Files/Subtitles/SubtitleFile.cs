using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Petal.Core.Formats;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Files.Subtitles;

/// <summary>
/// Every subtitle format DropWheel reads and writes, through one list of cues. A cue's text is SRT's: lines
/// separated by \n, with &lt;i&gt;, &lt;b&gt; and &lt;u&gt; the only formatting; each writer turns that into its own.
/// SRT and WebVTT keep their own reader and writers (<see cref="SubtitleConverter"/>); this adds ASS and SSA,
/// LRC (timed lyrics), SAMI, MicroDVD and TTML.
/// </summary>
public static partial class SubtitleFile
{
    static SubtitleFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>MicroDVD counts frames; a file that doesn't say its rate is read at film's.</summary>
    public const double DefaultFrameRate = 23.976;

    /// <summary>
    /// A subtitle file's text: UTF-8 or UTF-16 by its byte order mark, UTF-8 when it reads as UTF-8, and otherwise
    /// the PC's own code page (older Arabic subtitles are often Windows-1256, Turkish 1254).
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }

    public static List<SubtitleCue> Read(string text, FileFormat format)
    {
        text = text.TrimStart('﻿');
        var cues = format.Id switch
        {
            "ass" or "ssa" => ReadAss(text),
            "lrc" => ReadLrc(text),
            "smi" => ReadSami(text),
            "sub" => ReadMicroDvd(text),
            "ttml" => ReadTtml(text),
            _ => SubtitleConverter.Parse(text),
        };
        if (cues.Count == 0)
            throw new InvalidDataException($"No subtitles were found in this {format.Label} file.");
        return cues;
    }

    public static string Write(IReadOnlyList<SubtitleCue> cues, FileFormat format) => format.Id switch
    {
        "srt" => SubtitleConverter.WriteSrt(cues),
        "vtt" => SubtitleConverter.WriteVtt(cues),
        "ass" => WriteAss(cues, ssa: false),
        "ssa" => WriteAss(cues, ssa: true),
        "lrc" => WriteLrc(cues),
        "smi" => WriteSami(cues),
        "sub" => WriteMicroDvd(cues),
        "ttml" => WriteTtml(cues),
        _ => SubtitleConverter.WriteText(cues),
    };

    /// <summary>
    /// The same file with other words: an ASS or SSA file keeps its header, styles and each line's own leading
    /// tags (position, colour), only the words change; other formats are written again from the cues.
    /// </summary>
    public static string WithTexts(string original, FileFormat format, IReadOnlyList<SubtitleCue> cues, IReadOnlyList<string> texts)
    {
        if (format.Id is not ("ass" or "ssa"))
            return Write(cues.Select((c, i) => c with { Text = texts[i] }).ToList(), format);
        var lines = original.Replace("\r\n", "\n").Split('\n');
        var dialogue = AssDialogues(lines).ToList();
        // The cues were sorted by time; the file keeps its own order.
        var order = dialogue.Select((d, i) => i).OrderBy(i => dialogue[i].Cue.Start).ThenBy(i => i).ToList();
        for (int k = 0; k < order.Count && k < texts.Count; k++)
        {
            var (line, _, textStart) = dialogue[order[k]];
            var oldText = lines[line][textStart..];
            var lead = AssLeadingTags().Match(oldText).Value;
            lines[line] = lines[line][..textStart] + lead + ToAssText(texts[k]);
        }
        return string.Join("\r\n", lines);
    }

    // ---------- ASS and SSA ----------

    private static List<SubtitleCue> ReadAss(string text) =>
        AssDialogues(text.Replace("\r\n", "\n").Split('\n')).Select(d => d.Cue).OrderBy(c => c.Start).ToList();

    /// <summary>Each Dialogue line: where it is, its cue, and where its text begins.</summary>
    private static IEnumerable<(int Line, SubtitleCue Cue, int TextStart)> AssDialogues(string[] lines)
    {
        string[] fields = ["Marked", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
        bool events = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith('['))
            {
                events = line.Trim().Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!events)
                continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                fields = line[7..].Split(',').Select(f => f.Trim()).ToArray();
                continue;
            }
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                continue;
            int start = Array.FindIndex(fields, f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
            int end = Array.FindIndex(fields, f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
            int textField = Array.FindIndex(fields, f => f.Equals("Text", StringComparison.OrdinalIgnoreCase));
            if (start < 0 || end < 0 || textField < 0)
                continue;
            // The text is the last field and may hold commas: split only as far as it.
            int position = "Dialogue:".Length;
            var values = new List<string>();
            for (int f = 0; f < textField; f++)
            {
                int comma = line.IndexOf(',', position);
                if (comma < 0) break;
                values.Add(line[position..comma].Trim());
                position = comma + 1;
            }
            if (values.Count < textField || !AssTime(values[start], out var from) || !AssTime(values[end], out var to))
                continue;
            // A line that only draws shapes has no words to show or translate.
            var words = FromAssText(line[position..]);
            if (Plain(words).Trim().Length == 0)
                continue;
            yield return (i, new SubtitleCue(from, to, words), position);
        }
    }

    private static bool AssTime(string value, out TimeSpan time)
    {
        time = default;
        var m = AssTimePattern().Match(value);
        if (!m.Success)
            return false;
        time = new TimeSpan(0, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))
            + TimeSpan.FromSeconds(double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture));
        return true;
    }

    private static string FromAssText(string text)
    {
        // Drawings ({\p1}…{\p0}) are shapes, not words.
        text = AssDrawing().Replace(text, "");
        text = AssOverride().Replace(text, m =>
        {
            var tags = m.Value;
            var html = new StringBuilder();
            foreach (Match t in AssStyleTag().Matches(tags))
                html.Append(t.Groups[2].Value == "0" ? $"</{t.Groups[1].Value}>" : $"<{t.Groups[1].Value}>");
            return html.ToString();
        });
        text = text.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
        return Balance(text.Trim());
    }

    private static string ToAssText(string text) =>
        text.Replace("\r\n", "\n").Replace("{", "(").Replace("}", ")")
            .Replace("<i>", "{\\i1}").Replace("</i>", "{\\i0}").Replace("<b>", "{\\b1}").Replace("</b>", "{\\b0}")
            .Replace("<u>", "{\\u1}").Replace("</u>", "{\\u0}").Replace("\n", "\\N");

    private static string WriteAss(IReadOnlyList<SubtitleCue> cues, bool ssa)
    {
        var sb = new StringBuilder();
        sb.Append("[Script Info]\r\n; Written by DropWheel\r\n");
        sb.Append(ssa ? "ScriptType: v4.00\r\n" : "ScriptType: v4.00+\r\n");
        sb.Append("PlayResX: 1920\r\nPlayResY: 1080\r\nWrapStyle: 0\r\n");
        if (!ssa)
            sb.Append("ScaledBorderAndShadow: yes\r\n");
        sb.Append("\r\n");
        if (ssa)
        {
            sb.Append("[V4 Styles]\r\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding\r\n");
            sb.Append("Style: Default,Arial,64,16777215,65535,0,0,0,0,1,3,1,2,40,40,50,0,1\r\n\r\n");
            sb.Append("[Events]\r\nFormat: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n");
        }
        else
        {
            sb.Append("[V4+ Styles]\r\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\r\n");
            sb.Append("Style: Default,Arial,64,&H00FFFFFF,&H0000FFFF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,3,1,2,40,40,50,1\r\n\r\n");
            sb.Append("[Events]\r\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n");
        }
        foreach (var cue in cues)
            sb.Append(ssa ? "Dialogue: Marked=0," : "Dialogue: 0,").Append(AssStamp(cue.Start)).Append(',').Append(AssStamp(cue.End))
                .Append(",Default,,0,0,0,,").Append(ToAssText(cue.Text)).Append("\r\n");
        return sb.ToString();
    }

    private static string AssStamp(TimeSpan t)
    {
        var cs = (long)Math.Round(Math.Max(0, t.TotalMilliseconds) / 10);
        return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    [GeneratedRegex(@"^\s*(\d+):(\d{1,2}):(\d{1,2})[.,](\d{1,3})\s*$")]
    private static partial Regex AssTimePattern();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssOverride();

    [GeneratedRegex(@"\\([ibu])([01])(?![0-9])")]
    private static partial Regex AssStyleTag();

    [GeneratedRegex(@"^(\{[^}]*\})+")]
    private static partial Regex AssLeadingTags();

    [GeneratedRegex(@"\{[^}]*\\p[1-9][^}]*\}.*?(\{[^}]*\\p0[^}]*\}|$)", RegexOptions.Singleline)]
    private static partial Regex AssDrawing();

    // ---------- LRC ----------

    /// <summary>
    /// Timed lyrics: "[01:23.45]words", several times on one line for a chorus, [offset:+500] moving them all; a line
    /// lasts until the next one (the last, five seconds). Word times ("&lt;01:23.45&gt;") and the tags at the top are left out.
    /// </summary>
    private static List<SubtitleCue> ReadLrc(string text)
    {
        var timed = new List<(TimeSpan At, string Words)>();
        var offset = TimeSpan.Zero;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (LrcOffset().Match(line) is { Success: true } o)
                offset = TimeSpan.FromMilliseconds(-int.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture));
            var times = new List<TimeSpan>();
            int at = 0;
            Match m;
            while ((m = LrcStamp().Match(line, at)).Success && m.Index == at)
            {
                times.Add(new TimeSpan(0, 0, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0)
                    + TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture)));
                at = m.Index + m.Length;
            }
            if (times.Count == 0)
                continue;
            var words = LrcWordStamp().Replace(line[at..], "").Trim();
            foreach (var t in times)
                timed.Add((t + offset < TimeSpan.Zero ? TimeSpan.Zero : t + offset, words));
        }
        timed.Sort((a, b) => a.At.CompareTo(b.At));
        var cues = new List<SubtitleCue>();
        for (int i = 0; i < timed.Count; i++)
        {
            if (timed[i].Words.Length == 0)
                continue; // An empty line marks where the words before end.
            var end = i + 1 < timed.Count ? timed[i + 1].At : timed[i].At + TimeSpan.FromSeconds(5);
            cues.Add(new SubtitleCue(timed[i].At, end > timed[i].At ? end : timed[i].At + TimeSpan.FromSeconds(1), timed[i].Words));
        }
        return cues;
    }

    private static string WriteLrc(IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder("[re:DropWheel]\r\n");
        for (int i = 0; i < cues.Count; i++)
        {
            var words = Plain(cues[i].Text).Replace("\n", " ");
            sb.Append('[').Append(LrcStampText(cues[i].Start)).Append(']').Append(words).Append("\r\n");
            // A pause before the next line: an empty line ends this one on time.
            bool gap = i + 1 == cues.Count || cues[i + 1].Start - cues[i].End > TimeSpan.FromMilliseconds(500);
            if (gap)
                sb.Append('[').Append(LrcStampText(cues[i].End)).Append("]\r\n");
        }
        return sb.ToString();
    }

    private static string LrcStampText(TimeSpan t)
    {
        var cs = (long)Math.Round(Math.Max(0, t.TotalMilliseconds) / 10);
        return $"{cs / 6000:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex LrcStamp();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex LrcWordStamp();

    [GeneratedRegex(@"^\[offset:\s*([-+]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex LrcOffset();

    // ---------- SAMI ----------

    /// <summary>
    /// SAMI: a page of SYNC marks in milliseconds; a line lasts until the next mark (a mark with only &amp;nbsp; ends it).
    /// A file with several languages gives the one with the most lines.
    /// </summary>
    private static List<SubtitleCue> ReadSami(string text)
    {
        var syncs = SamiSync().Matches(text).ToList();
        var byClass = new Dictionary<string, List<(TimeSpan At, string Words)>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < syncs.Count; i++)
        {
            var at = TimeSpan.FromMilliseconds(long.Parse(syncs[i].Groups[1].Value, CultureInfo.InvariantCulture));
            int from = syncs[i].Index + syncs[i].Length;
            int to = i + 1 < syncs.Count ? syncs[i + 1].Index : text.Length;
            var body = text[from..to];
            var paragraphs = SamiParagraph().Matches(body).ToList();
            if (paragraphs.Count == 0)
                Add("", body);
            foreach (Match p in paragraphs)
                Add(p.Groups[1].Value, p.Groups[2].Value);

            void Add(string cls, string html)
            {
                if (!byClass.TryGetValue(cls, out var list))
                    byClass[cls] = list = [];
                list.Add((at, HtmlText(html)));
            }
        }
        var chosen = byClass.Values.OrderByDescending(l => l.Count(x => x.Words.Length > 0)).FirstOrDefault() ?? [];
        var cues = new List<SubtitleCue>();
        for (int i = 0; i < chosen.Count; i++)
        {
            if (chosen[i].Words.Length == 0)
                continue;
            var end = i + 1 < chosen.Count ? chosen[i + 1].At : chosen[i].At + TimeSpan.FromSeconds(5);
            cues.Add(new SubtitleCue(chosen[i].At, end > chosen[i].At ? end : chosen[i].At + TimeSpan.FromSeconds(1), chosen[i].Words));
        }
        return cues;
    }

    private static string WriteSami(IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        sb.Append("<SAMI>\r\n<HEAD>\r\n<TITLE>Subtitles</TITLE>\r\n<STYLE TYPE=\"text/css\">\r\n<!--\r\n");
        sb.Append("P { margin-left: 8pt; margin-right: 8pt; margin-bottom: 2pt; text-align: center; font-size: 20pt; font-family: Arial, sans-serif; color: white; }\r\n");
        sb.Append(".SUBTTL { Name: Subtitles; lang: und; }\r\n-->\r\n</STYLE>\r\n</HEAD>\r\n<BODY>\r\n");
        for (int i = 0; i < cues.Count; i++)
        {
            sb.Append("<SYNC Start=").Append((long)cues[i].Start.TotalMilliseconds).Append("><P Class=SUBTTL>")
                .Append(HtmlLines(cues[i].Text)).Append("</P></SYNC>\r\n");
            if (i + 1 == cues.Count || cues[i + 1].Start > cues[i].End)
                sb.Append("<SYNC Start=").Append((long)cues[i].End.TotalMilliseconds).Append("><P Class=SUBTTL>&nbsp;</P></SYNC>\r\n");
        }
        sb.Append("</BODY>\r\n</SAMI>\r\n");
        return sb.ToString();
    }

    [GeneratedRegex(@"<SYNC\s+[^>]*?Start\s*=\s*""?(\d+)""?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SamiSync();

    [GeneratedRegex(@"<P(?:\s+[^>]*?Class\s*=\s*""?([\w-]+)""?)?[^>]*>(.*?)(?=<P[\s>]|</SYNC|</BODY|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SamiParagraph();

    // ---------- MicroDVD ----------

    /// <summary>
    /// MicroDVD: "{start frame}{end frame}words", lines split by "|", italics as {y:i}. The rate is the first line's
    /// when it says one ({1}{1}25), else <see cref="DefaultFrameRate"/>. A .sub file of pictures (VobSub) is refused.
    /// </summary>
    private static List<SubtitleCue> ReadMicroDvd(string text)
    {
        var first = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (text.Contains('\0') || !MicroDvdLine().IsMatch(first))
            throw new InvalidDataException("This .sub file isn't MicroDVD text: it may be a VobSub file of pictures (with an .idx beside it), which DropWheel doesn't read.");
        double fps = DefaultFrameRate;
        var cues = new List<SubtitleCue>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = MicroDvdLine().Match(raw.Trim());
            if (!m.Success)
                continue;
            long a = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = m.Groups[2].Value.Length > 0 ? long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : a;
            var words = m.Groups[3].Value;
            if (cues.Count == 0 && a <= 1 && b <= 1 && double.TryParse(words.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate is > 1 and < 200)
            {
                fps = rate;
                continue;
            }
            bool italic = MicroDvdItalic().IsMatch(words);
            words = MicroDvdControl().Replace(words, "").Replace("|", "\n").Trim();
            if (words.Length == 0)
                continue;
            if (italic)
                words = $"<i>{words}</i>";
            var start = TimeSpan.FromSeconds(a / fps);
            var end = b > a ? TimeSpan.FromSeconds(b / fps) : start + TimeSpan.FromSeconds(3);
            cues.Add(new SubtitleCue(start, end, words));
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    private static string WriteMicroDvd(IReadOnlyList<SubtitleCue> cues)
    {
        var sb = new StringBuilder("{1}{1}").Append(DefaultFrameRate.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var cue in cues)
        {
            var words = cue.Text.Replace("\r\n", "\n");
            var italic = words.StartsWith("<i>", StringComparison.Ordinal) && words.EndsWith("</i>", StringComparison.Ordinal);
            words = Plain(words).Replace("\n", "|");
            sb.Append('{').Append((long)Math.Round(cue.Start.TotalSeconds * DefaultFrameRate)).Append("}{")
                .Append((long)Math.Round(cue.End.TotalSeconds * DefaultFrameRate)).Append('}')
                .Append(italic ? "{y:i}" : "").Append(words).Append("\r\n");
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^\{(\d+)\}\{(\d*)\}(.*)$")]
    private static partial Regex MicroDvdLine();

    [GeneratedRegex(@"\{[yY]:[^}]*i[^}]*\}")]
    private static partial Regex MicroDvdItalic();

    [GeneratedRegex(@"\{[a-zA-Z]:[^}]*\}")]
    private static partial Regex MicroDvdControl();

    // ---------- TTML ----------

    private static readonly XNamespace Tt = "http://www.w3.org/ns/ttml";
    private static readonly XNamespace TtStyle = "http://www.w3.org/ns/ttml#styling";
    private static readonly XNamespace TtParameter = "http://www.w3.org/ns/ttml#parameter";

    /// <summary>
    /// TTML (and DFXP, its older name): each &lt;p&gt; with begin and end (or dur), in clock time, frames, ticks or
    /// seconds; &lt;br/&gt; breaks a line; italic and bold spans keep their look. Times on the div or body are added.
    /// </summary>
    private static List<SubtitleCue> ReadTtml(string text)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"This TTML isn't valid XML (line {ex.LineNumber}).", ex);
        }
        var root = document.Root ?? throw new InvalidDataException("This TTML file is empty.");
        double frameRate = double.TryParse(Attribute(root, "frameRate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fr) ? fr : 30;
        if (Attribute(root, "frameRateMultiplier") is { } multiplier && multiplier.Split(' ') is [var num, var den]
            && double.TryParse(num, CultureInfo.InvariantCulture, out var n) && double.TryParse(den, CultureInfo.InvariantCulture, out var d) && d > 0)
            frameRate *= n / d;
        double tickRate = double.TryParse(Attribute(root, "tickRate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tr) ? tr : 10_000_000;

        var cues = new List<SubtitleCue>();
        foreach (var p in root.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            var offset = TimeSpan.Zero;
            foreach (var parent in p.Ancestors())
                if (TtmlTime(parent.Attribute("begin")?.Value, frameRate, tickRate) is { } b)
                    offset += b;
            var begin = TtmlTime(p.Attribute("begin")?.Value, frameRate, tickRate);
            var end = TtmlTime(p.Attribute("end")?.Value, frameRate, tickRate);
            var dur = TtmlTime(p.Attribute("dur")?.Value, frameRate, tickRate);
            if (begin is null || end is null && dur is null)
                continue;
            var words = TtmlWords(p).Trim();
            if (words.Length == 0)
                continue;
            cues.Add(new SubtitleCue(offset + begin.Value, offset + (end ?? begin.Value + dur!.Value), words));
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static string TtmlWords(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(WhiteSpace().Replace(text.Value, " "));
                    break;
                case XElement e when e.Name.LocalName == "br":
                    sb.Append('\n');
                    break;
                case XElement e:
                {
                    var inner = TtmlWords(e);
                    var style = (Attribute(e, "fontStyle") ?? "") + " " + (Attribute(e, "fontWeight") ?? "");
                    if (style.Contains("italic", StringComparison.OrdinalIgnoreCase)) inner = $"<i>{inner}</i>";
                    if (style.Contains("bold", StringComparison.OrdinalIgnoreCase)) inner = $"<b>{inner}</b>";
                    sb.Append(inner);
                    break;
                }
            }
        }
        return string.Join("\n", sb.ToString().Split('\n').Select(l => l.Trim()));
    }

    /// <summary>"00:01:02.500", "00:01:02:12" (frames), "62.5s", "1500ms", "2m", "1h", "90f", "600000000t".</summary>
    private static TimeSpan? TtmlTime(string? value, double frameRate, double tickRate)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (TtmlClock().Match(value) is { Success: true } c)
        {
            var t = new TimeSpan(0, int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture), 0)
                + TimeSpan.FromSeconds(double.Parse(c.Groups[3].Value, CultureInfo.InvariantCulture));
            if (c.Groups[4].Success)
                t += TimeSpan.FromSeconds(double.Parse(c.Groups[4].Value, CultureInfo.InvariantCulture) / frameRate);
            return t;
        }
        if (TtmlOffset().Match(value) is { Success: true } o)
        {
            var number = double.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture);
            return o.Groups[2].Value switch
            {
                "h" => TimeSpan.FromHours(number),
                "m" => TimeSpan.FromMinutes(number),
                "s" => TimeSpan.FromSeconds(number),
                "ms" => TimeSpan.FromMilliseconds(number),
                "f" => TimeSpan.FromSeconds(number / frameRate),
                _ => TimeSpan.FromSeconds(number / tickRate),
            };
        }
        return null;
    }

    private static string WriteTtml(IReadOnlyList<SubtitleCue> cues)
    {
        var div = new XElement(Tt + "div");
        foreach (var cue in cues)
        {
            var p = new XElement(Tt + "p", new XAttribute("begin", TtmlStamp(cue.Start)), new XAttribute("end", TtmlStamp(cue.End)));
            var lines = cue.Text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    p.Add(new XElement(Tt + "br"));
                AddStyled(p, lines[i]);
            }
            div.Add(p);
        }
        var tt = new XElement(Tt + "tt",
            new XAttribute(XNamespace.Xmlns + "tts", TtStyle),
            new XAttribute(XNamespace.Xmlns + "ttp", TtParameter),
            new XAttribute(TtParameter + "timeBase", "media"),
            new XElement(Tt + "head", new XElement(Tt + "styling",
                new XElement(Tt + "style", new XAttribute(XNamespace.Xml + "id", "s1"), new XAttribute(TtStyle + "textAlign", "center"),
                    new XAttribute(TtStyle + "fontFamily", "proportionalSansSerif"), new XAttribute(TtStyle + "color", "white")))),
            new XElement(Tt + "body", new XAttribute("style", "s1"), div));
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n" }))
            new XDocument(new XDeclaration("1.0", "utf-8", null), tt).Save(writer);
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"") + "\n";
    }

    /// <summary>A line with &lt;i&gt; and &lt;b&gt; as styled spans.</summary>
    private static void AddStyled(XElement p, string line)
    {
        int at = 0;
        foreach (Match m in StyledRun().Matches(line))
        {
            if (m.Index > at)
                p.Add(new XText(Plain(line[at..m.Index])));
            var span = new XElement(Tt + "span", new XText(Plain(m.Groups[2].Value)));
            span.Add(m.Groups[1].Value == "i" ? new XAttribute(TtStyle + "fontStyle", "italic") : new XAttribute(TtStyle + "fontWeight", "bold"));
            p.Add(span);
            at = m.Index + m.Length;
        }
        if (at < line.Length)
            p.Add(new XText(Plain(line[at..])));
    }

    private static string TtmlStamp(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

    [GeneratedRegex(@"^(\d+):(\d{2}):(\d{2}(?:\.\d+)?)(?::(\d+(?:\.\d+)?))?$")]
    private static partial Regex TtmlClock();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(h|m|s|ms|f|t)$")]
    private static partial Regex TtmlOffset();

    [GeneratedRegex(@"<([ib])>(.*?)</\1>")]
    private static partial Regex StyledRun();

    // ---------- shared ----------

    /// <summary>HTML-like text (SAMI): &lt;br&gt; breaks lines, i/b/u stay, other tags go, entities are decoded.</summary>
    private static string HtmlText(string html)
    {
        var text = Br().Replace(html, "\n");
        text = OtherTags().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = string.Join("\n", text.Split('\n').Select(l => WhiteSpace().Replace(l, " ").Trim()).Where(l => l.Length > 0));
        return Balance(text);
    }

    /// <summary>Lines for SAMI: the words encoded, i, b and u kept, &lt;br&gt; between lines.</summary>
    private static string HtmlLines(string text) =>
        string.Join("<br>", text.Replace("\r\n", "\n").Split('\n').Select(l =>
        {
            var encoded = WebUtility.HtmlEncode(l);
            foreach (var tag in new[] { "i", "b", "u" })
                encoded = encoded.Replace($"&lt;{tag}&gt;", $"<{tag}>").Replace($"&lt;/{tag}&gt;", $"</{tag}>");
            return encoded;
        }));

    /// <summary>Text without any formatting tags.</summary>
    public static string Plain(string text) => AnyTag().Replace(text, "");

    /// <summary>Closes formatting left open and drops closings never opened, so writers get well-formed text.</summary>
    private static string Balance(string text)
    {
        foreach (var tag in new[] { "i", "b", "u" })
        {
            int open = Regex.Matches(text, $"<{tag}>").Count, close = Regex.Matches(text, $"</{tag}>").Count;
            if (open > close)
                text += string.Concat(Enumerable.Repeat($"</{tag}>", open - close));
            else if (close > open)
                for (int k = 0; k < close - open; k++)
                    text = text[..text.IndexOf($"</{tag}>", StringComparison.Ordinal)] + text[(text.IndexOf($"</{tag}>", StringComparison.Ordinal) + tag.Length + 3)..];
        }
        return text.Replace("<i></i>", "").Replace("<b></b>", "").Replace("<u></u>", "");
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Br();

    [GeneratedRegex(@"</?(?!(?:i|b|u)>)[a-zA-Z][^>]*>")]
    private static partial Regex OtherTags();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
