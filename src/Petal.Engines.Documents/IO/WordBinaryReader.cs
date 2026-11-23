using System.Buffers.Binary;
using System.Text;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// The words of a Word 97–2003 document (.doc, [MS-DOC]) when neither Word nor LibreOffice is there to open it:
/// its paragraphs, line and page breaks and tables, from the main text's pieces; the formatting is not read. Fields
/// give what they show (a page number, a link's words), not their codes; pictures and notes' marks are left out.
/// </summary>
internal static class WordBinaryReader
{
    public static DocModel Read(string path)
    {
        var file = CompoundFile.Open(path);
        var word = file.Read("WordDocument") ?? throw new InvalidDataException("This is not a Word document.");
        if (word.Length < 0x200 || BinaryPrimitives.ReadUInt16LittleEndian(word) != 0xA5EC)
            throw new InvalidDataException("This is not a Word document.");
        var nFib = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(2));
        if (nFib < 0xC1)
            throw new NotSupportedException("This Word document is from before Word 97; open it in Word and save it again.");
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A));
        if ((flags & 0x0100) != 0)
            throw new NotSupportedException("This Word document is protected by a password.");
        var table = file.Read((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new InvalidDataException("The document's table stream is missing.");

        // The FIB's parts have lengths of their own: the count of 16-bit values, then of 32-bit ones, then the pairs.
        int csw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(32));
        var longs = 34 + csw * 2;
        int cslw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(longs));
        var lw = longs + 2;
        var ccpText = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(lw + 12));
        var pairs = lw + cslw * 4 + 2;
        var fcClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(pairs + 33 * 8));
        var lcbClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(pairs + 33 * 8 + 4));
        if (lcbClx == 0 || fcClx + lcbClx > table.Length)
            throw new InvalidDataException("The document's text can't be found.");

        var text = MainText(word, table.AsSpan((int)fcClx, (int)lcbClx), ccpText);
        var doc = new DocModel();
        Build(text, doc);
        return doc;
    }

    /// <summary>The main text, its pieces put together in order: 8-bit (Windows-1252) or UTF-16 each.</summary>
    private static string MainText(byte[] word, ReadOnlySpan<byte> clx, int ccpText)
    {
        // Formatting entries (0x01) come first; the piece table (0x02) after them.
        var at = 0;
        while (at < clx.Length && clx[at] == 0x01)
            at += 3 + BinaryPrimitives.ReadUInt16LittleEndian(clx[(at + 1)..]);
        if (at >= clx.Length || clx[at] != 0x02)
            throw new InvalidDataException("The document's text can't be found.");
        var lcb = (int)BinaryPrimitives.ReadUInt32LittleEndian(clx[(at + 1)..]);
        var plc = clx.Slice(at + 5, Math.Min(lcb, clx.Length - at - 5));
        var pieces = (plc.Length - 4) / 12;
        Text.TextFile.EnsureCodePages();
        var ansi = Encoding.GetEncoding(1252);
        var sb = new StringBuilder(ccpText);
        for (var i = 0; i < pieces && sb.Length < ccpText; i++)
        {
            var start = BinaryPrimitives.ReadInt32LittleEndian(plc[(i * 4)..]);
            var end = Math.Min(BinaryPrimitives.ReadInt32LittleEndian(plc[((i + 1) * 4)..]), ccpText);
            if (end <= start)
                continue;
            var pcd = plc[((pieces + 1) * 4 + i * 8)..];
            var fc = BinaryPrimitives.ReadUInt32LittleEndian(pcd[2..]);
            var count = end - start;
            if ((fc & 0x40000000) != 0)
            {
                var offset = (int)((fc & 0x3FFFFFFF) / 2);
                if (offset + count > word.Length)
                    throw new InvalidDataException("The document is cut short.");
                sb.Append(ansi.GetString(word, offset, count));
            }
            else
            {
                var offset = (int)fc;
                if (offset + count * 2 > word.Length)
                    throw new InvalidDataException("The document is cut short.");
                sb.Append(Encoding.Unicode.GetString(word, offset, count * 2));
            }
        }
        return sb.ToString();
    }

    /// <summary>Paragraphs, breaks and tables from the text's marks; a field shows its result, not its code.</summary>
    private static void Build(string text, DocModel doc)
    {
        var fields = new Stack<bool>(); // true once the field's result has begun
        var line = new StringBuilder();
        var inlines = new List<Inline>();
        Table? table = null;
        TableRow? row = null;
        TableCell? cell = null;
        var lastWasCellEnd = false;

        void FlushRun()
        {
            if (line.Length > 0)
                inlines.Add(new TextRun(line.ToString()));
            line.Clear();
        }

        Paragraph TakeParagraph()
        {
            FlushRun();
            var paragraph = new Paragraph();
            paragraph.Inlines.AddRange(inlines);
            inlines.Clear();
            return paragraph;
        }

        void EndTable()
        {
            if (table is { Rows.Count: > 0 })
                doc.Blocks.Add(table);
            table = null;
            row = null;
            cell = null;
        }

        foreach (var c in text)
        {
            if (c == '\u0013')
            {
                fields.Push(false);
                continue;
            }
            if (c == '\u0014')
            {
                if (fields.Count > 0)
                {
                    fields.Pop();
                    fields.Push(true);
                }
                continue;
            }
            if (c == '\u0015')
            {
                if (fields.Count > 0)
                    fields.Pop();
                continue;
            }
            if (fields.Any(result => !result))
                continue;

            switch (c)
            {
                case '\r':
                case '\u000E':
                {
                    var paragraph = TakeParagraph();
                    if (row is { Cells.Count: > 0 } || cell is not null)
                        (cell ??= new TableCell()).Blocks.Add(paragraph);
                    else
                    {
                        EndTable();
                        doc.Blocks.Add(paragraph);
                    }
                    lastWasCellEnd = false;
                    break;
                }
                case '\u0007':
                {
                    var empty = line.Length == 0 && inlines.Count == 0 && cell is null;
                    if (lastWasCellEnd && empty)
                    {
                        // The row's own mark, after its last cell.
                        if (row is { Cells.Count: > 0 })
                            (table ??= new Table()).Rows.Add(row);
                        row = null;
                        lastWasCellEnd = false;
                        break;
                    }
                    cell ??= new TableCell();
                    cell.Blocks.Add(TakeParagraph());
                    (row ??= new TableRow()).Cells.Add(cell);
                    cell = null;
                    lastWasCellEnd = true;
                    break;
                }
                case '\u000B':
                    FlushRun();
                    inlines.Add(new LineBreak());
                    break;
                case '\u000C':
                {
                    var paragraph = TakeParagraph();
                    EndTable();
                    if (paragraph.Inlines.Count > 0)
                        doc.Blocks.Add(paragraph);
                    doc.Blocks.Add(new PageBreak());
                    lastWasCellEnd = false;
                    break;
                }
                case '\t':
                    line.Append('\t');
                    break;
                case '\u001E':
                    line.Append('-');
                    break;
                case ' ':
                    line.Append(' ');
                    break;
                default:
                    if (c >= ' ')
                    {
                        line.Append(c);
                        lastWasCellEnd = false;
                    }
                    break;
            }
        }
        EndTable();
        if (line.Length > 0 || inlines.Count > 0)
            doc.Blocks.Add(TakeParagraph());
        // Empty paragraphs (Word's spacing) say nothing.
        doc.Blocks.RemoveAll(b => b is Paragraph p && p.Inlines.OfType<TextRun>().All(r => string.IsNullOrWhiteSpace(r.Text)) && !p.Inlines.Any(i => i is InlineImage));
    }
}
