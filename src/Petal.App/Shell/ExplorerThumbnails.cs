using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Petal.App.Shell;

/// <summary>
/// Thumbnails and the preview pane in File Explorer for files Windows can't draw: design files (Photoshop,
/// Illustrator, EPS, InDesign, Krita, OpenRaster, Sketch, Procreate), PDFs, 3D models, EPUBs, comic books, SVGs, and
/// Office files saved with a thumbnail. Petal.Shell.dll, built for .NET Framework 4.8, is registered for the current
/// user only, for the types that have no handler of that kind already, and Windows runs it in its own thumbnail and
/// preview processes. Turning it off removes exactly what was added.
/// </summary>
public static class ExplorerThumbnails
{
    public const string ClassId = "{8F4D2C1A-3B7E-4C55-9A1E-6D0B7E2F9C34}";
    public const string PreviewClassId = "{5D3A9E71-2C4B-4F86-A0D9-7B1E3C6F8A52}";
    private const string ThumbnailHandler = "{e357fccd-a995-4576-b01f-234630154e96}";
    private const string PreviewHandler = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const string ClassName = "Petal.Shell.ThumbnailProvider";
    private const string PreviewClassName = "Petal.Shell.PreviewHandler";
    private const string AssemblyName = "Petal.Shell, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    /// <summary>Windows' own process for preview handlers (prevhost.exe, 64-bit), which runs DropWheel's.</summary>
    private const string PreviewHost = "{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}";

    /// <summary>Where Windows lists the preview handlers it may run.</summary>
    private const string PreviewList = @"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers";

    /// <summary>The kinds of file it draws thumbnails of.</summary>
    public static readonly string[] Extensions =
    [
        ".psd", ".psb", ".pdd", ".ai", ".eps", ".epsf", ".indd", ".indt", ".kra", ".ora", ".sketch", ".procreate",
        ".pdf", ".svg", ".epub", ".cbz", ".stl", ".obj", ".ply", ".3mf", ".glb",
        ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".pptx", ".pptm", ".ppsx", ".odt", ".ods", ".odp", ".odg",
    ];

    /// <summary>The kinds of file it shows in the preview pane: those with more than a thumbnail to show.</summary>
    public static readonly string[] PreviewExtensions =
    [
        ".psd", ".psb", ".pdd", ".ai", ".eps", ".epsf", ".indd", ".indt", ".kra", ".ora", ".sketch", ".procreate",
        ".pdf", ".svg", ".epub", ".cbz", ".stl", ".obj", ".ply", ".3mf", ".glb",
    ];

    /// <summary>Where the handler is, beside Petal.</summary>
    public static string HandlerPath => Path.Combine(AppContext.BaseDirectory, "shell", "Petal.Shell.dll");

    /// <summary>
    /// Makes the registration match the setting: added (or pointed at this copy of Petal) when on,
    /// removed when off. The Store's copy leaves Explorer alone, since its package can't carry the handler.
    /// </summary>
    public static void Apply(bool on)
    {
        try
        {
            if (on && !Packaged.IsPackaged && File.Exists(HandlerPath))
                Register(Registry.CurrentUser, HandlerPath);
            else
                Unregister(Registry.CurrentUser);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error(ex, "Setting up thumbnails in File Explorer");
        }
    }

    /// <summary>The types that showed Petal's thumbnails, for Settings; empty when it is off.</summary>
    public static IReadOnlyList<string> Covered(RegistryKey root) =>
        Extensions.Where(ext => Value(root, $@"Software\Classes\{ext}\ShellEx\{ThumbnailHandler}") == ClassId).ToList();

    /// <summary>The types Petal shows in the preview pane; empty when it is off.</summary>
    public static IReadOnlyList<string> Previewed(RegistryKey root) =>
        PreviewExtensions.Where(ext => Value(root, $@"Software\Classes\{ext}\ShellEx\{PreviewHandler}") == PreviewClassId).ToList();

    /// <summary>A key's default value, the key closed straight after.</summary>
    private static string? Value(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(null) as string;
    }

    /// <summary>
    /// Where the handler is, written the way .NET Framework writes it itself (Assembly.CodeBase, which
    /// regasm /codebase registers): a file address with nothing escaped. The runtime takes it letter for
    /// letter, so the "%20" a <see cref="Uri"/> writes for a space is a folder that isn't there, and no
    /// thumbnail came for anyone whose user name has a space or a letter outside ASCII.
    /// </summary>
    internal static string CodeBase(string dll)
    {
        var path = Path.GetFullPath(dll).Replace('\\', '/');
        // A network path has its two slashes already: file://server/share/…
        return "file:" + (path.StartsWith("//", StringComparison.Ordinal) ? path : "///" + path);
    }

    /// <summary>
    /// Writes the class Windows asks for: mscoree, which finds the handler by these values. Those under the
    /// version are the ones the runtime reads when they are there, so both say the same. True when the
    /// handler's place changed.
    /// </summary>
    internal static bool RegisterClass(RegistryKey root, string classId, string dll) => RegisterClass(root, classId, dll, ClassName, "DropWheel thumbnails", "Both");

