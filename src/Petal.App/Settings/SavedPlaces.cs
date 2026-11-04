using System.IO;
using Petal.App.Actions;
using Petal.App.Localization;
using Petal.App.Screen;
using Petal.App.Shell;
using Petal.App.Wheels;
using Petal.App.Words;
using Petal.Core.Models;
using Petal.Core.Recording;
using Petal.Recipes;

namespace Petal.App.Settings;

/// <summary>A folder DropWheel writes to, for people to find it: what goes there, and where. No path means beside the original file.</summary>
internal sealed record SavedPlace(string Title, string Detail, string? Path);

/// <summary>Where everything goes, read from the same places the features save with, so this list can't say otherwise.</summary>
internal static class SavedPlaces
{
    /// <summary>Tables read from the screen.</summary>
    public static string Tables => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static IReadOnlyList<SavedPlace> Made(AppSettings settings)
    {
        var places = new List<SavedPlace>
        {
            new(L.T("Conversions and tools"), L.T("What the wheel and the tools make from your files."), settings.Output == OutputLocation.Folder && settings.OutputFolder is { Length: > 0 } chosen ? chosen : null),
            new(L.T("Screenshots"), L.T("Screenshots, scrolling captures, drawings on the screen, and pictures from the clipboard."), Screenshots.Folder),
            new(L.T("Screen recordings"), L.T("Screen recordings, and what the Recording Studio saves from them."), ScreenRecorder.Folder),
            new(L.T("Meeting notes"), L.T("The notes and summaries of your meetings."), MeetingRecorder.Folder),
            new(L.T("Step guides"), L.T("The guides made by recording your steps."), StepGuideWindow.Folder),
            new(L.T("Scans"), L.T("Pages scanned with your phone, as PDF files."), ScanWindow.SaveFolder()),
            new(L.T("Tables from the screen"), L.T("Excel files of the tables read from the screen."), Tables),
            new(L.T("Text from the clipboard"), L.T("Copied text kept as a file by the clipboard's wheel."), ClipboardSource.TextFolder),
            new(L.T("Files from your phone"), L.T("What your phone sends to this PC, and files dragged out of other programs."), PhoneWindow.DownloadsFolder()),
            new(L.T("Files from other PCs"), L.T("What other PCs send with Send to PC."), PcDrop.Folder),
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
        new(L.T("Downloaded updates"), L.T("Installers waiting to run."), UpdateService.Folder),
    ];
}
