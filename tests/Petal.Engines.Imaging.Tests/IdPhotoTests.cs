using ImageMagick;
using Xunit;

namespace Petal.Engines.Imaging.Tests;

/// <summary>ID photos: the head comes out the size asked for, the print its exact size, and a sheet holds as many as fit.</summary>
public sealed class IdPhotoTests
{
    private static readonly IdPhotoSpec Schengen = new("test-35x45", "Test", "", 35, 45, 32, 36);
    private static readonly IdPhotoSpec Square = new("test-2x2", "Test", "", 50.8, 50.8, 25, 35);
    private static readonly IdPhotoHead Head = new(1000, 500, 1100);

    [Fact]
    public void The_head_comes_out_the_size_asked_for_and_in_the_middle()
    {
        var crop = IdPhoto.Layout(Head, Schengen);
        Assert.Equal(34, IdPhoto.HeadMmIn(Head, crop, Schengen), 3);
        Assert.Equal(1000, crop.X + crop.Width / 2, 3);
        Assert.Equal(35.0 / 45, crop.Width / crop.Height, 5);
        // The crown is below the top, and more room is under the chin than above the hair.
        Assert.True(crop.Y < Head.Crown);
        Assert.True(crop.Bottom - Head.Chin > Head.Crown - crop.Y);
    }

    [Theory]
    [InlineData(1.1, 37.4)]
    [InlineData(0.9, 30.6)]
    public void A_hand_can_make_the_head_larger_or_smaller(double zoom, double mm)
    {
        var crop = IdPhoto.Layout(Head, Schengen, new IdPhotoAdjust(zoom));
        Assert.Equal(mm, IdPhoto.HeadMmIn(Head, crop, Schengen), 3);
    }

    [Fact]
    public void An_office_that_measures_from_the_hairline_gets_the_head_from_there()
    {
        var head = new IdPhotoHead(1000, 500, 1100, 600);
        var over = new IdPhotoSpec("over", "T", "", 35, 45, 32, 36);
        var under = over with { WithoutHair = true };
        Assert.Equal(34, IdPhoto.HeadMmIn(head, IdPhoto.Layout(head, under), under), 3);
        // The same person comes out larger, the hair going above what is measured.
        Assert.True(IdPhoto.Layout(head, under).Height < IdPhoto.Layout(head, over).Height);
    }

    [Fact]
    public void A_size_chosen_by_hand_has_the_head_the_usual_share_of_it()
    {
        var spec = IdPhotoCatalog.Sized(30, 40);
        Assert.Equal((30.0, 40.0, 28.0, 32.0), (spec.WidthMm, spec.HeightMm, spec.HeadMinMm, spec.HeadMaxMm));
        Assert.False(spec.HeadStated);
    }

    [Fact]
    public void A_hand_can_move_the_person()
    {
        var still = IdPhoto.Layout(Head, Schengen);
        var moved = IdPhoto.Layout(Head, Schengen, new IdPhotoAdjust(1, 0.1, 0.05));
        Assert.Equal(still.X - still.Width * 0.1, moved.X, 3);
        Assert.Equal(still.Y - still.Height * 0.05, moved.Y, 3);
    }

    [Fact]
    public void The_crown_is_where_the_outline_begins_above_the_face()
    {
        const int w = 400, h = 600;
        var mask = new byte[w * h];
        for (int y = 90; y < h; y++)
            for (int x = 120; x < 280; x++)
                mask[y * w + x] = 255;
        var head = IdPhoto.FindHead([new PixelRect(135, 130, 130, 130)], w, h, mask);
        Assert.Equal(90, head.Crown);
        Assert.Equal(200, head.CentreX, 1);
        Assert.InRange(head.Chin, 240, 262);
    }

    [Fact]
    public void A_hat_or_raised_hand_isnt_the_crown()
    {
        const int w = 400, h = 600;
        var mask = new byte[w * h];
        Array.Fill(mask, (byte)255);
        var head = IdPhoto.FindHead([new PixelRect(135, 300, 130, 130)], w, h, mask);
        Assert.True(head.Crown > 200, head.Crown.ToString());
    }

    [Fact]
    public void The_largest_face_is_taken_and_without_one_a_place_is_still_given()
    {
        var head = IdPhoto.FindHead([new PixelRect(10, 10, 20, 20), new PixelRect(200, 100, 100, 100)], 400, 600, null);
        Assert.Equal(250, head.CentreX, 1);
        var none = IdPhoto.FindHead([], 400, 600, null);
        Assert.True(none.Height > 0);
    }

    private static IdPhotoSource Source(int width = 2000, int height = 3000, IdPhotoHead? head = null, int faces = 1) => new()
    {
        Image = new MagickImage(new MagickColor(20, 120, 40), (uint)width, (uint)height),
        Head = head ?? Head,
        Faces = faces,
    };

    [Fact]
    public void A_print_is_its_exact_size_at_300_dpi()
    {
        using var source = Source();
        using var print = IdPhoto.Render(source, IdPhoto.Layout(source.Head, Schengen), Schengen, 0xFFFFFF);
        Assert.Equal(413u, print.Width);
        Assert.Equal(531u, print.Height);
        Assert.Equal(300, print.Density.X);
    }

