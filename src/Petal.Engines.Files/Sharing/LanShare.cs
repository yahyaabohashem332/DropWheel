using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Petal.Engines.Files.Sharing;

/// <summary>A file or folder offered to the phone.</summary>
public sealed record SharedItem(string Path, string Name, long Size, bool IsFolder);

/// <summary>Text of the phone's page, in the user's language.</summary>
public sealed record SharePageText
{
    public string Language { get; init; } = "en";
    public bool RightToLeft { get; init; }
    public string Title { get; init; } = "Files from {0}";
    public string Subtitle { get; init; } = "Tap a file to save it on this phone.";
    public string Folder { get; init; } = "Folder · downloads as ZIP";
    public string All { get; init; } = "Download everything as ZIP";
    public string SendTitle { get; init; } = "Send to this PC";
    public string SendHint { get; init; } = "Files you choose are saved in {0} on the PC.";
    public string Choose { get; init; } = "Choose files";
    public string Sending { get; init; } = "Sending…";
    public string Sent { get; init; } = "Sent";
    public string Failed { get; init; } = "Didn't arrive";
    public string Footer { get; init; } = "Straight from the PC over Wi-Fi. Nothing goes through the internet.";

    /// <summary>How sizes are written ("2.2 MB"), so the page matches the app.</summary>
    public Func<long, string>? FormatSize { get; init; }
}

public sealed record ShareActivity(ShareActivityKind Kind, string Name);

/// <summary>A request to the extra endpoints under "api/", such as the phone scanner's.</summary>
public sealed class LanApiRequest
{
    private readonly Func<Memory<byte>, CancellationToken, ValueTask<int>> _read;

    internal LanApiRequest(string method, string path, IReadOnlyDictionary<string, string> query, long length, Func<Memory<byte>, CancellationToken, ValueTask<int>> read)
    {
        Method = method;
        Path = path;
        Query = query;
        Length = length;
        _read = read;
    }

    public string Method { get; }

    /// <summary>What follows "api/", e.g. "photo".</summary>
    public string Path { get; }

    public IReadOnlyDictionary<string, string> Query { get; }

    /// <summary>Body size in bytes (0 without one).</summary>
    public long Length { get; }

    internal long Consumed { get; private set; }

