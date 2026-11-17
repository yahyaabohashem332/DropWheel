using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Petal.Shell;

/// <summary>
/// A PDF's pages drawn by PDFium, the library DropWheel ships beside itself: the first page for a thumbnail, any page
/// for the preview pane. PDFium can't be used from two threads at once, so every call takes a lock.
/// </summary>
public static class PdfPages
{
    private static readonly object Gate = new();
    private static bool _ready;

    /// <summary>True when the bytes start as a PDF does (allowing a little rubbish before the header, as readers do).</summary>
    public static bool Looks(byte[] head, int count)
    {
        for (int i = 0; i + 4 < Math.Min(count, 1024); i++)
            if (head[i] == '%' && head[i + 1] == 'P' && head[i + 2] == 'D' && head[i + 3] == 'F' && head[i + 4] == '-')
                return true;
        return false;
    }

    /// <summary>A page about <paramref name="size"/> pixels on its longer side, on white; null when it can't be drawn.</summary>
    public static Bitmap? Render(byte[] pdf, int page, int size) => Render(pdf, page, size, out _);

    /// <summary>A page and how many pages the file has.</summary>
    public static Bitmap? Render(byte[] pdf, int page, int size, out int pages)
    {
        pages = 0;
        lock (Gate)
        {
            if (!Ready())
                return null;
            var pinned = GCHandle.Alloc(pdf, GCHandleType.Pinned);
            var document = IntPtr.Zero;
            try
            {
                document = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdf.Length, null);
                if (document == IntPtr.Zero)
                    return null;
                pages = FPDF_GetPageCount(document);
                if (page < 0 || page >= pages)
                    return null;
                var handle = FPDF_LoadPage(document, page);
                if (handle == IntPtr.Zero)
                    return null;
                try
                {
                    double w = FPDF_GetPageWidthF(handle), h = FPDF_GetPageHeightF(handle);
                    if (w <= 0 || h <= 0)
                        return null;
                    double scale = size / Math.Max(w, h);
                    int width = Math.Max(1, (int)Math.Round(w * scale)), height = Math.Max(1, (int)Math.Round(h * scale));
                    var bitmap = FPDFBitmap_Create(width, height, 1);
                    if (bitmap == IntPtr.Zero)
                        return null;
                    try
                    {
                        FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        // FPDF_ANNOT: the page as printed, with its notes and form fields; BGRA, as GDI+ keeps it.
                        FPDF_RenderPageBitmap(bitmap, handle, 0, 0, width, height, 0, 0x01);
                        int stride = FPDFBitmap_GetStride(bitmap);
                        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                        var data = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            var row = new byte[width * 4];
                            var buffer = FPDFBitmap_GetBuffer(bitmap);
                            for (int y = 0; y < height; y++)
                            {
                                Marshal.Copy(buffer + y * stride, row, 0, row.Length);
                                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                            }
                        }
                        finally
                        {
                            result.UnlockBits(data);
                        }
                        return result;
                    }
                    finally
                    {
                        FPDFBitmap_Destroy(bitmap);
                    }
                }
                finally
                {
                    FPDF_ClosePage(handle);
                }
            }
            finally
            {
                if (document != IntPtr.Zero)
                    FPDF_CloseDocument(document);
                pinned.Free();
            }
        }
    }

    /// <summary>A stream's bytes, for PDFium (which reads from memory); null when it is too large to draw a preview of.</summary>
    public static byte[]? Bytes(Stream stream, long most = 512L * 1024 * 1024)
    {
        if (!stream.CanSeek || stream.Length > most)
            return null;
        stream.Position = 0;
        var bytes = new byte[stream.Length];
        int at = 0;
        while (at < bytes.Length)
        {
            int n = stream.Read(bytes, at, bytes.Length - at);
            if (n <= 0)
                break;
            at += n;
        }
        stream.Position = 0;
        return at == bytes.Length ? bytes : null;
    }

    /// <summary>
    /// PDFium loaded from DropWheel's folder (beside the shell folder this library is in, or beside this library when
    /// it is built into a test), and started once.
    /// </summary>
    private static bool Ready()
    {
        if (_ready)
            return true;
        var here = Path.GetDirectoryName(typeof(PdfPages).Assembly.Location) ?? "";
        foreach (var place in new[] { Path.Combine(here, "pdfium.dll"), Path.Combine(here, "..", "pdfium.dll") })
            if (File.Exists(place) && LoadLibrary(Path.GetFullPath(place)) != IntPtr.Zero)
            {
                FPDF_InitLibrary();
                _ready = true;
                return true;
            }
        return false;
    }


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    private const string Pdfium = "pdfium.dll";

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_InitLibrary();

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadPage(IntPtr document, int index);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageHeightF(IntPtr page);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
}
