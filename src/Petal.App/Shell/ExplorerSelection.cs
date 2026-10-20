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

    // ---------- reading the selection ----------

    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    // Only the first method (from IOleWindow) is needed: the window of a folder tab.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr hwnd);
    }

    /// <summary>
    /// The files and folders selected in a File Explorer window (its active tab) or on the desktop,
    /// given that window's handle. Empty for any other window, or when nothing is selected.
    /// </summary>
    public static Task<IReadOnlyList<string>> SelectionInAsync(IntPtr window)
    {
        var result = new TaskCompletionSource<IReadOnlyList<string>>();
        // Shell automation wants a single-threaded apartment and talks to Explorer across processes,
        // so it runs off the UI thread.
        var thread = new Thread(() =>
        {
            try
            {
                result.SetResult(SelectionIn(window));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reading the Explorer selection");
                result.SetResult([]);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return result.Task;
    }

    private static IReadOnlyList<string> SelectionIn(IntPtr window)
    {
        var kind = Interop.NativeMethods.ClassNameOf(window);
        bool desktop = kind is "Progman" or "WorkerW" && Interop.NativeMethods.FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
        if (!desktop && kind is not ("CabinetWClass" or "ExploreWClass"))
            return [];

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
            return [];
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic windows = shell.Windows();
            dynamic? document = desktop
                ? windows.FindWindowSW(0, null, SWC_DESKTOP, out int _, SWFO_NEEDDISPATCH)?.Document
                : FolderViewOf(windows, window);
            if (document is null)
                return [];
            var paths = new List<string>();
            foreach (dynamic item in document.SelectedItems())
            {
                // Virtual items (This PC, Recycle Bin, phones) have no file-system path.
                string? path = item.Path;
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                    paths.Add(path);
            }
            return paths;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>The folder view of an Explorer window; with tabs (Windows 11), the active tab's.</summary>
    private static dynamic? FolderViewOf(dynamic windows, IntPtr window)
    {
        var matches = new List<dynamic>();
        foreach (dynamic w in windows)
        {
            try
            {
                if ((long)w.HWND == window.ToInt64())
                    matches.Add(w);
            }
            catch
            {
                // Windows without a handle.
            }
        }
        if (matches.Count <= 1)
            return matches.Count == 1 ? matches[0].Document : null;

        // The active tab is the first tab window in z-order.
        var activeTab = Interop.NativeMethods.FindWindowEx(window, IntPtr.Zero, "ShellTabWindowClass", null);
        foreach (var w in matches)
        {
            if ((object)w is IServiceProvider services)
            {
                var service = SID_STopLevelBrowser;
                var iid = IID_IShellBrowser;
                if (services.QueryService(ref service, ref iid, out var browser) == 0 && browser is IShellBrowser shellBrowser
                    && shellBrowser.GetWindow(out var tab) == 0 && tab == activeTab)
                    return w.Document;
            }
        }
        return matches[0].Document;
    }
}
