using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Petal.Shell;

/// <summary>
/// File Explorer's preview pane (Alt+P) for the files DropWheel draws: Windows runs it in its own preview process,
/// gives it the file and a window, and it shows the file inside: a PDF's or a comic book's pages to scroll, a 3D model
/// to turn, a Photoshop file's picture beside its layers, or the picture any other file carries.
/// </summary>
[ComVisible(true)]
[Guid(ClassId)]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("Petal.Previews")]
public sealed class PreviewHandler : IPreviewHandler, IInitializeWithFile, IInitializeWithStream, IObjectWithSite, IOleWindow, IPreviewHandlerVisuals
{
    public const string ClassId = "5D3A9E71-2C4B-4F86-A0D9-7B1E3C6F8A52";

    static PreviewHandler() => Libraries.Guard();

    private byte[]? _bytes;
    private IntPtr _parent;
    private Rectangle _bounds;
    private PreviewSurface? _surface;
    private object? _site;
    private Color _background = SystemColors.Window;
    private Color _text = SystemColors.WindowText;

    /// <summary>The most of a file read for its preview.</summary>
    private const long Most = 512L * 1024 * 1024;

    public void Initialize(string path, int mode)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _bytes = PdfPages.Bytes(stream, Most);
    }

    public void Initialize(IStream stream, int mode)
    {
        try
        {
            using var wrapped = new ThumbnailProvider.ComStreamReader(stream);
            _bytes = PdfPages.Bytes(wrapped, Most);
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
        }
    }

    /// <summary>For a test: the file's bytes, as Windows would give them.</summary>
    public void InitializeWith(byte[] bytes) => _bytes = bytes;

    public void SetWindow(IntPtr hwnd, ref RECT rect)
    {
        _parent = hwnd;
        _bounds = rect.ToRectangle();
        if (_surface is { } surface)
        {
            SetParent(surface.Handle, _parent);
            surface.Bounds = _bounds;
        }
    }

    public void SetRect(ref RECT rect)
    {
        _bounds = rect.ToRectangle();
        if (_surface is { } surface)
            surface.Bounds = _bounds;
    }

    public void DoPreview()
    {
        _surface?.Dispose();
        _surface = PreviewSurface.For(_bytes ?? [], _background, _text);
        _surface.CreateControl();
        if (_parent != IntPtr.Zero)
            SetParent(_surface.Handle, _parent);
        _surface.Bounds = _bounds;
        _surface.Visible = true;
    }

    /// <summary>For a test: the preview made at a size, drawn as a picture.</summary>
    public Bitmap Snapshot(int width, int height)
    {
        _bounds = new Rectangle(0, 0, width, height);
        DoPreview();
        _surface!.Settle();
        var picture = new Bitmap(width, height);
        _surface.DrawToBitmap(picture, new Rectangle(0, 0, width, height));
        return picture;
    }

    public void Unload()
    {
        _surface?.Dispose();
        _surface = null;
        _bytes = null;
    }

    public void SetFocus() => _surface?.Focus();

    public void QueryFocus(out IntPtr hwnd) => hwnd = GetFocus();

    /// <summary>Keys the preview doesn't use go back to File Explorer (S_FALSE: not handled here).</summary>
    [PreserveSig]
    public int TranslateAccelerator(ref MSG message) => 1;

    public void SetSite(object site) => _site = site;

    public void GetSite(ref Guid riid, out object? site) => site = _site;

    public void GetWindow(out IntPtr hwnd) => hwnd = _parent;

    public void ContextSensitiveHelp(bool enter) => throw new NotImplementedException();

    public void SetBackgroundColor(uint colour) => _background = ColorTranslator.FromWin32((int)colour);

    public void SetFont(IntPtr font)
    {
    }

    public void SetTextColor(uint colour) => _text = ColorTranslator.FromWin32((int)colour);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;

    public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public IntPtr Hwnd;
    public int Message;
    public IntPtr WParam, LParam;
    public int Time, X, Y;
}

[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandler
{
    void SetWindow(IntPtr hwnd, ref RECT rect);
    void SetRect(ref RECT rect);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out IntPtr hwnd);
    [PreserveSig]
    int TranslateAccelerator(ref MSG message);
}

[ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, int mode);
}

[ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectWithSite
{
    void SetSite([MarshalAs(UnmanagedType.IUnknown)] object site);
    void GetSite(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? site);
}

[ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleWindow
{
    void GetWindow(out IntPtr hwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
}

[ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerVisuals
{
    void SetBackgroundColor(uint colour);
    void SetFont(IntPtr font);
    void SetTextColor(uint colour);
}
