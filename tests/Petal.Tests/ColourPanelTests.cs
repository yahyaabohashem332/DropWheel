using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Media;
using Petal.App.Notes;
using Petal.App.Screen.Studio;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;

namespace Petal.Tests;

/// <summary>Any colour can be chosen: the free palette's numbers, the hex code, and the choices that keep one.</summary>
[Collection("Language")]
public sealed class ColourPanelTests
{
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

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(0x2E, 0x8B, 0xC4)]
    [InlineData(0x7B, 0x5C, 0xFF)]
    [InlineData(128, 128, 128)]
    [InlineData(1, 2, 3)]
    public void A_colour_survives_the_trip_through_hue_saturation_and_brightness(byte r, byte g, byte b)
    {
        var colour = Color.FromRgb(r, g, b);
        var (hue, saturation, brightness) = ColourMath.ToHsv(colour);
        Assert.Equal(colour, ColourMath.FromHsv(hue, saturation, brightness));
    }

    [Fact]
    public void Every_colour_on_a_coarse_grid_survives_too()
    {
        for (int r = 0; r < 256; r += 17)
            for (int g = 0; g < 256; g += 17)
                for (int b = 0; b < 256; b += 17)
                {
                    var colour = Color.FromRgb((byte)r, (byte)g, (byte)b);
                    var (h, s, v) = ColourMath.ToHsv(colour);
                    Assert.Equal(colour, ColourMath.FromHsv(h, s, v));
                }
    }

    [Theory]
    [InlineData("#2E8BC4", 0x2E, 0x8B, 0xC4)]
    [InlineData("2e8bc4", 0x2E, 0x8B, 0xC4)]
    [InlineData(" #2E8BC4 ", 0x2E, 0x8B, 0xC4)]
    [InlineData("#F0A", 0xFF, 0x00, 0xAA)]
    [InlineData("000", 0, 0, 0)]
    public void Hex_codes_are_read(string text, byte r, byte g, byte b)
    {
        Assert.True(ColourMath.TryParseHex(text, out var colour));
        Assert.Equal(Color.FromRgb(r, g, b), colour);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    [InlineData("#FF2E8BC4")]
    [InlineData("violet")]
    public void Anything_else_is_not_a_hex_code(string? text) => Assert.False(ColourMath.TryParseHex(text, out _));

    [Fact]
    public void Typing_a_hex_code_changes_the_colour_and_the_red_green_and_blue()
    {
        OnSta(() =>
        {
            var panel = new ColourPanel(Colors.White);
            var boxes = Boxes(panel);
            Color? told = null;
            panel.Changing += c => told = c;
            boxes[0].Text = "#2E8BC4";
            Assert.Equal(Color.FromRgb(0x2E, 0x8B, 0xC4), panel.Colour);
            Assert.Equal(panel.Colour, told);
            Assert.Equal(["46", "139", "196"], boxes.Skip(1).Select(b => b.Text));
        });
    }

    [Fact]
    public void Half_a_hex_code_changes_nothing()
    {
        OnSta(() =>
        {
            var panel = new ColourPanel(Colors.White);
            var boxes = Boxes(panel);
            boxes[0].Text = "#2E8B";
            Assert.Equal(Colors.White, panel.Colour);
        });
    }

    [Fact]
    public void Typing_red_green_and_blue_changes_the_hex_code()
    {
        OnSta(() =>
        {
            var panel = new ColourPanel(Colors.Black);
            var boxes = Boxes(panel);
            boxes[1].Text = "255";
            boxes[3].Text = "16";
            Assert.Equal(Color.FromRgb(255, 0, 16), panel.Colour);
            Assert.Equal("#FF0010", boxes[0].Text);
        });
    }

    [Fact]
    public void The_panel_opens_on_the_colour_it_was_given()
    {
        OnSta(() =>
        {
            var panel = new ColourPanel(Color.FromRgb(0x12, 0x34, 0x56));
            Assert.Equal("#123456", Boxes(panel)[0].Text);
        });
    }

    [Fact]
    public void A_colour_used_goes_first_among_those_used_lately_once()
    {
        var recent = SettingsStore.Current.RecentColours;
        var before = recent.ToList();
        try
        {
            recent.Clear();
            for (int i = 0; i < 10; i++)
                ColourPanel.Remember(Color.FromRgb((byte)i, 0, 0));
            ColourPanel.Remember(Color.FromRgb(5, 0, 0));
            Assert.Equal(8, recent.Count);
            Assert.Equal("#050000", recent[0]);
            Assert.Single(recent, h => h == "#050000");
        }
        finally
        {
            recent.Clear();
            recent.AddRange(before);
        }
    }

    [Fact]
    public void A_note_keeps_a_ready_made_colour_by_name_and_any_other_by_its_code()
    {
        var yellow = StickyNotes.Colours[0];
        Assert.Equal(yellow.Key, StickyNotes.KeyOf(yellow.Paper));
        Assert.Equal((yellow.Paper, yellow.Band), StickyNotes.Paint(yellow.Key));
        var own = Color.FromRgb(0x20, 0x40, 0x60);
        Assert.Equal("#204060", StickyNotes.KeyOf(own));
        Assert.Equal(own, StickyNotes.Paint("#204060").Paper);
        Assert.NotEqual(own, StickyNotes.Paint("#204060").Band);
    }

    [Fact]
    public void A_recording_can_sit_on_a_colour_of_your_own()
    {
        var fill = StudioBackgrounds.Gradient("#204060");
        Assert.NotNull(fill);
        Assert.Equal(fill.Value.From, fill.Value.To);
        Assert.Equal((byte)0x20, fill.Value.From.Red);
        Assert.Null(StudioBackgrounds.Gradient("none"));
        Assert.NotNull(StudioBackgrounds.Gradient("violet"));
    }

    [Fact]
    public void The_accent_can_be_any_colour_and_stays_readable()
    {
        var before = (Theme.Preference, Theme.Accent);
        try
        {
            Theme.Configure(ThemePreference.Light, AccentChoice.Custom, "#FFE680");
            Assert.Equal(Color.FromRgb(0xFF, 0xE6, 0x80), Theme.Current.Accent);
            Assert.NotEqual(Colors.White, Theme.Current.TextOnAccent);
            Theme.Configure(ThemePreference.Light, AccentChoice.Custom, "#102040");
            Assert.Equal(Colors.White, Theme.Current.TextOnAccent);
        }
        finally
        {
            Theme.Configure(before.Preference, before.Accent);
        }
    }

    [Fact]
    public void A_ready_made_accent_still_has_white_words()
    {
        foreach (var accent in new[] { AccentChoice.Violet, AccentChoice.Ocean, AccentChoice.Mint, AccentChoice.Rose, AccentChoice.Amber, AccentChoice.Graphite })
            Assert.Equal(Colors.White, ColourMath.Readable(Theme.ColourOf(accent)));
    }

    /// <summary>The hex box, then red, green and blue.</summary>
    private static List<TextBox> Boxes(ColourPanel panel)
    {
        var found = new List<TextBox>();
        void Walk(object node)
        {
            if (node is TextBox box)
                found.Add(box);
            if (node is Panel p)
                foreach (var child in p.Children)
                    Walk(child);
        }
        Walk(panel);
        return found;
    }
}
