using System.Text;
using System.Text.RegularExpressions;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// A web page saved as one file (MHTML: Chrome's and Edge's "Webpage, Single File", Word's "Single File Web Page"):
/// a MIME message whose first HTML part is the page, its pictures in parts of their own named by address
/// (Content-Location) or by Content-ID. The page is read as any HTML is, its pictures taken from the file.
/// </summary>
internal static partial class MhtmlReader
{
    internal sealed record Part(Dictionary<string, string> Headers, byte[] Body)
    {
        public string Header(string name) => Headers.GetValueOrDefault(name) ?? "";

        public string MediaType => Header("content-type").Split(';')[0].Trim().ToLowerInvariant();

        public string? Parameter(string name) => MimeParameter(Header("content-type"), name);
    }

    public static DocModel Read(string path)
    {
        var parts = Parse(File.ReadAllBytes(path));
        var page = parts.FirstOrDefault(p => p.MediaType is "text/html" or "application/xhtml+xml")
            ?? throw new InvalidDataException("This saved web page holds no page.");
        var html = Text(page);
        var location = page.Header("content-location");
        Uri.TryCreate(location, UriKind.Absolute, out var pageUri);

        byte[]? Resolve(string src)
        {
            if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                var id = Uri.UnescapeDataString(src[4..]);
                return parts.FirstOrDefault(p => p.Header("content-id").Trim('<', '>', ' ') == id)?.Body;
            }
            var candidates = new List<string> { src };
            if (pageUri is not null && Uri.TryCreate(pageUri, src, out var absolute))
                candidates.Add(absolute.AbsoluteUri);
            foreach (var candidate in candidates)
            {
                var found = parts.FirstOrDefault(p => p != page && SameAddress(p.Header("content-location"), candidate));
                if (found is not null)
                    return found.Body;
            }
            return null;
        }

