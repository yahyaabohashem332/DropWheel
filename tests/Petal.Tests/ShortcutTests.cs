using System.Windows.Input;
using Petal.App.Input;

namespace Petal.Tests;

public class ShortcutTests
{
    [Fact]
    public void Default_is_ctrl_win_z()
    {
        Assert.Equal("Ctrl+Win+Z", Shortcut.Default.ToString());
        Assert.Equal("Ctrl + Win + Z", Shortcut.Default.Display);
        Assert.True(Shortcut.Default.IsValid);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Win+P")]
    [InlineData("Alt+Shift+F2")]
    [InlineData("Ctrl+Alt+Enter")]
    [InlineData("Ctrl+Shift+5")]
    [InlineData("Alt+Win+Backspace")]
    [InlineData("Ctrl+Alt+`")]
    public void Survives_a_round_trip_through_settings(string text) =>
        Assert.Equal(text, Shortcut.Parse(text)!.ToString());

    [Fact]
    public void Parsing_ignores_case_spaces_and_modifier_order()
    {
        var s = Shortcut.Parse(" alt + ctrl + space ");
        Assert.Equal(new Shortcut(ModifierKeys.Control | ModifierKeys.Alt, Key.Space), s);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Space")]            // no modifier
    [InlineData("Shift+A")]          // Shift alone would swallow capitals
    [InlineData("Ctrl+Alt")]         // no ordinary key
    [InlineData("Ctrl+Banana")]      // not a key
    public void Unusable_text_gives_no_shortcut(string? text) =>
        Assert.Null(Shortcut.Parse(text));

    [Fact]
    public void A_modifier_cannot_be_the_main_key() =>
        Assert.False(new Shortcut(ModifierKeys.Control, Key.LeftAlt).IsValid);
}
