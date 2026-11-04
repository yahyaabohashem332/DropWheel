using System.Reflection;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Petal.Core.Text;
using Petal.Engines.Documents.Ocr;
using Petal.Engines.Documents.Util;
using UglyToad.PdfPig;
using PdfDocument = PdfSharp.Pdf.PdfDocument;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Petal.Engines.Documents.Pdf;

/// <summary>
/// One PDF open in the editor. A protected file is read with its password into a plain copy held in memory, and saved
/// protected the same way again; the original on disk is only ever read. Safe to use from any thread.
/// </summary>
public sealed class PdfEditSession : IDisposable
{
    /// <summary>A page with fewer letters than this counts as a scan, which OCR reads when a search needs it.</summary>
    private const int ScanLetters = 8;

    private readonly string? _password;
    private readonly bool _protected;
    private readonly PdfReadLimits _limits;
    private readonly Lock _readGate = new();
    private readonly Dictionary<int, List<PdfTextLine>> _lines = [];
    private readonly Dictionary<int, List<PdfTextReader.TextRun>> _runs = [];
    private PigDocument? _reader;

    private PdfEditSession(string path, byte[] working, string? password, bool isProtected, PdfEditRights rights, PdfReadLimits limits)
    {
        Path = path;
        Working = working;
        _password = password;
        _protected = isProtected;
        _limits = limits;
        Rights = rights;
        lock (PdfRaster.Gate)
            Pages = PDFtoImage.Conversion.GetPageSizes(working).Select(s => new PdfPageSize(s.Width, s.Height)).ToList();
        using var document = PdfSharpIO.Open(working, PdfDocumentOpenMode.Modify);
        Fields = PdfForms.Read(document);
        IsXfaOnly = PdfForms.IsXfaOnly(document);
        IsSigned = PdfForms.IsSigned(document);
    }

    /// <summary>Opens a PDF. Throws <see cref="PdfOpenException"/> when it is damaged, or needs a password that wasn't given or doesn't fit.</summary>
    public static PdfEditSession Open(string path, string? password = null)
    {
        var bytes = File.ReadAllBytes(path);
        var (working, isProtected, rights, limits) = Plain(bytes, password, System.IO.Path.GetFileName(path));
        return new PdfEditSession(path, working, password, isProtected, rights, limits);
    }

    public string Path { get; }

    /// <summary>The file's bytes, without any protection.</summary>
    internal byte[] Working { get; }

    public IReadOnlyList<PdfPageSize> Pages { get; }

    public IReadOnlyList<PdfFormField> Fields { get; }

    public PdfEditRights Rights { get; }

    /// <summary>A form that exists only as XFA: this can show its pages but not fill it in.</summary>
    public bool IsXfaOnly { get; }

    /// <summary>Someone has signed it digitally: a changed copy no longer carries a valid signature.</summary>
    public bool IsSigned { get; }

    /// <summary>Draws one page <paramref name="width"/> pixels wide, its annotations and form as the file has them.</summary>
    public PdfPageBitmap Render(int page, int width)
    {
        using var bitmap = PdfRaster.RenderWidth(Working, null, page, width, 0);
        return PdfRaster.ToPageBitmap(bitmap);
    }

    /// <summary>A page's own text, by lines. Empty for a scan.</summary>
    public IReadOnlyList<PdfTextLine> Lines(int page)
    {
        lock (_readGate)
        {
            if (_lines.TryGetValue(page, out var known))
                return known;
            List<PdfTextLine> lines;
            try
            {
                lines = PdfTextReader.Lines(PdfTextReader.Horizontal(Reader().GetPage(page + 1), page));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lines = [];
            }
            return _lines[page] = lines;
        }
    }

