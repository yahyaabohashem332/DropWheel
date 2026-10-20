using System.Collections.Concurrent;
using Microsoft.Win32;
using PdfSharp.Fonts;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// Lets PDFsharp use every TrueType font installed in Windows (machine-wide and per-user),
/// found through the registry the same way GDI lists them. PDFsharp's own Windows lookup only
/// knows a handful of core families, so Calibri, Segoe UI or Arabic and CJK fonts would fail.
/// </summary>
internal sealed class WindowsFontResolver : IFontResolver
{
    private readonly record struct Face(string File, int Index);

    private readonly Dictionary<string, Dictionary<(bool Bold, bool Italic), Face>> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]?> _data = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Fallbacks = ["Segoe UI", "Arial", "Calibri", "Tahoma"];

    public WindowsFontResolver()
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        Load(Registry.LocalMachine, fontsDir);
        Load(Registry.CurrentUser, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"));
        // Without a registry (unusual), fall back to scanning file names of the core fonts.
        if (_families.Count == 0 && Directory.Exists(fontsDir))
        {
            Add("Arial", false, false, Path.Combine(fontsDir, "arial.ttf"), 0);
            Add("Arial", true, false, Path.Combine(fontsDir, "arialbd.ttf"), 0);
        }
    }

    public bool HasFamily(string family) => _families.ContainsKey(family);

    private void Load(RegistryKey root, string defaultDir)
    {
        try
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null)
                return;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string file)
                    continue;
                var path = Path.IsPathRooted(file) ? file : Path.Combine(defaultDir, file);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                // PDFsharp embeds TrueType outlines; CFF-based OpenType and bitmap fonts are skipped.
                if (ext is not (".ttf" or ".ttc"))
                    continue;
                var display = name;
                var paren = display.LastIndexOf(" (", StringComparison.Ordinal);
                if (paren > 0)
                    display = display[..paren];
                var faces = display.Split(" & ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < faces.Length; i++)
                {
                    var (family, bold, italic) = ParseStyle(faces[i]);
                    Add(family, bold, italic, path, ext == ".ttc" ? i : 0);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable registry: fewer fonts.
        }
    }

    private void Add(string family, bool bold, bool italic, string file, int index)
    {
        if (!_families.TryGetValue(family, out var styles))
            _families[family] = styles = [];
        styles.TryAdd((bold, italic), new Face(file, index));
    }

    private static (string Family, bool Bold, bool Italic) ParseStyle(string face)
    {
        var words = face.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool bold = false, italic = false;
        while (words.Count > 1)
        {
            var last = words[^1];
            if (last.Equals("Bold", StringComparison.OrdinalIgnoreCase)) bold = true;
            else if (last.Equals("Italic", StringComparison.OrdinalIgnoreCase) || last.Equals("Oblique", StringComparison.OrdinalIgnoreCase)) italic = true;
            else if (!last.Equals("Regular", StringComparison.OrdinalIgnoreCase)) break;
            words.RemoveAt(words.Count - 1);
        }
        return (string.Join(' ', words), bold, italic);
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        if (!_families.TryGetValue(familyName, out var styles))
        {
            styles = Fallbacks.Select(f => _families.GetValueOrDefault(f)).FirstOrDefault(s => s is not null);
            if (styles is null)
                return null;
        }
        (bool, bool)[] order = (isBold, isItalic) switch
        {
            (true, true) => [(true, true), (true, false), (false, true), (false, false)],
            (true, false) => [(true, false), (false, false)],
            (false, true) => [(false, true), (false, false)],
            _ => [(false, false), (false, true), (true, false), (true, true)],
        };
        foreach (var (bold, italic) in order)
        {
            if (!styles.TryGetValue((bold, italic), out var face))
                continue;
            return new FontResolverInfo($"{face.File}#{face.Index}", isBold && !bold, isItalic && !italic);
        }
        return null;
    }

    public byte[]? GetFont(string faceName) => _data.GetOrAdd(faceName, key =>
    {
        var hash = key.LastIndexOf('#');
        var file = hash > 0 ? key[..hash] : key;
        var index = hash > 0 && int.TryParse(key.AsSpan(hash + 1), out var i) ? i : 0;
        try
        {
            var bytes = File.ReadAllBytes(file);
            return IsCollection(bytes) ? ExtractFace(bytes, index) : bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    });

    private static bool IsCollection(byte[] b) => b.Length > 12 && b[0] == 't' && b[1] == 't' && b[2] == 'c' && b[3] == 'f';

    private static uint U32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    private static ushort U16(byte[] b, int at) => (ushort)(b[at] << 8 | b[at + 1]);

    /// <summary>
    /// Copies one face of a TrueType collection (.ttc) into a standalone font, which is what
    /// PDFsharp embeds: the face's table directory is rewritten with new offsets.
    /// </summary>
    internal static byte[] ExtractFace(byte[] ttc, int index)
    {
        var count = (int)U32(ttc, 8);
        if (index < 0 || index >= count)
            index = 0;
        var offset = (int)U32(ttc, 12 + 4 * index);
        var tables = U16(ttc, offset + 4);
        var headerLength = 12 + 16 * tables;
        var records = new List<(uint Tag, uint Checksum, int Offset, int Length)>();
        for (var t = 0; t < tables; t++)
        {
            var r = offset + 12 + 16 * t;
            records.Add((U32(ttc, r), U32(ttc, r + 4), (int)U32(ttc, r + 8), (int)U32(ttc, r + 12)));
        }
        var size = headerLength + records.Sum(r => (r.Length + 3) & ~3);
        var output = new byte[size];
        Array.Copy(ttc, offset, output, 0, 12);
        var position = headerLength;
        for (var t = 0; t < records.Count; t++)
        {
            var (tag, checksum, tableOffset, length) = records[t];
            var r = 12 + 16 * t;
            Put32(output, r, tag);
            Put32(output, r + 4, checksum);
            Put32(output, r + 8, (uint)position);
            Put32(output, r + 12, (uint)length);
            Array.Copy(ttc, tableOffset, output, position, length);
            position += (length + 3) & ~3;
        }
        return output;
    }

    private static void Put32(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }
}
