using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using Petal.App.Actions.Float;
using Petal.Engines.Documents;
using Petal.Engines.Documents.Sheets;
using Petal.Engines.Files.Subtitles;
using WpfList = System.Windows.Documents.List;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfTable = System.Windows.Documents.Table;

namespace Petal.Tests;

/// <summary>A file floating above the other windows: its sizes and zoom, and what each kind of file shows.</summary>
public sealed class FloatWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "petal-float-" + Guid.NewGuid().ToString("N"));

    public FloatWindowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    // ───────────── sizes and zoom ─────────────

    [Theory]
    [InlineData(1.0, 0, 0, 100, 80, 2.0)]
    [InlineData(2.0, -50, 20, 300, 140, 1.25)]
    [InlineData(0.5, 10, 10, 0, 0, 0.5)]
    public void A_zoom_keeps_the_point_under_the_pointer_where_it_was(double zoom, double ox, double oy, double x, double y, double factor)
    {
        var (next, offset) = FloatMath.ZoomAbout(zoom, new Point(ox, oy), new Point(x, y), factor);
        Assert.Equal(zoom * factor, next, 6);
        // The content point that was under the pointer is still there.
        double before = (x - ox) / zoom, after = (x - offset.X) / next;
        Assert.Equal(before, after, 6);
        Assert.Equal((y - oy) / zoom, (y - offset.Y) / next, 6);
    }

    [Fact]
    public void A_zoom_stays_between_five_percent_and_eight_hundred()
    {
        Assert.Equal(FloatMath.MostZoom, FloatMath.ZoomAbout(7, default, default, 3).Zoom);
        Assert.Equal(FloatMath.LeastZoom, FloatMath.ZoomAbout(0.06, default, default, 0.1).Zoom);
        Assert.Equal("800%", FloatMath.Percent(8).Replace(" ", ""));
    }

    [Fact]
    public void All_of_a_picture_in_view_never_blows_a_small_one_up()
    {
        Assert.Equal(0.5, FloatMath.Fit(new Size(2000, 1000), new Size(1000, 800)), 6);
        Assert.Equal(1, FloatMath.Fit(new Size(200, 100), new Size(1000, 800)));
        Assert.Equal(new Point(400, 350), FloatMath.Centred(new Size(200, 100), 1, new Size(1000, 800)));
    }

    [Fact]
    public void A_picture_floats_at_its_own_size_up_to_most_of_the_screen_and_other_files_at_the_size_they_were_given()
    {
        var work = new Size(1920, 1040);
        // A small picture at its own size, with room for the header and the shadow.
        var small = FloatMath.FirstSize(FloatKind.Picture, new Size(400, 300), work, null);
        Assert.Equal(400 + 2 * FloatMath.Shadow, small.Width, 3);
        Assert.Equal(300 + FloatMath.Header + 2 * FloatMath.Shadow, small.Height, 3);
        // A large one kept in proportion within 60 % of the screen.
        var large = FloatMath.FirstSize(FloatKind.Picture, new Size(6000, 4000), work, new Size(300, 300));
        Assert.True(large.Height <= work.Height * FloatMath.PictureShare + FloatMath.Header + 2 * FloatMath.Shadow + 0.5);
        Assert.Equal(6000.0 / 4000, (large.Width - 2 * FloatMath.Shadow) / (large.Height - FloatMath.Header - 2 * FloatMath.Shadow), 2);
        // A document at the size its kind had last, never bigger than the screen.
        Assert.Equal(new Size(700, 900), FloatMath.FirstSize(FloatKind.Pages, Size.Empty, work, new Size(700, 900)));
        Assert.Equal(new Size(1920, 1040), FloatMath.FirstSize(FloatKind.Sheet, Size.Empty, work, new Size(5000, 3000)));
        var pages = FloatMath.FirstSize(FloatKind.Pages, Size.Empty, work, null);
        Assert.True(pages.Height > pages.Width);
    }

    [Fact]
    public void A_ready_size_is_a_share_of_the_screen_and_a_picture_keeps_its_proportion()
    {
        var work = new Size(2000, 1000);
        Assert.Equal(new Size(1000, 500), FloatMath.ShareOfScreen(0.5, work, null));
        var half = FloatMath.ShareOfScreen(0.5, work, new Size(400, 400));
        Assert.Equal(half.Width - 2 * FloatMath.Shadow, half.Height - FloatMath.Header - 2 * FloatMath.Shadow, 3);
        Assert.True(half.Height <= 500 + 2 * FloatMath.Shadow + 0.5);
    }

    [Fact]
    public void A_window_is_moved_onto_the_screen_before_it_is_made_smaller()
    {
        var work = new Rect(0, 0, 1000, 800);
        Assert.Equal(new Rect(600, 500, 400, 300), FloatMath.Inside(new Rect(900, 700, 400, 300), work));
        Assert.Equal(new Rect(0, 0, 1000, 800), FloatMath.Inside(new Rect(-50, -50, 1400, 900), work));
    }

    [Theory]
    [InlineData(10, 10, Edge.TopLeft)]
    [InlineData(390, 10, Edge.TopRight)]
    [InlineData(10, 290, Edge.BottomLeft)]
    [InlineData(392, 292, Edge.BottomRight)]
    [InlineData(200, 8, Edge.Top)]
    [InlineData(200, 290, Edge.Bottom)]
    [InlineData(12, 150, Edge.Left)]
    [InlineData(389, 150, Edge.Right)]
    [InlineData(200, 150, Edge.None)]
    [InlineData(200, 40, Edge.None)]
    public void Every_edge_and_corner_of_the_card_resizes_the_window(double x, double y, Edge edge) =>
        Assert.Equal(edge, FloatMath.EdgeAt(new Point(x, y), new Size(400, 300), FloatMath.Shadow, 6));

    // ───────────── what each kind of file shows ─────────────

    [Theory]
    [InlineData("a.jpg", FloatKind.Picture)]
    [InlineData("a.heic", FloatKind.Picture)]
    [InlineData("a.psd", FloatKind.Picture)]
    [InlineData("a.pdf", FloatKind.Pages)]
    [InlineData("a.docx", FloatKind.Pages)]
    [InlineData("a.pptx", FloatKind.Pages)]
    [InlineData("a.odt", FloatKind.Pages)]
    [InlineData("a.xlsx", FloatKind.Sheet)]
    [InlineData("a.xls", FloatKind.Sheet)]
    [InlineData("a.ods", FloatKind.Sheet)]
    [InlineData("a.csv", FloatKind.Sheet)]
    [InlineData("a.mp4", FloatKind.Video)]
    [InlineData("a.mkv", FloatKind.Video)]
    [InlineData("a.mp3", FloatKind.Sound)]
    [InlineData("a.md", FloatKind.Text)]
    [InlineData("a.cs", FloatKind.Text)]
    [InlineData("a.epub", FloatKind.Text)]
    [InlineData("a.zip", FloatKind.Other)]
    public void Each_kind_of_file_floats_in_the_view_made_for_it(string name, FloatKind kind) =>
        Assert.Equal(kind, FloatViews.KindOf(Path.Combine(_dir, name)));

    [Fact]
    public void A_sheet_is_shown_as_its_cells_are_displayed_every_row_as_wide_as_the_widest()
    {
        var csv = Path.Combine(_dir, "people.csv");
        File.WriteAllText(csv, "Name,Age,City\nLayla,31,Istanbul\nOmar,27\n");
        var sheet = Assert.Single(Viewing.ReadSheets(csv));
        Assert.Equal(3, sheet.Columns);
        Assert.Equal(["Name", "Age", "City"], sheet.Rows[0]);
        Assert.Equal("Istanbul", sheet.Rows[1][2]);
        Assert.False(sheet.Cut);

        var xlsx = Path.Combine(_dir, "sales.xlsx");
        Spreadsheets.WriteXlsx(xlsx, [new SheetTable("Sales", ["Month", "Orders"], [["May", 120.0], ["June", 205.0]]), new SheetTable("Team", ["Name"], [["Ali"]])]);
        var sheets = Viewing.ReadSheets(xlsx, mostRows: 2);
        Assert.Equal(["Sales", "Team"], sheets.Select(s => s.Name));
        Assert.Equal(2, sheets[0].Rows.Count);
        Assert.True(sheets[0].Cut);
        Assert.Equal("120", sheets[0].Rows[1][1]);
    }

    [Fact]
    public void A_sheet_open_in_another_program_is_read_from_a_copy()
    {
        var csv = Path.Combine(_dir, "open.csv");
        File.WriteAllText(csv, "a,b\n1,2\n");
        // Held for writing, as Excel holds a file it has open.
        using var held = new FileStream(csv, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal("2", Viewing.ReadSheets(csv)[0].Rows[1][1]);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(51, "AZ")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Columns_are_lettered_as_Excel_letters_them(int column, string letters) => Assert.Equal(letters, SheetView.Letters(column));

    [Fact]
    public void Words_are_found_alone_or_several_in_a_row_without_case_or_accents()
    {
        List<(string, Rect)> first = [("The", new Rect(10, 700, 20, 10)), ("Cell", new Rect(35, 700, 25, 10)), ("membrane", new Rect(65, 700, 60, 10))];
        List<(string, Rect)> second = [("café", new Rect(10, 500, 30, 10)), ("cell", new Rect(10, 480, 25, 10))];
        List<List<(string, Rect)>> pages = [first, second];
        var cells = PdfView.Find(pages, "CELL");
        Assert.Equal([0, 1], cells.Select(f => f.Page));
        var phrase = Assert.Single(PdfView.Find(pages, "cell  membrane"));
        Assert.Equal(new Rect(35, 700, 90, 10), phrase.Box);
        Assert.Single(PdfView.Find(pages, "cafe"));
        Assert.Empty(PdfView.Find(pages, "nucleus"));
        Assert.Empty(PdfView.Find(pages, "   "));
    }

    [Fact]
    public void A_pages_words_are_copied_as_its_lines_top_to_bottom_left_to_right()
    {
        var words = new List<(string, Rect)>
        {
            ("world", new Rect(60, 700, 40, 10)), ("Hello", new Rect(10, 701, 40, 10)),
            ("line", new Rect(50, 680, 30, 10)), ("Second", new Rect(10, 680, 35, 10)),
        };
        Assert.Equal("Hello world" + Environment.NewLine + "Second line", PdfView.Lines(words));
    }

    [Fact]
    public void Reading_at_night_turns_each_colour_to_its_opposite_only_where_the_page_is_drawn()
    {
        byte[] pixels = [255, 255, 255, 255, 0, 0, 0, 255, 0, 0, 0, 0, 40, 80, 120, 128];
        PdfView.Invert(pixels);
        Assert.Equal([0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 0, 88, 48, 8, 128], pixels);
    }

    [Fact]
    public void A_documents_laid_out_pages_are_kept_until_the_file_changes()
    {
        var file = Path.Combine(_dir, "report.docx");
        File.WriteAllText(file, "one");
        var first = LayoutView.CachePath(file);
        Assert.Equal(first, LayoutView.CachePath(file));
        File.WriteAllText(file, "one two");
        Assert.NotEqual(first, LayoutView.CachePath(file));
        Assert.EndsWith(".pdf", first);
    }

    [Fact]
    public void Subtitles_beside_a_video_are_shown_as_it_plays()
    {
        var video = Path.Combine(_dir, "talk.mp4");
        File.WriteAllText(video, "");
        Assert.Empty(MediaView.Subtitles(video));
        File.WriteAllText(Path.ChangeExtension(video, ".srt"), "1\n00:00:01,000 --> 00:00:03,000\nHello there\n\n2\n00:00:04,000 --> 00:00:05,500\nGoodbye\n");
        var cues = MediaView.Subtitles(video);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello there", MediaView.CueAt(cues, TimeSpan.FromSeconds(2)));
        Assert.Null(MediaView.CueAt(cues, TimeSpan.FromSeconds(3.5)));
        Assert.Equal("Goodbye", MediaView.CueAt(cues, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_text_being_written_by_another_program_is_read_without_stopping_it()
    {
        var file = Path.Combine(_dir, "log.txt");
        File.WriteAllText(file, "first line\nsecond line\n");
        using var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.StartsWith("first line", await TextFloatView.ReadAsync(file, 1024));
        // A long text is cut, and says so.
        File.WriteAllText(Path.Combine(_dir, "long.txt"), new string('x', 5000));
        Assert.EndsWith("…", await TextFloatView.ReadAsync(Path.Combine(_dir, "long.txt"), 1000));
    }

    [Fact]
    public void Markdown_reads_with_its_headings_lists_quotes_tables_and_code() => OnSta(() =>
    {
        var document = MarkdownDocument.Build("# Plan\n\nWe ship **soon**.\n\n- one\n- [x] two\n\n> careful\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```\ncode\n```\n");
        var blocks = document.Blocks.ToList();
        var heading = Assert.IsType<WpfParagraph>(blocks[0]);
        Assert.True(heading.FontSize > 20);
        Assert.Equal("We ship soon.", new System.Windows.Documents.TextRange(blocks[1].ContentStart, blocks[1].ContentEnd).Text);
        Assert.Equal(2, Assert.IsType<WpfList>(blocks[2]).ListItems.Count);
        Assert.IsType<System.Windows.Documents.Section>(blocks[3]);
        Assert.Equal(2, Assert.IsType<WpfTable>(blocks[4]).RowGroups[0].Rows.Count);
        Assert.Equal(FlowDirection.LeftToRight, document.FlowDirection);
        Assert.Equal(FlowDirection.RightToLeft, MarkdownDocument.Build("# خطة\n\nنطلق قريبًا.").FlowDirection);
    });
}
