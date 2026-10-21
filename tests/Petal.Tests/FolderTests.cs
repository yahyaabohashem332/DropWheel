using System.Windows.Threading;
using ClosedXML.Excel;
using ImageMagick;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.Core.Actions;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Recipes;
using Xunit.Abstractions;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>A dropped folder stands for what it holds: the wheel, conversions, batch tools and the folder's own tools.</summary>
[Collection("History")]
public sealed class FolderTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-folders-").FullName;
    private readonly string _trip;
    private readonly string _previousHistory = History.FilePath;

    public FolderTests(ITestOutputHelper output)
    {
        _output = output;
        FolderLease.Install();
        History.FilePath = Path.Combine(_dir, "history.json");
        History.Enabled = () => true;
        History.Reload();
        // Trip\a.jpg, Trip\Day 2\b.png, Trip\Day 10\c.jpg, Trip\notes.txt, and what a folder should not count.
        _trip = Path.Combine(_dir, "Trip");
        Picture(Path.Combine(_trip, "a.jpg"), 400, 300);
        Picture(Path.Combine(_trip, "Day 2", "b.png"), 300, 400);
        Picture(Path.Combine(_trip, "Day 10", "c.jpg"), 320, 240);
        File.WriteAllText(Path.Combine(_trip, "notes.txt"), "packing list");
        Directory.CreateDirectory(Path.Combine(_trip, ".git"));
        File.WriteAllText(Path.Combine(_trip, ".git", "config"), "x");
        File.WriteAllText(Path.Combine(_trip, "~$draft.docx"), "x");
        File.WriteAllText(Path.Combine(_trip, "movie.mp4.crdownload"), "x");
        var hidden = Path.Combine(_trip, "hidden.jpg");
        Picture(hidden, 10, 10);
        File.SetAttributes(hidden, FileAttributes.Hidden);
    }

    public void Dispose()
    {
        History.FilePath = _previousHistory;
        History.Reload();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void Picture(string path, int width, int height, string? taken = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(MagickColors.SteelBlue, (uint)width, (uint)height);
        image.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Orange).Rectangle(width / 4.0, height / 4.0, width / 2.0, height / 2.0));
        if (taken is not null)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.DateTimeOriginal, taken);
            image.SetProfile(exif);
        }
        image.Write(path);
    }

    /// <summary>The repository's sample files.</summary>
    private static string Data
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testdata")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "testdata");
        }
    }

    private string[] Tree(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Order().ToArray();

    /// <summary>Runs on a thread with a dispatcher, as the app's interface does (the progress card needs one).</summary>
    private static async Task OnUi(Func<Task> work)
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var dispatcher = await ready.Task;
        try
        {
            await await dispatcher.InvokeAsync(work);
        }
        finally
        {
            dispatcher.InvokeShutdown();
        }
    }

    [Fact]
    public void A_folder_stands_for_its_files_in_the_order_Explorer_shows_them()
    {
        var loose = Path.Combine(_dir, "loose.jpg");
        Picture(loose, 50, 50);
        var contents = FolderContents.Read([_trip, loose]);
        Assert.True(contents.Complete);
        Assert.Equal([_trip], contents.Folders);
        // Day 2 before Day 10; hidden, temporary, half-downloaded files and .git left out.
        Assert.Equal(["a.jpg", "notes.txt", @"Day 2\b.png", @"Day 10\c.jpg", "loose.jpg"],
            contents.Files.Select(f => f.Root is null ? Path.GetFileName(f.Path) : Path.GetRelativePath(_trip, f.Path)));
        Assert.Equal(Petal.Core.Formats.FormatFamily.Image, contents.MainKind);
        Assert.Null(contents.Files[^1].Root);
        Assert.Equal("Day 2", contents.Files[2].RelativeDirectory);
        // With no time to look, the wheel says it hasn't seen everything.
        Assert.False(FolderContents.Read([_trip], TimeSpan.Zero).Complete);
    }

    [Fact]
    public void Results_of_a_folders_files_go_to_a_copy_of_its_tree_until_the_lease_ends()
    {
        var contents = FolderContents.Read([_trip]);
        var compress = ToolRegistry.Get(ToolId.Compress);
        var lease = FolderLease.Place(contents, f => f.Kind == Petal.Core.Formats.FormatFamily.Image, FolderDrop.Label(compress));
        Assert.Equal(3, lease.Files.Count);
        var copy = Path.Combine(_dir, "Trip (Compressed)");
        Assert.Equal([copy], lease.Copies);
        // The copy's name says what was done, so each file keeps its own name.
        Assert.Equal(Path.Combine(copy, "Day 2", "b.png"), OutputNaming.ForTool(Path.Combine(_trip, "Day 2", "b.png"), compress));
        Assert.Equal(Path.Combine(copy, "a.webp"), OutputNaming.ForConversion(Path.Combine(_trip, "a.jpg"), F.Webp));
        // A file the lease didn't take stays as usual: beside itself.
        Assert.Equal(Path.Combine(_trip, "notes Compressed.txt"), OutputNaming.ForTool(Path.Combine(_trip, "notes.txt"), compress));

        using (FolderLease.HoldFor([Path.Combine(_trip, "a.jpg")]))
        {
            lease.Release();
            // A running job still holds it.
            Assert.StartsWith(copy, OutputNaming.ForTool(Path.Combine(_trip, "a.jpg"), compress));
        }
        Assert.Equal(Path.Combine(_trip, "a Compressed.jpg"), OutputNaming.ForTool(Path.Combine(_trip, "a.jpg"), compress));
        // Nothing was written, so the copy went too.
        Assert.False(Directory.Exists(copy));
    }

    [Fact]
    public void The_wheel_offers_what_most_of_the_folder_can_become_and_the_folders_own_tools()
    {
        File.Copy(CreateZip(), Path.Combine(_trip, "old.zip"));
        var contents = FolderContents.Read([_trip]);
        var converters = Petal.App.EngineSetup.CreateConverters();
        var conversions = FolderDrop.Conversions(contents, converters, [], "Alt").Items.Select(i => i.Payload).OfType<Petal.Core.Formats.FileFormat>().ToList();
        Assert.Contains(F.Webp, conversions);
        Assert.Contains(F.Png, conversions);
        Assert.Contains(F.Pdf, conversions);
        Assert.DoesNotContain(F.Mp3, conversions);

        var tools = FolderDrop.Tools(contents, _ => true, "Shift", "Alt").Items.Select(i => ((ToolInfo)i.Payload!).Id).ToList();
        Assert.Equal([ToolId.OrganizeFolder, ToolId.FolderPdf, ToolId.ContactSheet, ToolId.FolderReport, ToolId.GatherFiles], tools.Take(5));
        Assert.Contains(ToolId.Compress, tools);
        Assert.Contains(ToolId.ResizeImage, tools);
        Assert.Contains(ToolId.ExtractArchive, tools);
        // Joining tools give way to the folder's own.
        Assert.DoesNotContain(ToolId.CreatePdf, tools);
        Assert.DoesNotContain(ToolId.CreateCollage, tools);
        Assert.DoesNotContain(ToolId.MakeSearchable, tools);
        var (title, subtitle) = FolderDrop.Hub(contents);
        _output.WriteLine($"{title} · {subtitle}");
        Assert.Equal("5 files", subtitle);
    }

    private string CreateZip()
    {
        var zip = Path.Combine(_dir, "pack.zip");
        if (!File.Exists(zip))
        {
            using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open());
            writer.Write("hello");
        }
        return zip;
    }

    [Fact]
    public async Task Converting_a_folder_fills_a_copy_of_its_tree_and_leaves_the_folder_alone()
    {
        var before = Tree(_trip);
        var contents = FolderContents.Read([_trip]);
        await OnUi(() => FolderDrop.ConvertAsync(new JobRunner(Petal.App.EngineSetup.CreateConverters()), contents, F.Webp, new ScreenPoint(300, 300)));

        var copy = Path.Combine(_dir, "Trip (WEBP)");
        Assert.Equal(["a.webp", @"Day 10\c.webp", @"Day 2\b.webp"], Tree(copy));
        using (var b = new MagickImage(Path.Combine(copy, "Day 2", "b.webp")))
            Assert.Equal((300u, 400u), (b.Width, b.Height));
        Assert.Equal(before, Tree(_trip));
        var entry = Assert.Single(History.Entries);
        Assert.Equal(3, entry.Outputs.Count);
    }

    [Fact]
    public async Task A_batch_tool_and_archive_extraction_put_their_results_in_the_copy()
    {
        File.Copy(CreateZip(), Path.Combine(_trip, "Day 2", "pack.zip"));
        var contents = FolderContents.Read([_trip]);
        var resize = ToolRegistry.Get(ToolId.ResizeImage);
        var lease = FolderLease.Place(contents, f => f.Format is { } format && ToolRegistry.Takes(resize, format), FolderDrop.Label(resize));
        try
        {
            foreach (var file in lease.Files)
                await Petal.Engines.Imaging.ImageTransformer.TransformAsync(file, OutputNaming.ForTool(file, resize), new Petal.Engines.Imaging.ImageTransform { Mode = Petal.Engines.Imaging.ResizeMode.Percent, Percent = 50 });
        }
        finally
        {
            lease.Release();
        }
        Assert.Equal(["a.jpg", @"Day 10\c.jpg", @"Day 2\b.png"], Tree(Path.Combine(_dir, "Trip (Resized)")));

        var extract = ToolRegistry.Get(ToolId.ExtractArchive);
        var archives = FolderLease.Place(contents, f => f.Format is { } format && ToolRegistry.Takes(extract, format), FolderDrop.Label(extract));
        try
        {
            foreach (var file in archives.Files)
                await Petal.Engines.Files.Archives.ArchiveExtractor.ExtractAsync(file, null, CancellationToken.None);
        }
        finally
        {
            archives.Release();
        }
        Assert.Equal([@"Day 2\pack\readme.txt"], Tree(Path.Combine(_dir, "Trip (Extracted)")));
    }

    [Fact]
    public async Task Clean_for_sharing_makes_a_clean_copy_of_the_folder()
    {
        Picture(Path.Combine(_trip, "Day 2", "dated.jpg"), 200, 100, "2021:08:09 10:11:12");
        var jobs = new JobRunner(Petal.App.EngineSetup.CreateConverters());
        await OnUi(() => new Petal.App.Actions.FileActionRunner(jobs).RunAsync(FileActionRegistry.All.First(a => a.Id == FileActionId.Clean), [_trip], new ScreenPoint(300, 300)));
        var copy = Path.Combine(_dir, "Trip (Clean)");
        Assert.Contains(@"Day 2\dated.jpg", Tree(copy));
        using var clean = new MagickImage(Path.Combine(copy, "Day 2", "dated.jpg"));
        Assert.Null(clean.GetExifProfile()?.GetValue(ExifTag.DateTimeOriginal));
    }

    [Fact]
    public async Task Organizing_by_month_moves_files_and_History_can_put_them_back()
    {
        Picture(Path.Combine(_trip, "beach.jpg"), 100, 100, "2021:08:09 10:11:12");
        var notes = Path.Combine(_trip, "notes.txt");
        File.SetLastWriteTime(notes, new DateTime(2023, 5, 4, 9, 0, 0));
        var moved = await Organizer.OrganizeByMonthAsync(Path.Combine(_trip, "beach.jpg"), _trip, false);
        Assert.Equal(Path.Combine(_trip, "2021", "2021-08", "beach.jpg"), moved);
        var note = await Organizer.OrganizeByMonthAsync(notes, _trip, false);
        Assert.Equal(Path.Combine(_trip, "2023", "2023-05", "notes.txt"), note);
        // Already where it belongs: left alone.
        Assert.Null(await Organizer.OrganizeByMonthAsync(note!, _trip, false));
        BatchRename.Undo([(Path.Combine(_trip, "beach.jpg"), moved!), (notes, note!)]);
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(Path.Combine(_trip, "beach.jpg")));
    }

    [Fact]
    public async Task Folder_to_PDF_keeps_the_order_of_pictures_and_PDF_pages()
    {
        var pdf = Path.Combine(_trip, "Day 2", "ticket.pdf");
        File.Copy(Path.Combine(Data, "album.pdf"), pdf);
        int albumPages = Petal.Engines.Documents.Pdf.PdfInfo.GetPageCount(pdf);
        var contents = FolderContents.Read([_trip]);
        var files = contents.Files.Where(f => f.Kind == Petal.Core.Formats.FormatFamily.Image || f.Format == F.Pdf).Select(f => f.Path).ToList();
        Assert.Equal(["a.jpg", @"Day 2\b.png", @"Day 2\ticket.pdf", @"Day 10\c.jpg"], files.Select(f => Path.GetRelativePath(_trip, f)));
        var output = Path.Combine(_dir, "Trip.pdf");
        int pages = await FolderPdf.CreateAsync(files, output, new FolderPdfOptions());
        var sizes = Petal.Engines.Documents.Pdf.PdfInfo.GetPageSizes(output);
        Assert.Equal(3 + albumPages, pages);
        Assert.Equal(pages, sizes.Count);
        // a.jpg is wide, b.png tall, the album's pages next, c.jpg last and wide.
        Assert.True(sizes[0].Width > sizes[0].Height);
        Assert.True(sizes[1].Width < sizes[1].Height);
        Assert.True(sizes[^1].Width > sizes[^1].Height);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_contact_sheet_has_every_picture_page_after_page(bool arabic)
    {
        var pictures = new List<string>();
        for (int i = 0; i < 13; i++)
        {
            var name = arabic ? $"صورة الرحلة {i + 1}.jpg" : $"Holiday photo {i + 1}.jpg";
            var path = Path.Combine(_dir, "sheet", name);
            Picture(path, 300 + 20 * i, 200);
            pictures.Add(path);
        }
        var options = new ContactSheetOptions { Columns = 3, Title = arabic ? "الرحلة" : "Holiday", RightToLeft = arabic, PageLabel = (p, n) => $"{p}/{n}" };
        var (columns, rows, _, _, _) = ContactSheet.Layout(options);
        var output = Path.Combine(_dir, $"sheet {arabic}.pdf");
        int pages = await ContactSheet.CreateAsync(pictures, output, options);
        Assert.Equal((13 + columns * rows - 1) / (columns * rows), pages);
        Assert.Equal(pages, Petal.Engines.Documents.Pdf.PdfInfo.GetPageCount(output));
        var png = Petal.Engines.Documents.Pdf.PdfInfo.RenderPagePng(output, 0, 900);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"petal-contact-sheet-{(arabic ? "ar" : "en")}.png"), png);
        _output.WriteLine($"{pages} pages of {columns}×{rows}");
    }

    [Fact]
    public async Task A_folder_report_lists_every_file_and_what_takes_the_space()
    {
        File.Copy(Path.Combine(Data, "weekend.mp4"), Path.Combine(_trip, "Day 10", "clip.mp4"));
        File.Copy(Path.Combine(Data, "album.pdf"), Path.Combine(_trip, "album.pdf"));
        var contents = FolderContents.Read([_trip]);
        var text = new FolderReportText("Files", "By kind", "Largest folders", "Largest files", "Name", "Folder", "Type", "Size", "Modified", "Created", "Width", "Height", "Duration", "Pages", "Files");
        var xlsx = Path.Combine(_dir, "Trip Report.xlsx");
        await FolderReport.CreateAsync(_trip, contents.Files, xlsx, new FolderReportOptions(), text);
        using (var book = new XLWorkbook(xlsx))
        {
            Assert.Equal(["Files", "By kind", "Largest folders", "Largest files"], book.Worksheets.Select(w => w.Name));
            var files = book.Worksheet("Files");
            Assert.Equal(contents.Files.Count + 1, files.LastRowUsed()!.RowNumber());
            var rows = files.RowsUsed().Skip(1).ToDictionary(r => r.Cell(1).GetString());
            Assert.Equal(@"Trip\Day 2", rows["b.png"].Cell(2).GetString());
            Assert.Equal(new FileInfo(Path.Combine(_trip, "a.jpg")).Length, rows["a.jpg"].Cell(4).GetDouble());
            Assert.Equal(400, rows["a.jpg"].Cell(7).GetDouble());
            Assert.True(rows["clip.mp4"].Cell(9).GetTimeSpan() > TimeSpan.Zero);
            Assert.Equal(Petal.Engines.Documents.Pdf.PdfInfo.GetPageCount(Path.Combine(_trip, "album.pdf")), rows["album.pdf"].Cell(10).GetDouble());
            // The whole folder is the largest, holding everything.
            var folders = book.Worksheet("Largest folders");
            Assert.Equal("Trip", folders.Cell(2, 1).GetString());
            Assert.Equal(contents.Files.Count, folders.Cell(2, 2).GetDouble());
        }
        var csv = Path.Combine(_dir, "Trip Report.csv");
        await FolderReport.CreateAsync(_trip, contents.Files, csv, new FolderReportOptions { Csv = true, Details = false }, text);
        var bytes = File.ReadAllBytes(csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = File.ReadAllLines(csv);
        Assert.Equal("Name,Folder,Type,Size,Modified,Created", lines[0]);
        Assert.Equal(contents.Files.Count + 1, lines.Length);
    }

    [Fact]
    public async Task Gathering_copies_everything_into_one_folder_and_numbers_the_same_names()
    {
        Picture(Path.Combine(_trip, "Day 10", "a.jpg"), 60, 60);
        var contents = FolderContents.Read([_trip]);
        var destination = Path.Combine(_dir, "Trip (Gathered)");
        var copied = await Gatherer.CopyAsync(contents.Files.Select(f => f.Path).ToList(), destination);
        Assert.Equal(["a 2.jpg", "a.jpg", "b.png", "c.jpg", "notes.txt"], Directory.GetFiles(destination).Select(Path.GetFileName).Order());
        Assert.Equal(contents.Files.Count, copied.Count);
        // The originals are still where they were.
        Assert.True(File.Exists(Path.Combine(_trip, "Day 10", "a.jpg")));
    }
}
