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
    /// Windows asks for a thumbnail the way File Explorer does, for a file type registered for the test
    /// only: the handler runs in Windows' thumbnail process and answers with the Photoshop file's picture.
    /// </summary>
    [Fact]
    public void Windows_gets_the_picture_from_the_handler()
    {
        var dll = Path.Combine(Repo(), "src", "Petal.Shell", "bin", "Debug", "net48", "Petal.Shell.dll");
        Assert.True(File.Exists(dll), dll);
        const string ext = ".petalthumbtest";
        var clsidPath = $@"Software\Classes\CLSID\{Petal.App.Shell.ExplorerThumbnails.ClassId}";
        bool hadClass = Registry.CurrentUser.OpenSubKey(clsidPath) is { } k && Dispose(k);
        var file = Path.Combine(Path.GetTempPath(), "petal-thumb-" + Guid.NewGuid().ToString("N")[..8] + ext);
        File.Copy(Data("layers.psd"), file);
        try
        {
            using (var clsid = Registry.CurrentUser.CreateSubKey(clsidPath))
            using (var server = clsid.CreateSubKey("InprocServer32"))
            {
                server.SetValue(null, "mscoree.dll");
                server.SetValue("ThreadingModel", "Both");
                server.SetValue("Class", "Petal.Shell.ThumbnailProvider");
                server.SetValue("Assembly", "Petal.Shell, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
                server.SetValue("RuntimeVersion", "v4.0.30319");
                server.SetValue("CodeBase", new Uri(dll).AbsoluteUri);
            }
            using (var handler = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\ShellEx\{{e357fccd-a995-4576-b01f-234630154e96}}"))
                handler.SetValue(null, Petal.App.Shell.ExplorerThumbnails.ClassId);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

            var size = OnSta(() =>
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(file, IntPtr.Zero, ref iid, out var factory));
                int hr = factory.GetImage(new SIZE { cx = 256, cy = 256 }, 0x8 /* SIIGBF_THUMBNAILONLY */, out var hbitmap);
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    using var bitmap = Image.FromHbitmap(hbitmap);
                    return (bitmap.Width, bitmap.Height);
                }
                finally
                {
                    DeleteObject(hbitmap);
                }
            });
            Assert.True(size.Width > 32 && size.Height > 32, size.ToString());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ext}", throwOnMissingSubKey: false);
            if (!hadClass)
                Registry.CurrentUser.DeleteSubKeyTree(clsidPath, throwOnMissingSubKey: false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private static bool Dispose(RegistryKey key)
    {
        key.Dispose();
        return true;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItemImageFactory item);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
