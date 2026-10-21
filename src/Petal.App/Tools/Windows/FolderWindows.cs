using System.IO;
using System.Windows;
using System.Windows.Controls;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Recipes;

namespace Petal.App.Tools.Windows;

/// <summary>The tools for a dropped folder as a whole.</summary>
public static class FolderToolSetup
{
    public static void Register()
    {
        ToolLauncher.RegisterFolder(ToolId.OrganizeFolder, (t, c, j, a) => new OrganizeFolderWindow(t, c, j, a));
        ToolLauncher.RegisterFolder(ToolId.FolderPdf, (t, c, j, a) => new FolderPdfWindow(t, c, j, a));
        ToolLauncher.RegisterFolder(ToolId.ContactSheet, (t, c, j, a) => new ContactSheetWindow(t, c, j, a));
        ToolLauncher.RegisterFolder(ToolId.FolderReport, (t, c, j, a) => new FolderReportWindow(t, c, j, a));
        ToolLauncher.RegisterFolder(ToolId.GatherFiles, (t, c, j, a) => new GatherFilesWindow(t, c, j, a));
    }
}

/// <summary>A window for one or more dropped folders; each folder is worked on by itself.</summary>
public abstract class FolderToolWindow : ToolWindow
{
    protected FolderToolWindow(ToolInfo tool, FolderContents contents, JobRunner jobs, ScreenPoint at, double width = 420)
        : base(tool.Name, width)
    {
        Tool = tool;
        Contents = contents;
        Jobs = jobs;
        At = at;
    }

    protected ToolInfo Tool { get; }
    protected FolderContents Contents { get; }
    protected JobRunner Jobs { get; }
    protected ScreenPoint At { get; }

    /// <summary>"Trip", or "3 folders".</summary>
    protected string FolderLabel => Contents.Folders.Count == 1 ? Path.GetFileName(Contents.Folders[0]) : L.P(Contents.Folders.Count, "{0} folder", "{0} folders");

    /// <summary>A folder's files, or only those directly inside it.</summary>
    protected IReadOnlyList<FolderFile> FilesOf(string root, bool subfolders) =>
        Contents.Files.Where(f => string.Equals(f.Root, root, StringComparison.OrdinalIgnoreCase) && (subfolders || f.RelativeDirectory.Length == 0)).ToList();

    protected IReadOnlyList<FolderFile> AllFiles(bool subfolders) => Contents.Folders.SelectMany(r => FilesOf(r, subfolders)).ToList();

    /// <summary>A new file or folder beside the dropped folder (or in the folder chosen for results): "Trip Report.xlsx".</summary>
    protected static string Beside(string root, string stem, string? extension) =>
        OutputNaming.Unique(SettingsStore.Current.OutputDirectoryFor(root) ?? Path.GetDirectoryName(root) ?? root, stem, extension);

    protected static CheckBox Check(string label, bool on) =>
        new() { Content = new TextBlock { Text = L.T(label), TextWrapping = TextWrapping.Wrap }, IsChecked = on, Margin = new Thickness(0, 10, 0, 0) };

    /// <summary>A file name for the footer; in Arabic a closing bracket stays with the name it belongs to.</summary>
    protected static string Name(string path) => L.IsArabic ? Path.GetFileName(path) + "\u200E" : Path.GetFileName(path);

    protected static bool On(CheckBox box) => box.IsChecked == true;

    protected static bool IsPicture(FolderFile f) => f.Kind == FormatFamily.Image;
}

/// <summary>Sorts a folder's files into folders by what they are, or by the month they are from. Undone from History.</summary>
public sealed class OrganizeFolderWindow : FolderToolWindow
{
    private readonly Segmented<string> _by = Segments([("kind", "By kind"), ("month", "By month")], "kind");
    private readonly CheckBox _screenshots = Check("Screenshots in a folder of their own", true);
    private readonly CheckBox _invoices = Check("Invoices and receipts in a folder of their own", true);
    private readonly CheckBox _rename = Check("Rename photos after the moment they were taken", false);
    private readonly CheckBox _subfolders = Check("Include files in subfolders", false);
    private readonly StackPanel _kindOptions = new();
    private readonly TextBlock _summary;

