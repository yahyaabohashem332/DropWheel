using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using Petal.App.Shell;

namespace Petal.Tests;

/// <summary>
/// A link dragged from a browser or a chat: the address is read from what the drag offers, the file's name
/// comes from the address or the server, and the file is downloaded into the drag's own folder.
/// </summary>
public sealed class LinkDropTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "petal-link-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("https://example.com/files/report.pdf", true)]
    [InlineData("http://example.com/a.png?w=300#top", true)]
    [InlineData("  https://example.com/x.mp4\r\n", true)]
    [InlineData("https://example.com/x.mp4\0garbage", true)]
    [InlineData("ftp://example.com/file.zip", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(@"C:\Users\me\photo.jpg", false)]
    [InlineData("see https://example.com/a.pdf here", false)]
    [InlineData("https://", false)]
    [InlineData("", false)]
    public void Only_one_web_address_is_a_link(string text, bool link)
    {
        Assert.Equal(link, LinkDrop.Address(text) is not null);
    }

    [Fact]
    public void The_address_is_read_from_what_browsers_offer()
    {
        OnSta(() =>
        {
            var chrome = new DataObject();
            chrome.SetData("UniformResourceLocatorW", new MemoryStream(Encoding.Unicode.GetBytes("https://example.com/a.pdf\0")));
            Assert.Equal("https://example.com/a.pdf", LinkDrop.LinkOf(chrome)?.AbsoluteUri);

            var firefox = new DataObject();
            firefox.SetData("text/x-moz-url", new MemoryStream(Encoding.Unicode.GetBytes("https://example.com/b.jpg\nA picture")));
            Assert.Equal("https://example.com/b.jpg", LinkDrop.LinkOf(firefox)?.AbsoluteUri);

            var text = new DataObject(DataFormats.UnicodeText, "https://example.com/c.mp3");
            Assert.Equal("https://example.com/c.mp3", LinkDrop.LinkOf(text)?.AbsoluteUri);

            Assert.Null(LinkDrop.LinkOf(new DataObject(DataFormats.UnicodeText, "just some words")));
            Assert.Null(LinkDrop.LinkOf(new DataObject(DataFormats.UnicodeText, "file:///C:/secret.txt")));
            return 0;
        });
    }

    [Theory]
    [InlineData("https://example.com/files/report.pdf", "application/pdf", null, "report.pdf")]
    [InlineData("https://example.com/download.php?id=3", "application/pdf", "Annual Report 2025.pdf", "Annual Report 2025.pdf")]
    // Sites hand the same picture out as WEBP or AVIF: the type the server gave wins for pictures, videos and sounds.
    [InlineData("https://cdn.example.com/photo.jpg", "image/webp", null, "photo.webp")]
    // A Word file is a zip inside; the name stays.
    [InlineData("https://example.com/letter.docx", "application/zip", null, "letter.docx")]
    [InlineData("https://www.example.com/", "text/html; charset=utf-8", null, "example.com.html")]
    [InlineData("https://example.com/a/My%20Song", "audio/mpeg", null, "My Song.mp3")]
    [InlineData("https://example.com/get", "application/pdf", @"..\..\Windows\x.pdf", "x.pdf")]
    [InlineData("https://example.com/thing", "application/octet-stream", null, "thing")]
    public void The_name_comes_from_the_server_then_the_address_then_the_type(string address, string? type, string? given, string expected)
    {
        Assert.Equal(expected, LinkDrop.NameFor(new Uri(address), type, given));
    }

    [Fact]
    public void Only_an_address_that_names_a_known_format_names_the_file_before_the_server_answers()
    {
        Assert.Equal("a.png", LinkDrop.NameFromAddress(new Uri("https://example.com/i/a.png?w=3")));
        Assert.Equal("Trip day 1.mp4", LinkDrop.NameFromAddress(new Uri("https://example.com/Trip%20day%201.mp4")));
        Assert.Null(LinkDrop.NameFromAddress(new Uri("https://example.com/watch?v=3")));
        Assert.Null(LinkDrop.NameFromAddress(new Uri("https://example.com/")));
    }

    [Fact]
    public async Task A_file_comes_down_whole_under_the_server_s_name()
    {
        var body = new byte[300_000];
        new Random(4).NextBytes(body);
        using var server = new TinyServer(path => path switch
        {
            "/a.pdf" => Response("200 OK", "application/pdf", body),
            "/get?id=1" => Response("200 OK", "application/pdf", body, "Content-Disposition: attachment; filename=\"Report 2025.pdf\""),
            _ => Response("404 Not Found", "text/plain", "no"u8.ToArray()),
        });

        var plain = new LinkDownload(server.At("/a.pdf"), _folder);
        Assert.Equal("a.pdf", plain.Name);
        Assert.False(File.Exists(plain.Path));
        plain.Start();
        var saved = await plain.Done.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(Path.Combine(_folder, "a.pdf"), saved);
        Assert.Equal(body, File.ReadAllBytes(saved));
        Assert.Equal(body.Length, plain.Received);
        Assert.Equal(body.Length, plain.Total);
        Assert.True(plain.IsDone);

        var named = new LinkDownload(server.At("/get?id=1"), _folder);
        Assert.Null(named.Name);
        int renamed = 0;
        named.Named += () => renamed++;
        named.Start();
        Assert.EndsWith("Report 2025.pdf", await named.Done.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, renamed);
        // Nothing half written is left beside them.
        Assert.Equal(["a.pdf", "Report 2025.pdf"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task What_goes_wrong_is_told_and_leaves_nothing_behind()
    {
        using var server = new TinyServer(path => path switch
        {
            "/missing.pdf" => Response("404 Not Found", "text/plain", "no"u8.ToArray()),
            "/huge.mp4" => Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: 5000000000\r\nConnection: close\r\n\r\n"),
            "/cut.pdf" => [.. Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/pdf\r\nContent-Length: 1000\r\nConnection: close\r\n\r\n"), .. new byte[10]],
            "/quiet.pdf" => null,
            _ => Response("404 Not Found", "text/plain", "no"u8.ToArray()),
        });
        var stalled = LinkDownload.Stalled;
        LinkDownload.Stalled = TimeSpan.FromSeconds(1);
        try
        {
            Assert.Equal((LinkDownloadProblem.Refused, 404), await Problem("/missing.pdf"));
            Assert.Equal((LinkDownloadProblem.TooLarge, 0), await Problem("/huge.mp4"));
            Assert.Equal((LinkDownloadProblem.Cut, 0), await Problem("/cut.pdf"));
            Assert.Equal((LinkDownloadProblem.Stalled, 0), await Problem("/quiet.pdf"));
        }
        finally
        {
            LinkDownload.Stalled = stalled;
        }

        // Stopped from the card, or the wheel went away: cancelled, and the half file goes.
        var stopped = new LinkDownload(server.At("/quiet.pdf"), _folder);
        stopped.Start();
        await Task.Delay(300);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.Done.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(!Directory.Exists(_folder) || Directory.GetFiles(_folder).Length == 0);

        // No server there at all.
        var nowhere = new LinkDownload(new Uri($"http://127.0.0.1:{TinyServer.FreePort()}/a.pdf"), _folder);
        nowhere.Start();
        var ex = await Assert.ThrowsAsync<LinkDownloadException>(() => nowhere.Done.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(LinkDownloadProblem.Unreachable, ex.Problem);

        async Task<(LinkDownloadProblem, int)> Problem(string path)
        {
            var link = new LinkDownload(server.At(path), _folder);
            link.Start();
            var ex = await Assert.ThrowsAsync<LinkDownloadException>(() => link.Done.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.True(!Directory.Exists(_folder) || Directory.GetFiles(_folder).Length == 0);
            return (ex.Problem, ex.Status);
        }
    }

    private static byte[] Response(string status, string type, byte[] body, string? extra = null) =>
    [
        .. Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n{(extra is null ? "" : extra + "\r\n")}Connection: close\r\n\r\n"),
        .. body,
    ];

    private static void OnSta(Func<int> work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException(error.ToString());
    }

    /// <summary>
    /// A web server of a few lines on this PC only: answers each request with the bytes the test gives for
    /// its path, or (null) with nothing at all, keeping the connection open until the test ends.
    /// </summary>
    private sealed class TinyServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<string, byte[]?> _answer;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<TcpClient> _open = [];

        public TinyServer(Func<string, byte[]?> answer)
        {
            _answer = answer;
            _listener.Start();
            _ = AcceptAsync();
        }

        public Uri At(string path) => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}");

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
                lock (_open)
                    _open.Add(client);
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var request = new StringBuilder();
                var buffer = new byte[4096];
                while (!request.ToString().Contains("\r\n\r\n"))
                {
                    int n = await stream.ReadAsync(buffer, _stop.Token);
                    if (n == 0)
                        return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                var path = request.ToString().Split(' ')[1];
                if (_answer(path) is not { } bytes)
                    return;
                await stream.WriteAsync(bytes, _stop.Token);
                await stream.FlushAsync(_stop.Token);
                client.Close();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            lock (_open)
                foreach (var client in _open)
                    client.Dispose();
        }
    }
}
