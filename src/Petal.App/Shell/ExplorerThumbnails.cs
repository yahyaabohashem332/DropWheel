using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Petal.App.Shell;

/// <summary>
/// Thumbnails in File Explorer for design files Windows can't draw (Photoshop, Illustrator, EPS,
/// InDesign, Krita, OpenRaster, Sketch). Petal.Shell.dll, built for .NET Framework 4.8, is registered
/// for the current user only, for the types that have no thumbnail handler already, and Windows runs it
/// in its own thumbnail process. Turning it off removes exactly what was added.
/// </summary>
public static class ExplorerThumbnails
{
    public const string ClassId = "{8F4D2C1A-3B7E-4C55-9A1E-6D0B7E2F9C34}";
    private const string ThumbnailHandler = "{e357fccd-a995-4576-b01f-234630154e96}";
    private const string ClassName = "Petal.Shell.ThumbnailProvider";
    private const string AssemblyName = "Petal.Shell, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    /// <summary>The kinds of file it draws.</summary>
    public static readonly string[] Extensions = [".psd", ".psb", ".ai", ".eps", ".epsf", ".indd", ".indt", ".kra", ".ora", ".sketch"];

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

    /// <summary>A key's default value, the key closed straight after.</summary>
    private static string? Value(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(null) as string;
    }

    internal static void Register(RegistryKey root, string dll)
    {
        bool changed = false;
        var codeBase = new Uri(dll).AbsoluteUri;
        using (var clsid = root.CreateSubKey($@"Software\Classes\CLSID\{ClassId}"))
        {
            clsid.SetValue(null, "Petal thumbnails");
            foreach (var key in new[] { "InprocServer32", @"InprocServer32\1.0.0.0" })
            {
                using var server = clsid.CreateSubKey(key);
                if (server.GetValue("CodeBase") as string != codeBase)
                    changed = true;
                if (key == "InprocServer32")
                {
                    server.SetValue(null, "mscoree.dll");
                    server.SetValue("ThreadingModel", "Both");
                }
                server.SetValue("Class", ClassName);
                server.SetValue("Assembly", AssemblyName);
                server.SetValue("RuntimeVersion", "v4.0.30319");
                server.SetValue("CodeBase", codeBase);
            }
        }
        foreach (var ext in Extensions)
        {
            if (HasOtherHandler(ext))
            {
                // A program that draws these came later: Petal steps aside for it.
                var ours = $@"Software\Classes\{ext}\ShellEx\{ThumbnailHandler}";
                if (Value(root, ours) == ClassId)
                {
                    root.DeleteSubKey(ours, throwOnMissingSubKey: false);
                    changed = true;
                }
                continue;
            }
            using var key = root.CreateSubKey($@"Software\Classes\{ext}\ShellEx\{ThumbnailHandler}");
            if (key.GetValue(null) as string != ClassId)
            {
                key.SetValue(null, ClassId);
                changed = true;
            }
        }
        if (changed)
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    internal static void Unregister(RegistryKey root)
    {
        bool changed = false;
        foreach (var ext in Extensions)
        {
            var path = $@"Software\Classes\{ext}\ShellEx\{ThumbnailHandler}";
            using var key = root.OpenSubKey(path);
            if (key?.GetValue(null) as string != ClassId)
                continue;
            key.Dispose();
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
            // Leave nothing empty behind that Petal made.
            DeleteIfEmpty(root, $@"Software\Classes\{ext}\ShellEx");
            DeleteIfEmpty(root, $@"Software\Classes\{ext}");
            changed = true;
        }
        if (root.OpenSubKey($@"Software\Classes\CLSID\{ClassId}") is { } existing)
        {
            existing.Dispose();
            root.DeleteSubKeyTree($@"Software\Classes\CLSID\{ClassId}", throwOnMissingSubKey: false);
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
    /// Whether another program already draws thumbnails for this type: on the extension itself, on the
    /// program it opens with, or on its kind of file. Petal never replaces one.
    /// </summary>
    private static bool HasOtherHandler(string ext)
    {
        // The machine's own registration too: once Petal's is in the user's, the merged view shows Petal's
        // over one a program installed for everyone later.
        bool Other(string path) =>
            new[] { Value(Registry.ClassesRoot, $@"{path}\ShellEx\{ThumbnailHandler}"), Value(Registry.LocalMachine, $@"Software\Classes\{path}\ShellEx\{ThumbnailHandler}") }
                .Any(h => h is { Length: > 0 } && !h.Equals(ClassId, StringComparison.OrdinalIgnoreCase));
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
