using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Petal.App.Actions;

/// <summary>
/// Open With for any number of files: the programs Windows suggests for them, each opening every file at
/// once, or any other program on the PC. Nothing becomes a default.
/// </summary>
internal sealed class OpenWithWindow : ToolWindow
{
    private readonly IReadOnlyList<string> _files;

    private OpenWithWindow(IReadOnlyList<string> files) : base("Open With", 400)
    {
        _files = files;
        var extension = Path.GetExtension(files[0]);
        Body.Children.Add(Text(files.Count == 1 ? Path.GetFileName(files[0]) : L.P(files.Count, "{0} file", "{0} files"), "Petal.Section"));
        var list = new StackPanel { Margin = new Thickness(-6, 8, -6, 0) };
        var handlers = Handlers(extension);
        foreach (var handler in handlers)
            list.Children.Add(HandlerButton(handler));
        if (handlers.Count == 0)
            list.Children.Add(Text("Windows suggests no programs for these files.", "Petal.Caption").WithMargin(6, 0, 0, 0));
        Body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PrimaryButton.Content = L.T("Another Program…");
        PrimaryButton.Click += (_, _) => ChooseProgram();
        FooterNote = L.T("Nothing becomes a default.");
    }

    public static void Show(IReadOnlyList<string> files, ScreenPoint at)
    {
        if (files.Count == 0) return;
        new OpenWithWindow(files).ShowNear(at);
    }

    private Button HandlerButton(Handler handler)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement icon = handler.Icon is { } picture
            ? new Image { Source = picture, Width = 24, Height = 24 }
            : ThemedIcon("appWindow", 20);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = handler.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 });
        var button = new Button
        {
            Content = row,
            Style = (Style)FindResource("Petal.SubtleButton"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 2),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, handler.Name);
        button.Click += (_, _) =>
        {
            Close();
            Invoke(handler, _files);
        };
        return button;
    }

    /// <summary>Any program: one picked here opens every file; with a single file, Windows' own chooser is offered too.</summary>
    private void ChooseProgram()
    {
        if (_files.Count == 1)
        {
            Close();
            var info = new OPENASINFO { cszFile = _files[0], oaifInFlags = OAIF_EXEC | OAIF_HIDE_REGISTRATION };
            SHOpenWithDialog(IntPtr.Zero, ref info);
            return;
        }
        var dialog = new OpenFileDialog { Title = L.T("Program to open the files with"), Filter = L.T("Programs") + " (*.exe)|*.exe", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        if (dialog.ShowDialog(this) != true)
            return;
        Close();
        var start = new ProcessStartInfo(dialog.FileName) { UseShellExecute = false };
        foreach (var file in _files)
            start.ArgumentList.Add(file);
        try
        {
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error(ex, $"Opening with {dialog.FileName}");
        }
    }

    // ---------- Windows' list of programs for a file type ----------

    internal sealed record Handler(string Name, ImageSource? Icon, IAssocHandler Native);

    /// <summary>The programs Windows recommends for an extension, as its own Open With menu lists them.</summary>
    internal static IReadOnlyList<Handler> Handlers(string extension)
    {
        var handlers = new List<Handler>();
        if (string.IsNullOrEmpty(extension))
            return handlers;
        try
        {
            if (SHAssocEnumHandlers(extension, ASSOC_FILTER_RECOMMENDED, out var list) != 0 || list is null)
                return handlers;
            var one = new IAssocHandler[1];
            while (list.Next(1, one, out var fetched) == 0 && fetched == 1)
            {
                var native = one[0];
                native.GetUIName(out var name);
                native.GetName(out var program);
                ImageSource? icon = File.Exists(program) ? Shell.ShellIcons.Thumbnail(program, 32) : null;
                handlers.Add(new Handler(name, icon, native));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Error(ex, $"Listing programs for {extension}");
        }
        return handlers;
    }

    /// <summary>Opens all the files in one go, as Explorer does for a selection.</summary>
    private static void Invoke(Handler handler, IReadOnlyList<string> files)
    {
        var pidls = new List<IntPtr>();
        try
        {
            foreach (var file in files)
                if (SHParseDisplayName(file, IntPtr.Zero, out var pidl, 0, out _) == 0)
                    pidls.Add(pidl);
            if (pidls.Count == 0)
                return;
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Count, [.. pidls], out var items));
            items.BindToHandler(IntPtr.Zero, BHID_DataObject, typeof(ComDataObject).GUID, out var data);
            try
            {
                handler.Native.Invoke((ComDataObject)Marshal.GetObjectForIUnknown(data));
            }
            finally
            {
                Marshal.Release(data);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Log.Error(ex, $"Opening with {handler.Name}");
        }
        finally
        {
            foreach (var pidl in pidls)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    private const int ASSOC_FILTER_RECOMMENDED = 1;
    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");

    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAssocHandler
    {
        void GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
        [PreserveSig] int IsRecommended();
        void MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
        void Invoke(ComDataObject data);
        void CreateInvoker(ComDataObject data, out IntPtr invoker);
    }

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig]
        int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeParamIndex = 0)] IAssocHandler[] handlers, out uint fetched);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid handler, [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string cszFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? cszClass;
        public int oaifInFlags;
    }

    private const int OAIF_EXEC = 0x4, OAIF_HIDE_REGISTRATION = 0x20;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string extension, int filter, out IEnumAssocHandlers handlers);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IShellItemArray items);

    [DllImport("shell32.dll")]
    private static extern int SHOpenWithDialog(IntPtr owner, ref OPENASINFO info);
}
