using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Petal.Shell;

namespace Petal.Tests;

/// <summary>
/// File Explorer's thumbnails for design files: the preview each kind of file carries is found and read
/// at the size asked for, and Windows itself, through its thumbnail process, gets a picture from the
/// .NET Framework 4.8 handler Petal registers.
/// </summary>
public sealed class ExplorerThumbnailTests
{
    private static string Data(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testdata")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "testdata", name);
    }

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>A picture in two colours, left half red and right half blue, so a reader that mixes it up shows.</summary>
    private static Bitmap Sample(int w = 120, int h = 80)
    {
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.FillRectangle(Brushes.Red, 0, 0, w / 2, h);
        g.FillRectangle(Brushes.Blue, w / 2, 0, w - w / 2, h);
        return bitmap;
    }

    private static byte[] Encode(Image image, ImageFormat format)
    {
        using var stream = new MemoryStream();
        image.Save(stream, format);
        return stream.ToArray();
    }

    private static void LooksLikeSample(Bitmap picture)
    {
        var left = picture.GetPixel(picture.Width / 4, picture.Height / 2);
        var right = picture.GetPixel(picture.Width * 3 / 4, picture.Height / 2);
        Assert.True(left.R > 180 && left.B < 90, left.ToString());
        Assert.True(right.B > 180 && right.R < 90, right.ToString());
    }

    [Fact]
    public void A_Photoshop_file_gives_its_merged_picture_at_the_size_asked()
    {
        using var stream = File.OpenRead(Data("layers.psd"));
        using var picture = EmbeddedPreview.Read(stream, 96);
        Assert.NotNull(picture);
        Assert.True(Math.Max(picture.Width, picture.Height) <= 96, $"{picture.Width}×{picture.Height}");
        // Not a blank: the picture has more than one colour in it.
        var colours = new HashSet<int>();
        for (int y = 0; y < picture.Height; y += 4)
            for (int x = 0; x < picture.Width; x += 4)
                colours.Add(picture.GetPixel(x, y).ToArgb());
        Assert.True(colours.Count > 3, colours.Count.ToString());
    }

    [Fact]
    public void Photoshop_run_lengths_unpack()
    {
        // 3 literal bytes (count 2), then 0xAA repeated 4 times (count -3), then one literal.
        byte[] packed = [2, 1, 2, 3, unchecked((byte)-3), 0xAA, 0, 9];
        var row = new byte[8];
        EmbeddedPreview.UnpackBits(packed, packed.Length, row);
        Assert.Equal(new byte[] { 1, 2, 3, 0xAA, 0xAA, 0xAA, 0xAA, 9 }, row);
        // A short or broken run never writes past the row.
        var tiny = new byte[2];
        EmbeddedPreview.UnpackBits([unchecked((byte)-100), 7], 2, tiny);
        Assert.Equal(new byte[] { 7, 7 }, tiny);
    }

