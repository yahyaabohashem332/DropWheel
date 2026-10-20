using System.Globalization;
using Petal.App.Tools.Parts;
using Petal.App.Util;
using Petal.Core.Jobs;
using Petal.Core.Tools;

namespace Petal.Tests;

public class TimeTextTests
{
    [Theory]
    [InlineData(0, "0:00.00")]
    [InlineData(4, "0:04.00")]
    [InlineData(65.25, "1:05.25")]
    [InlineData(3725.5, "1:02:05.50")]
    public void Formats_like_the_tool_windows(double seconds, string expected) =>
        Assert.Equal(expected, TimeText.Format(seconds));

    [Theory]
    [InlineData("75", 75)]
    [InlineData("75.5", 75.5)]
    [InlineData("1:15.5", 75.5)]
    [InlineData("0:01:15.5", 75.5)]
    public void Parses_seconds_and_clock_times(string text, double expected)
    {
        Assert.True(TimeText.TryParse(text, out var seconds));
        Assert.Equal(expected, seconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("1:2:3:4")]
    public void Rejects_nonsense(string text) => Assert.False(TimeText.TryParse(text, out _));
}

public class SizeTests
{
    [Fact]
    public void Formats_decimal_units()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("1 byte", Sizes.Format(1));
            Assert.Equal("999 bytes", Sizes.Format(999));
            Assert.Equal("2.4 MB", Sizes.Format(2_400_000));
            Assert.Equal("78.9 KB", Sizes.Format(78_855));
            Assert.Equal("120 GB", Sizes.Format(120_000_000_000));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }
}

public class ExtensionOverrideTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-ext-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Compressing_wav_writes_flac()
    {
        var input = Path.Combine(_dir, "voice.wav");
        var output = OutputNaming.ForTool(input, ToolRegistry.Get(ToolId.Compress), extensionOverride: "flac");
        Assert.Equal(Path.Combine(_dir, "voice Compressed.flac"), output);
    }
}
