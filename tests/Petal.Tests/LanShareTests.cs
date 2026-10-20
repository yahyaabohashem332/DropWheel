using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Petal.Engines.Files.Sharing;

namespace Petal.Tests;

public class LanShareTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("petal-share-").FullName;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private LanShare _share = null!;
    private string _photo = "", _folder = "", _inbox = "";
    private readonly byte[] _photoBytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    public Task InitializeAsync()
    {
        _photo = Path.Combine(_dir, "صورة 1.jpg");
        File.WriteAllBytes(_photo, _photoBytes);
        _folder = Path.Combine(_dir, "Trip");
        Directory.CreateDirectory(Path.Combine(_folder, "Day 1"));
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "hello");
        File.WriteAllText(Path.Combine(_folder, "Day 1", "plan.txt"), "walk");
        _inbox = Path.Combine(_dir, "Inbox");
        Directory.CreateDirectory(_inbox);
        _share = LanShare.Start([_photo, _folder, Path.Combine(_dir, "missing.txt")], IPAddress.Loopback, computerName: "DESK");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _share.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_page_lists_what_is_shared_under_a_secret_path()
    {
        Assert.Equal(2, _share.Items.Count);
        Assert.StartsWith("http://127.0.0.1:", _share.Url);
        Assert.True(new Uri(_share.Url).AbsolutePath.Length > 20);

        var opened = false;
        _share.Activity += a => opened |= a.Kind == ShareActivityKind.Opened;
        var page = await _http.GetStringAsync(_share.Url);
        Assert.Contains("صورة 1.jpg", page);
        Assert.Contains("Trip", page);
        Assert.Contains("Files from DESK", page);
        Assert.Contains("all.zip", page);
        Assert.DoesNotContain("type=\"file\"", page);
        Assert.True(opened);

        // Without the secret, or by another name, there is nothing to see.
        var root = new Uri(_share.Url).GetLeftPart(UriPartial.Authority);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(root + "/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(root + "/guess/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_share.Url + "files/9/x")).StatusCode);
        var renamed = new HttpRequestMessage(HttpMethod.Get, _share.Url);
        renamed.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(renamed)).StatusCode);
        Assert.Equal(HttpStatusCode.MovedPermanently, (await _http.GetAsync(_share.Url.TrimEnd('/'))).StatusCode);
    }

    [Fact]
    public async Task Files_download_whole_or_in_ranges()
    {
        using var response = await _http.GetAsync(_share.Url + "files/0/x.jpg");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("صورة 1.jpg", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(_photoBytes, await response.Content.ReadAsByteArrayAsync());

        var range = new HttpRequestMessage(HttpMethod.Get, _share.Url + "files/0/x.jpg");
        range.Headers.Range = new RangeHeaderValue(1000, 1999);
        using var part = await _http.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(_photoBytes[1000..2000], await part.Content.ReadAsByteArrayAsync());

        var beyond = new HttpRequestMessage(HttpMethod.Get, _share.Url + "files/0/x.jpg");
        beyond.Headers.Range = new RangeHeaderValue(5_000_000, null);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await _http.SendAsync(beyond)).StatusCode);
    }

    [Fact]
    public async Task Folders_and_everything_come_as_zip_files()
    {
        using (var zip = new ZipArchive(await _http.GetStreamAsync(_share.Url + "files/1/Trip.zip")))
        {
            Assert.Equal(["Trip/Day 1/plan.txt", "Trip/notes.txt"], zip.Entries.Select(e => e.FullName).Order());
        }

        var all = await _http.GetByteArrayAsync(_share.Url + "all.zip");
        using var everything = new ZipArchive(new MemoryStream(all));
        Assert.Equal(3, everything.Entries.Count);
        var photo = everything.GetEntry("صورة 1.jpg")!;
        using var read = new MemoryStream();
        await (await photo.OpenAsync()).CopyToAsync(read);
        Assert.Equal(_photoBytes, read.ToArray());
    }

    [Fact]
    public async Task The_phone_can_send_files_only_when_allowed()
    {
        var body = new ByteArrayContent([1, 2, 3, 4]);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.PostAsync(_share.Url + "upload?name=a.bin", body)).StatusCode);

        _share.UploadFolder = _inbox;
        string? received = null;
        _share.Activity += a => { if (a.Kind == ShareActivityKind.Received) received = a.Name; };
        var page = await _http.GetStringAsync(_share.Url);
        Assert.Contains("type=\"file\"", page);

        var response = await _http.PostAsync(_share.Url + "upload?name=" + Uri.EscapeDataString(@"..\..\Video من الهاتف.mp4"), new ByteArrayContent(_photoBytes));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = Path.Combine(_inbox, "Video من الهاتف.mp4");
        Assert.Equal(_photoBytes, File.ReadAllBytes(saved));
        Assert.Equal(saved, received);

        // Same name again: kept apart, never overwritten.
        await _http.PostAsync(_share.Url + "upload?name=Video%20%D9%85%D9%86%20%D8%A7%D9%84%D9%87%D8%A7%D8%AA%D9%81.mp4", new ByteArrayContent([9]));
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(_inbox, "Video من الهاتف 2.mp4")));
        Assert.Empty(Directory.GetFiles(_inbox, "*.part"));
        Assert.Empty(Directory.GetFiles(_dir, "*.mp4"));
    }

    [Fact]
    public void Shares_on_this_PCs_network_address()
    {
        // Offline machines have none; otherwise it's a real IPv4 address, never loopback or 169.254.x.x.
        if (LanShare.LocalAddress() is not { } address)
            return;
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, address.AddressFamily);
        Assert.False(IPAddress.IsLoopback(address));
        Assert.NotEqual((byte)169, address.GetAddressBytes()[0]);
    }

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData(@"..\..\Windows\evil.dll", "evil.dll")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData(".hidden", "hidden")]
    [InlineData("CON.txt", "From phone.txt")]
    [InlineData("", "From phone")]
    [InlineData("we:ird?.png", "we-ird-.png")]
    public void Names_from_the_phone_are_made_safe(string name, string expected) =>
        Assert.Equal(expected, LanShare.SafeName(name));

    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 99)]
    [InlineData("bytes=900-", 1000, 900, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=500-5000", 1000, 500, 999)]
    public void Ranges(string header, long length, long start, long end) =>
        Assert.Equal((start, end), LanShare.ParseRange(header, length));

    [Theory]
    [InlineData("bytes=1000-", 1000)]
    [InlineData("bytes=5-1", 1000)]
    [InlineData("bytes=0-1,5-6", 1000)]
    [InlineData("items=0-1", 1000)]
    public void Ranges_that_make_no_sense(string header, long length) =>
        Assert.Null(LanShare.ParseRange(header, length));
}