    /// <param name="threading">"Both" for thumbnails, which Windows calls from any thread; "Apartment" for the preview, a window.</param>
    internal static bool RegisterClass(RegistryKey root, string classId, string dll, string className, string description, string threading)
    {
        bool changed = false;
        var codeBase = CodeBase(dll);
        using var clsid = root.CreateSubKey($@"Software\Classes\CLSID\{classId}");
        clsid.SetValue(null, description);
        foreach (var key in new[] { "InprocServer32", @"InprocServer32\1.0.0.0" })
        {
            using var server = clsid.CreateSubKey(key);
            if (server.GetValue("CodeBase") as string != codeBase)
                changed = true;
            if (key == "InprocServer32")
            {
                server.SetValue(null, "mscoree.dll");
                server.SetValue("ThreadingModel", threading);
            }
            server.SetValue("Class", className);
            server.SetValue("Assembly", AssemblyName);
            server.SetValue("RuntimeVersion", "v4.0.30319");
            server.SetValue("CodeBase", codeBase);
        }
        return changed;
    }

    internal static void Register(RegistryKey root, string dll)
    {
        bool changed = RegisterClass(root, ClassId, dll);
        changed |= RegisterClass(root, PreviewClassId, dll, PreviewClassName, "DropWheel previews", "Apartment");
        using (var clsid = root.CreateSubKey($@"Software\Classes\CLSID\{PreviewClassId}"))
        {
            clsid.SetValue("AppID", PreviewHost);
            clsid.SetValue("DisplayName", "DropWheel previews");
        }
        using (var list = root.CreateSubKey(PreviewList))
            if (list.GetValue(PreviewClassId) as string != "DropWheel previews")
            {
                list.SetValue(PreviewClassId, "DropWheel previews");
                changed = true;
            }
        changed |= Claim(root, Extensions, ThumbnailHandler, ClassId);
        changed |= Claim(root, PreviewExtensions, PreviewHandler, PreviewClassId);
        if (changed)
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Each type given Petal's handler of a kind, unless another program has one for it (then Petal steps aside).</summary>
    private static bool Claim(RegistryKey root, string[] extensions, string handler, string classId)
    {
        bool changed = false;
        foreach (var ext in extensions)
        {
            var ours = $@"Software\Classes\{ext}\ShellEx\{handler}";
            if (HasOtherHandler(ext, handler, classId))
            {
                // A program that draws these came later: Petal steps aside for it.
                if (Value(root, ours) == classId)
                {
                    root.DeleteSubKey(ours, throwOnMissingSubKey: false);
                    changed = true;
                }
                continue;
            }
            using var key = root.CreateSubKey(ours);
            if (key.GetValue(null) as string != classId)
            {
                key.SetValue(null, classId);
                changed = true;
            }
        }
        return changed;
    }

    internal static void Unregister(RegistryKey root)
    {
        bool changed = false;
        foreach (var (extensions, handler, classId) in new[] { (Extensions, ThumbnailHandler, ClassId), (PreviewExtensions, PreviewHandler, PreviewClassId) })
            foreach (var ext in extensions)
            {
                var path = $@"Software\Classes\{ext}\ShellEx\{handler}";
                using var key = root.OpenSubKey(path);
                if (key?.GetValue(null) as string != classId)
                    continue;
                key.Dispose();
                root.DeleteSubKey(path, throwOnMissingSubKey: false);
                // Leave nothing empty behind that Petal made.
                DeleteIfEmpty(root, $@"Software\Classes\{ext}\ShellEx");
                DeleteIfEmpty(root, $@"Software\Classes\{ext}");
                changed = true;
            }
        foreach (var classId in new[] { ClassId, PreviewClassId })
            if (root.OpenSubKey($@"Software\Classes\CLSID\{classId}") is { } existing)
            {
                existing.Dispose();
                root.DeleteSubKeyTree($@"Software\Classes\CLSID\{classId}", throwOnMissingSubKey: false);
                changed = true;
            }
        using (var list = root.OpenSubKey(PreviewList, writable: true))
            if (list?.GetValue(PreviewClassId) is not null)
            {
                list.DeleteValue(PreviewClassId, throwOnMissingValue: false);
                changed = true;
            }
        if (changed)
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private static void DeleteIfEmpty(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        if (key is { SubKeyCount: 0, ValueCount: 0 })
        {
            key.Dispose();
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// Whether another program already has a handler of this kind (thumbnail or preview) for this type: on the
    /// extension itself, on the program it opens with, or on its kind of file. Petal never replaces one.
    /// </summary>
    private static bool HasOtherHandler(string ext, string handler, string classId)
    {
        // The machine's own registration too: once Petal's is in the user's, the merged view shows Petal's
        // over one a program installed for everyone later.
        bool Other(string path) =>
            new[] { Value(Registry.ClassesRoot, $@"{path}\ShellEx\{handler}"), Value(Registry.LocalMachine, $@"Software\Classes\{path}\ShellEx\{handler}") }
                .Any(h => h is { Length: > 0 } && !h.Equals(classId, StringComparison.OrdinalIgnoreCase));
        if (Other(ext) || Other($@"SystemFileAssociations\{ext}"))
            return true;
        foreach (var root in new[] { Registry.ClassesRoot, Registry.LocalMachine })
        {
            var progId = Value(root, root == Registry.LocalMachine ? $@"Software\Classes\{ext}" : ext);
            if (progId is { Length: > 0 } && Other(progId))
                return true;
        }
        return false;
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
