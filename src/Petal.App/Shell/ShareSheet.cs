using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;

namespace Petal.App.Shell;

/// <summary>Opens the Windows share sheet (Nearby Sharing, Mail, Teams…) for files.</summary>
public static class ShareSheet
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    private static Window? _host;

    public static void Show(IReadOnlyList<string> paths)
    {
        try
        {
            // The share sheet anchors to a window; use an invisible one near the cursor.
            _host?.Close();
            Interop.NativeMethods.GetCursorPos(out var pt);
            var host = new Window
            {
                Width = 1,
                Height = 1,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Opacity = 0,
                Left = pt.X,
                Top = pt.Y,
                Title = "Share",
            };
            host.Show();
            _host = host;
            var hwnd = new WindowInteropHelper(host).Handle;

            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DataTransferManagerIid;
            var manager = MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(hwnd, ref iid));
            manager.DataRequested += async (sender, args) =>
            {
                var deferral = args.Request.GetDeferral();
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var p in paths)
                    {
                        if (Directory.Exists(p))
                            items.Add(await StorageFolder.GetFolderFromPathAsync(p));
                        else if (File.Exists(p))
                            items.Add(await StorageFile.GetFileFromPathAsync(p));
                    }
                    args.Request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} items";
                    args.Request.Data.SetStorageItems(items);
                }
                finally
                {
                    deferral.Complete();
                }
            };
            interop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Opening the share sheet");
        }
    }
}
