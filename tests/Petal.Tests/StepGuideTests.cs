using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Screen;
using Petal.Core.Jobs;

namespace Petal.Tests;

/// <summary>Step-by-step guides: the words for each step, the pictures with their ring, and the saved guide.</summary>
[Collection("Language")]
public sealed class StepGuideTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-steps-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Steps_are_said_by_what_was_clicked()
    {
        Assert.Equal("Click «Save» in Notepad", StepWords.For(StepKind.Click, "Save", "button", "Notepad"));
        Assert.Equal("Click «Save»", StepWords.For(StepKind.Click, "Save", "button", null));
        // The window's own name is not said twice.
        Assert.Equal("Click «Notepad»", StepWords.For(StepKind.Click, "Notepad", "button", "Notepad"));
        Assert.Equal("Choose «Export…»", StepWords.For(StepKind.Click, "Export…", "menu item", "Photos"));
        Assert.Equal("Open the «Layout» tab", StepWords.For(StepKind.Click, "Layout", "tab item", "Word"));
        Assert.Equal("Tick «Remember me»", StepWords.For(StepKind.Click, "Remember me", "check box", null));
        Assert.Equal("Click in «File name»", StepWords.For(StepKind.Click, "File name", "edit", null));
        Assert.Equal("Select «Report.pdf»", StepWords.For(StepKind.Click, "Report.pdf", "list item", null));
        Assert.Equal("Right-click «Desktop»", StepWords.For(StepKind.RightClick, "Desktop", "list item", null));
        Assert.Equal("Double-click «Report.pdf»", StepWords.For(StepKind.DoubleClick, "Report.pdf", null, null));
        Assert.Equal("Click here in Paint", StepWords.For(StepKind.Click, "  ", null, "Paint"));
        Assert.Equal("Click here", StepWords.For(StepKind.Click, null, null, null));
        Assert.Equal("Type «hello world»", StepWords.For(StepKind.Type, null, null, null, "hello world"));
        Assert.Equal("Type the password", StepWords.For(StepKind.Secret, null, null, null, "••••"));
        Assert.Equal("Press Ctrl+S", StepWords.For(StepKind.Keys, null, null, null, "Ctrl+S"));
        // Names that run on are cut, and lines become one.
        Assert.EndsWith("…", StepWords.Clean(new string('a', 90)));
        Assert.Equal("Line one Line two", StepWords.Clean("Line one\r\nLine two"));
    }

    [Fact]
    public void Steps_read_in_Arabic_too()
    {
        Petal.App.Localization.L.Configure(Petal.App.Localization.UiLanguage.Arabic);
        try
        {
            var words = StepWords.For(StepKind.Click, "Save", "button", "Notepad");
            Assert.Contains("Save", words);
            Assert.StartsWith("انقر", words);
        }
        finally
        {
            Petal.App.Localization.L.Configure(Petal.App.Localization.UiLanguage.English);
        }
    }

    private string Picture(string name)
    {
        var path = Path.Combine(_dir, name);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 400, 240));
            dc.DrawRectangle(Brushes.White, null, new Rect(40, 40, 120, 40));
        }
        var bitmap = new RenderTargetBitmap(400, 240, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return StepRecorder.Save(bitmap, new Point(100, 60), _dir) is { } saved && File.Exists(saved) ? Rename(saved, path) : throw new InvalidOperationException();
    }

    private static string Rename(string from, string to)
    {
        File.Move(from, to);
        return to;
    }

    [Fact]
    public void A_click_picture_has_its_ring_where_the_click_was()
    {
        var path = OnSta(() => Picture("ring.png"));
        var image = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var pixels = new byte[4];
        // On the ring (22 px from the click at 100,60): the accent, not the white box or the blue.
        image.CopyPixels(new Int32Rect(122, 60, 1, 1), pixels, 4, 0);
        var accent = Petal.App.Visuals.Theme.Current.Accent;
        Assert.InRange(Math.Abs(pixels[2] - accent.R) + Math.Abs(pixels[1] - accent.G) + Math.Abs(pixels[0] - accent.B), 0, 60);
    }

    [Fact]
    public async Task A_guide_is_saved_as_Markdown_with_its_pictures_and_as_Word_and_a_web_page()
    {
        var steps = OnSta(() => new List<GuideStep>
        {
            new() { Kind = StepKind.Click, Text = "Click «File» in Notepad", Picture = Picture("one.png") },
            new() { Kind = StepKind.Click, Text = "Choose «Save as…» *now*", Picture = Picture("two.png") },
            new() { Kind = StepKind.Keys, Text = "Press Ctrl+S" },
        });
        var converters = Petal.App.EngineSetup.CreateConverters();
        var output = Path.Combine(_dir, "out");

        var md = await StepGuideWindow.Save("Save a note", steps, output, "md", converters, new ConversionSettings());
        var text = await File.ReadAllTextAsync(md);
        Assert.Contains("# Save a note", text);
        Assert.Contains("## 2. Choose «Save as…» \\*now\\*", text);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(output, "Save a note pictures")).Length);
        Assert.Contains("](Save%20a%20note%20pictures/step-001.png)", text);

        var docx = await StepGuideWindow.Save("Save a note", steps, output, "docx", converters, new ConversionSettings());
        using (var zip = ZipFile.OpenRead(docx))
        {
            Assert.Equal(2, zip.Entries.Count(e => e.FullName.Contains("media/", StringComparison.Ordinal)));
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            Assert.Contains("Press Ctrl+S", await reader.ReadToEndAsync());
        }

        var html = await StepGuideWindow.Save("Save a note", steps, output, "html", converters, new ConversionSettings());
        var page = await File.ReadAllTextAsync(html);
        Assert.Equal(2, page.Split("data:image/png;base64,").Length - 1);
        Assert.Contains("Click «File» in Notepad", page);

        var pdf = await StepGuideWindow.Save("Save a note", steps, output, "pdf", converters, new ConversionSettings());
        Assert.True(new FileInfo(pdf).Length > 5000);
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException("On the STA thread: " + error);
        return result;
    }
}