    public OrganizeFolderWindow(ToolInfo tool, FolderContents contents, JobRunner jobs, ScreenPoint at) : base(tool, contents, jobs, at)
    {
        Body.Children.Add(Text(L.F("Sorts the files of “{0}” into folders. Nothing is copied or deleted, and History can put everything back.", FolderLabel), "Petal.Caption"));
        Body.Children.Add(Row("Sort", _by, 90, 12));
        _kindOptions.Children.Add(_screenshots);
        _kindOptions.Children.Add(_invoices);
        _kindOptions.Children.Add(Text("Invoices are recognised by reading the text of PDFs and photos, which takes a little longer.", "Petal.Caption").WithMarginT(4));
        Body.Children.Add(_kindOptions);
        Body.Children.Add(_rename);
        Body.Children.Add(_subfolders);
        _summary = Text("", "Petal.Caption");
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 14, 0, 0);
        Body.Children.Add(_summary);
        _by.Changed += _ => Sync();
        _subfolders.Checked += (_, _) => Sync();
        _subfolders.Unchecked += (_, _) => Sync();
        PrimaryButton.Content = L.T("Organize");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    private void Sync()
    {
        _kindOptions.Visibility = _by.Value == "kind" ? Visibility.Visible : Visibility.Collapsed;
        var files = AllFiles(On(_subfolders));
        if (_by.Value == "month")
        {
            _summary.Text = L.P(files.Count, "{0} file goes into a folder for its year and month (2024 › 2024-07): photos by the day they were taken, the rest by the day they last changed.",
                "{0} files go into folders for their year and month (2024 › 2024-07): photos by the day they were taken, the rest by the day they last changed.");
            return;
        }
        // By what they are; screenshots and invoices are told apart while sorting.
        var plain = new OrganizeOptions { Screenshots = false, Invoices = false };
        var counts = files.GroupBy(f => Organizer.CategoryAsync(f.Path, plain, Jobs.Converters, SettingsStore.Current.Conversion).GetAwaiter().GetResult())
            .ToDictionary(g => g.Key ?? (FileCategory)(-1), g => g.Count());
        var parts = Enum.GetValues<FileCategory>().Where(counts.ContainsKey)
            .Select(c => $"{Organizer.Names(c)[L.IsArabic ? 1 : 0]} {counts[c]}").ToList();
        if (counts.TryGetValue((FileCategory)(-1), out var stay))
            parts.Add(L.P(stay, "{0} other file stays where it is", "{0} other files stay where they are"));
        _summary.Text = parts.Count == 0 ? L.T("There are no files to sort.") : string.Join(" · ", parts);
    }

    private void Apply()
    {
        bool byMonth = _by.Value == "month", subfolders = On(_subfolders), rename = On(_rename);
        var options = new OrganizeOptions { Screenshots = On(_screenshots), Invoices = On(_invoices), RenameByDate = rename };
        bool arabic = L.IsArabic;
        var settings = SettingsStore.Current.Conversion;
        int moved = 0;
        Close();
        var work = Contents.Folders.Select(root => new WorkItem(root, Path.GetFileName(root), async (p, ct) =>
        {
            var files = FilesOf(root, subfolders);
            var moves = new List<RenamedPath>();
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var from = files[i].Path;
                var to = byMonth
                    ? await Organizer.OrganizeByMonthAsync(from, root, rename, ct)
                    : await Organizer.OrganizeAsync(from, root, options, arabic, Jobs.Converters, settings, ct);
                if (to is not null)
                    moves.Add(new RenamedPath(from, to));
                p.Report((i + 1.0) / files.Count);
            }
            Interlocked.Add(ref moved, moves.Count);
            // Kept as renames, so undoing moves everything back (never into the Recycle Bin).
            History.Add(L.F("Organized {0}", Path.GetFileName(root)), moves.Select(m => m.From).ToList(), [], moves);
            return (IReadOnlyList<string>)[];
        })).ToList();
        _ = Jobs.RunAsync("Organizing", "Folder organized", work, At,
            _ => moved == 0 ? L.T("Everything was in its place already.") : L.P(moved, "{0} file moved", "{0} files moved"));
    }
}

