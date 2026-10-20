using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Petal.Engines.Documents.Pdf;

/// <summary>A PDF that cannot be opened: damaged, or protected by a password that was not given.</summary>
public sealed class PdfOpenException(string message, Exception? inner = null) : IOException(message, inner)
{
    public bool NeedsPassword { get; init; }
}

/// <summary>Opening and saving with PDFsharp, plus a precise rewrite of the document information dictionary.</summary>
internal static partial class PdfSharpIO
{
    public static PdfDocument Open(string path, PdfDocumentOpenMode mode, string? password = null) => Open(File.ReadAllBytes(path), mode, password, Path.GetFileName(path));

    public static PdfDocument Open(byte[] bytes, PdfDocumentOpenMode mode, string? password = null, string? name = null)
    {
        try
        {
            var stream = new MemoryStream(bytes, writable: false);
            return password is null ? PdfReader.Open(stream, mode) : PdfReader.Open(stream, password, mode);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new PdfOpenException($"{name ?? "The PDF"} is protected by a password.", ex) { NeedsPassword = true };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not PdfOpenException)
        {
            throw new PdfOpenException($"{name ?? "The PDF"} could not be read: {ex.Message}", ex);
        }
    }

    public static byte[] SaveToBytes(PdfDocument document)
    {
        using var ms = new MemoryStream();
        document.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    // ─────────────── Post-save patching of Info and XMP ───────────────

    /// <summary>What to do with the XMP packet PDFsharp writes on every save.</summary>
    internal enum XmpAction { Keep, Remove, Replace }

    /// <summary>
    /// PDFsharp stamps its own Producer into the information dictionary and regenerates an XMP
    /// packet from it on every save. This puts back exactly <paramref name="info"/> (PDF key
    /// without slash → value already encoded as a PDF object) and removes or replaces the XMP.
    /// Changes are made in place when they fit (offsets stay valid), otherwise with an
    /// incremental update that redefines the objects.
    /// </summary>
    public static byte[] Finish(byte[] pdf, IReadOnlyList<(string Key, string Value)> info, XmpAction xmpAction = XmpAction.Keep, byte[]? xmp = null)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (startxref < 0)
            return pdf;
        var xrefMatch = Number().Match(text, startxref + 9);
        if (!xrefMatch.Success || !long.TryParse(xrefMatch.Value, out var xrefOffset))
            return pdf;
        var trailerStart = text.LastIndexOf("trailer", startxref, StringComparison.Ordinal);
        if (trailerStart < 0)
            return pdf;
        var trailerDictStart = text.IndexOf("<<", trailerStart, StringComparison.Ordinal);
        var trailerDictEnd = DictionaryEnd(text, trailerDictStart);
        if (trailerDictStart < 0 || trailerDictEnd < 0)
            return pdf;
        var trailer = text[trailerDictStart..trailerDictEnd];
        var infoRef = InfoRef().Match(trailer);
        var rootRef = RootRef().Match(trailer);

        var bytes = (byte[])pdf.Clone();
        var appended = new SortedDictionary<int, byte[]>();
        var size = SizeOf().Match(trailer) is { Success: true } sm ? int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var dropInfo = false;
        int? newInfo = null;

        // Information dictionary.
        var desired = info.Count == 0 ? "<<>>" : "<<" + string.Concat(info.Select(e => "/" + e.Key + " " + e.Value)) + ">>";
        var infoDone = false;
        if (infoRef.Success && FindObject(text, infoRef.Groups[1].Value, infoRef.Groups[2].Value) is { } infoObj)
        {
            var dictStart = text.IndexOf("<<", infoObj, StringComparison.Ordinal);
            var dictEnd = DictionaryEnd(text, dictStart);
            if (dictStart >= 0 && dictEnd > dictStart && desired.Length <= dictEnd - dictStart)
            {
                Overwrite(bytes, dictStart, desired.PadRight(dictEnd - dictStart));
                dropInfo = info.Count == 0;
                infoDone = true;
            }
        }
        if (!infoDone && info.Count > 0 && size > 0)
        {
            newInfo = size++;
            appended[newInfo.Value] = Encoding.Latin1.GetBytes(desired);
        }
        else if (!infoDone && info.Count == 0)
            dropInfo = infoRef.Success;
        if (dropInfo && infoRef.Success)
            Overwrite(bytes, trailerDictStart + infoRef.Index, new string(' ', infoRef.Length));

        // XMP packet, referenced from the catalog.
        if (xmpAction != XmpAction.Keep && rootRef.Success && FindObject(text, rootRef.Groups[1].Value, rootRef.Groups[2].Value) is { } rootObj)
        {
            var catStart = text.IndexOf("<<", rootObj, StringComparison.Ordinal);
            var catEnd = DictionaryEnd(text, catStart);
            var metadataRef = catStart >= 0 && catEnd > catStart ? MetadataRef().Match(text, catStart, catEnd - catStart) : Match.Empty;
            if (metadataRef.Success)
            {
                var stream = FindStream(text, metadataRef.Groups[1].Value, metadataRef.Groups[2].Value);
                if (xmpAction == XmpAction.Remove || xmp is null)
                {
                    Overwrite(bytes, metadataRef.Index, new string(' ', metadataRef.Length));
                    if (stream is { } s)
                        Overwrite(bytes, s.Start, new string(' ', s.Length));
                }
                else if (stream is { } s && xmp.Length <= s.Length)
                {
                    // XMP packets may be padded with whitespace, which keeps the stream length.
                    Array.Copy(xmp, 0, bytes, s.Start, xmp.Length);
                    for (var i = s.Start + xmp.Length; i < s.Start + s.Length; i++)
                        bytes[i] = (byte)' ';
                }
                else
                {
                    var number = int.Parse(metadataRef.Groups[1].Value, CultureInfo.InvariantCulture);
                    var head = Encoding.Latin1.GetBytes($"<</Type/Metadata/Subtype/XML/Length {xmp.Length}>>\nstream\n");
                    var tail = Encoding.Latin1.GetBytes("\nendstream");
                    appended[number] = [.. head, .. xmp, .. tail];
                }
            }
            else if (xmpAction == XmpAction.Replace && xmp is not null && size > 0 && catStart >= 0 && catEnd > catStart)
            {
                // No packet yet: redefine the catalog with a reference to a new one.
                var number = size++;
                var head = Encoding.Latin1.GetBytes($"<</Type/Metadata/Subtype/XML/Length {xmp.Length}>>\nstream\n");
                appended[number] = [.. head, .. xmp, .. Encoding.Latin1.GetBytes("\nendstream")];
                var catalog = text[catStart..catEnd];
                appended[int.Parse(rootRef.Groups[1].Value, CultureInfo.InvariantCulture)] =
                    Encoding.Latin1.GetBytes(catalog[..^2] + $"/Metadata {number} 0 R>>");
            }
        }

        if (appended.Count == 0)
            return bytes;

        // Incremental update: the new objects, an xref section listing them, and a new trailer.
        var output = new MemoryStream();
        output.Write(bytes);
        output.WriteByte((byte)'\n');
        var offsets = new SortedDictionary<int, long>();
        foreach (var (number, body) in appended)
        {
            offsets[number] = output.Position;
            output.Write(Encoding.Latin1.GetBytes($"{number} 0 obj\n"));
            output.Write(body);
            output.Write(Encoding.Latin1.GetBytes("\nendobj\n"));
        }
        var newXref = output.Position;
        var sb = new StringBuilder("xref\n");
        foreach (var (number, offset) in offsets)
            sb.Append(number).Append(" 1\n").Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n\r\n");
        var id = IdArray().Match(trailer);
        sb.Append("trailer\n<</Size ").Append(Math.Max(size, offsets.Keys.Max() + 1));
        if (rootRef.Success) sb.Append("/Root ").Append(rootRef.Groups[1].Value).Append(' ').Append(rootRef.Groups[2].Value).Append(" R");
        if (newInfo is { } ni) sb.Append("/Info ").Append(ni).Append(" 0 R");
        else if (infoRef.Success && !dropInfo) sb.Append("/Info ").Append(infoRef.Groups[1].Value).Append(' ').Append(infoRef.Groups[2].Value).Append(" R");
        if (id.Success) sb.Append("/ID").Append(id.Groups[1].Value);
        sb.Append("/Prev ").Append(xrefOffset).Append(">>\nstartxref\n").Append(newXref).Append("\n%%EOF\n");
        output.Write(Encoding.Latin1.GetBytes(sb.ToString()));
        return output.ToArray();
    }