    [Fact]
    public void Beyond_the_picture_is_the_background_asked_for()
    {
        using var source = Source(1000, 1000, new IdPhotoHead(100, 100, 400));
        using var print = IdPhoto.Render(source, IdPhoto.Layout(source.Head, Schengen), Schengen, 0x2040FF);
        var corner = print.GetPixels().GetPixel(0, 0);
        Assert.Equal(0x20, corner.GetChannel(0) >> 0 & 0xFF);
        Assert.Equal(0xFF, corner.GetChannel(2) & 0xFF);
    }

    [Fact]
    public void The_person_stays_and_the_background_goes()
    {
        using var source = new IdPhotoSource
        {
            Image = new MagickImage(new MagickColor(20, 120, 40), 2000, 3000),
            // White on the right half: there is the person.
            Mask = Half(2000, 3000),
            Head = Head,
            Faces = 1,
        };
        using var print = IdPhoto.Render(source, IdPhoto.Layout(source.Head, Schengen), Schengen, 0xFFFFFF);
        var pixels = print.GetPixels();
        Assert.Equal(255, pixels.GetPixel(20, 250).GetChannel(1));
        Assert.Equal(120, pixels.GetPixel(390, 250).GetChannel(1));

        static MagickImage Half(int w, int h)
        {
            var bytes = new byte[w * h];
            for (int y = 0; y < h; y++)
                Array.Fill(bytes, (byte)255, y * w + w / 2, w / 2);
            var mask = new MagickImage(bytes, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "R"));
            mask.ColorType = ColorType.Grayscale;
            return mask;
        }
    }

    [Fact]
    public void What_is_wrong_is_said()
    {
        using var fine = Source();
        Assert.Empty(IdPhoto.Check(fine, IdPhoto.Layout(fine.Head, Schengen), Schengen));
        using var none = Source(faces: 0);
        Assert.Contains(IdPhotoProblem.NoFace, IdPhoto.Check(none, IdPhoto.Layout(none.Head, Schengen), Schengen));
        using var many = Source(faces: 2);
        Assert.Contains(IdPhotoProblem.SeveralFaces, IdPhoto.Check(many, IdPhoto.Layout(many.Head, Schengen), Schengen));
        using var small = Source(200, 300, new IdPhotoHead(100, 60, 120));
        Assert.Contains(IdPhotoProblem.LowResolution, IdPhoto.Check(small, IdPhoto.Layout(small.Head, Schengen), Schengen));
        using var close = Source(2000, 1150);
        Assert.Contains(IdPhotoProblem.TooClose, IdPhoto.Check(close, IdPhoto.Layout(close.Head, Schengen), Schengen));
    }

    [Theory]
    // On 152.4 × 101.6 mm with 1.5 mm margins and 2 mm gaps.
    [InlineData(35, 45, 4, 2)]
    [InlineData(50.8, 50.8, 2, 1)]
    public void A_sheet_holds_as_many_as_fit(double w, double h, int across, int down)
    {
        var (a, d, _) = IdPhoto.Fit(new IdPhotoSpec("t", "T", "", w, h, 1, 2), IdPhotoPaper.Print4By6);
        Assert.Equal(across * down, a * d);
        Assert.True(a * d > 0);
        _ = (across, down);
    }

    [Fact]
    public void A_sheet_is_the_papers_size_and_the_prints_are_on_it()
    {
        using var source = Source();
        using var print = IdPhoto.Render(source, IdPhoto.Layout(source.Head, Schengen), Schengen, 0xFFFFFF);
        using var sheet = IdPhoto.Sheet(print, Schengen, IdPhotoPaper.Print4By6);
        Assert.Equal(1800u, sheet.Width);
        Assert.Equal(1200u, sheet.Height);
        Assert.Equal(300, sheet.Density.X);
        var (across, down, _) = IdPhoto.Fit(Schengen, IdPhotoPaper.Print4By6);
        Assert.Equal(8, across * down);
        // The paper's corner is white; the middle of the first print is the photo's green.
        var pixels = sheet.GetPixels();
        Assert.Equal(255, pixels.GetPixel(2, 2).GetChannel(1));
        int gap = IdPhoto.Pixels(2);
        int left = (1800 - (across * 413 + (across - 1) * gap)) / 2, top = (1200 - (down * 531 + (down - 1) * gap)) / 2;
        Assert.Equal(120, pixels.GetPixel(left + 200, top + 500).GetChannel(1));
    }

    [Fact]
    public void Every_country_has_sensible_numbers_and_a_distinct_name()
    {
        Assert.Equal(IdPhotoCatalog.All.Count, IdPhotoCatalog.All.Select(s => s.Id).Distinct().Count());
        Assert.Contains(IdPhotoCatalog.Default, IdPhotoCatalog.All);
        foreach (var spec in IdPhotoCatalog.All)
        {
            Assert.InRange(spec.WidthMm, 20, 60);
            Assert.InRange(spec.HeightMm, 25, 80);
            Assert.True(spec.HeadMinMm > 0 && spec.HeadMinMm <= spec.HeadMaxMm && spec.HeadMaxMm < spec.HeightMm, spec.Id);
            var fit = IdPhoto.Fit(spec, IdPhotoPaper.Print4By6);
            Assert.True(fit.Across * fit.Down >= 1, spec.Id);
        }
    }
}
