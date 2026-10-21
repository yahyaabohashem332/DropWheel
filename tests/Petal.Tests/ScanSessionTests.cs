using System.Net;
using System.Net.Http;
using System.Text.Json;
using ImageMagick;
using ImageMagick.Drawing;
using Petal.App.Actions;
using Petal.Engines.Files.Sharing;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>The phone scanner end to end over HTTP, as the phone's page talks to it.</summary>
public sealed class ScanSessionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-scan-session-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A page of text photographed on a dark desk, as the phone would send it.</summary>
    internal static byte[] PhonePhoto(string title = "PHONE SCAN SESSION")
    {
        using var page = new MagickImage(new MagickColor("#FBFAF6"), 1240, 1754);
        new Drawables().Font("Arial").FillColor(new MagickColor("#1E1E1E")).FontPointSize(60).Text(110, 220, title)
            .FontPointSize(40).Text(110, 380, "Pages become one searchable PDF.").Draw(page);
        using var photo = new MagickImage(new MagickColor("#3B3F46"), 2400, 3200);
        page.VirtualPixelMethod = VirtualPixelMethod.Transparent;
        page.Alpha(AlphaAction.Set);
        page.Distort(new DistortSettings(DistortMethod.Perspective) { Viewport = new MagickGeometry(0, 0, 2400, 3200) },
            [0, 0, 420, 380, 1239, 0, 2010, 460, 1239, 1753, 2150, 2850, 0, 1753, 300, 2760]);
        photo.Composite(page, 0, 0, CompositeOperator.Over);
        photo.Format = MagickFormat.Jpeg;
        photo.Quality = 90;
        return photo.ToByteArray();
    }

    [Fact]
    public async Task A_photo_becomes_a_page_and_the_pages_a_searchable_pdf()
    {
        var saveIn = Path.Combine(_dir, "Scans");
        using var session = new ScanSession(saveIn);
        string? saved = null;
        session.Saved += p => saved = p;
        await using var share = LanShare.Start([], IPAddress.Loopback);
        share.CustomPage = () => ScanPage.Html(new ScanPageText(), "TEST-PC");
        share.Api = session.HandleAsync;
        using var http = new HttpClient { BaseAddress = new Uri(share.Url) };

        var page = await http.GetStringAsync("");
        Assert.Contains("api/photo", page);
        Assert.Contains("Scan to TEST-PC", page);

        // The phone sends the photo; the PC answers with where it thinks the page is.
        var bytes = PhonePhoto();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var photo = await http.PostAsync("api/photo", new ByteArrayContent(bytes));
        output.WriteLine($"photo: {clock.ElapsedMilliseconds} ms");
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        using var found = JsonDocument.Parse(await photo.Content.ReadAsStringAsync());
        int id = found.RootElement.GetProperty("id").GetInt32();
        var corners = found.RootElement.GetProperty("corners").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        output.WriteLine(string.Join(", ", corners.Select(c => c.ToString("0.000"))));
        double[] expected = [420 / 2400.0, 380 / 3200.0, 2010 / 2400.0, 460 / 3200.0, 2150 / 2400.0, 2850 / 3200.0, 300 / 2400.0, 2760 / 3200.0];
        for (int i = 0; i < 8; i++)
            Assert.InRange(corners[i], expected[i] - 0.015, expected[i] + 0.015);

        var c = string.Join(",", corners.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        clock.Restart();
        var added = await http.PostAsync($"api/page?id={id}&look=document&c={c}", null);
        output.WriteLine($"page: {clock.ElapsedMilliseconds} ms");
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        int pageId = JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("page").GetInt32();
        var thumb = await http.GetByteArrayAsync($"api/thumb?page={pageId}");
        using (var image = new MagickImage(thumb))
            Assert.True(image.Width <= 168 && image.Height <= 232);
        Assert.Equal(1, session.PageCount);

        // A second photo, added then taken away again.
        var second = JsonDocument.Parse(await (await http.PostAsync("api/photo", new ByteArrayContent(PhonePhoto("SECOND PAGE")))).Content.ReadAsStringAsync());
        var secondPage = JsonDocument.Parse(await (await http.PostAsync($"api/page?id={second.RootElement.GetProperty("id").GetInt32()}&look=bw&c={c}", null)).Content.ReadAsStringAsync());
        Assert.Equal(2, session.PageCount);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"api/remove?page={secondPage.RootElement.GetProperty("page").GetInt32()}", null)).StatusCode);
        Assert.Contains($"[{pageId}]", await http.GetStringAsync("api/state"));

        // Nonsense is turned away.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("api/page?id=999&c=0,0,1,0,1,1,0,1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("api/photo", new ByteArrayContent("not a picture"u8.ToArray()))).StatusCode);

        clock.Restart();
        var finished = await http.PostAsync("api/finish", null);
        output.WriteLine($"finish: {clock.ElapsedMilliseconds} ms");
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        var result = JsonDocument.Parse(await finished.Content.ReadAsStringAsync()).RootElement;
        output.WriteLine(result.ToString());
        Assert.Equal(1, result.GetProperty("pages").GetInt32());
        Assert.NotNull(saved);
        Assert.Equal(Path.Combine(saveIn, result.GetProperty("name").GetString()!), saved);
        Assert.Equal(0, session.PageCount);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(saved);
        Assert.Equal(1, pdf.NumberOfPages);
        // A4-wide, and its text can be searched.
        Assert.InRange(pdf.GetPage(1).Width, 580, 610);
        if (Petal.Engines.Documents.Pdf.SearchablePdf.IsAvailable)
        {
            Assert.True(result.GetProperty("searchable").GetBoolean());
            var text = string.Join(" ", pdf.GetPage(1).GetWords().Select(w => w.Text));
            output.WriteLine(text);
            Assert.Contains("searchable", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// Not a test: with PETAL_SCAN_SERVE set to a file path, serves the scanner page on this PC's
/// loopback address and writes its address there, until that file is deleted (for driving the
/// phone page in a browser by hand).
/// </summary>
public sealed class ScanServeHarness
{
    [Fact]
    public async Task Serve()
    {
        var file = Environment.GetEnvironmentVariable("PETAL_SCAN_SERVE");
        if (file is null)
            return;
        var dir = Path.Combine(Path.GetDirectoryName(file)!, "scans");
        using var session = new ScanSession(dir);
        await using var share = LanShare.Start([], IPAddress.Loopback);
        share.CustomPage = () => ScanPage.Html(new ScanPageText(), "TEST-PC");
        share.Api = session.HandleAsync;
        File.WriteAllText(file, share.Url);
        for (int i = 0; i < 900 && File.Exists(file); i++)
            await Task.Delay(1000);
    }
}
