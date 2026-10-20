using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Petal.App.Shell;

/// <summary>Explorer's own thumbnails and icons for any path.</summary>
public static class ShellIcons
{
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1;

    public static ImageSource? Thumbnail(string path, int size)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, out var hbitmap) != 0)
                return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                // Shell bitmaps carry premultiplied alpha in a BGR32 container; reinterpret as BGRA.
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                return converted;
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch
        {
            return null;
        }
    }
}
