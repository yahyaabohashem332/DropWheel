using System.Globalization;
using System.Text;

namespace Petal.Engines.Documents.Text;

/// <summary>Reads text files whatever their encoding, and writes them the way Windows apps expect.</summary>
internal static class TextFile
{
    /// <summary>UTF-8 with a byte order mark: Excel and older Windows tools need it to recognise UTF-8.</summary>
    public static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    static TextFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static void EnsureCodePages()
    {
        // Touching the class runs the static constructor that registers the code-page provider.
    }

    public static string ReadAllText(string path) => Decode(File.ReadAllBytes(path));

    public static string Decode(byte[] bytes)
    {
        var (encoding, skip) = Detect(bytes);
        return encoding.GetString(bytes, skip, bytes.Length - skip);
    }

    public static (Encoding Encoding, int BomLength) Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return (Utf8NoBom, 3);
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
            return (new UTF32Encoding(false, true), 4);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return (Encoding.Unicode, 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);

        if (LooksLikeUtf16(b, out var bigEndian))
            return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode, 0);

        try
        {
            StrictUtf8.GetCharCount(b);
            return (Utf8NoBom, 0);
        }
        catch (DecoderFallbackException)
        {
            EnsureCodePages();
            var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            try
            {
                return (Encoding.GetEncoding(codePage), 0);
            }
            catch (ArgumentException)
            {
                return (Encoding.Latin1, 0);
            }
        }
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> b, out bool bigEndian)
    {
        bigEndian = false;
        var n = Math.Min(b.Length, 4096) & ~1;
        if (n < 8)
            return false;
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < n; i += 2)
        {
            if (b[i] == 0) evenZeros++;
            if (b[i + 1] == 0) oddZeros++;
        }
        var pairs = n / 2;
        if (oddZeros > pairs * 0.4 && evenZeros < pairs * 0.05)
            return true;
        if (evenZeros > pairs * 0.4 && oddZeros < pairs * 0.05)
        {
            bigEndian = true;
            return true;
        }
        return false;
    }

    /// <summary>Normalises CRLF / CR line endings to LF.</summary>
    public static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
