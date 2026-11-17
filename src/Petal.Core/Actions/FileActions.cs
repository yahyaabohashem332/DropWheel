namespace Petal.Core.Actions;

public enum FileActionId
{
    Share,
    Phone,
    Clean,
    Float,
    Copy,
    Move,
    CopyPath,
    Terminal,
    Rename,
    Zip,
    Duplicates,
    Ask,
    CopyName,
    OpenInCode,
    OpenWith,
    SendToPc,
    Checksum,
}

public sealed record FileActionInfo(FileActionId Id, string WheelTitle, string Name, string Icon, string Description);

/// <summary>Which actions sit on the wheel itself; the rest wait under More.</summary>
public static class ActionWheel
{
    /// <summary>Eleven on the wheel and More: the same twelve petals as before the newer actions came.</summary>
    public static readonly IReadOnlyList<FileActionId> Default =
    [
        FileActionId.Ask, FileActionId.Share, FileActionId.Phone, FileActionId.Clean, FileActionId.Float, FileActionId.Copy,
        FileActionId.Move, FileActionId.Rename, FileActionId.Zip, FileActionId.OpenWith, FileActionId.Duplicates,
    ];

    /// <summary>The actions on the wheel for a list of names from settings (unknown names are skipped), or the usual ones.</summary>
    public static IReadOnlyList<FileActionId> Parse(IEnumerable<string>? names)
    {
        if (names is null)
            return Default;
        var ids = new List<FileActionId>();
        foreach (var name in names)
            if (Enum.TryParse<FileActionId>(name, out var id) && !ids.Contains(id))
                ids.Add(id);
        return ids;
    }
}

public static class FileActionRegistry
{
    public static readonly IReadOnlyList<FileActionInfo> All =
    [
        new(FileActionId.Ask, "ASK", "Ask DropWheel", "ask", "Say what to do with the files in your own words, in English or Arabic. DropWheel shows the steps before anything runs."),
        new(FileActionId.Share, "SHARE", "Share", "share", "Send files and folders with Nearby Sharing or any app in the Windows share sheet."),
        new(FileActionId.Phone, "TO PHONE", "Send to Phone", "smartphone", "Show a QR code to scan with a phone on the same Wi-Fi, then save the files on it. Nothing goes through the internet."),
        new(FileActionId.SendToPc, "TO PC", "Send to PC", "monitor", "Send files and folders to another PC with DropWheel on the same network. It asks before saving them; nothing goes through the internet."),
        new(FileActionId.Clean, "CLEAN", "Clean for Sharing", "shieldCheck", "Make copies without the location, device and author details hidden in photos, videos, PDFs and Office files."),
        new(FileActionId.Float, "FLOAT", "Float", "pin", "Float a file above the other windows, shown by DropWheel at the size you choose; several files go on a floating shelf to drag into other apps."),
        new(FileActionId.Copy, "COPY", "Copy", "clipboard", "Copy files to the clipboard, ready to paste in File Explorer."),
        new(FileActionId.Move, "MOVE", "Move", "folderInput", "Choose a destination folder and move the originals there."),
        new(FileActionId.Rename, "RENAME", "Rename", "rename", "Rename a selection with a pattern, numbers and dates, previewed before anything changes."),
        new(FileActionId.Zip, "ZIP", "Compress to ZIP", "folderZip", "Pack files and folders into one ZIP archive, with an optional password."),
        new(FileActionId.CopyPath, "COPY PATH", "Copy Path", "link", "Copy full file paths as text, one path per line for a selection."),
        new(FileActionId.CopyName, "COPY NAME", "Copy Name", "fileText", "Copy file names as text, without their folders, one per line for a selection."),
        new(FileActionId.OpenWith, "OPEN WITH", "Open With", "appWindow", "Open the files in a program you choose: the ones Windows suggests for them, or any other."),
        new(FileActionId.OpenInCode, "VS CODE", "Open in VS Code", "code", "Open files or a folder in Visual Studio Code."),
        new(FileActionId.Terminal, "TERMINAL", "Terminal", "terminal", "Open a folder, or a file's containing folder, in Windows Terminal."),
        new(FileActionId.Checksum, "CHECK", "Check File", "fileCheck", "See a file's SHA-256 and whether it matches what its publisher says, to know a download is the real one."),
        new(FileActionId.Duplicates, "DUPES", "Find Duplicates", "copyX", "Find identical files, and photos or videos copied at another size, in a selection or folder. Extra copies go to the Recycle Bin."),
    ];
}
