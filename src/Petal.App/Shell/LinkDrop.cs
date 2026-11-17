using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Petal.Core.Formats;
using DataObject = System.Windows.IDataObject;

namespace Petal.App.Shell;

/// <summary>
/// A link dragged from a browser, a chat or a document: an address of a file on the web rather than the
/// file. The wheel opens on what the address names, and the file is downloaded while it is open.
/// </summary>
public static class LinkDrop
{
    private const string UrlW = "UniformResourceLocatorW";
    private const string Url = "UniformResourceLocator";
    private const string MozUrl = "text/x-moz-url";

    /// <summary>The longest text taken as an address (a selected word is not one; a pasted essay is not either).</summary>
    private const int MostLength = 8192;

    /// <summary>The web address the drag carries, http or https only; null for anything else.</summary>
    public static Uri? LinkOf(DataObject data)
    {
        try
        {
            foreach (var text in Texts(data))
                if (Address(text) is { } uri)
                    return uri;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or OutOfMemoryException or ArgumentException)
        {
        }
        return null;
    }

    /// <summary>What the drag offers as an address, in the order browsers fill them in.</summary>
    private static IEnumerable<string> Texts(DataObject data)
    {
        if (data.GetDataPresent(UrlW) && data.GetData(UrlW) is MemoryStream wide)
            yield return Encoding.Unicode.GetString(wide.ToArray());
        if (data.GetDataPresent(MozUrl) && data.GetData(MozUrl) is MemoryStream moz)
            yield return Encoding.Unicode.GetString(moz.ToArray()).Split('\n')[0];
        if (data.GetDataPresent(Url) && data.GetData(Url) is MemoryStream narrow)
            yield return Encoding.Default.GetString(narrow.ToArray());
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string unicode)
            yield return unicode;
        else if (data.GetDataPresent(DataFormats.Text) && data.GetData(DataFormats.Text) is string text)
            yield return text;
    }

    /// <summary>The text as one absolute http or https address, or null: words around it, other schemes and file paths are not links.</summary>
    internal static Uri? Address(string text)
    {
        int end = text.IndexOf('\0');
        if (end >= 0)
            text = text[..end];
        text = text.Trim();
        if (text.Length is 0 or > MostLength || text.Any(char.IsWhiteSpace))
            return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            return null;
        return uri;
    }

    /// <summary>
    /// The file's name before anything is downloaded: the address's last part, when it names a format
    /// DropWheel knows; null when only the server can tell (a page, "download.php?id=3").
    /// </summary>
    public static string? NameFromAddress(Uri uri)
    {
        var last = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]);
        var name = Clean(last);
        return name is not null && Formats.FromPath(name) is not null ? name : null;
    }

    /// <summary>
    /// The name the file is saved under, from what the server said: its own name for it first, then the
    /// address's, then a name with the extension its type has. A picture, video or sound takes the type's
    /// extension over the address's when they differ, since sites hand the same address out as WEBP or AVIF.
    /// </summary>
    public static string NameFor(Uri uri, string? contentType, string? serverName)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        var typed = type is null ? null : ExtensionOf(type);
        if (Clean(serverName) is { } given && Formats.FromPath(given) is not null)
            return given;
        var last = Clean(Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]));
        if (last is not null && Formats.FromPath(last) is { } named)
        {
            bool media = type is not null && (type.StartsWith("image/", StringComparison.Ordinal) || type.StartsWith("video/", StringComparison.Ordinal) || type.StartsWith("audio/", StringComparison.Ordinal));
            if (media && typed is not null && Formats.FromPath("x." + typed) is { } served && served != named)
                return Path.GetFileNameWithoutExtension(last) + "." + typed;
            return last;
        }
        var stem = Clean(serverName) is { } plain ? Path.GetFileNameWithoutExtension(plain)
            : last is not null ? Path.GetFileNameWithoutExtension(last)
            : null;
        if (string.IsNullOrWhiteSpace(stem))
            stem = Clean(uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host) ?? "Download";
        return typed is null ? stem : stem + "." + typed;
    }

    /// <summary>The extension for a content type: the common ones DropWheel knows, then the one Windows has on record.</summary>
    internal static string? ExtensionOf(string type)
    {
        if (ContentTypes.Extension(type) is { } known)
            return known;
        if (type is "application/octet-stream" or "binary/octet-stream" || type.Length > 100 || type.Contains('\\'))
            return null;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"MIME\Database\Content Type\" + type);
            return key?.GetValue("Extension") is string ext && ext.StartsWith('.') && ext.Length > 1 ? ext[1..].ToLowerInvariant() : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A name safe to save under: no folders, no characters Windows refuses, not too long; null when nothing is left.</summary>
    internal static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = Path.GetFileName(name.Replace('/', '\\'));
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c is '_' or '.'))
            return null;
        if (cleaned.Length > 120)
        {
            var ext = Path.GetExtension(cleaned);
            cleaned = cleaned[..(120 - Math.Min(ext.Length, 20))] + (ext.Length <= 20 ? ext : "");
        }
        return cleaned;
    }
}