    /// <summary>Blanks the "% Created with PDFsharp…" comment lines (same length, so offsets stay valid).</summary>
    public static byte[] BlankWriterComments(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var bytes = (byte[])pdf.Clone();
        for (var at = text.IndexOf("% Created with", StringComparison.Ordinal); at >= 0; at = text.IndexOf("% Created with", at + 1, StringComparison.Ordinal))
        {
            var end = text.IndexOfAny(['\r', '\n'], at);
            if (end < 0) end = text.Length;
            Overwrite(bytes, at + 1, new string(' ', end - at - 1));
        }
        return bytes;
    }

    /// <summary>Kept for callers that only restore the information dictionary.</summary>
    public static byte[] SetInfo(byte[] pdf, IReadOnlyList<(string Key, string Value)> entries) => Finish(pdf, entries);

    private static void Overwrite(byte[] bytes, int at, string text) =>
        Encoding.Latin1.GetBytes(text, 0, text.Length, bytes, at);

    /// <summary>Index just after "N G obj" for the last definition of that object.</summary>
    private static int? FindObject(string text, string number, string generation)
    {
        var match = new Regex($@"(?<![0-9]){number}\s+{generation}\s+obj").Matches(text).LastOrDefault();
        return match is null ? null : match.Index + match.Length;
    }