/// <summary>A folder's pictures and PDFs, in their order in the folder, as one PDF beside it.</summary>
public sealed class FolderPdfWindow : FolderToolWindow
{
    private readonly CheckBox _subfolders = Check("Include subfolders", true);
    private readonly Segmented<Petal.Engines.Imaging.PdfPageSize> _page = Segments<Petal.Engines.Imaging.PdfPageSize>(
        [(Petal.Engines.Imaging.PdfPageSize.FitImage, "Picture size"), (Petal.Engines.Imaging.PdfPageSize.A4, "A4"), (Petal.Engines.Imaging.PdfPageSize.Letter, "Letter")],
        Petal.Engines.Imaging.PdfPageSize.FitImage);
    private readonly CheckBox _searchable = Check("Read the text of scanned pages, so the PDF can be searched", false);
    private readonly TextBlock _summary;

    public FolderPdfWindow(ToolInfo tool, FolderContents contents, JobRunner jobs, ScreenPoint at) : base(tool, contents, jobs, at)
    {
        _summary = Text("", "Petal.Caption");
        _summary.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_summary);
        Body.Children.Add(Row("Pages", _page, 90, 12));
        Body.Children.Add(_subfolders);
        _searchable.IsEnabled = Petal.Engines.Documents.Pdf.SearchablePdf.IsAvailable;
        Body.Children.Add(_searchable);
        _subfolders.Checked += (_, _) => Sync();
        _subfolders.Unchecked += (_, _) => Sync();
        PrimaryButton.Content = L.T("Create PDF");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    private IReadOnlyList<FolderFile> Pages(string root) => FilesOf(root, On(_subfolders)).Where(f => IsPicture(f) || f.Format == Formats.Pdf).ToList();

    private void Sync()
    {
        var all = Contents.Folders.SelectMany(Pages).ToList();
        int pictures = all.Count(IsPicture), pdfs = all.Count - pictures;
        var parts = new List<string>();
        if (pictures > 0) parts.Add(L.P(pictures, "{0} picture", "{0} pictures"));
        if (pdfs > 0) parts.Add(L.P(pdfs, "{0} PDF", "{0} PDFs"));
        _summary.Text = all.Count == 0
            ? L.T("There are no pictures or PDFs here.")
            : L.F("{0} become one PDF, in the order they have in the folder.", string.Join(" · ", parts));
        PrimaryButton.IsEnabled = all.Count > 0;
        FooterNote = Contents.Folders.Count == 1 ? Name(Beside(Contents.Folders[0], Path.GetFileName(Contents.Folders[0]), "pdf")) : FolderLabel;
    }

