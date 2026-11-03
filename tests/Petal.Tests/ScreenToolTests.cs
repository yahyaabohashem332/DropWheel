using System.Windows;
using System.Windows.Media;
using Petal.App.Clips;
using Petal.App.Overlay;
using Petal.App.Screen;
using Petal.App.Settings;
using Petal.App.Wheels;
using Petal.App.Words;
using Petal.Core.Text;
using Petal.Recipes;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>
/// The screen and text tools that can be checked without a screen: shapes, colours, timers, joined
/// lines, the clipboard history, the usual petal for a kind of file, the new ready-made wheels, and that
/// nothing acts on a window that isn't there.
/// </summary>
[Collection("History")]
public sealed class ScreenToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-screen-").FullName;
    private readonly AppSettings _saved = SettingsStore.Current;

    public void Dispose()
    {
        SettingsStore.Replace(_saved);
        ClipboardHistory.Clear(keepPinned: false);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("7", 7 * 60)]
    [InlineData("1:30", 90)]
    [InlineData("1:02:03", 3723)]
    [InlineData("90s", 90)]
    [InlineData("1h 15m", 4500)]
    [InlineData("2.5", 150)]
    [InlineData("٥ دقائق", 300)]
    [InlineData("10 ثواني", 10)]
    public void Times_typed_by_hand_are_read(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), Durations.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("99:99:99:99")]
    public void Nonsense_is_no_time(string text) => Assert.Null(Durations.Parse(text));

    [Fact]
    public void Times_read_like_a_clock()
    {
        Assert.Equal("05:00", Durations.Format(TimeSpan.FromMinutes(5)));
        Assert.Equal("00:09", Durations.Format(TimeSpan.FromSeconds(9)));
        Assert.Equal("1:02:03", Durations.Format(new TimeSpan(1, 2, 3)));
        Assert.Equal("00:00", Durations.Format(TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void Shapes_are_drawn_as_one_line()
    {
        var a = new Point(10, 10);
        var b = new Point(110, 60);
        Assert.Equal([a, b], DrawOverlay.ShapeOutline(DrawTool.Line, a, b, 4));
        var box = DrawOverlay.ShapeOutline(DrawTool.Rectangle, a, b, 4);
        Assert.Equal(5, box.Count);
        Assert.Equal(box[0], box[^1]);
        // An arrow ends at its point and doubles back for both sides of its head.
        var arrow = DrawOverlay.ShapeOutline(DrawTool.Arrow, a, b, 4);
        Assert.Equal([a, b], arrow.Take(2));
        Assert.Equal(b, arrow[3]);
        Assert.True((arrow[2] - b).Length > 5 && (arrow[4] - b).Length > 5);
        // A ring goes all the way round inside the box.
        var ring = DrawOverlay.ShapeOutline(DrawTool.Ellipse, a, b, 4);
        Assert.True(ring.Count > 30);
        Assert.All(ring, p => Assert.True(p.X >= 9.99 && p.X <= 110.01 && p.Y >= 9.99 && p.Y <= 60.01));
        Assert.Equal(ring[0].X, ring[^1].X, 6);
    }

    [Fact]
    public void Colours_come_in_every_form_with_their_contrast()
    {
        Assert.Equal("#1E90FF", ColourCard.Hex(Color.FromRgb(0x1E, 0x90, 0xFF)));
        var (h, s, l) = ColourCard.Hsl(Color.FromRgb(255, 0, 0));
        Assert.Equal((0.0, 100.0, 50.0), (h, s, l));
        Assert.Equal(21, ColourCard.Contrast(Colors.Black, Colors.White), 2);
        Assert.Equal(1, ColourCard.Contrast(Colors.Gray, Colors.Gray), 6);
        // #767676 on white is the classic just-AA grey.
        Assert.InRange(ColourCard.Contrast(Color.FromRgb(0x76, 0x76, 0x76), Colors.White), 4.5, 4.6);
    }

    [Fact]
    public void Lines_broken_by_the_screen_join_into_paragraphs()
    {
        var text = "Petal reads the text\nin any part of the\nscreen. It runs on\nthis PC.\n\nA new para-\ngraph here.";
        var joined = ScreenTextWindow.JoinLines(text).Replace("\r\n", "\n");
        Assert.Equal("Petal reads the text in any part of the screen. It runs on this PC.\n\nA new paragraph here.", joined);
    }

    [Fact]
    public void Model_answers_lose_their_end_marks_and_noise_is_not_speech()
    {
        Assert.Equal("مرحبا", AskPetal.CleanAnswer("  مرحبا<|im_end|>\nmore"));
        Assert.Equal("Done.", AskPetal.CleanAnswer("Done. [end of text]"));
        Assert.True(Listener.IsNoise("[BLANK_AUDIO]"));
        Assert.True(Listener.IsNoise("(music)"));
        Assert.True(Listener.IsNoise("♪ ♪"));
        Assert.False(Listener.IsNoise("السلام عليكم"));
        Assert.Contains("Arabic", AskPetal.Instructions.Summary(points: false, arabic: true));
        Assert.Contains("English", AskPetal.Instructions.Translate(intoEnglish: true));
    }

    [Fact]
    public void Arabic_text_goes_into_English_and_the_rest_into_Arabic()
    {
        Assert.Equal("ar", Translation.From("مرحبا بالعالم"));
        Assert.Equal("en", Translation.From("Hello world and the people"));
    }

    [Fact]
    public void The_clipboard_history_keeps_the_latest_once_and_pinned_ones_always()
    {
        SettingsStore.Replace(new AppSettings { RememberClipboardHistory = false });
        ClipboardHistory.Clear(keepPinned: false);
        for (int i = 0; i < ClipboardHistory.Keep + 5; i++)
            ClipboardHistory.Add(new ClipItem { Kind = ClipKind.Text, Text = $"clip {i}", Copied = DateTime.Now.AddMinutes(i - 100) });
        Assert.Equal(ClipboardHistory.Keep, ClipboardHistory.All.Count);
        Assert.Equal($"clip {ClipboardHistory.Keep + 4}", ClipboardHistory.All[0].Text);
        // The same text copied again moves up instead of appearing twice.
        ClipboardHistory.Add(new ClipItem { Kind = ClipKind.Text, Text = "clip 10" });
        Assert.Equal(ClipboardHistory.Keep, ClipboardHistory.All.Count);
        Assert.Equal("clip 10", ClipboardHistory.All[0].Text);
        // A pinned clip stays on top and survives clearing.
        var pinned = ClipboardHistory.All.Last();
        ClipboardHistory.TogglePin(pinned);
        Assert.Same(pinned, ClipboardHistory.All[0]);
        ClipboardHistory.Clear(keepPinned: true);
        Assert.Same(pinned, Assert.Single(ClipboardHistory.All));
        Assert.Equal("clip 10", new ClipItem { Kind = ClipKind.Text, Text = "  clip\n10 " }.Preview);
        Assert.Equal(2, Rings.ClipPetals(new WheelEntry { Kind = EntryKind.Clips }).Count + 1);
    }

    [Fact]
    public void A_petal_chosen_often_for_a_kind_of_file_becomes_its_usual()
    {
        WheelMemory.FilePath = Path.Combine(_dir, "memory.json");
        Assert.Equal("jpg", WheelMemory.KindOf([@"C:\a\one.JPG", @"C:\a\two.jpg"]));
        Assert.Equal("jpg,png", WheelMemory.KindOf([@"C:\a\one.png", @"C:\a\two.jpg"]));
        Assert.Null(WheelMemory.KindOf([]));
        var webp = WheelItem.From(F.Webp);
        for (int i = 0; i < WheelMemory.UsualAfter - 1; i++)
            WheelMemory.CountChoice("jpg", "files:Conversions", webp.Key);
        Assert.Null(WheelMemory.UsualFor("jpg", "files:Conversions"));
        WheelMemory.CountChoice("jpg", "files:Conversions", webp.Key);
        Assert.Equal(webp.Key, WheelMemory.UsualFor("jpg", "files:Conversions"));
        Assert.Null(WheelMemory.UsualFor("png", "files:Conversions"));
        Assert.Null(WheelMemory.UsualFor("jpg", "files:Tools"));
        WheelMemory.Clear();
    }

    [Fact]
    public void The_new_ready_made_wheels_arrive_once_after_the_quick_and_media_ones()
    {
        var settings = new AppSettings();
        WheelTemplates.Upgrade(settings);
        var order = settings.Wheels.Select(w => w.Template).ToList();
        Assert.Equal(["quick", "media", "screen", "present", "text", "windows", "clipboard", "work"], order.Take(8));
        // Settings from 1.5 get them too, once, and keep their own wheels where they were.
        var old = new AppSettings { WheelsVersion = 1, Wheels = [WheelTemplates.Quick(), WheelTemplates.Media(), WheelTemplates.Custom("Mine")] };
        Assert.True(WheelTemplates.Upgrade(old));
        Assert.Equal(["quick", "media", "screen", "present", "text", "windows", "clipboard", "work", null], old.Wheels.Select(w => w.Template));
        Assert.False(WheelTemplates.Upgrade(old));
        // The windows wheel points where the window goes: up is maximise, right the right half, left the left half.
        var windows = WheelTemplates.Windows().Items;
        Assert.Equal(8, windows.Count);
        Assert.Equal("window.maximize", windows[0].Action);
        Assert.Equal("window.right", windows[2].Action);
        Assert.Equal("window.left", windows[6].Action);
    }

    [Fact]
    public void Nothing_acts_on_a_window_that_isnt_there()
    {
        Assert.False(WindowArranger.CanArrange(IntPtr.Zero));
        Assert.False(WindowArranger.Place(IntPtr.Zero, WindowPlace.LeftHalf));
        Assert.False(WindowArranger.Close(new IntPtr(0x7FFF_FFF0)));
        Assert.Null(WindowArranger.IsOnTop(IntPtr.Zero));
        Assert.Null(WindowArranger.CycleOpacity(IntPtr.Zero));
    }

    [Fact]
    public async Task Text_is_never_typed_into_nowhere_or_into_Petal()
    {
        Assert.False(await KeySender.TypeTextAsync(IntPtr.Zero, "hello"));
        Assert.False(await KeySender.TypeTextAsync(new IntPtr(0x7FFF_FFF0), "hello"));
    }
}
