using System.IO;
using Petal.App.Actions;
using Petal.App.Localization;
using Petal.Core.Models;
using Petal.Core.Recording;
using Petal.Recipes;

namespace Petal.App.Settings;

/// <summary>The kinds of thing whose folder people can choose.</summary>
internal enum SavedKind
{
    Screenshots,
    Recordings,
    Meetings,
    Guides,
    Scans,
    Tables,
    ClipboardText,
    FromPhone,
    FromPcs,
}

/// <summary>A folder DropWheel writes to, for people to find it: what goes there, and where. No path means beside the original file.</summary>
internal sealed record SavedPlace(string Title, string Detail, string? Path, SavedKind? Kind = null);

/// <summary>
/// Where everything goes. The features save with <see cref="Folder"/>, so this list can't say otherwise. The usual folders
/// have the same English names whatever the interface's language: a folder doesn't move when the language changes.
/// </summary>
internal static class SavedPlaces
{
    private static string Known(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

    /// <summary>The folder this kind goes to when none was chosen.</summary>
    public static string Usual(SavedKind kind) => kind switch
    {
        SavedKind.Screenshots => Path.Combine(Known(Environment.SpecialFolder.MyPictures), "Screenshots"),
        SavedKind.Recordings => Path.Combine(Known(Environment.SpecialFolder.MyVideos), "Screen Recordings"),
        SavedKind.Meetings => Path.Combine(Known(Environment.SpecialFolder.MyDocuments), "DropWheel Meetings"),
        SavedKind.Guides => Path.Combine(Known(Environment.SpecialFolder.MyDocuments), "DropWheel Guides"),
        SavedKind.Scans => Path.Combine(Known(Environment.SpecialFolder.MyDocuments), "Scans"),
        SavedKind.Tables => Known(Environment.SpecialFolder.MyDocuments),
        SavedKind.ClipboardText => Path.Combine(Known(Environment.SpecialFolder.MyDocuments), "DropWheel"),
        SavedKind.FromPhone => PhoneWindow.DownloadsFolder(),
        _ => Path.Combine(PhoneWindow.DownloadsFolder(), "DropWheel"),
    };

    /// <summary>The folder the user chose for this kind; null when they chose none.</summary>
    public static string? Chosen(SavedKind kind, AppSettings settings)
    {
        var chosen = kind == SavedKind.Scans ? settings.ScanFolder : settings.SavedFolders.GetValueOrDefault(kind.ToString());
        return chosen is { Length: > 0 } ? chosen : null;
    }

    /// <summary>Where this kind is saved now: the folder chosen, unless its drive has gone (a USB disk taken out), then the usual one.</summary>
    public static string Folder(SavedKind kind, AppSettings? settings = null)
    {
        var chosen = Chosen(kind, settings ?? SettingsStore.Current);
        return chosen is not null && Path.IsPathFullyQualified(chosen) && Directory.Exists(Path.GetPathRoot(chosen)) ? chosen : Usual(kind);
    }

    /// <summary>Saves this kind in a folder from now on, or in the usual one when given none.</summary>
    public static void Choose(SavedKind kind, string? folder, AppSettings settings)
    {
        if (kind == SavedKind.Scans)
            settings.ScanFolder = folder;
        else if (folder is null)
            settings.SavedFolders.Remove(kind.ToString());
        else
            settings.SavedFolders[kind.ToString()] = folder;
    }

    public static IReadOnlyList<SavedPlace> Made(AppSettings settings)
    {
        SavedPlace Place(string title, string detail, SavedKind kind) => new(L.T(title), L.T(detail), Folder(kind, settings), kind);
        var places = new List<SavedPlace>
        {
            new(L.T("Conversions and tools"), L.T("What the wheel and the tools make from your files."), settings.Output == OutputLocation.Folder && settings.OutputFolder is { Length: > 0 } chosen ? chosen : null),
            Place("Screenshots", "Screenshots, scrolling captures, drawings on the screen, and pictures from the clipboard.", SavedKind.Screenshots),
            Place("Screen recordings", "Screen recordings, and what the Recording Studio saves from them.", SavedKind.Recordings),
            Place("Meeting notes", "The notes and summaries of your meetings.", SavedKind.Meetings),
            Place("Step guides", "The guides made by recording your steps.", SavedKind.Guides),
            Place("Scans", "Pages scanned with your phone, as PDF files.", SavedKind.Scans),
            Place("Tables from the screen", "Excel files of the tables read from the screen.", SavedKind.Tables),
            Place("Text from the clipboard", "Copied text kept as a file by the clipboard's wheel.", SavedKind.ClipboardText),
            Place("Files from your phone", "What your phone sends to this PC, and files dragged out of other programs.", SavedKind.FromPhone),
            Place("Files from other PCs", "What other PCs send with Send to PC.", SavedKind.FromPcs),
        };
        foreach (var rule in settings.Automations.Where(rule => rule.Enabled))
            places.Add(new SavedPlace(L.T("Automations"), L.F("What arrives in {0} is converted into here.", Path.GetFileName(rule.Folder.TrimEnd('\\', '/'))), rule.OutputFolder));
        return places;
    }

    public static IReadOnlyList<SavedPlace> Own() =>
    [
        new(L.T("AI models"), L.T("The models you downloaded."), ModelStore.Folder),
        new(L.T("Recording Studio projects"), L.T("Cinematic recordings before they are saved as videos."), StudioLibrary.Folder),
        new(L.T("Settings"), L.T("Your settings, wheels, petals and clipboard history. API keys are kept here too, encrypted."), SettingsStore.Directory),
        new(L.T("Prepared videos"), L.T("What Dub, Make Shorts, Edit by Text and Ask a File keep, so a file opens again at once."), Path.GetDirectoryName(Dubbing.CacheFolder)),
        new(L.T("Activity log"), L.T("What DropWheel did, for finding out what went wrong."), Path.GetDirectoryName(Log.FilePath)),
        new(L.T("Downloaded updates"), L.T("Installers waiting to run."), Shell.UpdateService.Folder),
    ];
}