        return HtmlReader.Parse(html, Resolve);
    }

    private static bool SameAddress(string a, string b) =>
        a.Length > 0 && (string.Equals(a, b, StringComparison.Ordinal) ||
                         string.Equals(Uri.UnescapeDataString(a), Uri.UnescapeDataString(b), StringComparison.OrdinalIgnoreCase));

    /// <summary>The message's parts, each decoded from its transfer encoding; a message of one part is that part.</summary>
    internal static List<Part> Parse(byte[] bytes)
    {
        var (headers, bodyStart) = Headers(bytes, 0);
        var message = new Part(headers, bytes[bodyStart..]);
        if (!message.MediaType.StartsWith("multipart/", StringComparison.Ordinal))
            return [Decoded(message)];
        var boundary = message.Parameter("boundary") ?? throw new InvalidDataException("This saved web page has no part boundaries.");
        var parts = new List<Part>();
        foreach (var raw in Split(message.Body, Encoding.ASCII.GetBytes("--" + boundary)))
        {
            var (partHeaders, start) = Headers(raw, 0);
            var part = Decoded(new Part(partHeaders, raw[start..]));
            if (part.MediaType.StartsWith("multipart/", StringComparison.Ordinal))
            {
                // A part of parts (rare): its own headers start it again.
                parts.AddRange(Parse(raw));
            }
            else
                parts.Add(part);
        }
        return parts;
    }

    /// <summary>The headers from <paramref name="at"/> to the first empty line (lines that start with a space continue the one before), and where the body starts.</summary>
    private static (Dictionary<string, string> Headers, int BodyStart) Headers(byte[] bytes, int at)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        while (at < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', at);
            if (end < 0)
                end = bytes.Length;
            var line = Encoding.Latin1.GetString(bytes, at, end - at).TrimEnd('\r');
            at = end + 1;
            if (line.Length == 0)
                break;
            if ((line[0] == ' ' || line[0] == '\t') && last is not null)
            {
                headers[last] += " " + line.Trim();
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            last = line[..colon].Trim();
            headers[last] = line[(colon + 1)..].Trim();
        }
        return (headers, Math.Min(at, bytes.Length));
    }

    /// <summary>The pieces between boundary lines, without the line ends around them; the closing "--" ends it.</summary>
    private static IEnumerable<byte[]> Split(byte[] body, byte[] boundary)
    {
        var starts = new List<int>();
        for (var at = 0; (at = IndexOf(body, boundary, at)) >= 0; at += boundary.Length)
        {
            // A boundary counts only at the start of a line.
            if (at == 0 || body[at - 1] == '\n')
                starts.Add(at);
        }
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i] + boundary.Length;
            if (from + 1 < body.Length && body[from] == '-' && body[from + 1] == '-')
                yield break;
            var next = Array.IndexOf(body, (byte)'\n', from);
            if (next < 0)
                yield break;
            from = next + 1;
            var to = i + 1 < starts.Count ? starts[i + 1] : body.Length;
            // The line end before the next boundary belongs to the boundary.
            if (to > from && body[to - 1] == '\n') to--;
            if (to > from && body[to - 1] == '\r') to--;
            yield return body[from..Math.Max(from, to)];
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from) =>
        haystack.AsSpan(from).IndexOf(needle) is var at and >= 0 ? at + from : -1;

    private static Part Decoded(Part part)
    {
        var encoding = part.Header("content-transfer-encoding").Trim().ToLowerInvariant();
        return encoding switch
        {
            "base64" => part with { Body = Base64(part.Body) },
            "quoted-printable" => part with { Body = QuotedPrintable(part.Body) },
            _ => part,
        };
    }

    private static byte[] Base64(byte[] body)
    {
        var text = Encoding.ASCII.GetString(body);
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/')
                clean.Append(c);
        }
        while (clean.Length % 4 != 0)
            clean.Append('=');
        try
        {
            return Convert.FromBase64String(clean.ToString());
        }
        catch (FormatException)
        {
            return [];
        }
    }

    internal static byte[] QuotedPrintable(byte[] body)
    {
        var output = new List<byte>(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var b = body[i];
            if (b != '=')
            {
                output.Add(b);
                continue;
            }
            // "=" at a line's end joins it to the next.
            if (i + 1 < body.Length && body[i + 1] == '\n')
            {
                i += 1;
                continue;
            }
            if (i + 2 < body.Length && body[i + 1] == '\r' && body[i + 2] == '\n')
            {
                i += 2;
                continue;
            }
            if (i + 2 < body.Length && Uri.IsHexDigit((char)body[i + 1]) && Uri.IsHexDigit((char)body[i + 2]))
            {
                output.Add(Convert.ToByte(Encoding.ASCII.GetString(body, i + 1, 2), 16));
                i += 2;
                continue;
            }
            output.Add(b);
        }
        return [.. output];
    }

    /// <summary>A text part in its own character set: the header's, else the page's meta tag's, else UTF-8 (or what a BOM says).</summary>
    private static string Text(Part part)
    {
        var charset = part.Parameter("charset");
        if (charset is null)
        {
            var head = Encoding.Latin1.GetString(part.Body, 0, Math.Min(part.Body.Length, 4096));
            var meta = MetaCharset().Match(head);
            if (meta.Success)
                charset = meta.Groups[1].Value;
        }
        if (charset is not null)
        {
            Petal.Engines.Documents.Text.TextFile.EnsureCodePages();
            try
            {
                var encoding = Encoding.GetEncoding(charset);
                var (detected, bom) = Petal.Engines.Documents.Text.TextFile.Detect(part.Body);
                return bom > 0 ? detected.GetString(part.Body, bom, part.Body.Length - bom) : encoding.GetString(part.Body);
            }
            catch (ArgumentException)
            {
            }
        }
        return Petal.Engines.Documents.Text.TextFile.Decode(part.Body);
    }

    private static string? MimeParameter(string header, string name)
    {
        foreach (var piece in header.Split(';').Skip(1))
        {
            var eq = piece.IndexOf('=');
            if (eq > 0 && piece[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return piece[(eq + 1)..].Trim().Trim('"');
        }
        return null;
    }

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?([\w\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();
}
