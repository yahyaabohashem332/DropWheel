using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Petal.Shell;

/// <summary>
/// File Explorer's thumbnail for design files Windows can't draw. Explorer loads it in its separate
/// thumbnail process (never inside Explorer itself), hands it the file as a stream, and asks for a
/// picture of a given size; it answers with the preview the file carries.
/// </summary>
[ComVisible(true)]
[Guid(ClassId)]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("Petal.Thumbnails")]
public sealed class ThumbnailProvider : IInitializeWithStream, IThumbnailProvider
{
    public const string ClassId = "8F4D2C1A-3B7E-4C55-9A1E-6D0B7E2F9C34";

    private IStream? _stream;

    public void Initialize(IStream stream, int mode)
    {
        if (_stream is not null)
            throw new COMException("Already initialised", unchecked((int)0x8007139F)); // HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED)
        _stream = stream;
    }

    public void GetThumbnail(int size, out IntPtr bitmap, out int alpha)
    {
        bitmap = IntPtr.Zero;
        alpha = 0;
        var source = _stream ?? throw new COMException("Not initialised", unchecked((int)0x8000FFFF));
        try
        {
            using var stream = new ComStream(source);
            using var picture = EmbeddedPreview.Read(stream, size) ?? throw new COMException("No preview in the file", unchecked((int)0x80004005));
            bitmap = ToHBitmap(picture);
            alpha = 2; // WTSAT_ARGB
        }
        finally
        {
            Marshal.ReleaseComObject(source);
            _stream = null;
        }
    }

    /// <summary>A 32-bit top-down DIB with premultiplied alpha, as the shell wants it.</summary>
    internal static IntPtr ToHBitmap(Bitmap picture)
    {
        int w = picture.Width, h = picture.Height;
        var info = new BITMAPINFO
        {
            biSize = Marshal.SizeOf<BITMAPINFO>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
        };
        var dib = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
            throw new COMException("No memory for the thumbnail", unchecked((int)0x8007000E));
        try
        {
            using var copy = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(copy))
                g.DrawImage(picture, new Rectangle(0, 0, w, h));
            var data = copy.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    Marshal.Copy(row, 0, bits + y * w * 4, row.Length);
                }
            }
            finally
            {
                copy.UnlockBits(data);
            }
            return dib;
        }
        catch
        {
            // The bitmap goes back to Windows, not left behind in Explorer's thumbnail process.
            DeleteObject(dib);
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    /// <summary>A COM stream as a .NET stream, for reading and seeking.</summary>
    private sealed class ComStream(IStream stream) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                stream.Stat(out var stat, 1); // STATFLAG_NONAME
                return stat.cbSize;
            }
        }

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
                return 0;
            var chunk = offset == 0 ? buffer : new byte[count];
            var read = Marshal.AllocCoTaskMem(4);
            try
            {
                stream.Read(chunk, count, read);
                int n = Marshal.ReadInt32(read);
                if (offset != 0)
                    Buffer.BlockCopy(chunk, 0, buffer, offset, n);
                _position += n;
                return n;
            }
            finally
            {
                Marshal.FreeCoTaskMem(read);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var result = Marshal.AllocCoTaskMem(8);
            try
            {
                stream.Seek(offset, (int)origin, result);
                _position = Marshal.ReadInt64(result);
                return _position;
            }
            finally
            {
                Marshal.FreeCoTaskMem(result);
            }
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    void Initialize(IStream stream, int mode);
}

[ComImport]
[Guid("e357fccd-a995-4576-b01f-234630154e96")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IThumbnailProvider
{
    void GetThumbnail(int size, out IntPtr bitmap, out int alpha);
}
