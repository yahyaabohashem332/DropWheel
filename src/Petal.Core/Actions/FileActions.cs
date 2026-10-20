namespace Petal.Core.Actions;

public enum FileActionId
{
    Share,
    Phone,
    Float,
    Copy,
    Move,
    CopyPath,
    Terminal,
    Rename,
    Zip,
}

public sealed record FileActionInfo(FileActionId Id, string WheelTitle, string Name, string Icon, string Description);

public static class FileActionRegistry
{
    public static readonly IReadOnlyList<FileActionInfo> All =
    [
        new(FileActionId.Share, "SHARE", "Share", "share", "Send files and folders with Nearby Sharing or any app in the Windows share sheet."),
        new(FileActionId.Phone, "TO PHONE", "Send to Phone", "smartphone", "Show a QR code to scan with a phone on the same Wi-Fi, then save the files on it. Nothing goes through the internet."),
        new(FileActionId.Float, "FLOAT", "Float", "pin", "Keep files on a floating shelf. Pin it to a screen edge and drag files into another app."),
        new(FileActionId.Copy, "COPY", "Copy", "clipboard", "Copy files to the clipboard, ready to paste in File Explorer."),
        new(FileActionId.Move, "MOVE", "Move", "folderInput", "Choose a destination folder and move the originals there."),
        new(FileActionId.Rename, "RENAME", "Rename", "rename", "Rename a selection with a pattern, numbers and dates, previewed before anything changes."),
        new(FileActionId.Zip, "ZIP", "Compress to ZIP", "folderZip", "Pack files and folders into one ZIP archive, with an optional password."),
        new(FileActionId.CopyPath, "COPY PATH", "Copy Path", "link", "Copy full file paths as text, one path per line for a selection."),
        new(FileActionId.Terminal, "TERMINAL", "Terminal", "terminal", "Open a folder, or a file's containing folder, in Windows Terminal."),
    ];
}