    /// <summary>Copies the whole body into <paramref name="destination"/>.</summary>
    public async Task CopyBodyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        while (Consumed < Length)
        {
            int read = await _read(buffer.AsMemory(0, (int)Math.Min(buffer.Length, Length - Consumed)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("The phone stopped sending.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            Consumed += read;
        }
    }
}

public sealed record LanApiResponse(int Status, string ContentType, byte[] Body)
{
    public static LanApiResponse Json(string json, int status = 200) => new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

    public static LanApiResponse NotFound { get; } = Json("{\"ok\":false}", 404);
}

public enum ShareActivityKind
{
    /// <summary>A phone opened the page.</summary>
    Opened,
    /// <summary>A download finished.</summary>
    Downloaded,
    /// <summary>A file sent from the phone was saved; <see cref="ShareActivity.Name"/> is its path.</summary>
    Received,
}

/// <summary>
/// Hands files to a phone on the same network: a small web server on this PC's local address,
/// reachable only under a random secret path, for as long as it runs. Downloads support ranges;
/// folders (and "everything") are zipped on the fly. Uploads from the phone are off unless allowed.
/// </summary>
public sealed class LanShare : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const long MaxUploadBytes = 16L * 1024 * 1024 * 1024;
    private static readonly TimeSpan IdleConnection = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _token;
    private readonly string _host;
    private readonly SharePageText _text;
    private readonly Task _accepting;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    private LanShare(IReadOnlyList<SharedItem> items, IPAddress address, SharePageText text, string computerName)
    {
        Items = items;
        _text = text;
        ComputerName = computerName;
        _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        _listener = new TcpListener(address, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
        Url = $"http://{_host}/{_token}/";
        _accepting = AcceptAsync();
    }

    /// <summary>Starts sharing <paramref name="paths"/> on <paramref name="address"/> (normally <see cref="LocalAddress"/>).</summary>
    public static LanShare Start(IReadOnlyList<string> paths, IPAddress address, SharePageText? text = null, string? computerName = null)
    {
        var items = paths
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Select(p => Directory.Exists(p)
                ? new SharedItem(p, System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(p)), FolderSize(p), true)
                : new SharedItem(p, System.IO.Path.GetFileName(p), new FileInfo(p).Length, false))
            .ToList();
        return new LanShare(items, address, text ?? new SharePageText(), computerName ?? Environment.MachineName);
    }

    public IReadOnlyList<SharedItem> Items { get; }

    /// <summary>The address to open on the phone, secret path included.</summary>
    public string Url { get; }

    public string ComputerName { get; }

    /// <summary>When the phone may send files to this PC, and where they are saved.</summary>
    public string? UploadFolder { get; set; }

    /// <summary>Answers requests under "api/"; null turns them away.</summary>
    public Func<LanApiRequest, CancellationToken, Task<LanApiResponse>>? Api { get; set; }

    /// <summary>The largest body an "api/" request may carry.</summary>
    public long MaxApiBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Shown on the phone instead of the list of files, e.g. the scanner.</summary>
    public Func<string>? CustomPage { get; set; }

    public DateTime LastActivity => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    public event Action<ShareActivity>? Activity;

    /// <summary>
    /// This PC's address on the local network: an IPv4 address of a connected adapter with a
    /// gateway, preferring Wi-Fi and Ethernet over virtual adapters. Null when offline.
    /// </summary>
    public static IPAddress? LocalAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (Adapter: n, Props: n.GetIPProperties()))
                .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(x => x.Props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address) && !IsLinkLocal(u.Address))
                    .Select(u => (x.Adapter, u.Address)))
                .OrderBy(x => IsVirtual(x.Adapter) ? 1 : 0)
                .ThenBy(x => x.Adapter.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => 0,
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 1,
                    _ => 2,
                })
                .ThenBy(x => IsPrivate(x.Address) ? 0 : 1)
                .Select(x => x.Address)
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool IsVirtual(NetworkInterface adapter)
    {
        var text = adapter.Description + " " + adapter.Name;
        return new[] { "virtual", "hyper-v", "vmware", "virtualbox", "vethernet", "wsl", "bluetooth", "loopback", "tap-", "wireguard", "vpn" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLinkLocal(IPAddress address) => address.GetAddressBytes() is [169, 254, ..];

    private static bool IsPrivate(IPAddress address) => address.GetAddressBytes() switch
    {
        [10, ..] => true,
        [172, var b, ..] when b is >= 16 and <= 31 => true,
        [192, 168, ..] => true,
        _ => false,
    };

    private static long FolderSize(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new HeadReader(stream);
            try
            {
                // Browsers keep connections open for the next request.
                while (!_stop.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    idle.CancelAfter(IdleConnection);
                    var request = await reader.ReadAsync(idle.Token).ConfigureAwait(false);
                    if (request is null)
                        return;
                    Touch();
                    bool keepOpen = await RespondAsync(request, reader, stream, _stop.Token).ConfigureAwait(false);
                    if (!keepOpen || request.Headers.GetValueOrDefault("connection")?.Equals("close", StringComparison.OrdinalIgnoreCase) == true)
                        return;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
            {
                // The phone went away or sent something we don't speak; nothing to clean up.
            }
        }
    }

    /// <returns>False when the connection must close afterwards.</returns>
    private async Task<bool> RespondAsync(Request request, HeadReader reader, NetworkStream stream, CancellationToken ct)
    {
        // Only answer requests addressed to us by IP (no DNS rebinding through another name).
        if (!string.Equals(request.Headers.GetValueOrDefault("host"), _host, StringComparison.OrdinalIgnoreCase))
            return await SendTextAsync(stream, 400, "Bad Request", "text/plain", "", ct);

        var path = request.Path;
        var prefix = "/" + _token;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return await SendTextAsync(stream, 404, "Not Found", "text/plain", "", ct);
        var rest = path[prefix.Length..];
        bool head = request.Method == "HEAD";

        if (rest == "" && request.Method is "GET" or "HEAD")
            return await SendAsync(stream, 301, "Moved Permanently", [("Location", prefix + "/")], [], ct);

        if (rest == "/" && request.Method is "GET" or "HEAD")
        {
            Activity?.Invoke(new ShareActivity(ShareActivityKind.Opened, ""));
            return await SendTextAsync(stream, 200, "OK", "text/html; charset=utf-8", CustomPage?.Invoke() ?? Page(), ct, head);
        }

        if (rest.StartsWith("/files/", StringComparison.Ordinal) && request.Method is "GET" or "HEAD"
            && int.TryParse(rest["/files/".Length..].Split('/')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index >= 0 && index < Items.Count)
        {
            var item = Items[index];
            if (item.IsFolder)
                return await SendZipAsync(stream, item.Name + ".zip", [item], head, ct);
            return await SendFileAsync(stream, request, item, head, ct);
        }

        if (rest.StartsWith("/all.zip", StringComparison.Ordinal) && request.Method is "GET" or "HEAD" && Items.Count > 0)
            return await SendZipAsync(stream, ZipName(), Items, head, ct);

        if (rest.StartsWith("/upload", StringComparison.Ordinal) && request.Method == "POST")
            return await ReceiveAsync(request, reader, stream, ct);

        if (rest.StartsWith("/api/", StringComparison.Ordinal) && request.Method is "GET" or "POST" && Api is { } api)
            return await ApiAsync(api, request, reader, stream, rest["/api/".Length..], ct);

        return await SendTextAsync(stream, 404, "Not Found", "text/plain", "", ct);
    }

    private async Task<bool> ApiAsync(Func<LanApiRequest, CancellationToken, Task<LanApiResponse>> api, Request request, HeadReader reader, NetworkStream stream, string path, CancellationToken ct)
    {
        long length = 0;
        if (request.Headers.TryGetValue("content-length", out var header) && !long.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out length))
            return await SendTextAsync(stream, 400, "Bad Request", "application/json", "{\"ok\":false}", ct, keepAlive: false);
        if (length > MaxApiBytes)
            return await SendTextAsync(stream, 413, "Content Too Large", "application/json", "{\"ok\":false}", ct, keepAlive: false);
        var call = new LanApiRequest(request.Method, path, request.Query, length, async (into, token) =>
        {
            int read = await reader.ReadBodyAsync(into, token).ConfigureAwait(false);
            Touch();
            return read;
        });
        LanApiResponse response;
        try
        {
            response = await api(call, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            response = LanApiResponse.Json($"{{\"ok\":false,\"error\":\"{JsonEscape(ex.Message)}\"}}", 500);
        }
        Touch();
        // A body left unread would be taken for the next request: close the connection instead.
        bool keepAlive = call.Consumed == call.Length;
        return await SendAsync(stream, response.Status, response.Status switch { 200 => "OK", 404 => "Not Found", 400 => "Bad Request", _ => "Error" },
            [("Content-Type", response.ContentType)], response.Body, ct, keepAlive: keepAlive);
    }

    private string ZipName()
    {
        var name = string.Concat(ComputerName.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        return $"{(name.Length > 0 ? name : "Petal")} files.zip";
    }

    private async Task<bool> SendFileAsync(NetworkStream stream, Request request, SharedItem item, bool head, CancellationToken ct)
    {
        FileStream file;
        try
        {
            file = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await SendTextAsync(stream, 410, "Gone", "text/plain", "", ct);
        }
        await using (file)
        {
            long length = file.Length, start = 0, end = length - 1;
            int status = 200;
            string reason = "OK";
            var headers = new List<(string, string)>
            {
                ("Content-Type", ContentType(item.Name)),
                ("Content-Disposition", Disposition(item.Name)),
                ("Accept-Ranges", "bytes"),
            };
            if (request.Headers.GetValueOrDefault("range") is { } range && ParseRange(range, length) is { } r)
            {
                (start, end) = r;
                status = 206;
                reason = "Partial Content";
                headers.Add(("Content-Range", $"bytes {start}-{end}/{length}"));
            }
            else if (request.Headers.ContainsKey("range"))
            {
                return await SendAsync(stream, 416, "Range Not Satisfiable", [("Content-Range", $"bytes */{length}")], [], ct);
            }
            long count = Math.Max(0, end - start + 1);
            headers.Add(("Content-Length", count.ToString(CultureInfo.InvariantCulture)));
            await WriteHeadAsync(stream, status, reason, headers, keepAlive: true, ct);
            if (head)
                return true;
            file.Position = start;
            var buffer = new byte[1 << 16];
            long left = count;
            while (left > 0)
            {
                int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                left -= read;
                Touch();
            }
            if (end == length - 1)
                Activity?.Invoke(new ShareActivity(ShareActivityKind.Downloaded, item.Name));
            return left == 0;
        }
    }

    /// <summary>"bytes=100-199", "bytes=500-" or "bytes=-200"; one range only.</summary>
    internal static (long Start, long End)? ParseRange(string header, long length)
    {
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',') || length <= 0)
            return null;
        var spec = header[6..].Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0)
            return null;
        var a = spec[..dash].Trim();
        var b = spec[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix == 0)
                return null;
            return (Math.Max(0, length - suffix), length - 1);
        }
        if (!long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out long start) || start >= length)
            return null;
        long end = length - 1;
        if (b.Length > 0 && (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start))
            return null;
        return (start, Math.Min(end, length - 1));
    }

    /// <summary>Streams a ZIP made on the fly; the length isn't known, so the connection closes after it.</summary>
    private async Task<bool> SendZipAsync(NetworkStream stream, string name, IReadOnlyList<SharedItem> items, bool head, CancellationToken ct)
    {
        await WriteHeadAsync(stream, 200, "OK", [("Content-Type", "application/zip"), ("Content-Disposition", Disposition(name))], keepAlive: false, ct);
        if (head)
            return false;
        var output = new ActivityStream(stream, Touch);
        await using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var files = item.IsFolder
                    ? Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories)
                        .Select(f => (File: f, Entry: item.Name + "/" + System.IO.Path.GetRelativePath(item.Path, f).Replace('\\', '/')))
                    : [(item.Path, item.Name)];
                foreach (var (file, entryName) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var unique = entryName;
                    for (int n = 2; !used.Add(unique); n++)
                        unique = System.IO.Path.ChangeExtension(entryName, null) + $" {n}" + System.IO.Path.GetExtension(entryName);
                    try
                    {
                        await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
                        var entry = zip.CreateEntry(unique, IsCompressed(file) ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                        entry.LastWriteTime = File.GetLastWriteTime(file);
                        await using var target = await entry.OpenAsync(ct).ConfigureAwait(false);
                        await source.CopyToAsync(target, 1 << 16, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException && ex.InnerException is not SocketException)
                    {
                        // A file that can't be read (locked, removed) is left out rather than ending the ZIP;
                        // a broken connection (a socket error) still ends it.
                    }
                }
            }
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
        Activity?.Invoke(new ShareActivity(ShareActivityKind.Downloaded, name));
        return false;
    }

    private static bool IsCompressed(string file) => System.IO.Path.GetExtension(file).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" or ".heif" or ".avif" or ".gif" or ".mp4" or ".mov" or ".mkv" or ".webm"
        or ".m4a" or ".mp3" or ".aac" or ".ogg" or ".opus" or ".flac" or ".zip" or ".7z" or ".rar" or ".gz" or ".docx" or ".xlsx" or ".pptx" or ".pdf";

    /// <summary>One file from the phone, sent as the raw request body with its name in the query.</summary>
    private async Task<bool> ReceiveAsync(Request request, HeadReader reader, NetworkStream stream, CancellationToken ct)
    {
        if (UploadFolder is not { } folder || !Directory.Exists(folder))
            return await SendTextAsync(stream, 403, "Forbidden", "application/json", "{\"ok\":false}", ct, keepAlive: false);
        if (!long.TryParse(request.Headers.GetValueOrDefault("content-length"), NumberStyles.None, CultureInfo.InvariantCulture, out long length))
            return await SendTextAsync(stream, 411, "Length Required", "application/json", "{\"ok\":false}", ct, keepAlive: false);
        if (length > MaxUploadBytes)
            return await SendTextAsync(stream, 413, "Content Too Large", "application/json", "{\"ok\":false}", ct, keepAlive: false);

        var name = SafeName(request.Query.GetValueOrDefault("name"));
        var final = UniquePath(folder, name);
        var partial = final + ".part";
        bool complete = false;
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long left = length;
                while (left > 0)
                {
                    int read = await reader.ReadBodyAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    left -= read;
                    Touch();
                }
                complete = left == 0;
            }
            if (!complete)
                return false;
            // Someone may have taken the name meanwhile; never overwrite.
            var stem = System.IO.Path.GetFileNameWithoutExtension(final);
            var ext = System.IO.Path.GetExtension(final);
            for (int n = 2; ; n++)
            {
                try
                {
                    File.Move(partial, final);
                    break;
                }
                catch (IOException) when (File.Exists(final) || Directory.Exists(final))
                {
                    final = System.IO.Path.Combine(folder, $"{stem} {n}{ext}");
                }
            }
            Activity?.Invoke(new ShareActivity(ShareActivityKind.Received, final));
            var json = $"{{\"ok\":true,\"name\":\"{JsonEscape(System.IO.Path.GetFileName(final))}\"}}";
            return await SendTextAsync(stream, 200, "OK", "application/json", json, ct);
        }
        finally
        {
            if (!complete)
                TryDelete(partial);
        }
    }

    /// <summary>Only a plain file name survives: no folders, no reserved names, nothing hidden.</summary>
    internal static string SafeName(string? name)
    {
        name = System.IO.Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "");
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim().TrimStart('.').TrimEnd('.', ' ');
        if (name.Length > 150)
        {
            var ext = System.IO.Path.GetExtension(name);
            if (ext.Length > 16)
                ext = "";
            name = name[..(150 - ext.Length)] + ext;
        }
        var stem = System.IO.Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (name.Length == 0 || stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
            name = "From phone" + System.IO.Path.GetExtension(name);
        return name;
    }

    private static string UniquePath(string folder, string name)
    {
        var path = System.IO.Path.Combine(folder, name);
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var ext = System.IO.Path.GetExtension(name);
        for (int n = 2; File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".part"); n++)
            path = System.IO.Path.Combine(folder, $"{stem} {n}{ext}");
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------- HTTP plumbing ----------

    private static Task<bool> SendTextAsync(NetworkStream stream, int status, string reason, string type, string body, CancellationToken ct, bool head = false, bool keepAlive = true)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return SendAsync(stream, status, reason, [("Content-Type", type)], head ? [] : bytes, ct, bytes.Length, keepAlive);
    }

    private static async Task<bool> SendAsync(NetworkStream stream, int status, string reason, List<(string, string)> headers, byte[] body, CancellationToken ct, long? length = null, bool keepAlive = true)
    {
        headers.Add(("Content-Length", (length ?? body.Length).ToString(CultureInfo.InvariantCulture)));
        await WriteHeadAsync(stream, status, reason, headers, keepAlive, ct);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        return keepAlive;
    }

    private static async Task WriteHeadAsync(NetworkStream stream, int status, string reason, List<(string Name, string Value)> headers, bool keepAlive, CancellationToken ct)
    {
        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n");
        foreach (var (name, value) in headers)
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("X-Content-Type-Options: nosniff\r\n");
        head.Append("Referrer-Policy: no-referrer\r\n");
        head.Append("X-Frame-Options: DENY\r\n");
        head.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
    }

    /// <summary>attachment; filename="photo.jpg"; filename*=UTF-8''photo.jpg — the second form carries any language.</summary>
    internal static string Disposition(string name)
    {
        var ascii = new string(name.Select(c => c is >= ' ' and < (char)127 && c != '"' && c != '\\' ? c : '_').ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    private static string ContentType(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".heic" or ".heif" => "image/heic",
        ".avif" => "image/avif",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case < ' ': sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Html(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Decimal units, like the rest of Petal; isolated so "77 KB" keeps its order on a right-to-left page.</summary>
    private string Size(long bytes) => "<bdi>" + Html(_text.FormatSize?.Invoke(bytes) ?? bytes switch
    {
        < 1000 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1000 * 1000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1000.0:0} KB"),
        < 1000L * 1000 * 1000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.##} GB"),
    }) + "</bdi>";

    /// <summary>The phone's page: the files, and the upload box when allowed.</summary>
    internal string Page()
    {
        var t = _text;
        var rows = new StringBuilder();
        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            var detail = item.IsFolder ? $"{Html(t.Folder)} · {Size(item.Size)}" : Size(item.Size);
            var ext = item.IsFolder ? "ZIP" : Html(System.IO.Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant());
            rows.Append(CultureInfo.InvariantCulture,
                $"<a class=\"item\" href=\"files/{i}/{Uri.EscapeDataString(item.IsFolder ? item.Name + ".zip" : item.Name)}\" download><span class=\"badge\">{(ext.Length is > 0 and <= 5 ? ext : "FILE")}</span><span class=\"text\"><b dir=\"auto\">{Html(item.Name)}</b><small>{detail}</small></span></a>\n");
        }
        var all = Items.Count > 1 ? $"<a class=\"all\" href=\"all.zip\" download>{Html(t.All)} · {Size(Items.Sum(x => x.Size))}</a>" : "";
        var upload = UploadFolder is { } folder
            ? $$"""
              <section class="send">
                <h2>{{Html(t.SendTitle)}}</h2>
                <p>{{Html(string.Format(CultureInfo.InvariantCulture, t.SendHint, System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder))))}}</p>
                <label class="choose">{{Html(t.Choose)}}<input id="pick" type="file" multiple></label>
                <div id="sent"></div>
              </section>
              <script>
              const pick = document.getElementById('pick'), list = document.getElementById('sent');
              const words = { sending: {{Js(t.Sending)}}, sent: {{Js(t.Sent)}}, failed: {{Js(t.Failed)}} };
              pick.addEventListener('change', async () => {
                for (const file of pick.files) {
                  const row = document.createElement('div'); row.className = 'up';
                  const name = document.createElement('b'); name.dir = 'auto'; name.textContent = file.name;
                  const state = document.createElement('small'); state.textContent = words.sending;
                  const bar = document.createElement('i');
                  row.append(name, state, bar); list.prepend(row);
                  await new Promise(done => {
                    const xhr = new XMLHttpRequest();
                    xhr.open('POST', 'upload?name=' + encodeURIComponent(file.name));
                    xhr.upload.onprogress = e => { if (e.lengthComputable) bar.style.width = (100 * e.loaded / e.total) + '%'; };
                    xhr.onload = () => { const ok = xhr.status === 200; state.textContent = ok ? words.sent : words.failed; row.classList.add(ok ? 'ok' : 'bad'); done(); };
                    xhr.onerror = () => { state.textContent = words.failed; row.classList.add('bad'); done(); };
                    xhr.send(file);
                  });
                }
                pick.value = '';
              });
              </script>
              """
            : "";
        return $$"""
            <!doctype html>
            <html lang="{{Html(t.Language)}}" dir="{{(t.RightToLeft ? "rtl" : "ltr")}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
            <meta name="referrer" content="no-referrer">
            <title>{{Html(string.Format(CultureInfo.InvariantCulture, t.Title, ComputerName))}}</title>
            <style>
            :root { color-scheme: light dark; --bg: #f5f5f7; --card: #fff; --text: #16161a; --muted: #6b6b75; --line: #e4e4ea; --accent: #e8590c; --ok: #2b8a3e; --bad: #c92a2a; }
            @media (prefers-color-scheme: dark) { :root { --bg: #141417; --card: #1f1f24; --text: #f2f2f5; --muted: #a0a0aa; --line: #2e2e35; --accent: #ff8a3d; --ok: #69db7c; --bad: #ff8787; } }
            * { box-sizing: border-box; }
            body { margin: 0; background: var(--bg); color: var(--text); font: 16px/1.4 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; padding: max(16px, env(safe-area-inset-top)) 16px max(24px, env(safe-area-inset-bottom)); }
            main { max-width: 560px; margin: 0 auto; }
            h1 { font-size: 22px; margin: 8px 0 2px; }
            h2 { font-size: 17px; margin: 0 0 4px; }
            p, small { color: var(--muted); }
            p { margin: 0 0 16px; }
            .list { background: var(--card); border: 1px solid var(--line); border-radius: 14px; overflow: hidden; }
            .item { display: flex; gap: 12px; align-items: center; padding: 14px; color: inherit; text-decoration: none; border-top: 1px solid var(--line); }
            .item:first-child { border-top: 0; }
            .item:active { background: var(--line); }
            .badge { flex: none; width: 46px; height: 46px; border-radius: 10px; display: grid; place-items: center; background: var(--accent); color: #fff; font-weight: 700; font-size: 12px; letter-spacing: .02em; }
            .text { min-width: 0; display: flex; flex-direction: column; }
            .text b { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            .all, .choose { display: block; text-align: center; margin: 14px 0 0; padding: 13px; border-radius: 12px; background: var(--accent); color: #fff; font-weight: 600; text-decoration: none; }
            .send { margin-top: 28px; background: var(--card); border: 1px solid var(--line); border-radius: 14px; padding: 16px; }
            .send p { margin: 0; }
            .choose input { display: none; }
            .up { position: relative; display: flex; flex-direction: column; padding: 10px 0; border-top: 1px solid var(--line); margin-top: 10px; }
            .up i { position: absolute; left: 0; bottom: -1px; height: 2px; width: 0; background: var(--accent); transition: width .2s; }
            .up.ok small { color: var(--ok); } .up.bad small { color: var(--bad); } .up.ok i { background: var(--ok); }
            footer { margin-top: 24px; text-align: center; font-size: 13px; color: var(--muted); }
            </style>
            </head>
            <body>
            <main>
            <h1 dir="auto">{{Html(string.Format(CultureInfo.InvariantCulture, t.Title, ComputerName))}}</h1>
            <p>{{Html(t.Subtitle)}}</p>
            <div class="list">
            {{rows}}</div>
            {{all}}
            {{upload}}
            <footer>{{Html(t.Footer)}}</footer>
            </main>
            </body>
            </html>
            """;
    }

    private static string Js(string s) => "\"" + JsonEscape(s).Replace("<", "\\u003c") + "\"";

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
            return;
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        _stop.Dispose();
    }

    // ---------- request parsing ----------

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers);

    /// <summary>Reads request heads from a connection, keeping any body bytes that came along.</summary>
    private sealed class HeadReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[MaxHeaderBytes];
        private int _start, _end;

        public async Task<Request?> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                int found = IndexOfHeadEnd();
                if (found >= 0)
                {
                    var text = Encoding.Latin1.GetString(_buffer, _start, found - _start);
                    _start = found + 4;
                    return Parse(text);
                }
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                if (_end == _buffer.Length)
                    throw new InvalidDataException("Request head too large");
                int read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
                if (read == 0)
                    return null;
                _end += read;
            }
        }

        /// <summary>Body bytes: first what was read along with the head, then from the connection.</summary>
        public async ValueTask<int> ReadBodyAsync(Memory<byte> into, CancellationToken ct)
        {
            if (_end > _start)
            {
                int n = Math.Min(into.Length, _end - _start);
                _buffer.AsMemory(_start, n).CopyTo(into);
                _start += n;
                return n;
            }
            return await stream.ReadAsync(into, ct).ConfigureAwait(false);
        }

        private int IndexOfHeadEnd()
        {
            for (int i = _start; i + 3 < _end; i++)
            {
                if (_buffer[i] == '\r' && _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        private static Request Parse(string head)
        {
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                throw new InvalidDataException("Not an HTTP/1.x request");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            var target = parts[1];
            int q = target.IndexOf('?');
            var path = q >= 0 ? target[..q] : target;
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            if (q >= 0)
            {
                foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    var key = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
                    var value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                    query[key] = value;
                }
            }
            return new Request(parts[0].ToUpperInvariant(), path, query, headers);
        }
    }

    /// <summary>A write-only pass-through that notes activity while a ZIP streams out.</summary>
    private sealed class ActivityStream(Stream inner, Action touch) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            touch();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            touch();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
