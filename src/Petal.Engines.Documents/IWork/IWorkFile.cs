using System.IO.Compression;
using System.Text;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Documents.IWork;

/// <summary>
/// What can be taken from an Apple Pages, Numbers or Keynote file without Apple's programs. It is a zip. Files from
/// older versions hold a whole PDF of the document; today's hold a picture of its first page, and its words in Apple's
/// own format (Snappy-packed protocol buffers), of which the text is read here and the layout isn't.
/// </summary>
internal static class IWorkFile
{
    /// <summary>TSWP.StorageArchive: a run of text, in all three programs.</summary>
    private const int TextStorage = 2001;

    /// <summary>TST.TableDataList: the strings a Numbers table's cells share.</summary>
    private const int TableStrings = 6005;

    private static readonly string[] PdfEntries = ["QuickLook/Preview.pdf", "preview.pdf", "Preview.pdf"];
    private static readonly string[] PictureEntries = ["preview.jpg", "QuickLook/Thumbnail.jpg", "preview-web.jpg", "preview-micro.jpg", "Thumbnail.jpg"];

    /// <summary>An entry by its name, wherever a folder inside the zip holds it: a zipped Keynote file may have one around all of it.</summary>
    private static ZipArchiveEntry? Find(ZipArchive zip, IEnumerable<string> names) =>
        names.Select(n => zip.Entries.FirstOrDefault(e =>
        {
            var name = e.FullName.Replace((char)92, '/');
            return string.Equals(name, n, StringComparison.OrdinalIgnoreCase) || name.EndsWith("/" + n, StringComparison.OrdinalIgnoreCase);
        })).FirstOrDefault(e => e is not null);

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static ZipArchive Open(string path)
    {
        if (Directory.Exists(path))
            throw new InvalidDataException("This file is a folder bundle from an older Mac: compress it to a ZIP on the Mac, or export it from there.");
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("This file couldn't be opened. It may be from a very old version: export it from the Mac or iPhone.");
        }
    }

    /// <summary>The whole document as a PDF, when the file holds one.</summary>
    public static byte[]? Pdf(string path)
    {
        using var zip = Open(path);
        return Find(zip, PdfEntries) is { } entry ? Read(entry) : null;
    }

    /// <summary>The picture of its first page.</summary>
    public static byte[]? Picture(string path)
    {
        using var zip = Open(path);
        var entry = Find(zip, PictureEntries) ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && e.FullName.StartsWith("preview", StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : Read(entry);
    }

    /// <summary>The words of the document: its body and slides in order, then what its tables hold.</summary>
    public static string Text(string path)
    {
        using var zip = Open(path);
        var files = new List<(string Name, byte[] Data)>();
        void Take(string name, byte[] data)
        {
            if (name.EndsWith(".iwa", StringComparison.OrdinalIgnoreCase) && !IsTemplate(name))
                files.Add((name.Replace((char)92, '/'), data));
        }
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith("Index.zip", StringComparison.OrdinalIgnoreCase))
            {
                using var inner = new ZipArchive(new MemoryStream(Read(entry)));
                foreach (var e in inner.Entries)
                    Take(e.FullName, Read(e));
            }
            else if (entry.FullName.EndsWith(".iwa", StringComparison.OrdinalIgnoreCase))
                Take(entry.FullName, Read(entry));
        }
        var parts = new List<string>();
        var seen = new HashSet<string>();
        foreach (var (_, data) in files.OrderBy(f => Rank(f.Name)).ThenBy(f => Number(f.Name)).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            var runs = new SortedDictionary<ulong, string>();
            foreach (var (id, text) in Runs(Unpack(data)))
                runs.TryAdd(id, text);
            foreach (var text in runs.Values.Select(Clean))
                if (text.Length > 0 && seen.Add(text))
                    parts.Add(text);
        }
        return string.Join(new string((char)10, 2), parts);
    }

    /// <summary>What is there to build a document from and isn't in it: master slides with their "Title Text", styles, what the window last showed.</summary>
    private static bool IsTemplate(string name)
    {
        var file = Path.GetFileName(name);
        return file.StartsWith("MasterSlide", StringComparison.OrdinalIgnoreCase) || file.Contains("Stylesheet", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("ViewState", StringComparison.OrdinalIgnoreCase) || file.StartsWith("Theme", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("AnnotationAuthor", StringComparison.OrdinalIgnoreCase) || file.StartsWith("CalculationEngine", StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(string name)
    {
        var file = Path.GetFileName(name);
        return file.StartsWith("Document", StringComparison.OrdinalIgnoreCase) ? 0 : file.StartsWith("Slide", StringComparison.OrdinalIgnoreCase) ? 1 : name.Contains("/Tables/", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
    }

    /// <summary>"Slide-12.iwa" comes after "Slide-2.iwa".</summary>
    private static long Number(string name)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(name).Where(char.IsAsciiDigit).Take(15).ToArray());
        return digits.Length == 0 ? 0 : long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Words without the marks that stand in the text for pictures, and the line and page breaks as such.</summary>
    private static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is (char)0xFFFC or (char)0xFFFD or (char)0x0004 or (char)0x0005 or (char)0x000E)
                continue;
            clean.Append(c is (char)0x2028 or (char)0x2029 or (char)0x000C ? '\n' : c);
        }
        return clean.ToString().Trim();
    }

    /// <summary>An .iwa file unpacked: chunks each with a type byte, a three-byte length, and raw Snappy.</summary>
    internal static byte[] Unpack(byte[] data)
    {
        var output = new MemoryStream();
        int at = 0;
        while (at + 4 <= data.Length)
        {
            if (data[at] != 0)
                throw new InvalidDataException("This file's contents aren't in a form DropWheel knows.");
            int length = data[at + 1] | data[at + 2] << 8 | data[at + 3] << 16;
            at += 4;
            if (at + length > data.Length)
                throw new InvalidDataException("This file is damaged.");
            Snappy(data, at, length, output);
            at += length;
        }
        return output.ToArray();
    }

    /// <summary>Raw Snappy: its length, then literal runs and copies from what came before.</summary>
    internal static void Snappy(byte[] d, int start, int length, MemoryStream output)
    {
        int at = start, end = start + length;
        long size = 0;
        for (int shift = 0; at < end; shift += 7)
        {
            byte b = d[at++];
            size |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
        }
        var result = new byte[size];
        int put = 0;
        while (at < end)
        {
            byte tag = d[at++];
            int kind = tag & 3;
            if (kind == 0)
            {
                int run = tag >> 2;
                if (run >= 60)
                {
                    int bytes = run - 59;
                    run = 0;
                    for (int i = 0; i < bytes; i++)
                        run |= d[at++] << (8 * i);
                }
                run++;
                Array.Copy(d, at, result, put, run);
                at += run;
                put += run;
                continue;
            }
            int count, offset;
            if (kind == 1)
            {
                count = 4 + (tag >> 2 & 7);
                offset = (tag >> 5) << 8 | d[at++];
            }
            else if (kind == 2)
            {
                count = (tag >> 2) + 1;
                offset = d[at] | d[at + 1] << 8;
                at += 2;
            }
            else
            {
                count = (tag >> 2) + 1;
                offset = d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24;
                at += 4;
            }
            if (offset <= 0 || offset > put || put + count > result.Length)
                throw new InvalidDataException("This file is damaged.");
            for (int i = 0; i < count; i++, put++)
                result[put] = result[put - offset];
        }
        output.Write(result, 0, put);
    }

    private static ulong Varint(byte[] d, ref int at)
    {
        ulong value = 0;
        for (int shift = 0; at < d.Length && shift < 64; shift += 7)
        {
            byte b = d[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("This file is damaged.");
    }

    /// <summary>The fields of a protocol buffer message, as field number, wire type, number, and where its bytes are.</summary>
    private static IEnumerable<(int Field, int Wire, ulong Number, int Start, int Length)> Fields(byte[] d, int start, int length)
    {
        int at = start, end = start + length;
        while (at < end)
        {
            ulong key = Varint(d, ref at);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                    yield return (field, 0, Varint(d, ref at), 0, 0);
                    break;
                case 1:
                    at += 8;
                    break;
                case 2:
                    int size = (int)Varint(d, ref at);
                    if (at + size > end)
                        yield break;
                    yield return (field, 2, 0, at, size);
                    at += size;
                    break;
                case 5:
                    at += 4;
                    break;
                default:
                    yield break;
            }
        }
    }

    /// <summary>The runs of text in unpacked data: each archive has a header saying what its messages are, then the messages.</summary>
    internal static IEnumerable<(ulong Id, string Text)> Runs(byte[] d)
    {
        int at = 0;
        while (at < d.Length)
        {
            int headerLength = (int)Varint(d, ref at);
            if (at + headerLength > d.Length)
                yield break;
            ulong id = 0;
            var messages = new List<(int Type, int Length)>();
            foreach (var f in Fields(d, at, headerLength))
            {
                if (f.Field == 1 && f.Wire == 0)
                    id = f.Number;
                else if (f.Field == 2 && f.Wire == 2)
                {
                    int type = 0, length = 0;
                    foreach (var m in Fields(d, f.Start, f.Length))
                    {
                        if (m.Field == 1 && m.Wire == 0)
                            type = (int)m.Number;
                        else if (m.Field == 3 && m.Wire == 0)
                            length = (int)m.Number;
                    }
                    messages.Add((type, length));
                }
            }
            at += headerLength;
            foreach (var (type, length) in messages)
            {
                if (at + length > d.Length)
                    yield break;
                if (type == TextStorage)
                {
                    var words = Fields(d, at, length).Where(f => f.Field == 3 && f.Wire == 2).Select(f => Encoding.UTF8.GetString(d, f.Start, f.Length));
                    var text = string.Concat(words);
                    if (text.Length > 0)
                        yield return (id, text);
                }
                else if (type == TableStrings)
                {
                    var strings = new List<string>();
                    foreach (var entry in Fields(d, at, length).Where(f => f.Field == 3 && f.Wire == 2))
                        foreach (var s in Fields(d, entry.Start, entry.Length).Where(f => f.Field == 3 && f.Wire == 2))
                            strings.Add(Encoding.UTF8.GetString(d, s.Start, s.Length));
                    if (strings.Count > 0)
                        yield return (id, string.Join('\n', strings));
                }
                at += length;
            }
        }
    }
}

/// <summary>
/// Pages, Numbers and Keynote files into PDF, a picture and text. A PDF is the document itself when the file holds one;
/// otherwise it is its words, set plainly, as the layout can only be drawn by Apple's programs.
/// </summary>
public sealed class IWorkConverter(DocumentEngineOptions? options = null) : IConverter
{
    public string Name => "Pages, Numbers and Keynote";

    public bool CanConvert(FileFormat from, FileFormat to) =>
        (from == F.Pages || from == F.Numbers || from == F.Keynote) && (to == F.Pdf || to == F.Txt || to == F.Jpg);

    public Task<JobResult> ConvertAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            progress?.Report(0.1);
            if (job.To == F.Jpg)
            {
                var picture = IWorkFile.Picture(job.InputPath) ?? throw new InvalidDataException("This file holds no picture of its first page.");
                await File.WriteAllBytesAsync(job.OutputPath, picture, cancellationToken).ConfigureAwait(false);
            }
            else if (job.To == F.Txt)
            {
                var text = IWorkFile.Text(job.InputPath);
                if (text.Length == 0)
                    throw new InvalidDataException("No words could be read from this file. Export it from the Mac or iPhone.");
                await File.WriteAllTextAsync(job.OutputPath, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else if (IWorkFile.Pdf(job.InputPath) is { } pdf)
                await File.WriteAllBytesAsync(job.OutputPath, pdf, cancellationToken).ConfigureAwait(false);
            else
                await WordsAsPdfAsync(job, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            return JobResult.Single(job.OutputPath);
        }, cancellationToken);

    private async Task WordsAsPdfAsync(ConversionJob job, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var text = IWorkFile.Text(job.InputPath);
        if (text.Length == 0)
        {
            // Nothing but the picture of its first page: better that than nothing, and the guide says so.
            var picture = IWorkFile.Picture(job.InputPath) ?? throw new InvalidDataException("No words could be read from this file. Export it from the Mac or iPhone.");
            using var document = new PdfSharp.Pdf.PdfDocument();
            using var stream = new MemoryStream(picture);
            using var image = PdfSharp.Drawing.XImage.FromStream(stream);
            var page = document.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(image.PointWidth);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(image.PointHeight);
            using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
                graphics.DrawImage(image, 0, 0, image.PointWidth, image.PointHeight);
            document.Save(job.OutputPath);
            return;
        }
        var words = Path.Combine(Path.GetTempPath(), "Petal", $"iwork-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(words)!);
        try
        {
            await File.WriteAllTextAsync(words, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await new DocumentConverter(options).ConvertAsync(new ConversionJob(words, F.Txt, F.Pdf, job.OutputPath, job.Settings), progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(words); } catch (IOException) { }
        }
    }
}