    [Fact]
    public void Illustrator_and_InDesign_previews_come_from_their_XMP()
    {
        using var sample = Sample();
        var base64 = Convert.ToBase64String(Encode(sample, ImageFormat.Jpeg));
        // Written the way Illustrator does: wrapped lines with &#xA; between them.
        var wrapped = string.Join("&#xA;", Enumerable.Range(0, (base64.Length + 75) / 76).Select(i => base64.Substring(i * 76, Math.Min(76, base64.Length - i * 76))));
        var file = "%PDF-1.6\n%stuff\n<x:xmpmeta><rdf:li><xmpGImg:width>120</xmpGImg:width><xmpGImg:image>" + wrapped + "</xmpGImg:image></rdf:li></x:xmpmeta>\n%%EOF";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(file));
        using var picture = EmbeddedPreview.Read(stream, 256);
        Assert.NotNull(picture);
        Assert.Equal((120, 80), (picture.Width, picture.Height));
        LooksLikeSample(picture);
        // The attribute form, as older files write it.
        Assert.NotNull(EmbeddedPreview.XmpThumbnail("<rdf:li xapGImg:image=\"" + base64 + "\"/>"));
        Assert.Null(EmbeddedPreview.XmpThumbnail("<xmpGImg:image>not base64!</xmpGImg:image>"));
    }

    [Fact]
    public void An_EPS_file_shows_its_TIFF_preview()
    {
        using var sample = Sample();
        var tiff = Encode(sample, ImageFormat.Tiff);
        var postScript = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 120 80\n");
        var header = new byte[30];
        new byte[] { 0xC5, 0xD0, 0xD3, 0xC6 }.CopyTo(header, 0);
        BitConverter.GetBytes(30).CopyTo(header, 4);
        BitConverter.GetBytes(postScript.Length).CopyTo(header, 8);
        BitConverter.GetBytes(30 + postScript.Length).CopyTo(header, 20);
        BitConverter.GetBytes(tiff.Length).CopyTo(header, 24);
        header[28] = header[29] = 0xFF;
        using var stream = new MemoryStream([.. header, .. postScript, .. tiff]);
        using var picture = EmbeddedPreview.Read(stream, 64);
        Assert.NotNull(picture);
        Assert.Equal(64, picture.Width);
        LooksLikeSample(picture);
    }

    [Fact]
    public void Krita_and_OpenRaster_files_show_their_merged_image()
    {
        using var sample = Sample();
        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimetype = zip.CreateEntry("mimetype");
            using (var w = new StreamWriter(mimetype.Open()))
                w.Write("application/x-krita");
            var merged = zip.CreateEntry("mergedimage.png");
            using var s = merged.Open();
            s.Write(Encode(sample, ImageFormat.Png));
        }
        zipped.Position = 0;
        using var picture = EmbeddedPreview.Read(zipped, 256);
        Assert.NotNull(picture);
        LooksLikeSample(picture);
    }

    [Fact]
    public void Files_without_a_preview_give_nothing()
    {
        Assert.Null(EmbeddedPreview.Read(new MemoryStream(Encoding.ASCII.GetBytes("just some words, no pictures at all")), 128));
        Assert.Null(EmbeddedPreview.Read(new MemoryStream([0x38, 0x42, 0x50, 0x53, 0, 1]), 128));
        Assert.Null(EmbeddedPreview.Read(new MemoryStream([]), 128));
    }

    /// <summary>
    /// The handler's place is registered the way .NET Framework writes it itself, nothing escaped: the
    /// runtime reads "%20" as those three characters, not as a space.
    /// </summary>
    [Fact]
    public void The_handlers_place_is_written_with_nothing_escaped()
    {
        Assert.Equal("file:///C:/Users/Ayşe Gül/AppData/Local/Programs/Petal/shell/Petal.Shell.dll",
            Petal.App.Shell.ExplorerThumbnails.CodeBase(@"C:\Users\Ayşe Gül\AppData\Local\Programs\Petal\shell\Petal.Shell.dll"));
        Assert.Equal("file:///D:/100% #1/يحيى/Petal.Shell.dll", Petal.App.Shell.ExplorerThumbnails.CodeBase(@"D:\100% #1\يحيى\Petal.Shell.dll"));
        Assert.Equal("file://server/share/Petal/shell/Petal.Shell.dll", Petal.App.Shell.ExplorerThumbnails.CodeBase(@"\\server\share\Petal\shell\Petal.Shell.dll"));
    }

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    /// <summary>A class of the test's own: what DropWheel registered on this PC for its thumbnails is never touched.</summary>
    private const string TestClassId = "{BC5AF004-CA5F-489B-B057-D27F7E048259}";

    // How Windows' thumbnail cache is asked (WTS_FLAGS): the handler is run now, and its picture isn't kept.
    private const int Afresh = 0x4 /* WTS_FORCEEXTRACTION */ | 0x20 /* WTS_EXTRACTDONOTCACHE */;
    private const int InThisProcess = 0x100; // WTS_EXTRACTINPROC

    /// <summary>
    /// Windows' thumbnail cache, the one File Explorer asks, is asked for the picture of a file whose type
    /// and class are registered for the test only, by the code that registers DropWheel's own: Windows finds
    /// the handler, runs it in its thumbnail process, and gets the Photoshop file's picture. The handler sits
    /// in a folder named as a user's own can be, with spaces, Arabic and Turkish letters, a # and a %.
    /// </summary>
    [Fact]
    public void Windows_gets_the_picture_from_the_handler()
    {
        var built = Path.Combine(Repo(), "src", "Petal.Shell", "bin", Configuration, "net48", "Petal.Shell.dll");
        Assert.True(File.Exists(built), built);
        const string ext = ".petalthumbtest";
        var clsidPath = $@"Software\Classes\CLSID\{TestClassId}";
        const string approvals = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached";
        // What an earlier run couldn't delete, Windows' thumbnail process still holding the handler.
        foreach (var old in Directory.GetDirectories(Path.GetTempPath(), "petal-thumb-*"))
            if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddHours(-1))
                Delete(old);
        var folder = Path.Combine(Path.GetTempPath(), "petal-thumb-" + Guid.NewGuid().ToString("N")[..8]);
        var dll = Path.Combine(folder, "يحيى Ayşe Gül #1 100%", "Petal.Shell.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.Copy(built, dll);
        var file = Path.Combine(folder, "layers" + ext);
        File.Copy(Data("layers.psd"), file);
        try
        {
            Petal.App.Shell.ExplorerThumbnails.RegisterClass(Registry.CurrentUser, TestClassId, dll);
            using (var handler = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\ShellEx\{{e357fccd-a995-4576-b01f-234630154e96}}"))
                handler.SetValue(null, TestClassId);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

            // Started from a packaged app (the Claude app is one), what this process writes to the registry
            // goes to a copy that is the app's own, and Windows' thumbnail process never sees the class:
            // there Windows runs the handler in this process instead. All the rest is the same.
            int flags = Afresh | (WindowsSees(clsidPath + @"\InprocServer32") ? 0 : InThisProcess);
            using var got = OnSta(() =>
            {
                var iid = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(file, IntPtr.Zero, ref iid, out var item));
                var cache = (IThumbnailCache)new LocalThumbnailCache();
                Marshal.ThrowExceptionForHR(cache.GetThumbnail(item, 256, flags, out var shared, IntPtr.Zero, IntPtr.Zero));
                // The bitmap stays the cache's own: it is copied here, not deleted.
                Marshal.ThrowExceptionForHR(shared!.GetSharedBitmap(out var hbitmap));
                return Image.FromHbitmap(hbitmap);
            });
            // The file's own picture, as the reader gives it, came through Windows: not just any bitmap.
            using var stream = File.OpenRead(Data("layers.psd"));
            using var own = EmbeddedPreview.Read(stream, 256);
            Assert.NotNull(own);
            Assert.True(own.Width > 32 && own.Height > 32, $"{own.Width}×{own.Height}");
            Assert.Equal((own.Width, own.Height), (got.Width, got.Height));
            for (int y = 2; y < own.Height; y += 8)
                for (int x = 2; x < own.Width; x += 8)
                {
                    var (a, b) = (own.GetPixel(x, y), got.GetPixel(x, y));
                    Assert.True(Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2, $"At {x},{y}: {a} in the file, {b} from Windows");
                }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ext}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(clsidPath, throwOnMissingSubKey: false);
            // Windows notes each handler it let in; the test's goes with the rest.
            using (var noted = Registry.CurrentUser.OpenSubKey(approvals, writable: true))
                noted?.DeleteValue($"{TestClassId} {{E357FCCD-A995-4576-B01F-234630154E96}} 0xFFFF", throwOnMissingValue: false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            Delete(folder);
        }
    }

    /// <summary>
    /// Whether the rest of Windows sees a key this process wrote in the user's registry: WMI's registry
    /// provider is asked, which reads it in a process of Windows' own. Taken as seen when WMI can't say.
    /// </summary>
    private static bool WindowsSees(string key)
    {
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic registry = locator.ConnectServer(".", @"root\default").Get("StdRegProv");
            dynamic asked = registry.Methods_.Item("EnumValues").InParameters.SpawnInstance_();
            asked.Properties_.Item("hDefKey").Value = unchecked((int)0x80000001); // HKEY_CURRENT_USER
            asked.Properties_.Item("sSubKeyName").Value = key;
            dynamic answer = registry.ExecMethod_("EnumValues", asked);
            return (int)answer.Properties_.Item("ReturnValue").Value == 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whoever ran the handler can still hold its file: the rest goes now, and that with a later run.
    /// </summary>
    private static void Delete(string folder)
    {
        try
        {
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException("On the STA thread: " + error);
        return result;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
    }

    [ComImport, Guid("f676c15d-596a-4ce2-8234-33996f445db1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailCache
    {
        [PreserveSig]
        int GetThumbnail(IShellItem item, uint size, int flags, out ISharedBitmap? bitmap, IntPtr cacheFlags, IntPtr id);
    }

    [ComImport, Guid("091162a4-bc96-411f-aae8-c5122cd03363"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISharedBitmap
    {
        [PreserveSig]
        int GetSharedBitmap(out IntPtr bitmap);
    }

    [ComImport, Guid("50ef4544-ac9f-4a8e-b21b-8a26180db13f")]
    private class LocalThumbnailCache
    {
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