    private PigDocument Reader() => _reader ??= PigDocument.Open(Working, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });

    /// <summary>A page's text for searching; a scan is read with OCR once, if this PC has an OCR language.</summary>
    private async Task<List<PdfTextReader.TextRun>> RunsAsync(int page, CancellationToken cancellationToken)
    {
        bool scan;
        lock (_readGate)
        {
            if (_runs.TryGetValue(page, out var known))
                return known;
            var read = Reader().GetPage(page + 1);
            var runs = PdfTextReader.Runs(read, page);
            scan = read.Letters.Count(l => l.Value.Any(char.IsLetterOrDigit)) < ScanLetters;
            if (!scan || !OcrService.IsAvailable)
                return _runs[page] = runs;
        }
        var scanned = await PdfTextReader.ReadScanAsync(Working, page, Pages[page].Width, cancellationToken).ConfigureAwait(false);
        lock (_readGate)
            return _runs[page] = scanned;
    }

    /// <summary>Every place a word or phrase is, on all pages, in fields too. Scans are read with OCR.</summary>
    public async Task<IReadOnlyList<PdfFound>> FindAsync(string phrase, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var found = new List<PdfFound>();
        for (int page = 0; page < Pages.Count; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runs = await RunsAsync(page, cancellationToken).ConfigureAwait(false);
            found.AddRange(PdfTextReader.Find(runs, phrase).Select(f => new PdfFound(page, f.Area, f.Text)));
            progress?.Report((page + 1.0) / Pages.Count);
        }
        foreach (var field in Fields)
            if (PdfTextReader.Holds(field.Value, phrase))
                found.AddRange(field.Widgets.Select(w => new PdfFound(w.Page, w.Area, field.Value)));
        return found;
    }

    /// <summary>E-mail addresses, phones, card numbers, IBANs, IP addresses and passwords, on all pages and in fields. Scans are read with OCR.</summary>
    public async Task<IReadOnlyList<PdfFound>> FindPrivateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var found = new List<PdfFound>();
        for (int page = 0; page < Pages.Count; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var run in await RunsAsync(page, cancellationToken).ConfigureAwait(false))
                foreach (var match in PrivateInfo.Find(run.Text))
                    if (PdfTextReader.AreaOf(run, match.Start, match.Length) is { } area)
                        found.Add(new PdfFound(page, area, run.Text.Substring(match.Start, match.Length)));
            progress?.Report((page + 1.0) / Pages.Count);
        }
        foreach (var field in Fields)
            if (PrivateInfo.Find(field.Value).Count > 0)
                found.AddRange(field.Widgets.Select(w => new PdfFound(w.Page, w.Area, field.Value)));
        return found;
    }

    /// <summary>The colour behind some words on a page, for a retyped line's cover when its old words can't be taken out.</summary>
    public int BackgroundAround(int page, PdfArea area)
    {
        using var bitmap = PdfRaster.RenderContentOnly(Working, page, 72);
        return PdfEditor.BackgroundAround(bitmap, bitmap.Width / Math.Max(1, Pages[page].Width), area);
    }

    /// <summary>Saves a new file, protected as the original was. The original isn't touched; nothing is left behind on failure.</summary>
    public Task<PdfEditResult> SaveAsync(PdfEditRequest request, string output, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            SafeOutput.EnsureNotInput(Path, output);
            var pdf = Working;
            var result = PdfEditor.Apply(ref pdf, request, Util.ScaledProgress.Of(progress, 0, 0.95), cancellationToken);
            if (_protected)
                pdf = Protect(pdf);
            SafeOutput.WriteBytes(output, pdf);
            progress?.Report(1);
            return result;
        }, cancellationToken);

    public void Dispose()
    {
        lock (_readGate)
        {
            _reader?.Dispose();
            _reader = null;
        }
    }

    // ---------- protection ----------

    /// <summary>
    /// A file's bytes without protection, and what its author allows. A file opened by its owner's password, or with none needed,
    /// may be changed at will. PDFsharp otherwise won't let a document go past reading, though it has decrypted it by then and
    /// the file itself says what is allowed: that is honoured here, so a locked form that may be filled can be filled and no more.
    /// </summary>
    private static (byte[] Working, bool IsProtected, PdfEditRights Rights, PdfReadLimits Limits) Plain(byte[] bytes, string? password, string name)
    {
        try
        {
            using var document = PdfSharpIO.Open(bytes, PdfDocumentOpenMode.Modify, password, name);
            if (!IsEncrypted(bytes, password))
                return (bytes, false, PdfEditRights.Full, PdfReadLimits.None);
            document.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
            return (PdfSharpIO.SaveToBytes(document), true, PdfEditRights.Full, PdfReadLimits.None);
        }
        catch (PdfOpenException ex) when (ex.LockedForChanges)
        {
            using var document = PdfSharpIO.Open(bytes, PdfDocumentOpenMode.Import, password, name);
            var settings = document.SecuritySettings;
            var rights = settings.PermitModifyDocument ? PdfEditRights.Full
                : settings.PermitAnnotations ? PdfEditRights.Comment
                : settings.PermitFormsFill ? PdfEditRights.Fill
                : PdfEditRights.None;
            var limits = new PdfReadLimits(settings.PermitPrint, settings.PermitFullQualityPrint, settings.PermitExtractContent);
            var openMode = typeof(PdfDocument).GetField("_openMode", BindingFlags.Instance | BindingFlags.NonPublic);
            if (openMode is null)
                throw;
            openMode.SetValue(document, PdfDocumentOpenMode.Modify);
            document.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
            _ = document.Pages;
            return (PdfSharpIO.SaveToBytes(document), true, rights, limits);
        }
    }

    private static bool IsEncrypted(byte[] bytes, string? password)
    {
        var options = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
        if (password is not null)
            options.Password = password;
        using var pig = PigDocument.Open(bytes, options);
        return pig.IsEncrypted;
    }

    /// <summary>The same protection on the way out: the same password to open, and a new owner password nobody holds keeping the author's limits.</summary>
    private byte[] Protect(byte[] pdf)
    {
        using var document = PdfSharpIO.Open(pdf, PdfDocumentOpenMode.Modify);
        document.SecurityHandler.SetEncryptionToV5(true);
        var settings = document.SecuritySettings;
        settings.UserPassword = _password ?? "";
        settings.OwnerPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        settings.PermitPrint = _limits.Print;
        settings.PermitFullQualityPrint = _limits.FullQualityPrint;
        settings.PermitExtractContent = _limits.Extract;
        settings.PermitModifyDocument = settings.PermitAssembleDocument = Rights == PdfEditRights.Full;
        settings.PermitAnnotations = Rights is PdfEditRights.Full or PdfEditRights.Comment;
        settings.PermitFormsFill = Rights is not PdfEditRights.None;
        return PdfSharpIO.SaveToBytes(document);
    }

    /// <summary>What a locked file's author allows a reader to do with it. Its saved copy allows the same and no more.</summary>
    private readonly record struct PdfReadLimits(bool Print, bool FullQualityPrint, bool Extract)
    {
        public static PdfReadLimits None => new(true, true, true);
    }
}