    private void Apply()
    {
        var options = new FolderPdfOptions { PageSize = _page.Value, Searchable = On(_searchable) && _searchable.IsEnabled };
        var lists = Contents.Folders.Select(root => (Root: root, Files: Pages(root).Select(f => f.Path).ToList())).Where(l => l.Files.Count > 0).ToList();
        Close();
        var work = lists.Select(l => new WorkItem(l.Root, Path.GetFileName(l.Root), async (p, ct) =>
        {
            var output = Beside(l.Root, Path.GetFileName(l.Root), "pdf");
            await FolderPdf.CreateAsync(l.Files, output, options with { Title = Path.GetFileName(l.Root) }, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = Jobs.RunAsync("Creating PDF", "PDF created", work, At);
    }
}

/// <summary>Every picture of a folder as a small print with its name, page after page, in one PDF.</summary>
public sealed class ContactSheetWindow : FolderToolWindow
{
    private readonly Segmented<int> _columns = Segments([(3, "3"), (4, "4"), (5, "5"), (6, "6")], 4);
    private readonly Segmented<SheetPaper> _paper = Segments([(SheetPaper.A4Portrait, "A4"), (SheetPaper.A4Landscape, "A4 landscape"), (SheetPaper.Letter, "Letter")], SheetPaper.A4Portrait);
    private readonly CheckBox _names = Check("Show file names", true);
    private readonly CheckBox _subfolders = Check("Include subfolders", true);
    private readonly TextBlock _summary;

    public ContactSheetWindow(ToolInfo tool, FolderContents contents, JobRunner jobs, ScreenPoint at) : base(tool, contents, jobs, at)
    {
        _summary = Text("", "Petal.Caption");
        Body.Children.Add(_summary);
        Body.Children.Add(Row("Columns", _columns, 90, 12));
        Body.Children.Add(Row("Paper", _paper, 90, 10));
        Body.Children.Add(_names);
        Body.Children.Add(_subfolders);
        _columns.Changed += _ => Sync();
        _paper.Changed += _ => Sync();
        foreach (var box in new[] { _names, _subfolders })
        {
            box.Checked += (_, _) => Sync();
            box.Unchecked += (_, _) => Sync();
        }
        PrimaryButton.Content = L.T("Create Contact Sheet");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    private ContactSheetOptions Options(string title) => new()
    {
        Columns = _columns.Value,
        Paper = _paper.Value,
        Names = On(_names),
        Title = title,
        RightToLeft = L.IsArabic,
        PageLabel = (page, pages) => L.F("Page {0} of {1}", page, pages),
    };

    private IReadOnlyList<string> Pictures(string root) => FilesOf(root, On(_subfolders)).Where(IsPicture).Select(f => f.Path).ToList();

    private void Sync()
    {
        var (columns, rows, _, _, _) = ContactSheet.Layout(Options(""));
        var counts = Contents.Folders.Select(r => Pictures(r).Count).ToList();
        int pictures = counts.Sum(), pages = counts.Sum(n => (n + columns * rows - 1) / (columns * rows));
        string[] parts =
        [
            L.P(pictures, "{0} picture", "{0} pictures"),
            L.P(pages, "{0} page", "{0} pages"),
            L.P(columns * rows, "{0} picture a page", "{0} pictures a page"),
        ];
        _summary.Text = pictures == 0 ? L.T("There are no pictures here.") : string.Join(" · ", parts);
        PrimaryButton.IsEnabled = pictures > 0;
        FooterNote = Contents.Folders.Count == 1 ? Name(Beside(Contents.Folders[0], $"{Path.GetFileName(Contents.Folders[0])} {Tool.OutputSuffix}", "pdf")) : FolderLabel;
    }

    private void Apply()
    {
        var lists = Contents.Folders.Select(root => (Root: root, Files: Pictures(root))).Where(l => l.Files.Count > 0).ToList();
        var options = lists.ToDictionary(l => l.Root, l => Options(Path.GetFileName(l.Root)));
        Close();
        var work = lists.Select(l => new WorkItem(l.Root, Path.GetFileName(l.Root), async (p, ct) =>
        {
            var output = Beside(l.Root, $"{Path.GetFileName(l.Root)} {Tool.OutputSuffix}", "pdf");
            await ContactSheet.CreateAsync(l.Files, output, options[l.Root], p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = Jobs.RunAsync("Creating contact sheet", "Contact sheet created", work, At);
    }
}

/// <summary>A spreadsheet of everything in a folder, and of what takes its space.</summary>
public sealed class FolderReportWindow : FolderToolWindow
{
    private readonly Segmented<bool> _format = Segments([(false, "Excel"), (true, "CSV")], false);
    private readonly CheckBox _details;
    private readonly CheckBox _subfolders = Check("Include subfolders", true);
    private readonly TextBlock _summary;

    public FolderReportWindow(ToolInfo tool, FolderContents contents, JobRunner jobs, ScreenPoint at) : base(tool, contents, jobs, at)
    {
        _details = Check("Picture sizes, durations and page counts (reads every file, so it takes longer)", contents.Files.Count <= 1000);
        _summary = Text("", "Petal.Caption");
        _summary.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(_summary);
        Body.Children.Add(Row("Save as", _format, 90, 12));
        Body.Children.Add(_details);
        Body.Children.Add(_subfolders);
        _format.Changed += _ => Sync();
        _subfolders.Checked += (_, _) => Sync();
        _subfolders.Unchecked += (_, _) => Sync();
        PrimaryButton.Content = L.T("Create Report");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    private string Extension => _format.Value ? "csv" : "xlsx";

    private void Sync()
    {
        var files = AllFiles(On(_subfolders));
        var parts = new List<string> { L.P(files.Count, "{0} file", "{0} files"), Util.Sizes.Format(files.Sum(f => f.Length)) };
        if (!_format.Value)
            parts.Add(L.T("A sheet of every file, then the space taken by kind, the largest folders and the largest files."));
        _summary.Text = string.Join(" · ", parts);
        PrimaryButton.IsEnabled = files.Count > 0;
        FooterNote = Contents.Folders.Count == 1 ? Name(Beside(Contents.Folders[0], $"{Path.GetFileName(Contents.Folders[0])} {Tool.OutputSuffix}", Extension)) : FolderLabel;
    }

    private void Apply()
    {
        bool subfolders = On(_subfolders);
        var options = new FolderReportOptions { Csv = _format.Value, Details = On(_details), RightToLeft = L.IsArabic };
        var text = new FolderReportText(
            L.T("Files"), L.T("By kind"), L.T("Largest folders"), L.T("Largest files"),
            L.T("Name"), L.T("Folder"), L.T("Type"), L.T("Size"), L.T("Modified"), L.T("Created"),
            L.T("Width"), L.T("Height"), L.T("Duration"), L.T("Pages"), L.T("Files"));
        var extension = Extension;
        var lists = Contents.Folders.Select(root => (Root: root, Files: FilesOf(root, subfolders))).Where(l => l.Files.Count > 0).ToList();
        Close();
        var work = lists.Select(l => new WorkItem(l.Root, Path.GetFileName(l.Root), async (p, ct) =>
        {
            var output = Beside(l.Root, $"{Path.GetFileName(l.Root)} {Tool.OutputSuffix}", extension);
            await FolderReport.CreateAsync(l.Root, l.Files, output, options, text, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = Jobs.RunAsync("Creating report", "Report created", work, At);
    }
}

/// <summary>Copies every file of a folder's tree (or only one kind) into one new folder beside it.</summary>
public sealed class GatherFilesWindow : FolderToolWindow
{
    private static readonly (string Key, FormatFamily Kind, string Label)[] Kinds =
        [("pictures", FormatFamily.Image, "Pictures"), ("videos", FormatFamily.Video, "Videos"), ("music", FormatFamily.Audio, "Music"), ("pdfs", FormatFamily.Pdf, "PDFs")];

    private readonly ComboBox _kind;
    private readonly TextBlock _summary;

    public GatherFilesWindow(ToolInfo tool, FolderContents contents, JobRunner jobs, ScreenPoint at) : base(tool, contents, jobs, at)
    {
        var kinds = new List<(string Value, string Label)> { ("all", "All files") };
        foreach (var (key, kind, label) in Kinds)
            if (contents.Files.Any(f => f.Root is not null && f.Kind == kind))
                kinds.Add((key, label));
        _kind = Choice(kinds, "all");
        _summary = Text("", "Petal.Caption");
        _summary.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(Row("Files", _kind, 90, 0));
        Body.Children.Add(_summary.WithMarginT(12));
        _kind.SelectionChanged += (_, _) => Sync();
        PrimaryButton.Content = L.T("Gather Files");
        PrimaryButton.Click += (_, _) => Apply();
        Sync();
    }

    private IReadOnlyList<string> Chosen(string root)
    {
        var only = Kinds.FirstOrDefault(k => k.Key == ValueOf<string>(_kind));
        return FilesOf(root, true).Where(f => only.Key is null || f.Kind == only.Kind).Select(f => f.Path).ToList();
    }

    private void Sync()
    {
        int count = Contents.Folders.Sum(r => Chosen(r).Count);
        _summary.Text = L.P(count, "{0} file is copied into one new folder. Files with the same name are numbered; the originals stay where they are.",
            "{0} files are copied into one new folder. Files with the same name are numbered; the originals stay where they are.");
        PrimaryButton.IsEnabled = count > 0;
        FooterNote = Contents.Folders.Count == 1 ? Name(Beside(Contents.Folders[0], $"{Path.GetFileName(Contents.Folders[0])} ({Tool.OutputSuffix})", null)) : FolderLabel;
    }

    private void Apply()
    {
        var lists = Contents.Folders.Select(root => (Root: root, Files: Chosen(root))).Where(l => l.Files.Count > 0).ToList();
        Close();
        var work = lists.Select(l => new WorkItem(l.Root, Path.GetFileName(l.Root), async (p, ct) =>
        {
            var folder = Beside(l.Root, $"{Path.GetFileName(l.Root)} ({Tool.OutputSuffix})", null);
            await Gatherer.CopyAsync(l.Files, folder, p, ct);
            return (IReadOnlyList<string>)[folder];
        })).ToList();
        _ = Jobs.RunAsync("Gathering files", "Files gathered", work, At);
    }
}
