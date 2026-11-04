using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Petal.Engines.Files.Sharing;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using DataObject = System.Windows.IDataObject;

namespace Petal.App.Shell;

/// <summary>A file another app offers by name and contents rather than by path (Outlook, browsers, WhatsApp, Teams).</summary>
public sealed record VirtualFile(string Name, long? Size, bool IsFolder);

/// <summary>
/// Drags that carry no file on disk: an attachment from Outlook, a picture from a browser, a file from
/// WhatsApp or Teams, or a bare picture. Their names and contents are read while the wheel takes the drag
/// and written to a folder of Petal's own; when the drop lands on a petal they move to Downloads, the way
/// dropping them on the desktop would save them, and the petal works on them there.
/// </summary>
public static class VirtualDrop
{
    /// <summary>
    /// More than this is left to the app's own drop: it is read while the drag goes on, on the interface
    /// thread (the app hands it over only there), so a large one would hold up the drag.
    /// </summary>
    public const long MostBytes = 100L * 1024 * 1024;

    private const string GroupDescriptorW = "FileGroupDescriptorW";
    private const string GroupDescriptorA = "FileGroupDescriptor";
    private const string Contents = "FileContents";
    private const int FD_FILESIZE = 0x40;
    private const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const int DescriptorSize = 592;
    private const int AnsiDescriptorSize = 332;

    /// <summary>Where read files wait for the drop: one folder per drag.</summary>
    public static string StagingRoot => Path.Combine(Path.GetTempPath(), "Petal Drops");

    /// <summary>Where they go once dropped on a petal.</summary>
    public static string Downloads => Settings.SavedPlaces.Folder(Settings.SavedKind.FromPhone);