/// <summary>
/// One link's file coming down while the wheel is open: into the drag's own folder of read files, so the
/// wheel's drop moves it to Downloads like a file read from a drag, and a drag that goes elsewhere drops it.
/// </summary>
public sealed class LinkDownload
{
    /// <summary>Larger than this is refused before anything is written.</summary>
    public const long MostBytes = 4_000_000_000;

    /// <summary>A download that receives nothing for this long stops.</summary>
    public static TimeSpan Stalled { get; set; } = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = MakeClient();

    private readonly CancellationTokenSource _cancel = new();
    private readonly string _folder;
    private readonly SynchronizationContext? _context;
    private readonly TaskCompletionSource<string> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _received;
    private long _startedAt;

    public LinkDownload(Uri address, string folder)
    {
        Address = address;
        _folder = folder;
        _context = SynchronizationContext.Current;
        Name = LinkDrop.NameFromAddress(address);
    }

    public Uri Address { get; }

    /// <summary>The drag's own folder under <see cref="VirtualDrop.StagingRoot"/>.</summary>
    public string Folder => _folder;

    /// <summary>The name it will be saved under; null until the address or the server says what it is.</summary>
    public string? Name { get; private set; }

    /// <summary>Where it will be once whole (nothing is there before).</summary>
    public string? Path => Name is null ? null : System.IO.Path.Combine(_folder, Name);

    public long Received => Interlocked.Read(ref _received);

    /// <summary>The size the server gave; null when it didn't.</summary>
    public long? Total { get; private set; }

    /// <summary>Bytes a second since the first byte.</summary>
    public double Speed
    {
        get
        {
            double seconds = (Environment.TickCount64 - Interlocked.Read(ref _startedAt)) / 1000.0;
            return _startedAt == 0 || seconds < 0.25 ? 0 : Received / seconds;
        }
    }

    public bool IsDone => _done.Task.IsCompletedSuccessfully;

    /// <summary>The saved file's path, or the reason it couldn't be (a <see cref="LinkDownloadException"/>, or cancelled).</summary>
    public Task<string> Done => _done.Task;

    /// <summary>The name changed (the server named or typed it); on the thread that started the download.</summary>
    public event Action? Named;

    /// <summary>More came, or it finished or failed; on the thread that started the download.</summary>
    public event Action? Changed;

    public void Start() => _ = RunAsync();

    /// <summary>A download part way through, for the design snapshots: nothing is fetched.</summary>
    internal static LinkDownload ForSnapshot(Uri address, string folder, long received, long? total, double speed)
    {
        var link = new LinkDownload(address, folder) { Total = total };
        link._received = received;
        link._startedAt = Environment.TickCount64 - (long)(1000 * received / Math.Max(1, speed));
        return link;
    }

    public void Cancel() => _cancel.Cancel();

