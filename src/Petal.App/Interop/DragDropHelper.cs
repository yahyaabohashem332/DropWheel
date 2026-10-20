using System.Runtime.InteropServices;
using System.Windows;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Petal.App.Interop;

[ComImport]
[Guid("4657278B-411B-11D2-839A-00C04FD918D0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTargetHelper
{
    void DragEnter(IntPtr hwndTarget, ComTypes.IDataObject pDataObject, ref NativeMethods.POINT ppt, int dwEffect);
    void DragLeave();
    void DragOver(ref NativeMethods.POINT ppt, int dwEffect);
    void Drop(ComTypes.IDataObject pDataObject, ref NativeMethods.POINT ppt, int dwEffect);
    void Show([MarshalAs(UnmanagedType.Bool)] bool fShow);
}

/// <summary>
/// Keeps Explorer's drag thumbnail visible over the wheel and shows a caption under the
/// cursor such as "Convert to PNG", the same way File Explorer labels its own drops.
/// </summary>
internal sealed class DragDropHelper
{
    private static readonly Guid ClsidDragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    private readonly IDropTargetHelper? _helper;

    public DragDropHelper()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidDragDropHelper);
            _helper = type is null ? null : Activator.CreateInstance(type) as IDropTargetHelper;
        }
        catch
        {
            _helper = null;
        }
    }

    public void Enter(IntPtr hwnd, IDataObject data, NativeMethods.POINT pt, DragDropEffects effect)
    {
        if (_helper is null || data is not ComTypes.IDataObject com) return;
        try { _helper.DragEnter(hwnd, com, ref pt, (int)effect); } catch { /* cosmetic */ }
    }

    public void Over(NativeMethods.POINT pt, DragDropEffects effect)
    {
        if (_helper is null) return;
        try { _helper.DragOver(ref pt, (int)effect); } catch { /* cosmetic */ }
    }

    public void Leave()
    {
        if (_helper is null) return;
        try { _helper.DragLeave(); } catch { /* cosmetic */ }
    }

    public void Drop(IDataObject data, NativeMethods.POINT pt, DragDropEffects effect)
    {
        if (_helper is null || data is not ComTypes.IDataObject com) return;
        try { _helper.Drop(com, ref pt, (int)effect); } catch { /* cosmetic */ }
    }

    // DROPDESCRIPTION { DROPIMAGETYPE type; WCHAR szMessage[MAX_PATH]; WCHAR szInsert[MAX_PATH]; }
    private const int DropImageCopy = 1;
    private const int DropImageNoImage = -1;
    private const int DropImageInvalid = -1;

    /// <summary>Sets the caption under the cursor. <paramref name="insert"/> replaces "%1" in <paramref name="message"/>.</summary>
    public static void SetDescription(IDataObject data, string? message, string? insert = null)
    {
        if (data is not ComTypes.IDataObject com) return;
        try
        {
            const int maxPath = 260;
            int size = 4 + maxPath * 2 * 2;
            var hglobal = Marshal.AllocHGlobal(size);
            var bytes = new byte[size];
            BitConverter.GetBytes(message is null ? DropImageInvalid : DropImageCopy).CopyTo(bytes, 0);
            if (message is not null)
            {
                var m = System.Text.Encoding.Unicode.GetBytes(Truncate(message, maxPath - 1));
                Array.Copy(m, 0, bytes, 4, m.Length);
            }
            if (insert is not null)
            {
                var i = System.Text.Encoding.Unicode.GetBytes(Truncate(insert, maxPath - 1));
                Array.Copy(i, 0, bytes, 4 + maxPath * 2, i.Length);
            }
            Marshal.Copy(bytes, 0, hglobal, size);

            var format = new ComTypes.FORMATETC
            {
                cfFormat = (short)DataFormats.GetDataFormat("DropDescription").Id,
                dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = ComTypes.TYMED.TYMED_HGLOBAL,
            };
            var medium = new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = hglobal };
            com.SetData(ref format, ref medium, true);
        }
        catch
        {
            // Sources that do not use the shell drag image ignore descriptions.
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
