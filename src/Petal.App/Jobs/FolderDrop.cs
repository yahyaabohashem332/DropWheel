using Petal.App.Hud;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Overlay;
using Petal.App.Settings;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Tools;

namespace Petal.App.Jobs;

/// <summary>
/// The wheel for a drop that includes folders: a folder stands for the files inside it. Conversions
/// and batch tools work on its files of the right kind, and their results go to a copy of the folder's
/// tree beside it (see <see cref="FolderLease"/>); tools of their own work on the folder as a whole.
/// </summary>
public static class FolderDrop
{
    /// <summary>How long the wheel may spend looking inside folders before it opens.</summary>
    public static readonly TimeSpan QuickLook = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The conversions: every format at least half of the folder's main kind of file can become (in the
    /// order its most common format lists them), after the favourites for that format.
    /// </summary>
    public static WheelContent Conversions(FolderContents contents, ConverterRegistry converters, IEnumerable<Favourite> favourites, string actionsKey)
    {
        var files = contents.MainKind is { } kind ? contents.OfKind(kind) : [];
        var common = files.GroupBy(f => f.Format!).Select(g => (Format: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();
        var reach = new Dictionary<FileFormat, int>();
        var order = new List<FileFormat>();
        foreach (var (format, count) in common)
        {
            foreach (var target in converters.AvailableTargets([format]))
            {
                if (!reach.ContainsKey(target))
                {
                    reach[target] = 0;
                    order.Add(target);
                }
                reach[target] += count;
            }
        }
        var targets = order.Where(t => reach[t] * 2 >= files.Count);
        var starred = common.Count == 0 ? [] : favourites.Where(f => f.AppliesTo([common[0].Format], converters));
        return new WheelContent(starred.Select(WheelItem.From).Concat(targets.Select(WheelItem.From)).ToList(),
            L.F("Nothing in this folder can be converted. Tap {0} for file actions.", actionsKey));
    }

    /// <summary>The tools: the folder's own (organize, one PDF, contact sheet, report, gather), then batch tools for its main kind of file.</summary>
    public static WheelContent Tools(FolderContents contents, Func<ToolInfo, bool> available, string convertKey, string actionsKey)
    {
        var main = contents.MainKind is { } kind ? contents.OfKind(kind).Select(f => f.Format!).Distinct().ToList() : [];
        // Photo folders with a few RAW or Photoshop files: the batch tools are for the ordinary pictures.
        if (contents.MainKind == FormatFamily.Image && main.Any(f => f.Family == FormatFamily.Image))
            main = main.Where(f => f.Family == FormatFamily.Image).ToList();
        var tools = ToolRegistry.ForFolder(main,
            pictures: contents.Files.Any(f => f.Kind == FormatFamily.Image),
            pdfs: contents.Files.Any(f => f.Format == Formats.Pdf),
            archives: contents.Files.Any(f => f.Kind == FormatFamily.Archive));
        return new WheelContent(tools.Where(available).Select(WheelItem.From).ToList(),
            L.F("No tools for this selection. Tap {0} to convert or {1} for file actions.", convertKey, actionsKey));
    }

    /// <summary>The centre of the wheel: the size of everything inside, and how many files.</summary>
    public static (string Title, string Subtitle) Hub(FolderContents contents) =>
        (Util.Sizes.Format(contents.TotalLength) + (contents.Complete ? "" : "+"),
         contents.Complete ? L.P(contents.Files.Count, "{0} file", "{0} files") : L.F("Over {0} files", contents.Files.Count));

    /// <summary>Converts the folder's files that can become <paramref name="target"/> into a copy of its tree.</summary>
    public static Task ConvertAsync(JobRunner jobs, FolderContents contents, FileFormat target, ScreenPoint at) =>
        RunPlaced(contents, f => f.Format is { } from && from != target && jobs.Converters.Find(from, target) is not null, target.Label, at,
            (files, describe) => jobs.ConvertAsync(files, target, at, describe: describe));

    public static Task ConvertAsync(JobRunner jobs, FolderContents contents, Favourite favourite, ScreenPoint at) =>
        RunPlaced(contents, f => f.Format is { } from && favourite.ConverterFor(from, jobs.Converters) is not null, FavouriteText.Label(favourite), at,
            (files, describe) => jobs.ConvertAsync(files, favourite, at, describe: describe));

    /// <summary>Runs a batch tool over the folder's files it works on, with the results in a copy of the tree.</summary>
    public static async Task RunToolAsync(Tools.ToolLauncher tools, ToolInfo tool, FolderContents contents, ScreenPoint at)
    {
        if (tool.Selection == ToolSelection.Folder)
        {
            await tools.LaunchFolderAsync(tool, contents, at);
            return;
        }
        var lease = FolderLease.Place(contents, f => f.Format is { } format && ToolRegistry.Takes(tool, format), Label(tool), SettingsStore.Current.OutputDirectoryFor);
        if (lease.Files.Count == 0)
        {
            lease.Release();
            Nothing(at, L.T(tool.Name));
            return;
        }
        await tools.LaunchAsync(tool, lease.Files, at, lease);
    }

    /// <summary>What the copy of a folder is called after: the tool's usual suffix, "Trip (Compressed)".</summary>
    public static string Label(ToolInfo tool) =>
        tool.OutputSuffix.Length > 0 ? tool.OutputSuffix : tool.Id == ToolId.ExtractArchive ? "Extracted" : tool.Name;

    /// <summary>How many of a folder's files a run left out, for the finished card.</summary>
    public static Func<IReadOnlyList<string>, string?> LeftOut(int count) =>
        _ => count > 0 ? L.P(count, "{0} file left as it is", "{0} files left as they are") : null;

    private static async Task RunPlaced(FolderContents contents, Func<FolderFile, bool> include, string label, ScreenPoint at,
        Func<IReadOnlyList<string>, Func<IReadOnlyList<string>, string?>, Task> run)
    {
        var lease = FolderLease.Place(contents, include, label, SettingsStore.Current.OutputDirectoryFor);
        try
        {
            if (lease.Files.Count == 0)
            {
                Nothing(at, label);
                return;
            }
            await run(lease.Files, LeftOut(contents.Files.Count - lease.Files.Count));
        }
        finally
        {
            lease.Release();
        }
    }

    private static void Nothing(ScreenPoint at, string what)
    {
        var card = HudCard.ShowAt(at, what, "");
        card.Fail(what, L.T("Nothing in this folder can be used for that."));
    }
}
