using System.IO;
using ImageMagick;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Engines.Documents;
using Petal.Engines.Imaging;
using Petal.Engines.Media;

namespace Petal.Recipes.Search;

/// <summary>
/// Fills a <see cref="SearchIndex"/>: finds the files in the chosen folders, then reads them one at
/// a time — the words in documents, what pictures and videos show (and the text in screenshots
/// and photos of pages). The caller decides when: Petal does it while the PC is idle.
/// </summary>
public sealed class SearchIndexer(SearchIndex index, ConverterRegistry converters, ConversionSettings settings)
{
    private const int MaxFiles = 300_000;
    private const long MaxDocumentBytes = 60L * 1024 * 1024;
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", "$RECYCLE.BIN", "System Volume Information", "AppData", ".vs", ".idea", "__pycache__",
    };
    private float[]? _textLike, _photoLike;

    /// <summary>The folders' files (no hidden or system ones, nor program folders), and records them in the index.</summary>
    public int Scan(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var existing = roots.Where(Directory.Exists).ToList();
        var files = new List<FileInfo>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Temporary,
        };
        var folders = new Stack<DirectoryInfo>(existing.Select(r => new DirectoryInfo(r)));
        while (folders.Count > 0 && files.Count < MaxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Pop();
            try
            {
                foreach (var file in folder.EnumerateFiles("*", options))
                {
                    if (file.Length > 0 && !file.Name.EndsWith(".petal-tmp", StringComparison.OrdinalIgnoreCase) && !file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        files.Add(file);
                }
                foreach (var sub in folder.EnumerateDirectories("*", options))
                {
                    if (!SkippedFolders.Contains(sub.Name))
                        folders.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        index.Update(files, existing);
        return files.Count;
    }

    /// <summary>Reads the next waiting file. False when nothing is waiting.</summary>
    public async Task<bool> ReadNextAsync(CancellationToken cancellationToken)
    {
        var next = index.Waiting(1);
        if (next.Count == 0)
            return false;
        var (id, path, kind) = next[0];
        string? body = null;
        float[]? vector = null;
        bool failed = false;
        try
        {
            if (File.Exists(path))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                (body, vector) = kind switch
                {
                    IndexedKind.Picture => await PictureAsync(path, timeout.Token),
                    IndexedKind.Video => (Sidecar(path), await VideoAsync(path, timeout.Token)),
                    IndexedKind.Document => (await DocumentAsync(path, timeout.Token), null),
                    IndexedKind.Audio => (Sidecar(path), null),
                    _ => (null, null),
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unreadable file is still found by its name.
            failed = true;
        }
        index.Store(id, path, body, vector, failed);
        return true;
    }

    private async Task<(string?, float[]?)> PictureAsync(string path, CancellationToken ct)
    {
        float[]? vector = null;
        if (Meaning.IsReady)
            vector = await Task.Run(() => Meaning.EmbedImage(path), ct);
        string? text = null;
        // Photoshop and Illustrator files carry their words; other pictures are read when they look like text.
        if (Formats.FromPath(path) is { Family: FormatFamily.Vector } design && design != Formats.Svg && design != Formats.Raw && design != Formats.Eps)
            text = await DocumentText.ReadAsync(path, converters, settings, null, ct);
        else if (DocumentEngine.IsOcrAvailable && LooksLikeText(path, vector))
            text = await ImageRedactor.ReadTextAsync(path, ct);
        return (text, vector);
    }

    /// <summary>
    /// Worth reading with OCR: screenshots and scans (PNG, TIFF…), and pictures whose look is
    /// closer to a page of text than to a photo.
    /// </summary>
    private bool LooksLikeText(string path, float[]? vector)
    {
        var format = Formats.FromPath(path);
        if (format == Formats.Png || format == Formats.Tiff || format == Formats.Bmp)
            return true;
        if (vector is null)
            return Path.GetFileName(path).Contains("scan", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Contains("screenshot", StringComparison.OrdinalIgnoreCase);
        _textLike ??= Meaning.EmbedText("a screenshot or a scanned page of a document with text");
        _photoLike ??= Meaning.EmbedText("a photo of people, places, animals or things");
        return Meaning.Similarity(vector, _textLike) > Meaning.Similarity(vector, _photoLike);
    }

    /// <summary>What a third of the way into the video shows.</summary>
    private static async Task<float[]?> VideoAsync(string path, CancellationToken ct)
    {
        if (!Meaning.IsReady)
            return null;
        var info = await MediaProbe.ProbeAsync(path, ct);
        if (!info.HasVideo)
            return null;
        var png = await MediaPreview.ExtractFrameAsync(path, TimeSpan.FromSeconds(info.DurationSeconds / 3), 448, ct);
        using var frame = new MagickImage(png);
        return Meaning.EmbedImage(frame);
    }

    private async Task<string?> DocumentAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > MaxDocumentBytes || Formats.FromPath(path) is not { } format || !DocumentText.CanRead(format, converters))
            return null;
        return await DocumentText.ReadAsync(path, converters, settings, null, ct);
    }

    /// <summary>Subtitles or a transcript saved next to a video or recording ("talk.srt" beside "talk.mp4").</summary>
    private static string? Sidecar(string path)
    {
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        foreach (var extension in new[] { ".srt", ".vtt", ".txt" })
        {
            if (File.Exists(stem + extension))
                return DocumentEngine.ReadTextFile(stem + extension);
        }
        return null;
    }
}
