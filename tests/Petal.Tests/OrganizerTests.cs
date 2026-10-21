using ImageMagick;
using ImageMagick.Drawing;
using Petal.Core.Jobs;
using Petal.Engines.Imaging;
using Petal.Recipes;

namespace Petal.Tests;

public sealed class OrganizerTests : IDisposable
{
    private static readonly ConverterRegistry Converters = Petal.App.EngineSetup.CreateConverters();
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-organize-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Picture(string name, uint width, uint height, string? text = null)
    {
        var path = Path.Combine(_dir, name);
        using var image = new MagickImage(MagickColors.White, width, height);
        if (text is not null)
        {
            var draw = new Drawables().Font("Arial").FontPointSize(40).FillColor(MagickColors.Black);
            int y = 90;
            foreach (var line in text.Split('\n'))
            {
                draw.Text(60, y, line);
                y += 70;
            }
            draw.Draw(image);
        }
        image.Write(path);
        return path;
    }

    private Task<FileCategory?> Category(string path, OrganizeOptions? options = null) =>
        Organizer.CategoryAsync(path, options ?? new OrganizeOptions(), Converters, new ConversionSettings());

    [Fact]
    public async Task Screenshots_are_told_from_photos()
    {
        Assert.Equal(FileCategory.Screenshots, await Category(Picture("Screenshot 2024-05-01 101010.png", 800, 600)));
        Assert.Equal(FileCategory.Screenshots, await Category(Picture("لقطة شاشة 2024-05-01.png", 800, 600)));
        // A PNG exactly the size of a screen.
        Assert.Equal(FileCategory.Screenshots, await Category(Picture("image.png", 1920, 1080)));
        Assert.Equal(FileCategory.Pictures, await Category(Picture("holiday.jpg", 1920, 1080)));
        Assert.Equal(FileCategory.Pictures, await Category(Picture("drawing.png", 1000, 700)));
    }

    [Fact]
    public async Task Invoices_are_recognised_by_their_words()
    {
        Assert.True(Organizer.LooksLikeInvoice("INVOICE #2041\nSubtotal 100.00\nVAT 15.00\nTotal due 115.00 USD"));
        Assert.True(Organizer.LooksLikeInvoice("فاتورة ضريبية\nالمجموع: 250 ريال"));
        Assert.True(Organizer.LooksLikeInvoice("Receipt — paid $42.50"));
        Assert.False(Organizer.LooksLikeInvoice("Minutes of the team meeting. We discussed the payment of nobody."));
        Assert.False(Organizer.LooksLikeInvoice("A short story about a long summer."));

        var text = Path.Combine(_dir, "doc.txt");
        File.WriteAllText(text, "ACME Ltd\nINVOICE 2041\nSubtotal 100.00\nVAT 15.00\nTotal due 115.00 USD");
        var pdf = Path.Combine(_dir, "scan0007.pdf");
        await Converters.Find(Petal.Core.Formats.Formats.Txt, Petal.Core.Formats.Formats.Pdf)!.ConvertAsync(
            new ConversionJob(text, Petal.Core.Formats.Formats.Txt, Petal.Core.Formats.Formats.Pdf, pdf, new ConversionSettings()), null, CancellationToken.None);
        Assert.Equal(FileCategory.Invoices, await Category(pdf));

        if (Petal.Engines.Documents.DocumentEngine.IsOcrAvailable)
        {
            var photo = Picture("IMG_2041.jpg", 1200, 700, "RECEIPT\nCoffee 3.50\nTotal 3.50 EUR");
            Assert.Equal(FileCategory.Invoices, await Category(photo));
        }
        // Switched off, a PDF is just a document.
        Assert.Equal(FileCategory.Documents, await Category(pdf, new OrganizeOptions { Invoices = false }));
    }

    [Fact]
    public async Task Everything_else_goes_by_kind()
    {
        string Touch(string name)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            return path;
        }
        Assert.Equal(FileCategory.Installers, await Category(Touch("setup.exe")));
        Assert.Equal(FileCategory.Archives, await Category(Touch("photos.zip")));
        Assert.Equal(FileCategory.Archives, await Category(Touch("backup.7z")));
        Assert.Equal(FileCategory.Music, await Category(Touch("song.mp3")));
        Assert.Equal(FileCategory.Videos, await Category(Touch("clip.mp4")));
        Assert.Equal(FileCategory.Documents, await Category(Touch("notes.docx")));
        Assert.Null(await Category(Touch("data.bin")));
        Assert.Null(await Category(Touch("song2.mp3"), new OrganizeOptions { ByKind = false }));
    }

    [Fact]
    public async Task Files_move_into_folders_named_in_the_users_language()
    {
        var photo = Picture("beach.jpg", 640, 480);
        var moved = await Organizer.OrganizeAsync(photo, _dir, new OrganizeOptions(), arabic: true, Converters, new ConversionSettings());
        Assert.Equal(Path.Combine(_dir, "الصور", "beach.jpg"), moved);
        Assert.False(File.Exists(photo));

        // An existing folder wins, whatever its language.
        Directory.CreateDirectory(Path.Combine(_dir, "Music"));
        var song = Path.Combine(_dir, "song.mp3");
        File.WriteAllBytes(song, [1, 2, 3]);
        Assert.Equal(Path.Combine(_dir, "Music", "song.mp3"), await Organizer.OrganizeAsync(song, _dir, new OrganizeOptions(), arabic: true, Converters, new ConversionSettings()));

        // Never over another file.
        var again = Picture("beach.jpg", 640, 480);
        Assert.Equal(Path.Combine(_dir, "الصور", "beach 2.jpg"), await Organizer.OrganizeAsync(again, _dir, new OrganizeOptions(), arabic: true, Converters, new ConversionSettings()));
    }

    [Fact]
    public async Task Photos_can_be_named_after_when_they_were_taken()
    {
        var photo = Picture("IMG_0042.jpg", 640, 480);
        var dated = Path.Combine(_dir, "dated.jpg");
        await ImageMetadata.ApplyAsync(photo, dated, [new MetadataChange.Set("exif:9003", "2023:08:14 09:30:00")]);
        File.Delete(photo);
        Assert.Equal("2023-08-14 09.30.00", await Organizer.DateNameAsync(dated));

        var options = new OrganizeOptions { ByKind = false, Screenshots = false, Invoices = false, RenameByDate = true };
        var renamed = await Organizer.OrganizeAsync(dated, _dir, options, arabic: false, Converters, new ConversionSettings());
        Assert.Equal(Path.Combine(_dir, "2023-08-14 09.30.00.jpg"), renamed);
        // Seen again by the watcher: left as it is, not renamed for ever.
        Assert.Null(await Organizer.OrganizeAsync(renamed!, _dir, options, arabic: false, Converters, new ConversionSettings()));
    }

    [Fact]
    public void Sorting_ignores_what_is_already_in_subfolders()
    {
        var rule = new WatchRule { Folder = _dir, Kind = WatchedKind.Any, Action = WatchAction.Organize, IncludeSubfolders = true };
        Assert.True(rule.Wants(Path.Combine(_dir, "new.pdf")));
        Assert.False(rule.Wants(Path.Combine(_dir, "Documents", "new.pdf")));
    }
}
