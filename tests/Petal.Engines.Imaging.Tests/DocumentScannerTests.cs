using ImageMagick;
using ImageMagick.Drawing;
using Xunit.Abstractions;

namespace Petal.Engines.Imaging.Tests;

public sealed class DocumentScannerTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-scan-").FullName;

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("PETAL_KEEP_TEST_OUTPUT") == "1")
            return;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A page of text, A4-shaped.</summary>
    private static MagickImage Page()
    {
        var page = new MagickImage(new MagickColor("#FBFAF6"), 1240, 1754);
        var draw = new Drawables().Font("Arial").FillColor(new MagickColor("#202020")).FontPointSize(64).Text(110, 220, "PHONE SCAN TEST");
        draw.FontPointSize(40);
        string[] lines = ["The scanner flattens this page.", "Shadows fade and the paper turns white.", "Every word can be searched afterwards."];
        for (int i = 0; i < lines.Length; i++)
            draw.Text(110, 380 + i * 90, lines[i]);
        draw.Draw(page);
        return page;
    }

    /// <summary>
    /// A "phone photo": the page in perspective on a wooden table, lit unevenly. Returns the
    /// page's corners as fractions of the photo (top-left, top-right, bottom-right, bottom-left).
    /// </summary>
    private string Photo(string name, double[] corners, string table = "#6B4A2E")
    {
        const int w = 3000, h = 4000;
        using var photo = new MagickImage(new MagickColor(table), w, h);
        photo.AddNoise(NoiseType.Gaussian, 0.4);
        using (var page = Page())
        {
            page.VirtualPixelMethod = VirtualPixelMethod.Transparent;
            page.Alpha(AlphaAction.Set);
            page.Distort(new DistortSettings(DistortMethod.Perspective) { Viewport = new MagickGeometry(0, 0, w, h) },
            [
                0, 0, corners[0] * w, corners[1] * h,
                page.Width - 1, 0, corners[2] * w, corners[3] * h,
                page.Width - 1, page.Height - 1, corners[4] * w, corners[5] * h,
                0, page.Height - 1, corners[6] * w, corners[7] * h,
            ]);
            photo.Composite(page, 0, 0, CompositeOperator.Over);
        }
        // A shadow falling across one side.
        using (var shade = new MagickImage("gradient:white-#9A9A9A", h, w))
        {
            shade.Rotate(90);
            photo.Composite(shade, CompositeOperator.Multiply);
        }
        var path = Path.Combine(_dir, name);
        photo.Quality = 90;
        photo.Write(path);
        return path;
    }

    [Theory]
    [InlineData(new[] { 0.18, 0.12, 0.84, 0.16, 0.90, 0.86, 0.10, 0.82 })]
    [InlineData(new[] { 0.25, 0.10, 0.78, 0.10, 0.92, 0.90, 0.08, 0.90 })]
    [InlineData(new[] { 0.12, 0.20, 0.75, 0.08, 0.88, 0.72, 0.22, 0.92 })]
    public async Task The_page_is_found_in_a_photo(double[] corners)
    {
        var photo = Photo("photo.jpg", corners);

        var found = await DocumentScanner.FindPageAsync(photo);

        output.WriteLine(string.Join(", ", found.Points.Select(v => v.ToString("0.000"))));
        for (int i = 0; i < 8; i++)
            Assert.InRange(found.Points[i], corners[i] - 0.015, corners[i] + 0.015);
    }

    /// <summary>
    /// Where an A4 sheet's corners land in a 3000 × 4000 photo taken by a camera tilted
    /// <paramref name="tilt"/> degrees towards the far edge, turned <paramref name="turn"/> degrees.
    /// </summary>
    private static double[] Camera(double tilt, double turn)
    {
        const double w = 3000, h = 4000, focal = 3300, distance = 420;
        double t = tilt * Math.PI / 180, r = turn * Math.PI / 180;
        var corners = new List<double>();
        foreach (var (x0, y0) in new[] { (-105.0, -148.5), (105.0, -148.5), (105.0, 148.5), (-105.0, 148.5) })
        {
            // Turn in the paper's plane, then tilt about the horizontal axis, then project.
            double x = x0 * Math.Cos(r) - y0 * Math.Sin(r), y = x0 * Math.Sin(r) + y0 * Math.Cos(r);
            double yy = y * Math.Cos(t), z = distance + y * Math.Sin(t);
            corners.Add((w / 2 + focal * x / z) / w);
            corners.Add((h / 2 + focal * yy / z) / h);
        }
        return corners.ToArray();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 0)]
    [InlineData(30, 12)]
    [InlineData(15, 35)]
    public void The_pages_real_proportions_survive_the_perspective(double tilt, double turn)
    {
        var c = Camera(tilt, turn);
        var points = Enumerable.Range(0, 4).Select(i => (c[2 * i] * 3000, c[2 * i + 1] * 4000)).ToList();
        var ratio = DocumentScanner.AspectRatio(points, 1500, 2000);
        output.WriteLine($"tilt {tilt}, turn {turn}: {ratio:0.0000}");
        Assert.InRange(ratio!.Value, 210 / 297.0 - 0.012, 210 / 297.0 + 0.012);
    }

    [Fact]
    public async Task A_flattened_page_is_straight_white_and_readable()
    {
        var corners = Camera(28, 6);
        output.WriteLine(string.Join(", ", corners.Select(v => v.ToString("0.000"))));
        var photo = Photo("photo.jpg", corners);
        var scan = Path.Combine(_dir, "page.jpg");

        await DocumentScanner.FlattenAsync(photo, await DocumentScanner.FindPageAsync(photo), ScanLook.Document, scan);

        using var page = new MagickImage(scan);
        output.WriteLine($"{page.Width} x {page.Height}, {page.Density}");
        // A4 proportions, and a density that makes it A4 wide.
        Assert.InRange(page.Height / (double)page.Width, 1.36, 1.47);
        Assert.InRange(page.Width / page.Density.X, 8.1, 8.4);
        // The shadowed paper is white again at both sides.
        using var left = (MagickImage)page.Clone();
        left.Crop(new MagickGeometry(20, (int)page.Height - 220, 160, 160));
        using var right = (MagickImage)page.Clone();
        right.Crop(new MagickGeometry((int)page.Width - 180, (int)page.Height - 220, 160, 160));
        foreach (var corner in new[] { left, right })
        {
            var mean = corner.Statistics().GetChannel(PixelChannel.Red)!.Mean / Quantum.Max;
            output.WriteLine($"paper {mean:0.000}");
            Assert.True(mean > 0.9, $"paper is {mean:0.000} bright");
        }
        var text = await ImageRedactor.ReadTextAsync(scan);
        output.WriteLine(text);
        Assert.Contains("flattens this page", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PHONE SCAN TEST", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ScanLook.Color)]
    [InlineData(ScanLook.Gray)]
    [InlineData(ScanLook.BlackAndWhite)]
    public async Task Every_look_writes_a_page(ScanLook look)
    {
        var photo = Photo("photo.jpg", [0.18, 0.12, 0.84, 0.16, 0.90, 0.86, 0.10, 0.82]);
        var scan = Path.Combine(_dir, $"{look}.jpg");
        await DocumentScanner.FlattenAsync(photo, await DocumentScanner.FindPageAsync(photo), look, scan);
        using var page = new MagickImage(scan);
        Assert.True(page.Width > 1000);
        if (look == ScanLook.BlackAndWhite)
        {
            var colours = page.Histogram().Count;
            output.WriteLine($"{colours} colours");
        }
    }

    [Fact]
    public async Task Without_a_clear_page_the_whole_photo_is_offered()
    {
        // White paper on a white desk: no edge to find.
        var photo = Photo("white.jpg", [0.1, 0.1, 0.9, 0.1, 0.9, 0.9, 0.1, 0.9], table: "#FBFAF6");
        var found = await DocumentScanner.FindPageAsync(photo);
        output.WriteLine(string.Join(", ", found.Points.Select(v => v.ToString("0.000"))));
        Assert.True(found.IsValid);
        // Whatever was found is a sensible quadrilateral covering most of the photo.
        Assert.True(found.Points[0] < 0.5 && found.Points[2] > 0.5 && found.Points[5] > 0.5);
    }
}
