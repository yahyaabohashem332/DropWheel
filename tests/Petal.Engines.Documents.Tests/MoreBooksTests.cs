using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents.IO;
using Petal.Engines.Documents.Model;
using UglyToad.PdfPig;
using F = Petal.Core.Formats.Formats;
using M = Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.Tests;

/// <summary>1.24's books: Kindle's MOBI and AZW3 read, Kobo's KEPUB both ways, comic books to PDF, EPUB and CBZ.</summary>
public sealed class MoreBooksTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly DocumentEngineOptions NoOffice = new() { UseMicrosoftOffice = false, UseLibreOffice = false };

    private static async Task<string> ConvertAsync(string fixture, FileFormat from, FileFormat to)
    {
        var dir = Fixtures.Instance.NewOutputDir($"{from.Id}-{to.Id}");
        var input = Fixtures.Instance.CopyTo(fixture, dir);
        var converter = Fixtures.ConverterFor(from, to, NoOffice) ?? throw new InvalidOperationException($"No converter for {from} → {to}");
        var result = await Fixtures.ConvertAsync(converter, input, from, to);
        return result.OutputPaths[0];
    }

    [Fact]
    public void PalmDoc_compression_comes_back_byte_for_byte()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The petal turns, the petal turns again. مرحبا بالعالم \u0001\u0007 ", 40)));
        var packed = BookSamples.PalmDocCompress(text);
        Assert.True(packed.Length < text.Length / 2);
        Assert.Equal(text, Mobi.PalmDoc(packed));
        // KindleGen's trailing entries: a 3-byte index entry and a multibyte count of none.
        Assert.Equal(4, Mobi.TrailingSize([1, 2, 3, 0x00, 0xAA, 0xBB, 0x83], 3));
    }

    [Fact]
    public void A_mobi_book_reads_with_its_picture_and_metadata()
    {
        var doc = Mobi.Read(Fixtures.Instance.Get("mobi"));
        Assert.Equal("Petal Kindle Book", doc.Metadata.Title);
        Assert.Equal("Petal Tests", doc.Metadata.Author);
        Assert.Equal("ar", doc.Metadata.Language);
        Assert.Equal([(1, "Petal Kindle Book"), (2, "Second chapter")], doc.Blocks.OfType<Heading>().Select(h => (h.Level, h.Inlines.PlainText())));
        Assert.Contains("مرحبا بالعالم", doc.PlainText(), StringComparison.Ordinal);
        Assert.Contains("so that the compression has runs to copy", doc.PlainText(), StringComparison.Ordinal);
        Assert.Single(doc.AllImages());
        Assert.Contains(doc.AllInlines().OfType<TextRun>(), r => r.Text == "bold" && r.Has(TextStyle.Bold));
        // KindleGen's page break between the chapters.
        var first = doc.Blocks.FindIndex(b => b is Heading { Level: 2 });
        Assert.Contains(doc.Blocks.Take(first), b => b is PageBreak);
    }

    [Fact]
    public void An_azw3_book_is_put_back_together_from_its_skeletons_and_fragments()
    {
        var doc = Mobi.Read(Fixtures.Instance.Get("azw3"));
        Assert.Equal("Petal KF8 Book", doc.Metadata.Title);
        Assert.Equal(["Petal KF8 Book", "Second part"], doc.Blocks.OfType<Heading>().Select(h => h.Inlines.PlainText()));
        var text = doc.PlainText();
        Assert.Contains("The first part, with a picture", text, StringComparison.Ordinal);
        Assert.Contains("مرحبا بالعالم", text, StringComparison.Ordinal);
        Assert.Contains("The end of the book.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("kindle:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("margin", text, StringComparison.Ordinal); // the style sheet's flow is no text
        Assert.Single(doc.AllImages());
        Assert.Single(doc.Blocks.OfType<PageBreak>());
    }

    [Fact]
    public void A_combined_kindle_file_is_read_from_its_KF8_half()
    {
        var png = File.ReadAllBytes(Fixtures.Instance.Get("png-image"));
        var path = Path.Combine(Fixtures.Instance.NewOutputDir("combined"), "combined.mobi");
        File.WriteAllBytes(path, BookSamples.Combined("<html><body><p>Only the KF7 words.</p></body></html>", Fixtures.Kf8Files, "Petal Combined", "Petal Tests", png));
        var text = Mobi.Read(path).PlainText();
        Assert.Contains("The end of the book.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("KF7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Mobipocket_titles_set_in_large_type_are_headings()
    {
        var html = "<p height=\"6em\" align=\"center\"><font size=\"7\">CHAPTER I.</font><br></br><font size=\"7\"> Down the Rabbit-Hole</font></p>" +
            "<p><font size=\"4\"><b>An edition</b></font></p><p><font size=\"2\">Small words.</font></p>" +
            "<p><font size=\"7\">Big</font> and plain words.</p>";
        var doc = HtmlReader.Parse(Mobi.FontHeadings(html));
        var headings = doc.Blocks.OfType<Heading>().ToList();
        Assert.Equal([1, 4], headings.Select(h => h.Level));
        Assert.Equal("CHAPTER I.", headings[0].Inlines.OfType<TextRun>().First().Text.Trim());
        Assert.Contains(headings[0].Inlines, i => i is LineBreak);
        Assert.Equal(2, doc.Blocks.OfType<M.Paragraph>().Count());
    }

    [Fact]
    public void A_book_with_DRM_is_refused_with_a_reason()
    {
        var bytes = File.ReadAllBytes(Fixtures.Instance.Get("mobi"));
        var record0 = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(78));
        bytes[record0 + 13] = 2;
        var path = Path.Combine(Fixtures.Instance.NewOutputDir("drm"), "locked.azw");
        File.WriteAllBytes(path, bytes);
        var e = Assert.Throws<NotSupportedException>(() => Mobi.Read(path));
        Assert.Contains("DRM", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kindle_books_become_epub_with_their_pictures()
    {
        var epub = await ConvertAsync(Fixtures.Instance.Get("azw3"), F.Azw3, F.Epub);
        var back = Epub.Read(epub);
        Assert.Contains("The end of the book.", back.PlainText(), StringComparison.Ordinal);
        Assert.Single(back.AllImages());
        Assert.Equal("Petal KF8 Book", back.Metadata.Title);
    }

    [Fact]
    public async Task An_epub_becomes_a_kepub_and_back_with_the_same_words()
    {
        var epub = Fixtures.Instance.Get("epub");
        var kepub = await ConvertAsync(epub, F.Epub, F.Kepub);
        Assert.EndsWith(".kepub.epub", kepub, StringComparison.Ordinal);
        Assert.Equal(F.Kepub, F.FromPath(kepub));
        using (var zip = ZipFile.OpenRead(kepub))
        {
            var page = zip.Entries.First(e => e.FullName.EndsWith("chapter1.xhtml", StringComparison.Ordinal));
            using var reader = new StreamReader(page.Open());
            var xhtml = XDocument.Parse(reader.ReadToEnd());
            XNamespace x = "http://www.w3.org/1999/xhtml";
            var spans = xhtml.Descendants(x + "span").Where(s => (string?)s.Attribute("class") == "koboSpan").ToList();
            Assert.NotEmpty(spans);
            Assert.Equal(spans.Count, spans.Select(s => (string)s.Attribute("id")!).Distinct().Count());
            Assert.All(spans, s => Assert.Matches(@"^kobo\.\d+\.\d+$", (string)s.Attribute("id")!));
            Assert.NotNull(xhtml.Descendants(x + "div").SingleOrDefault(d => (string?)d.Attribute("id") == "book-inner"));
            // Every word of the body is in a sentence's span.
            var body = xhtml.Root!.Element(x + "body")!;
            Assert.All(body.DescendantNodes().OfType<XText>().Where(t => t.Value.Trim().Length > 0),
                t => Assert.Contains(t.Ancestors(), a => (string?)a.Attribute("class") == "koboSpan"));
        }
        Assert.Equal(Epub.Read(epub).PlainText(), Epub.Read(kepub).PlainText());

        var again = await ConvertAsync(kepub, F.Kepub, F.Epub);
        Assert.Equal("sample.epub", Path.GetFileName(again));
        using (var zip = ZipFile.OpenRead(again))
        using (var original = ZipFile.OpenRead(epub))
        {
            Assert.Equal(original.Entries.Select(e => e.FullName), zip.Entries.Select(e => e.FullName));
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xhtml", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                Assert.DoesNotContain("koboSpan", text, StringComparison.Ordinal);
                Assert.DoesNotContain("book-inner", text, StringComparison.Ordinal);
            }
        }
        Assert.Equal(Epub.Read(epub).PlainText(), Epub.Read(again).PlainText());
    }

    [Theory]
    [InlineData("cbz")]
    [InlineData("cbr")]
    [InlineData("cb7")]
    public void A_comic_book_reads_its_pages_in_order(string id)
    {
        var comic = Comics.Read(Fixtures.Instance.Get(id));
        // "page 1", "page 2", "page 10": numbers counted, the Mac's leftovers skipped.
        Assert.Equal([600, 620, 640], comic.Pages.Select(p => p.Width));
        if (id == "cbz")
        {
            Assert.Equal("Petal Comics 3: The Wheel", comic.Title);
            Assert.Equal("Petal Tests", comic.Author);
            Assert.True(comic.RightToLeft);
        }
    }

    [Fact]
    public void Names_sort_as_people_count()
    {
        string[] names = ["p10.jpg", "p2.jpg", "P1.jpg", "p02b.jpg", "cover.jpg"];
        Assert.Equal(["cover.jpg", "P1.jpg", "p2.jpg", "p02b.jpg", "p10.jpg"], names.Order(Comparer<string>.Create(Comics.NaturalCompare)));
    }

    [Fact]
    public async Task A_comic_becomes_a_pdf_page_a_picture_read_right_to_left()
    {
        var pdf = await ConvertAsync(Fixtures.Instance.Get("cbz"), F.Cbz, F.Pdf);
        using (var document = PdfDocument.Open(pdf))
        {
            Assert.Equal(3, document.NumberOfPages);
            Assert.Equal([450.0, 465.0, 480.0], document.GetPages().Select(p => Math.Round(p.Width, 1)));
            Assert.All(document.GetPages(), p => Assert.Single(p.GetImages()));
        }
        Assert.Contains("/Direction/R2L", Encoding.Latin1.GetString(File.ReadAllBytes(pdf)), StringComparison.Ordinal);
        ConversionPathTests.AssertStrictlyValid(pdf, F.Pdf);
    }

    [Fact]
    public async Task A_comic_becomes_a_fixed_layout_epub()
    {
        var epub = await ConvertAsync(Fixtures.Instance.Get("cbz"), F.Cbz, F.Epub);
        using var zip = ZipFile.OpenRead(epub);
        using var reader = new StreamReader(zip.GetEntry("OEBPS/content.opf")!.Open());
        var opf = reader.ReadToEnd();
        Assert.Contains("pre-paginated", opf, StringComparison.Ordinal);
        Assert.Contains("page-progression-direction=\"rtl\"", opf, StringComparison.Ordinal);
        Assert.Contains("Petal Comics 3: The Wheel", opf, StringComparison.Ordinal);
        Assert.Equal(3, zip.Entries.Count(e => e.FullName.StartsWith("OEBPS/images/page", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("cbr")]
    [InlineData("cb7")]
    public async Task Rar_and_7z_comics_become_cbz(string id)
    {
        var from = F.FromId(id)!;
        var cbz = await ConvertAsync(Fixtures.Instance.Get(id), from, F.Cbz);
        using var zip = ZipFile.OpenRead(cbz);
        Assert.Equal(["page001.png", "page002.png", "page003.png", "ComicInfo.xml"], zip.Entries.Select(e => e.FullName));
        Assert.Equal([600, 620, 640], Comics.Read(cbz).Pages.Select(p => p.Width));
    }

    [Fact]
    public async Task A_pdf_becomes_a_comic_of_its_pages()
    {
        var pdf = Fixtures.Instance.Get("pdf");
        var pages = Petal.Engines.Documents.Pdf.PdfInfo.GetPageCount(pdf);
        var cbz = await ConvertAsync(pdf, F.Pdf, F.Cbz);
        using var zip = ZipFile.OpenRead(cbz);
        Assert.Equal(pages, zip.Entries.Count(e => e.FullName.EndsWith(".jpg", StringComparison.Ordinal)));
        Assert.Equal(pages, Comics.Read(cbz).Pages.Count);
    }

    [Fact]
    public void Kepub_names_keep_their_double_extension()
    {
        Assert.Equal(F.Kepub, F.FromPath(@"C:\books\novel.kepub.epub"));
        Assert.Equal(F.Epub, F.FromPath(@"C:\books\novel.epub"));
        Assert.Equal("novel", F.BaseName(@"C:\books\novel.kepub.epub"));
        Assert.Equal("kepub.epub", OutputNaming.ExtensionOf(@"C:\books\novel.kepub.epub"));
    }

    /// <summary>
    /// Kindle books other tools made (PETAL_KINDLE: a folder of .mobi, .azw and .azw3 files, such as Project
    /// Gutenberg's), each read whole and written as EPUB beside it in "out"; what was read is printed.
    /// </summary>
    [Fact]
    public void Real_kindle_books_read_whole()
    {
        var folder = Environment.GetEnvironmentVariable("PETAL_KINDLE");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;
        var outDir = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;
        foreach (var path in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".mobi" or ".azw" or ".azw3" or ".prc"))
        {
            var doc = Mobi.Read(path);
            var text = doc.PlainText();
            var headings = doc.Blocks.OfType<Heading>().Select(h => h.Inlines.PlainText().Trim()).ToList();
            output.WriteLine($"{Path.GetFileName(path)}: \"{doc.Metadata.Title}\" by {doc.Metadata.Author} ({doc.Metadata.Language}); " +
                $"{text.Length} characters, {doc.Blocks.Count} blocks, {headings.Count} headings, {doc.AllImages().Count()} pictures, " +
                $"{doc.Blocks.Count(b => b is PageBreak)} page breaks, {doc.Blocks.OfType<M.Table>().Count()} tables");
            output.WriteLine("  headings: " + string.Join(" | ", headings.Take(20)));
            output.WriteLine("  starts: " + text[..Math.Min(400, text.Length)].ReplaceLineEndings(" / "));
            output.WriteLine("  ends: " + text[Math.Max(0, text.Length - 300)..].ReplaceLineEndings(" / "));
            Assert.True(text.Length > 1000, "Too little text");
            Assert.DoesNotContain("kindle:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("filepos", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<", text, StringComparison.Ordinal);
            var epub = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + "-" + Path.GetExtension(path).TrimStart('.') + ".epub");
            File.Delete(epub);
            Epub.Write(doc, epub);
        }
    }
}
