using System.Globalization;
using System.IO;
using ImageMagick;
using Petal.App.Localization;
using Petal.Core.Jobs;
using Petal.Engines.Documents.Pdf;
using Petal.Engines.Files.Sharing;
using Petal.Engines.Imaging;

namespace Petal.App.Actions;

/// <summary>
/// The PC's half of the phone scanner: photos arrive, the page in each is found and straightened,
/// and the pages become one searchable PDF. Everything in between lives in a private temporary
/// folder that goes when the session ends.
/// </summary>
internal sealed class ScanSession : IDisposable
{
    private const int MaxPages = 200;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "Petal", "scan", Guid.NewGuid().ToString("N")[..10]);
    private readonly Lock _state = new();
    // Straightening and saving take a lot of memory: one at a time.
    private readonly SemaphoreSlim _heavy = new(1, 1);
    private readonly HashSet<int> _photos = [];
    private readonly List<int> _pages = [];
    private int _next;

    public ScanSession(string saveFolder)
    {
        SaveFolder = saveFolder;
        Directory.CreateDirectory(_work);
    }

    public string SaveFolder { get; set; }

    /// <summary>Raised (on a worker thread) with the number of pages waiting to be saved.</summary>
    public event Action<int>? PagesChanged;

    /// <summary>Raised (on a worker thread) with the PDF just saved.</summary>
    public event Action<string>? Saved;

    public int PageCount
    {
        get
        {
            lock (_state)
                return _pages.Count;
        }
    }

    public async Task<LanApiResponse> HandleAsync(LanApiRequest request, CancellationToken ct) => (request.Method, request.Path) switch
    {
        ("POST", "photo") => await PhotoAsync(request, ct),
        ("POST", "page") => await PageAsync(request, ct),
        ("GET", "thumb") => Thumb(request),
        ("POST", "remove") => Remove(request),
        ("POST", "finish") => await FinishAsync(ct),
        ("GET", "state") => State(),
        _ => LanApiResponse.NotFound,
    };

    private string PhotoPath(int id) => Path.Combine(_work, $"photo-{id}.jpg");
    private string PagePath(int id) => Path.Combine(_work, $"page-{id}.jpg");
    private string ThumbPath(int id) => Path.Combine(_work, $"thumb-{id}.jpg");

    private static LanApiResponse Bad() => LanApiResponse.Json("{\"ok\":false}", 400);

    private async Task<LanApiResponse> PhotoAsync(LanApiRequest request, CancellationToken ct)
    {
        if (request.Length == 0)
            return Bad();
        int id = Interlocked.Increment(ref _next);
        var path = PhotoPath(id);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await request.CopyBodyToAsync(file, ct);
        PageCorners corners;
        try
        {
            corners = await DocumentScanner.FindPageAsync(path, ct);
        }
        catch (Exception ex) when (ex is MagickException or InvalidOperationException)
        {
            File.Delete(path);
            return Bad();
        }
        lock (_state)
            _photos.Add(id);
        var points = string.Join(",", corners.Points.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture)));
        return LanApiResponse.Json($"{{\"ok\":true,\"id\":{id},\"corners\":[{points}]}}");
    }

    private async Task<LanApiResponse> PageAsync(LanApiRequest request, CancellationToken ct)
    {
        if (!int.TryParse(request.Query.GetValueOrDefault("id"), NumberStyles.None, CultureInfo.InvariantCulture, out int photo))
            return Bad();
        var values = (request.Query.GetValueOrDefault("c") ?? "").Split(',');
        var points = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out points[i]))
                return Bad();
        }
        var corners = new PageCorners(points);
        var look = request.Query.GetValueOrDefault("look") switch
        {
            "color" => ScanLook.Color,
            "gray" => ScanLook.Gray,
            "bw" => ScanLook.BlackAndWhite,
            _ => ScanLook.Document,
        };
        lock (_state)
        {
            if (!_photos.Contains(photo) || !corners.IsValid || _pages.Count >= MaxPages)
                return Bad();
        }

        int page = Interlocked.Increment(ref _next);
        await _heavy.WaitAsync(ct);
        try
        {
            await DocumentScanner.FlattenAsync(PhotoPath(photo), corners, look, PagePath(page), ct);
            using var thumb = new MagickImage(PagePath(page));
            thumb.Thumbnail(new MagickGeometry(168, 232));
            thumb.Quality = 80;
            await thumb.WriteAsync(ThumbPath(page), ct);
        }
        finally
        {
            _heavy.Release();
        }
        int count;
        lock (_state)
        {
            _pages.Add(page);
            count = _pages.Count;
        }
        PagesChanged?.Invoke(count);
        return LanApiResponse.Json($"{{\"ok\":true,\"page\":{page}}}");
    }

    private LanApiResponse Thumb(LanApiRequest request)
    {
        if (!int.TryParse(request.Query.GetValueOrDefault("page"), NumberStyles.None, CultureInfo.InvariantCulture, out int page))
            return Bad();
        lock (_state)
        {
            if (!_pages.Contains(page))
                return LanApiResponse.NotFound;
        }
        return new LanApiResponse(200, "image/jpeg", File.ReadAllBytes(ThumbPath(page)));
    }

    private LanApiResponse Remove(LanApiRequest request)
    {
        if (!int.TryParse(request.Query.GetValueOrDefault("page"), NumberStyles.None, CultureInfo.InvariantCulture, out int page))
            return Bad();
        int count;
        lock (_state)
        {
            if (!_pages.Remove(page))
                return LanApiResponse.NotFound;
            count = _pages.Count;
        }
        TryDelete(PagePath(page));
        TryDelete(ThumbPath(page));
        PagesChanged?.Invoke(count);
        return LanApiResponse.Json("{\"ok\":true}");
    }

    private LanApiResponse State()
    {
        lock (_state)
            return LanApiResponse.Json("{\"ok\":true,\"pages\":[" + string.Join(",", _pages) + "]}");
    }

    /// <summary>The pages so far as one PDF in <see cref="SaveFolder"/>, made searchable when Windows can read the text.</summary>
    private async Task<LanApiResponse> FinishAsync(CancellationToken ct)
    {
        List<int> pages;
        lock (_state)
            pages = _pages.ToList();
        if (pages.Count == 0)
            return Bad();
        await _heavy.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(SaveFolder);
            // File names stay in English, like the other files Petal makes.
            var final = OutputNaming.Unique(SaveFolder, string.Create(CultureInfo.InvariantCulture, $"Scan {DateTime.Now:yyyy-MM-dd HH.mm}"), "pdf");
            var combined = Path.Combine(_work, $"combined-{Guid.NewGuid():N}.pdf");
            await Petal.Engines.Imaging.PdfCreator.CreateAsync(pages.Select(PagePath).ToList(), combined, new PdfCreateOptions(), null, ct);
            bool searchable = false;
            if (SearchablePdf.IsAvailable)
            {
                try
                {
                    var result = await SearchablePdf.MakeAsync(combined, final, null, null, ct);
                    searchable = result.Written && result.Words > 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // The scan matters more than its text layer.
                    Log.Error(ex, "Reading the text of a phone scan");
                }
            }
            if (!File.Exists(final))
                File.Copy(combined, final);
            TryDelete(combined);
            int left;
            lock (_state)
            {
                _pages.RemoveAll(pages.Contains);
                left = _pages.Count;
            }
            foreach (var page in pages)
            {
                TryDelete(PagePath(page));
                TryDelete(ThumbPath(page));
            }
            PagesChanged?.Invoke(left);
            Saved?.Invoke(final);
            var name = Path.GetFileName(final).Replace("\\", "\\\\").Replace("\"", "\\\"");
            return LanApiResponse.Json($"{{\"ok\":true,\"name\":\"{name}\",\"pages\":{pages.Count},\"searchable\":{(searchable ? "true" : "false")}}}");
        }
        finally
        {
            _heavy.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        _heavy.Dispose();
    }
}