    /// <summary>Where a stream object's data starts and how long it is (direct /Length only).</summary>
    private static (int Start, int Length)? FindStream(string text, string number, string generation)
    {
        if (FindObject(text, number, generation) is not { } obj)
            return null;
        var dictStart = text.IndexOf("<<", obj, StringComparison.Ordinal);
        var dictEnd = DictionaryEnd(text, dictStart);
        if (dictStart < 0 || dictEnd < 0)
            return null;
        var length = DirectLength().Match(text[dictStart..dictEnd]);
        if (!length.Success)
            return null;
        var keyword = text.IndexOf("stream", dictEnd, StringComparison.Ordinal);
        if (keyword < 0)
            return null;
        var start = keyword + 6;
        if (start < text.Length && text[start] == '\r') start++;
        if (start < text.Length && text[start] == '\n') start++;
        var len = int.Parse(length.Groups[1].Value, CultureInfo.InvariantCulture);
        return start + len <= text.Length ? (start, len) : null;
    }

    /// <summary>Index just past the "&gt;&gt;" that closes the dictionary opened at <paramref name="start"/>, skipping strings.</summary>
    private static int DictionaryEnd(string text, int start)
    {
        if (start < 0)
            return -1;
        var depth = 0;
        for (var i = start; i < text.Length - 1; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                var parens = 1;
                for (i++; i < text.Length && parens > 0; i++)
                {
                    if (text[i] == '\\') i++;
                    else if (text[i] == '(') parens++;
                    else if (text[i] == ')') parens--;
                }
                i--;
            }
            else if (c == '<' && text[i + 1] == '<')
            {
                depth++;
                i++;
            }
            else if (c == '>' && text[i + 1] == '>')
            {
                depth--;
                i++;
                if (depth == 0)
                    return i + 1;
            }
            else if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);
                if (close < 0) return -1;
                i = close;
            }
        }
        return -1;
    }

    /// <summary>A PDF text string: literal when plain ASCII, else UTF-16BE hex with a byte order mark.</summary>
    public static string EncodeString(string value)
    {
        if (value.All(c => c is >= ' ' and < (char)127))
            return "(" + value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        var sb = new StringBuilder("<FEFF");
        foreach (var b in Encoding.BigEndianUnicode.GetBytes(value))
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return sb.Append('>').ToString();
    }

    public static string EncodeDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return $"(D:{date:yyyyMMddHHmmss}{sign}{abs.Hours:00}'{abs.Minutes:00}')";
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    [GeneratedRegex(@"/Info\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex InfoRef();

    [GeneratedRegex(@"/Root\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex RootRef();

    [GeneratedRegex(@"/Size\s+(\d+)")]
    private static partial Regex SizeOf();

    [GeneratedRegex(@"/ID\s*(\[[^\]]*\])")]
    private static partial Regex IdArray();

    [GeneratedRegex(@"/Metadata\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex MetadataRef();

    [GeneratedRegex(@"/Length\s+(\d+)\b(?!\s+\d+\s+R)")]
    private static partial Regex DirectLength();
}
