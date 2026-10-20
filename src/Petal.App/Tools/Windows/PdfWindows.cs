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
    private readonly Button _resetOrder = new() { Content = "Reset Order" };
    private readonly CheckBox _uniform = new() { Content = "Make all pages the same width", Margin = new Thickness(0, 12, 0, 0) };
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
        PrimaryButton.Content = "Save Organized PDF";
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
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null, bitmap.Pixels, bitmap.Stride);
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
        _count.Text = n == 1 ? "1 page" : $"{n} pages";
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
        ToolLauncher.RegisterWindow(ToolId.MergePdfs, (t, f, j, a) => new OrderedFilesWindow(t, f, j, a, "Document order", "Merge PDFs", "Merging PDFs", "PDFs merged",
            async (inputs, output, p, ct) =>
            {
                await PdfPageTools.MergeAsync(inputs, output, new PdfMergeOptions(), p, ct);
                return output;
            }));
        ToolLauncher.RegisterDirect(ToolId.SplitPdf, (tool, files, jobs, at) => jobs.RunAsync("Splitting PDF", "PDF split",
            files.Select(f => new WorkItem(f, Path.GetFileName(f), async (p, ct) =>
            {
                var folder = OutputNaming.ForTool(f, tool);
                await PdfPageTools.SplitAsync(f, folder, p, ct);
                return (IReadOnlyList<string>)[folder];
            })).ToList(), at));
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
