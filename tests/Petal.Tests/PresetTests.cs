using Petal.App.Jobs;
using Petal.App.Settings;
using Petal.Core.Jobs;
using F = Petal.Core.Formats.Formats;

namespace Petal.Tests;

/// <summary>
/// Favourites as presets: the resolution, metadata, folder and naming they carry, and favourites shared as a
/// .petalpreset file or a line of text, checked before they are added, and the gallery.
/// </summary>
[Collection(PdfEditorWindowTests.OneApplication)]
public sealed class PresetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-presets-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime Today = new(2026, 11, 23);

    [Theory]
    [InlineData("{name} {date}", "holiday 2026-11-23")]
    [InlineData("{DATE} {Name}", "2026-11-23 holiday")]
    [InlineData("{name} {preset}", "holiday Web")]
    [InlineData("Trip - {name}", "Trip - holiday")]
    [InlineData("{name} {unknown}", "holiday {unknown}")]
    [InlineData("a/b:c*{name}", "a-b-c-holiday")]
    [InlineData("  ", null)]
    [InlineData("...", null)]
    [InlineData("", null)]
    public void A_name_pattern_makes_the_result_s_name(string pattern, string? expected) =>
        Assert.Equal(expected, PresetNaming.Stem(pattern, @"C:\photos\holiday.jpg", "Web", Today));

    [Fact]
    public void A_shared_pattern_is_tidied()
    {
        Assert.Null(PresetNaming.Tidy(null));
        Assert.Null(PresetNaming.Tidy(" <>|"));
        Assert.Equal("{name} x", PresetNaming.Tidy("  {name}   x  "));
        Assert.Equal(PresetNaming.MostLength, PresetNaming.Tidy(new string('a', 500))!.Length);
    }

    [Fact]
    public void A_favourite_s_resolution_and_metadata_go_into_the_conversion()
    {
        var png = new Favourite { Format = "png", Dpi = 150, RemoveMetadata = true }.Apply(ConversionSettings.Default);
        Assert.Equal(150, png.RenderDpi);
        Assert.False(png.KeepMetadata);
        // A PDF has no picture metadata to leave out, and isn't drawn at a resolution.
        var pdf = new Favourite { Format = "pdf", Dpi = 150, RemoveMetadata = true }.Apply(ConversionSettings.Default);
        Assert.Equal(ConversionSettings.Default.RenderDpi, pdf.RenderDpi);
        Assert.True(pdf.KeepMetadata);
        Assert.True(new Favourite { Format = "mp3", RemoveMetadata = true }.ChangesMoreThanFormat);
        Assert.False(new Favourite { Format = "pdf", RemoveMetadata = true }.ChangesMoreThanFormat);
        // The defaults never lose metadata.
        Assert.True(new Favourite { Format = "jpg" }.Apply(ConversionSettings.Default).KeepMetadata);
        Assert.True(ConversionSettings.Default.KeepMetadata);
    }

    [Fact]
    public void A_favourite_s_results_go_to_its_folder_under_its_names()
    {
        var input = Path.Combine(_dir, "holiday.jpg");
        File.WriteAllText(input, "x");
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "Exports")).FullName;
        var favourite = new Favourite { Format = "webp", Name = "Web", Folder = folder, NamePattern = "{name} {preset}" };
        Assert.Equal(folder, favourite.FolderIfThere);
        var output = JobRunner.FavouriteOutput(input, F.Jpg, favourite, favourite.FolderIfThere);
        Assert.Equal(Path.Combine(folder, "holiday Web.webp"), output);
        // Without a pattern: DropWheel's usual name; a folder that has gone is ignored.
        favourite.NamePattern = null;
        Assert.Equal(Path.Combine(_dir, "holiday.webp"), JobRunner.FavouriteOutput(input, F.Jpg, favourite, _dir));
        favourite.Folder = Path.Combine(_dir, "gone");
        Assert.Null(favourite.FolderIfThere);
        // Saved again in its own format under a pattern, the original is never the result.
        var same = new Favourite { Format = "jpg", LongestSide = 800, NamePattern = "{name}" };
        Assert.Equal(Path.Combine(_dir, "holiday 2.jpg"), JobRunner.FavouriteOutput(input, F.Jpg, same, _dir));
    }

    [Fact]
    public void A_shared_favourite_comes_back_whole_with_a_new_id()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "Out")).FullName;
        var mine = new Favourite { Name = "Web", Format = "webp", LongestSide = 1600, Quality = 80, Dpi = 150, RemoveMetadata = true, Folder = folder, NamePattern = "{name} {date}" };
        var code = PresetSharing.ToCode([mine]);
        Assert.StartsWith(PresetSharing.CodePrefix, code, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", code, StringComparison.Ordinal);
        Assert.DoesNotContain(mine.Id.ToString(), PresetSharing.ToJson([mine]), StringComparison.OrdinalIgnoreCase);
        foreach (var text in new[] { code, "  " + code.ToLowerInvariant()[..13] + code[13..] + "\r\n", PresetSharing.ToJson([mine]) })
        {
            var (list, problem) = PresetSharing.Read(text, []);
            Assert.Null(problem);
            var item = Assert.Single(list!);
            Assert.Equal(PresetSharing.Verdict.Add, item.Verdict);
            var f = item.Favourite;
            Assert.NotEqual(mine.Id, f.Id);
            Assert.NotEqual(Guid.Empty, f.Id);
            Assert.Equal(("Web", "webp", 1600, 80, 150, true, folder, "{name} {date}"),
                (f.Name, f.Format, f.LongestSide, f.Quality, f.Dpi, f.RemoveMetadata, f.Folder, f.NamePattern));
        }
    }

    [Fact]
    public void An_incoming_favourite_says_what_adding_it_does()
    {
        var have = new Favourite { Name = "Mine", Format = "jpg", LongestSide = 1280, Quality = 80 };
        var file = PresetSharing.ToJson(
        [
            new Favourite { Name = "Same settings", Format = "jpg", LongestSide = 1280, Quality = 80 },
            new Favourite { Name = "Elsewhere", Format = "png", Folder = @"Z:\nowhere\on\this\pc" },
            new Favourite { Name = "Odd", Format = "xyz" },
            new Favourite { Name = new string('n', 40), Format = "webp", LongestSide = 999_999, Quality = -5, Dpi = 5, NamePattern = "<{name}>" },
            new Favourite { Name = "Twice", Format = "webp", LongestSide = 999_999, Quality = -5, Dpi = 5, NamePattern = "{name}" },
        ]);
        var (list, problem) = PresetSharing.Read(file, [have]);
        Assert.Null(problem);
        Assert.Equal([PresetSharing.Verdict.AlreadyThere, PresetSharing.Verdict.AddWithoutFolder, PresetSharing.Verdict.CantSave, PresetSharing.Verdict.Add, PresetSharing.Verdict.AlreadyThere],
            list!.Select(i => i.Verdict));
        Assert.Equal(@"Z:\nowhere\on\this\pc", list[1].Folder);
        Assert.Null(list[1].Favourite.Folder);
        var clamped = list[3].Favourite;
        Assert.Equal(24, clamped.Name.Length);
        Assert.Equal((16384, 0, 36, "{name}"), (clamped.LongestSide, clamped.Quality, clamped.Dpi, clamped.NamePattern));
    }

    [Fact]
    public void Anything_that_isnt_a_favourite_is_turned_away()
    {
        Assert.Equal("This isn't a DropWheel favourite.", PresetSharing.Read("hello", []).Problem);
        Assert.Equal("This isn't a DropWheel favourite.", PresetSharing.Read(PresetSharing.CodePrefix + "!!!", []).Problem);
        Assert.Equal("This isn't a DropWheel favourite.", PresetSharing.Read("{\"petalPreset\":2,\"favourites\":[{\"format\":\"jpg\"}]}", []).Problem);
        Assert.Equal("This isn't a DropWheel favourite.", PresetSharing.Read("{\"petalPreset\":1,\"favourites\":[]}", []).Problem);
        Assert.Equal("This favourite is too large.", PresetSharing.Read("{" + new string(' ', 300_000) + "}", []).Problem);
        // A wheel is not a favourite.
        var wheel = Petal.App.Wheels.WheelSharing.ToCode(Petal.App.Wheels.WheelTemplates.Work());
        Assert.NotNull(PresetSharing.Read(wheel, []).Problem);
        // A file of many is cut to the most there can be in one.
        var many = PresetSharing.ToJson(Enumerable.Range(1, 40).Select(i => new Favourite { Format = "jpg", LongestSide = i * 10 }));
        Assert.Equal(PresetSharing.MostInFile, PresetSharing.Read(many, []).Favourites!.Count);
    }

    [Fact]
    public void Every_ready_made_favourite_can_be_saved_and_says_what_it_is_for()
    {
        Assert.NotEmpty(PresetSharing.Gallery);
        foreach (var ready in PresetSharing.Gallery)
        {
            Assert.True(PresetSharing.CanSave(ready.Favourite), ready.Favourite.Name);
            Assert.False(string.IsNullOrWhiteSpace(ready.Description));
            Assert.True(Petal.App.Visuals.Icons.Keys.Contains(ready.Icon), ready.Icon);
            var made = PresetSharing.Make(ready);
            Assert.NotEqual(ready.Favourite.Id, made.Id);
            Assert.InRange(made.Name.Length, 1, 24);
        }
        // No two do the same thing.
        var all = PresetSharing.Gallery.Select(r => r.Favourite).ToList();
        Assert.All(all, (f, i) => Assert.DoesNotContain(all.Take(i), o => PresetSharing.Same(o, f)));
    }

    /// <summary>Runs with the user's favourites put aside and the settings folder in a scratch place.</summary>
    private void WithFavourites(IEnumerable<Favourite> start, Action test) => PdfEditorWindowTests.RunOnInterface(() =>
    {
        var favourites = SettingsStore.Current.Favourites;
        var kept = favourites.ToList();
        var store = SettingsStore.Directory;
        SettingsStore.Directory = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(SettingsStore.Directory);
        favourites.Clear();
        favourites.AddRange(start);
        try
        {
            test();
        }
        finally
        {
            favourites.Clear();
            favourites.AddRange(kept);
            SettingsStore.Directory = store;
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void Import_shows_each_favourite_and_adds_only_what_has_room() => WithFavourites(
        Enumerable.Range(1, FavouriteText.Most - 2).Select(i => new Favourite { Format = "jpg", LongestSide = i * 100 }), () =>
    {
        int added = 0;
        var window = new ImportPresetsWindow(() => added++, "");
        var text = PresetSharing.ToCode(
        [
            new Favourite { Format = "jpg", LongestSide = 100 },
            new Favourite { Format = "webp", LongestSide = 1600 },
            new Favourite { Format = "png", Dpi = 300 },
            new Favourite { Format = "mp3", Quality = 128 },
        ]);
        window.CheckForTest(text);
        Assert.Equal(2, window.AddsForTest);
        Assert.Equal(["webp", "png"], window.ToAddForTest.Select(f => f.Format));
        window.AddForTest();
        Assert.Equal(1, added);
        Assert.Equal(FavouriteText.Most, SettingsStore.Current.Favourites.Count);

        var again = new ImportPresetsWindow(() => added++, text);
        Assert.Equal(0, again.AddsForTest);
        again.CheckForTest("not a favourite");
        Assert.Equal(0, again.AddsForTest);
        again.Close();
    });

    [Fact]
    public void The_gallery_adds_ready_made_favourites_and_its_folder_s_files() => WithFavourites([], () =>
    {
        Directory.CreateDirectory(PresetSharing.GalleryFolder);
        File.WriteAllText(Path.Combine(PresetSharing.GalleryFolder, "From a friend" + PresetSharing.Extension),
            PresetSharing.ToJson([new Favourite { Name = "Friend's", Format = "avif", LongestSide = 2048 }]));
        File.WriteAllText(Path.Combine(PresetSharing.GalleryFolder, "broken" + PresetSharing.Extension), "{ not json");
        int added = 0;
        var window = new PresetGalleryWindow(() => added++);
        var cards = window.CardsForTest;
        Assert.Equal(PresetSharing.Gallery.Count(r => PresetSharing.CanSave(r.Favourite)) + 1, cards.Count);
        Assert.Equal("Friend's", cards[^1].Label);
        Assert.All(cards, c => Assert.True(c.CanAdd));
        window.AddForTest(cards.Count - 1);
        Assert.Equal(1, added);
        Assert.Equal("avif", Assert.Single(SettingsStore.Current.Favourites).Format);
        Assert.False(window.CardsForTest[^1].CanAdd);
        for (int i = 0; SettingsStore.Current.Favourites.Count < FavouriteText.Most; i++)
            window.AddForTest(i);
        Assert.All(window.CardsForTest, c => Assert.False(c.CanAdd));
        window.Close();
    });
}
