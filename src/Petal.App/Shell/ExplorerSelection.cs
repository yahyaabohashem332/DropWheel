using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Petal.App.Shell;

/// <summary>
/// Selects files in an Explorer window (or on the desktop) that already shows their folder,
/// without opening new windows or stealing focus. Falls back to nothing if the folder is not open.
/// </summary>
public static class ExplorerSelection
{
    private const int SVSI_SELECT = 0x1, SVSI_DESELECTOTHERS = 0x4, SVSI_ENSUREVISIBLE = 0x8, SVSI_FOCUSED = 0x10;
    private const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, uint dwFlags);

    /// <summary>Opens (or focuses) Explorer at the folder with the files selected.</summary>
    public static void Reveal(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        var folder = Path.GetDirectoryName(paths[0]);
        if (folder is null)
            return;
        IntPtr folderPidl = IntPtr.Zero;
        var items = new List<IntPtr>();
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _) != 0)
                throw new IOException();
            foreach (var p in paths.Where(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase)))
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0)
                    items.Add(pidl);
            if (items.Count == 0)
                throw new IOException();
            var relative = items.Select(ILFindLastID).ToArray();
            SHOpenFolderAndSelectItems(folderPidl, (uint)relative.Length, relative, 0);
        }
        catch
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{paths[0]}\"") { UseShellExecute = true });
        }
        finally
        {
            foreach (var p in items) ILFree(p);
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
        }
    }

    /// <summary>
    /// If an open Explorer window or the desktop shows the files' folder, select the files there,
    /// the way new results appear already selected next to the original.
    /// </summary>
    public static void SelectIfVisible(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        var thread = new Thread(() =>
        {
            try
            {
                SelectInOpenViews(paths);
            }
            catch
            {
                // Purely cosmetic.
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static void SelectInOpenViews(IReadOnlyList<string> paths)
    {
        var folder = Path.GetDirectoryName(paths[0]);
        if (folder is null)
            return;
        // Give Explorer a moment to notice the new file.
        Thread.Sleep(350);

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
            return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            var views = new List<dynamic>();
            dynamic windows = shell.Windows();
            foreach (dynamic window in windows)
            {
                try
                {
                    string? location = window.Document?.Folder?.Self?.Path;
                    if (location is not null && string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        views.Add(window.Document);
                }
                catch
                {
                    // Internet Explorer windows and others without a folder view.
                }
            }

            // The desktop is not in Shell.Windows(); it shows the user's (and Public) Desktop folder.
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (string.Equals(folder.TrimEnd('\\'), desktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(folder.TrimEnd('\\'), publicDesktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                dynamic desktopWindow = windows.FindWindowSW(0, null, SWC_DESKTOP, out int _, SWFO_NEEDDISPATCH);
                if (desktopWindow?.Document is { } doc)
                    views.Add(doc);
            }

            foreach (var view in views)
            {
                bool first = true;
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    bool all = true;
                    first = true;
                    foreach (var path in paths)
                    {
                        dynamic? item = view.Folder.ParseName(Path.GetFileName(path));
                        if (item is null)
                        {
                            all = false;
                            continue;
                        }
                        int flags = SVSI_SELECT | SVSI_ENSUREVISIBLE | (first ? SVSI_DESELECTOTHERS | SVSI_FOCUSED : 0);
                        view.SelectItem(item, flags);
                        first = false;
                    }
                    if (all)
                        break;
                    Thread.Sleep(250);
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
