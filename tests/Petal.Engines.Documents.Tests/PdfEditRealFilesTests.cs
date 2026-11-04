using Petal.Engines.Documents.Pdf;
using Xunit.Abstractions;

namespace Petal.Engines.Documents.Tests;

/// <summary>
/// Edit PDF on files other programs wrote, in the folder PETAL_REAL_PDFS names: each one is redacted, retyped, marked up and
/// (when it is a form) filled into its "out" folder, for another program to read back. Skipped when it isn't set.
/// </summary>
public sealed class PdfEditRealFilesTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Files_from_other_programs_are_edited()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_REAL_PDFS");
        if (folder is null)
        {
            output.WriteLine("Skipped: PETAL_REAL_PDFS isn't set.");
            return;
        }
        var results = Path.Combine(folder, "out");
        Directory.CreateDirectory(results);
        foreach (var path in Directory.GetFiles(Path.Combine(folder, "in"), "*.pdf"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var before = File.ReadAllBytes(path);
            using var session = PdfEditSession.Open(path);
            output.WriteLine($"{name}: {session.Pages.Count} pages, {session.Fields.Count} fields, rights {session.Rights}, XFA only {session.IsXfaOnly}, signed {session.IsSigned}");

            // The name wherever it is, and everything that looks private.
            var found = (await session.FindAsync("Mohammad Al-Khatib")).Concat(await session.FindAsync("محمد الخطيب")).Concat(await session.FindPrivateAsync()).ToList();
            foreach (var place in found)
                output.WriteLine($"  found on {place.Page}: {place.Text}");
            var redacted = await session.SaveAsync(new PdfEditRequest
            {
                Items = found.Select(f => new PdfRedactItem(f.Page, f.Area)).ToList(),
                Secrets = ["Mohammad Al-Khatib", "محمد الخطيب"],
            }, Path.Combine(results, name + ".redacted.pdf"));
            output.WriteLine($"  redacted: {redacted}");

            // The first line and the last of the first page typed again.
            var lines = session.Lines(0);
            foreach (var line in lines.Where(l => l.Text.Any(c => c > 0x600)))
                output.WriteLine($"  line: {line.Text} [{string.Join(' ', line.Text.Take(12).Select(c => ((int)c).ToString("X")))}]");
            if (lines.Count > 0)
            {
                var items = new[] { lines[0], lines[^1] }.Distinct().Select(l => new PdfReplaceItem(0, l.Area, l.Words.Select(w => w.Area).ToList(), "Typed again here", l.Size, l.Colour, l.Bold, l.Italic, l.FontName, false)).ToList();
                var retyped = await session.SaveAsync(new PdfEditRequest { Items = items }, Path.Combine(results, name + ".retyped.pdf"));
                output.WriteLine($"  retyped '{lines[0].Text}': {retyped}");
            }

            // One of each mark.
            var size = session.Pages[0];
            double x = size.Width * 0.5, y = size.Height * 0.6;
            await session.SaveAsync(new PdfEditRequest
            {
                Items =
                [
                    new PdfTextItem(0, new PdfArea(x, y, 200, 18), "Written anywhere", 13, 0xE02020, true),
                    new PdfTextItem(0, new PdfArea(x, y + 22, 200, 18), "نص مكتوب في أي مكان", 13, 0x1D2B6B),
                    new PdfMarkItem(0, new PdfArea(x, y + 46, 16, 16), PdfMark.Check, 0x1D2B6B),
                    new PdfNoteItem(0, new PdfArea(x + 40, y + 46, 20, 20), "A note", 0xFFD23F),
                    new PdfShapeItem(0, new PdfArea(x, y + 70, 80, 30), false, 2, 0xE02020),
                    new PdfMarkupItem(0, PdfMarkup.Highlight, lines.Take(1).Select(l => l.Area).ToList(), 0xFFEB3B),
                ],
            }, Path.Combine(results, name + ".marked.pdf"));

            if (session.Fields.Count > 0)
            {
                var values = new Dictionary<string, string>();
                foreach (var field in session.Fields)
                    switch (field.Kind)
                    {
                        case PdfFieldKind.Text:
                            values[field.Name] = field.Name == "given" ? "يحيى أبو هاشم" : "Filled " + field.Name;
                            break;
                        case PdfFieldKind.CheckBox or PdfFieldKind.Radio:
                            values[field.Name] = field.Widgets[0].OnState!;
                            break;
                        case PdfFieldKind.Combo or PdfFieldKind.List:
                            values[field.Name] = field.Options[^1].Export;
                            break;
                    }
                await session.SaveAsync(new PdfEditRequest { Fields = values }, Path.Combine(results, name + ".filled.pdf"));
                await session.SaveAsync(new PdfEditRequest { Fields = values, Flatten = true }, Path.Combine(results, name + ".flat.pdf"));
            }
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }
}