    private async Task RunAsync()
    {
        string? partial = null;
        // Told only once the half file is gone, so whoever waits finds the folder as it will stay.
        Action finish = () => _done.TrySetException(new LinkDownloadException(LinkDownloadProblem.Unreachable, 0));
        try
        {
            var ct = _cancel.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, Address);
            HttpResponseMessage answered;
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // A server that never answers stops it as one that stops sending does.
                waiting.CancelAfter(Stalled);
                try
                {
                    answered = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, waiting.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new LinkDownloadException(LinkDownloadProblem.Stalled, 0);
                }
            }
            using var response = answered;
            if (!response.IsSuccessStatusCode)
                throw new LinkDownloadException(LinkDownloadProblem.Refused, (int)response.StatusCode);
            var final = response.RequestMessage?.RequestUri ?? Address;
            if (final.Scheme != Uri.UriSchemeHttp && final.Scheme != Uri.UriSchemeHttps)
                throw new LinkDownloadException(LinkDownloadProblem.Refused, 0);
            var headers = response.Content.Headers;
            Total = headers.ContentLength;
            if (Total > MostBytes)
                throw new LinkDownloadException(LinkDownloadProblem.TooLarge, 0);
            var disposition = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"');
            var name = LinkDrop.NameFor(final, headers.ContentType?.ToString(), disposition);
            if (name != Name)
            {
                Name = name;
                Post(() => Named?.Invoke());
            }
            Directory.CreateDirectory(_folder);
            partial = System.IO.Path.Combine(_folder, "~" + Guid.NewGuid().ToString("N") + ".download");
            Interlocked.Exchange(ref _startedAt, Environment.TickCount64);
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long lastReport = 0;
                while (true)
                {
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    quiet.CancelAfter(Stalled);
                    int n;
                    try
                    {
                        n = await input.ReadAsync(buffer, quiet.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new LinkDownloadException(LinkDownloadProblem.Stalled, 0);
                    }
                    if (n == 0)
                        break;
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    long now = Interlocked.Add(ref _received, n);
                    if (now > MostBytes)
                        throw new LinkDownloadException(LinkDownloadProblem.TooLarge, 0);
                    long tick = Environment.TickCount64;
                    if (tick - lastReport >= 100)
                    {
                        lastReport = tick;
                        Post(() => Changed?.Invoke());
                    }
                }
            }
            if (Total is { } expected && Received != expected)
                throw new LinkDownloadException(LinkDownloadProblem.Cut, 0);
            var target = Path!;
            File.Move(partial, target, overwrite: true);
            partial = null;
            finish = () => _done.TrySetResult(target);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            finish = () => _done.TrySetCanceled();
        }
        catch (LinkDownloadException ex)
        {
            finish = () => _done.TrySetException(ex);
        }
        catch (HttpIOException ex) when (ex.HttpRequestError == HttpRequestError.ResponseEnded)
        {
            finish = () => _done.TrySetException(new LinkDownloadException(LinkDownloadProblem.Cut, 0, ex));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            Log.Error($"Downloading a dragged link from {Address.Host}: {ex.GetType().Name}: {ex.Message}");
            finish = () => _done.TrySetException(new LinkDownloadException(LinkDownloadProblem.Unreachable, 0, ex));
        }
        finally
        {
            if (partial is not null)
            {
                try
                {
                    File.Delete(partial);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            finish();
            Post(() => Changed?.Invoke());
        }
    }

    private void Post(Action action)
    {
        if (_context is null)
            action();
        else
            _context.Post(_ => action(), null);
    }

    private static HttpClient MakeClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DropWheel/{UpdateService.CurrentText}");
        return client;
    }
}

public enum LinkDownloadProblem
{
    /// <summary>The server answered with an error (its status in <see cref="LinkDownloadException.Status"/>).</summary>
    Refused,
    TooLarge,
    Stalled,
    /// <summary>Less came than the server said it would send.</summary>
    Cut,
    /// <summary>No answer: no connection, no such server, or the connection broke.</summary>
    Unreachable,
}

public sealed class LinkDownloadException(LinkDownloadProblem problem, int status, Exception? inner = null)
    : Exception(problem.ToString(), inner)
{
    public LinkDownloadProblem Problem { get; } = problem;

    public int Status { get; } = status;
}
