using System.Text.Json;
using Petal.App.Jobs;
using Petal.App.Settings;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Imaging;
using Petal.Engines.Media;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

// Labels go through the translation table, so keep clear of the tests that switch language.
[Collection("Language")]
public class FavouriteTests
{
    private static readonly ConverterRegistry Converters = Create();

    private static ConverterRegistry Create()
    {
        var registry = new ConverterRegistry();
        registry.Register(new ImageConverter());
        registry.Register(new MediaConverter());
        return registry;
    }

    [Fact]
    public void Settings_follow_the_kind_of_target()
    {
        var defaults = new ConversionSettings();

        var picture = new Favourite { Format = "webp", LongestSide = 1600, Quality = 80 }.Apply(defaults);
        Assert.Equal(80, picture.ImageQuality);
        Assert.Equal(1600, picture.MaxLongEdge);
        Assert.Equal(defaults.VideoCrf, picture.VideoCrf);

        var video = new Favourite { Format = "mp4", LongestSide = 1280, Quality = 28 }.Apply(defaults);
        Assert.Equal(28, video.VideoCrf);
        Assert.Equal(1280, video.MaxLongEdge);
        Assert.Equal(defaults.ImageQuality, video.ImageQuality);

        // Audio has no size, and a lossless picture format has no quality.
        var audio = new Favourite { Format = "mp3", LongestSide = 1280, Quality = 128 }.Apply(defaults);
        Assert.Equal(128, audio.AudioBitrateKbps);
        Assert.Equal(0, audio.MaxLongEdge);
        var png = new Favourite { Format = "png", Quality = 50 }.Apply(defaults);
        Assert.Equal(defaults.ImageQuality, png.ImageQuality);

        // The user's defaults are left alone.
        Assert.Equal(0, defaults.MaxLongEdge);
        Assert.Equal(90, defaults.ImageQuality);
    }

    [Fact]
    public void Offered_for_files_it_can_convert_or_make_smaller()
    {
        var plainJpg = new Favourite { Format = "jpg" };
        Assert.True(plainJpg.AppliesTo([F.Png], Converters));
        Assert.False(plainJpg.AppliesTo([F.Jpg], Converters));

        var smallJpg = new Favourite { Format = "jpg", LongestSide = 1600 };
        Assert.True(smallJpg.AppliesTo([F.Jpg], Converters));
        Assert.True(smallJpg.AppliesTo([F.Jpg, F.Png, F.Heic], Converters));
        Assert.False(smallJpg.AppliesTo([F.Jpg, F.Mp3], Converters));
        Assert.False(smallJpg.AppliesTo([], Converters));

        var hd = new Favourite { Format = "mp4", LongestSide = 1280 };
        Assert.True(hd.AppliesTo([F.Mp4, F.Mov, F.Mkv], Converters));
        Assert.False(hd.AppliesTo([F.Mp3], Converters));

        var podcast = new Favourite { Format = "mp3", Quality = 96 };
        Assert.True(podcast.AppliesTo([F.Mp3, F.Wav, F.Mp4], Converters));

        Assert.False(new Favourite { Format = "nope" }.AppliesTo([F.Png], Converters));
    }

    [Fact]
    public void Petals_and_settings_name_them_by_what_they_do()
    {
        var web = new Favourite { Format = "webp", LongestSide = 1600, Quality = 80 };
        Assert.Equal("WEBP 1600", FavouriteText.Label(web));
        Assert.Equal("WEBP · 1600 px · quality 80", FavouriteText.Describe(web));

        var hd = new Favourite { Format = "mp4", LongestSide = 1280, Quality = 28 };
        Assert.Equal("MP4 720p", FavouriteText.Label(hd));
        Assert.Equal("MP4 · 720p · Small quality", FavouriteText.Describe(hd));

        Assert.Equal("MP3 128k", FavouriteText.Label(new Favourite { Format = "mp3", Quality = 128 }));
        Assert.Equal("JPG 70%", FavouriteText.Label(new Favourite { Format = "jpg", Quality = 70 }));
        Assert.Equal("PNG", FavouriteText.Label(new Favourite { Format = "png" }));

        var named = new Favourite { Format = "jpg", LongestSide = 1024, Name = " Email " };
        Assert.Equal("Email", FavouriteText.Label(named));
        Assert.Equal("Email", FavouriteText.Suffix(named));
        Assert.Equal("1600", FavouriteText.Suffix(new Favourite { Format = "jpg", LongestSide = 1600 }));
        Assert.Equal("a-b", FavouriteText.Suffix(new Favourite { Format = "jpg", Name = "a/b" }));
    }

    [Fact]
    public void Kept_in_the_settings_file_without_derived_values()
    {
        var settings = new AppSettings();
        settings.Favourites.Add(new Favourite { Format = "webp", LongestSide = 1600, Quality = 80, Name = "Web" });
        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("\"Target\"", json);
        Assert.DoesNotContain("MaxLongEdge", json);

        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        var favourite = Assert.Single(back.Favourites);
        Assert.Equal(("webp", 1600, 80, "Web"), (favourite.Format, favourite.LongestSide, favourite.Quality, favourite.Name));
        Assert.Equal(settings.Favourites[0].Id, favourite.Id);
    }

    [Fact]
    public void Every_listed_target_is_a_format_Petal_can_write()
    {
        Assert.Contains(F.Webp, FavouriteText.Targets);
        Assert.Contains(F.Mp4, FavouriteText.Targets);
        Assert.Contains(F.Mp3, FavouriteText.Targets);
        Assert.All(FavouriteText.Targets, f => Assert.True(f.CanWrite));
        Assert.DoesNotContain(F.Svg, FavouriteText.Targets);
    }
}