    /// <summary>Whether the drag offers something this can read, without reading it.</summary>
    public static bool Offers(DataObject data)
    {
        try
        {
            return data.GetDataPresent(GroupDescriptorW) || data.GetDataPresent(GroupDescriptorA) || HasPicture(data);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool HasPicture(DataObject data) =>
        data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Dib) || data.GetDataPresent("DeviceIndependentBitmapV5")
        || data.GetDataPresent(DataFormats.Bitmap);

    /// <summary>
    /// Writes what the drag carries into a new folder under <see cref="StagingRoot"/> and returns the
    /// top-level paths (files, or folders with their insides); null when there is nothing to read, it is
    /// too large, or the app won't hand it over now.
    /// </summary>
    public static IReadOnlyList<string>? Read(DataObject data)
    {
        string folder = Path.Combine(StagingRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var described = Describe(data);
            IReadOnlyList<string>? paths = described is { Count: > 0 }
                ? ReadDescribed(data, described, folder)
                : HasPicture(data) ? ReadPicture(data, folder) : null;
            if (paths is null or { Count: 0 })
                Forget(folder);
            return paths is { Count: > 0 } ? paths : null;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or OutOfMemoryException or ArgumentException or NotSupportedException)
        {
            Log.Error(ex, "Reading a drag without files");
            Forget(folder);
            return null;
        }
    }

    /// <summary>The names the drag offers, from its file group descriptor (Unicode, else ANSI).</summary>
    public static IReadOnlyList<VirtualFile>? Describe(DataObject data)
    {
        if (data.GetDataPresent(GroupDescriptorW) && data.GetData(GroupDescriptorW) is MemoryStream wide)
            return ParseDescriptor(wide.ToArray(), unicode: true);
        if (data.GetDataPresent(GroupDescriptorA) && data.GetData(GroupDescriptorA) is MemoryStream narrow)
            return ParseDescriptor(narrow.ToArray(), unicode: false);
        return null;
    }

    /// <summary>A FILEGROUPDESCRIPTOR: a count, then one FILEDESCRIPTOR (592 bytes, or 332 for ANSI) per entry.</summary>
    internal static IReadOnlyList<VirtualFile> ParseDescriptor(byte[] bytes, bool unicode)
    {
        if (bytes.Length < 4)
            return [];
        int count = BitConverter.ToInt32(bytes, 0);
        int size = unicode ? DescriptorSize : AnsiDescriptorSize;
        if (count <= 0 || count > PetalDrop.MaxFiles || 4L + (long)count * size > bytes.Length)
            return [];
        var list = new List<VirtualFile>(count);
        for (int i = 0; i < count; i++)
        {
            int at = 4 + i * size;
            int flags = BitConverter.ToInt32(bytes, at);
            int attributes = BitConverter.ToInt32(bytes, at + 36);
            long sizeHigh = (uint)BitConverter.ToInt32(bytes, at + 64);
            long sizeLow = (uint)BitConverter.ToInt32(bytes, at + 68);
            int nameAt = at + 72;
            string name = unicode
                ? Encoding.Unicode.GetString(bytes, nameAt, 520)
                : Encoding.Default.GetString(bytes, nameAt, 260);
            int end = name.IndexOf('\0');
            if (end >= 0)
                name = name[..end];
            list.Add(new VirtualFile(name, (flags & FD_FILESIZE) != 0 ? (sizeHigh << 32) | sizeLow : null, (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0));
        }
        return list;
    }

    private static List<string>? ReadDescribed(DataObject data, IReadOnlyList<VirtualFile> files, string folder)
    {
        if (files.Sum(f => f.Size ?? 0) > MostBytes)
            return null;
        if (data is not ComDataObject com)
            return null;
        short format = (short)DataFormats.GetDataFormat(Contents).Id;
        var top = new List<string>();
        long written = 0;
        Directory.CreateDirectory(folder);
        for (int i = 0; i < files.Count; i++)
        {
            if (PetalDrop.SafeRelativePath(files[i].Name) is not { } relative)
                continue;
            var target = Path.Combine(folder, relative);
            if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            if (files[i].IsFolder)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                written += WriteContents(com, format, i, target, MostBytes - written);
            }
            var first = relative.Split(Path.DirectorySeparatorChar)[0];
            var topPath = Path.Combine(folder, first);
            if (!top.Contains(topPath, StringComparer.OrdinalIgnoreCase))
                top.Add(topPath);
        }
        return top.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
    }

    /// <summary>One entry's contents, as a stream, memory or (an Outlook message) a structured storage.</summary>
    private static long WriteContents(ComDataObject data, short format, int index, string target, long allowed)
    {
        var request = new FORMATETC
        {
            cfFormat = format,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTORAGE,
        };
        data.GetData(ref request, out var medium);
        try
        {
            switch (medium.tymed)
            {
                case TYMED.TYMED_ISTREAM:
                {
                    var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                    try
                    {
                        return CopyStream(stream, target, allowed);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(stream);
                    }
                }
                case TYMED.TYMED_HGLOBAL:
                {
                    long size = (long)GlobalSize(medium.unionmember);
                    if (size > allowed)
                        throw new IOException("Too large to read during a drag.");
                    var pointer = GlobalLock(medium.unionmember);
                    try
                    {
                        var bytes = new byte[size];
                        Marshal.Copy(pointer, bytes, 0, (int)size);
                        File.WriteAllBytes(target, bytes);
                        return size;
                    }
                    finally
                    {
                        GlobalUnlock(medium.unionmember);
                    }
                }
                case TYMED.TYMED_ISTORAGE:
                {
                    var source = (IStorage)Marshal.GetObjectForIUnknown(medium.unionmember);
                    try
                    {
                        Marshal.ThrowExceptionForHR(StgCreateDocfile(target, STGM_CREATE | STGM_READWRITE | STGM_SHARE_EXCLUSIVE, 0, out var copy));
                        try
                        {
                            source.CopyTo(0, IntPtr.Zero, IntPtr.Zero, copy);
                            copy.Commit(0);
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(copy);
                        }
                        return new FileInfo(target).Length;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(source);
                    }
                }
                default:
                    return 0;
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static long CopyStream(IStream stream, string target, long allowed)
    {
        var buffer = new byte[1 << 16];
        long total = 0;
        var read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            using var output = File.Create(target);
            while (true)
            {
                stream.Read(buffer, buffer.Length, read);
                int n = Marshal.ReadInt32(read);
                if (n <= 0)
                    break;
                total += n;
                if (total > allowed)
                    throw new IOException("Too large to read during a drag.");
                output.Write(buffer, 0, n);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
        }
        return total;
    }

    /// <summary>A picture with no file (copied from a page or an image editor), saved as a PNG.</summary>
    private static List<string>? ReadPicture(DataObject data, string folder)
    {
        BitmapSource? picture = null;
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
        {
            var decoder = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            picture = decoder.Frames[0];
        }
        else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            picture = bitmap;
        }
        if (picture is null)
            return null;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Picture " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture) + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using (var file = File.Create(path))
            encoder.Save(file);
        return [path];
    }

    /// <summary>
    /// The read files, moved into Downloads under names that are free there, once the drop lands on a
    /// petal. Their staging folder is removed.
    /// </summary>
    public static IReadOnlyList<string> Keep(IReadOnlyList<string> staged)
    {
        var kept = new List<string>();
        Directory.CreateDirectory(Downloads);
        foreach (var path in staged)
        {
            var name = Path.GetFileName(path);
            if (Directory.Exists(path))
            {
                var target = Petal.Core.Jobs.OutputNaming.Unique(Downloads, name, null);
                Directory.Move(path, target);
                kept.Add(target);
            }
            else if (File.Exists(path))
            {
                var target = Petal.Core.Jobs.OutputNaming.Unique(Downloads, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name).TrimStart('.'));
                File.Move(path, target);
                kept.Add(target);
            }
        }
        if (staged.Count > 0 && Path.GetDirectoryName(staged[0]) is { } folder)
            Forget(folder);
        return kept;
    }

    /// <summary>Whether these paths are files read from a drag and still waiting for their drop.</summary>
    public static bool IsStaged(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(p => p.StartsWith(StagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>Drops a drag's folder of read files (the drop went elsewhere, or was cancelled).</summary>
    public static void Forget(IReadOnlyList<string> staged)
    {
        if (IsStaged(staged) && Path.GetDirectoryName(staged[0]) is { } folder)
            Forget(folder);
    }

    private static void Forget(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && folder.StartsWith(StagingRoot, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Folders left behind by drags of earlier sessions (Petal closed mid-drag).</summary>
    public static void Tidy()
    {
        try
        {
            if (!Directory.Exists(StagingRoot))
                return;
            foreach (var old in Directory.EnumerateDirectories(StagingRoot))
                if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddHours(-1))
                    Forget(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const int STGM_CREATE = 0x1000;
    private const int STGM_READWRITE = 0x2;
    private const int STGM_SHARE_EXCLUSIVE = 0x10;

    [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorage
    {
        void CreateStream(string name, int mode, int reserved1, int reserved2, out IStream stream);
        void OpenStream(string name, IntPtr reserved1, int mode, int reserved2, out IStream stream);
        void CreateStorage(string name, int mode, int reserved1, int reserved2, out IStorage storage);
        void OpenStorage(string name, IntPtr priority, int mode, IntPtr exclude, int reserved, out IStorage storage);
        void CopyTo(int excludeCount, IntPtr excludeIids, IntPtr excludeNames, IStorage destination);
        void MoveElementTo(string name, IStorage destination, string newName, int flags);
        void Commit(int flags);
        void Revert();
        void EnumElements(int reserved1, IntPtr reserved2, int reserved3, out IntPtr enumerator);
        void DestroyElement(string name);
        void RenameElement(string oldName, string newName);
        void SetElementTimes(string name, IntPtr created, IntPtr accessed, IntPtr modified);
        void SetClass(ref Guid clsid);
        void SetStateBits(int bits, int mask);
        void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG stat, int flags);
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int StgCreateDocfile(string name, int mode, int reserved, out IStorage storage);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);
}
