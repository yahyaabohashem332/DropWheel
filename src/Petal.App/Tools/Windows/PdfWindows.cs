using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Tools.Parts;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Documents.Pdf;
using Petal.App.Localization;

namespace Petal.App.Tools.Windows;

/// <summary>Organize PDF: reorder, rotate, duplicate or remove pages, then save a new PDF.</summary>
public sealed class OrganizePdfWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly PageGrid _grid = new();
    private readonly TextBlock _count = new();
    private readonly Button _resetOrder = new() { Content = L.T("Reset Order") };
    private readonly CheckBox _uniform = new() { Content = L.T("Make all pages the same width"), Margin = new Thickness(0, 12, 0, 0) };
    private PdfDocumentView? _view;
    private int _pageCount;

    public OrganizePdfWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 600)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_resetOrder, Dock.Right);
        header.Children.Add(_resetOrder);
        _count.Style = (Style)FindResource("Petal.Label");
        header.Children.Add(_count);
        Body.Children.Add(header);

        var gridHost = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Height = 380 };
        gridHost.SetResourceReference(Border.BackgroundProperty, "Petal.Control");
        gridHost.Child = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Body.Children.Add(gridHost);
        Body.Children.Add(_uniform);
        Body.Children.Add(Text("Pages are scaled proportionally to match the narrowest page; their heights may still differ.", "Petal.Caption").WithMarginT(4));
        Body.Children.Add(Text("Drag pages to reorder. Hover a page to rotate, duplicate or remove it.", "Petal.Caption").WithMarginT(8));

        _grid.Changed += Sync;
        _resetOrder.Click += (_, _) => _grid.Load(_pageCount);
        PrimaryButton.Content = L.T("Save Organized PDF");
        PrimaryButton.Click += (_, _) => Save();
        FooterNote = Path.GetFileName(OutputNaming.ForTool(file, tool));
        Closed += (_, _) => _view?.Dispose();

        Loaded += async (_, _) =>
        {
            try
            {
                _view = await Task.Run(() => PdfDocumentView.Open(file));
                _pageCount = _view.PageSizes.Count;
                _grid.ThumbnailLoader = LoadThumbnailAsync;
                _grid.Load(_pageCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Opening PDF to organize");
                FooterNote = "Couldn't open this PDF.";
                PrimaryButton.IsEnabled = false;
            }
        };
    }

    private readonly SemaphoreSlim _renderGate = new(1);

    private async Task<ImageSource?> LoadThumbnailAsync(int page)
    {
        if (_view is null) return null;
        await _renderGate.WaitAsync();
        try
        {
            var view = _view;
            var bitmap = await Task.Run(() => view.RenderPage(page, 220));
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null, bitmap.Pixels, bitmap.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private void Sync()
    {
        int n = _grid.Pages.Count;
        _count.Text = L.P(n, "{0} page", "{0} pages");
        _resetOrder.IsEnabled = !_grid.IsOriginalOrder(_pageCount);
        PrimaryButton.IsEnabled = n > 0;
    }

    private void Save()
    {
        var ops = _grid.Pages.Select(p => new PdfPageOperation(p.SourceIndex, p.Rotation)).ToList();
        var options = new PdfOrganizeOptions(_uniform.IsChecked == true);
        var output = OutputNaming.ForTool(_file, _tool);
        Close();
        _ = _jobs.RunAsync("Organizing PDF", "PDF saved", [new WorkItem(_file, Path.GetFileName(_file), async (p, ct) =>
        {
            await PdfPageTools.OrganizeAsync(_file, output, ops, options, p, ct);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }
}

/// <summary>PDF hooks into the shared tool plumbing.</summary>
public static class PdfToolSetup
{
    public static void Register()
    {
        CompressWindow.Backends[FormatFamily.Pdf] = CompressPdf;
        ToolLauncher.RegisterWindow(ToolId.OrganizePdf, (t, f, j, a) => new OrganizePdfWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.ProtectPdf, (t, f, j, a) => new PdfPasswordWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.NumberPages, (t, f, j, a) => new PageNumbersWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.SignPdf, (t, f, j, a) => new SignPdfWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.EditPdf, (t, f, j, a) => new PdfEditorWindow(t, f[0], j, a));
        ToolLauncher.RegisterDirect(ToolId.ExtractImages, (tool, files, jobs, at) => jobs.RunAsync("Extracting pictures", "Pictures extracted",
            files.Select(f => new WorkItem(f, Path.GetFileName(f), async (p, ct) =>
            {
                var folder = OutputNaming.ForTool(f, tool);
                await PdfStamper.ExtractImagesAsync(f, folder, null, p, ct);
                return (IReadOnlyList<string>)[folder];
            })).ToList(), at));
        ToolLauncher.RegisterWindow(ToolId.MergePdfs, (t, f, j, a) => new OrderedFilesWindow(t, f, j, a, "Document order", "Merge PDFs", "Merging PDFs", "PDFs merged",
            async (inputs, output, p, ct) =>
            {
                await PdfPageTools.MergeAsync(inputs, output, new PdfMergeOptions(), p, ct);
                return output;
            }));
        ToolLauncher.RegisterWindow(ToolId.ReadAloud, (t, f, j, a) => new ReadAloudWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.RemoveNoise, (t, f, j, a) => new RemoveNoiseWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.SeparateVocals, (t, f, j, a) => new SeparateVocalsWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.EraseObject, (t, f, j, a) => new EraseObjectWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.TranslateSubtitles, (t, f, j, a) => new TranslateSubtitlesWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.TranslateDocument, (t, f, j, a) => new TranslateDocumentWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.TranslatePicture, (t, f, j, a) => new TranslatePictureWindow(t, f, j, a));
        ToolLauncher.RegisterWindow(ToolId.AutoBlur, (t, f, j, a) => new AutoBlurWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.StudyNotes, (t, f, j, a) => new StudyNotesWindow(t, f[0], j, a));
        ToolLauncher.RegisterWindow(ToolId.StudyFile, (t, f, j, a) => new StudyFileWindow(t, f[0], j, a));
        ToolLauncher.RegisterDirect(ToolId.MakeSearchable, (tool, files, jobs, at) => jobs.RunAsync("Reading text", "Searchable PDF saved", SearchableWork(tool, files), at));
        ToolLauncher.RegisterDirect(ToolId.SplitPdf, (tool, files, jobs, at) => jobs.RunAsync("Splitting PDF", "PDF split",
            files.Select(f => new WorkItem(f, Path.GetFileName(f), async (p, ct) =>
            {
                var folder = OutputNaming.ForTool(f, tool);
                await PdfPageTools.SplitAsync(f, folder, p, ct);
                return (IReadOnlyList<string>)[folder];
            })).ToList(), at));
    }

    /// <summary>PDFs each get their own text layer; pictures become the pages of one searchable PDF.</summary>
    private static List<WorkItem> SearchableWork(ToolInfo tool, IReadOnlyList<string> files)
    {
        if (files.All(f => Formats.FromPath(f) == Formats.Pdf))
        {
            return files.Select(f => new WorkItem(f, Path.GetFileName(f), async (p, ct) =>
            {
                var output = OutputNaming.ForTool(f, tool);
                await MakeSearchableAsync(f, output, p, ct);
                return (IReadOnlyList<string>)[output];
            })).ToList();
        }
        var combined = OutputNaming.ForTool(files[0], tool);
        return [new WorkItem(files[0], L.P(files.Count, "{0} image", "{0} images"), async (p, ct) =>
        {
            var pages = Path.Combine(Path.GetTempPath(), "Petal", $"pages-{Guid.NewGuid():N}.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(pages)!);
            try
            {
                await Petal.Engines.Imaging.PdfCreator.CreateAsync(files, pages, new Petal.Engines.Imaging.PdfCreateOptions(), new Progress<double>(f => p.Report(f * 0.15)), ct);
                await MakeSearchableAsync(pages, combined, new Progress<double>(f => p.Report(0.15 + f * 0.85)), ct);
                return (IReadOnlyList<string>)[combined];
            }
            finally
            {
                try { File.Delete(pages); } catch (IOException) { }
            }
        })];
    }

    private static async Task MakeSearchableAsync(string input, string output, IProgress<double> progress, CancellationToken ct)
    {
        var result = await SearchablePdf.MakeAsync(input, output, null, progress, ct);
        if (!result.Written)
            throw new InvalidOperationException("Every page already has text, so this PDF is searchable already.");
        if (result.Words == 0)
        {
            File.Delete(output);
            throw new InvalidOperationException("No text was found. Try a sharper scan, or add its language under Windows Settings › Time & language.");
        }
    }

    private static async Task<string> CompressPdf(string input, CompressRequest request, IProgress<double> progress, CancellationToken ct)
    {
        var output = OutputNaming.ForTool(input, ToolRegistry.Get(ToolId.Compress));
        var options = new PdfCompressOptions
        {
            Preset = request.Strong ? PdfCompressionPreset.Strong : PdfCompressionPreset.Balanced,
            TargetBytes = request.TargetBytes,
            TargetReductionPercent = request.ReductionPercent,
        };
        var result = await PdfCompressor.CompressAsync(input, output, options, progress, ct);
        // Nothing could be saved without making the file bigger: the original stays the best version.
        return result.Written ? output : input;
    }
}
