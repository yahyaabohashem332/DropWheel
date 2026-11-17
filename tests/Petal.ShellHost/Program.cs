using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace Petal.ShellHost;

/// <summary>
/// "Petal.ShellHost.exe &lt;Petal.Shell.dll&gt; &lt;file&gt; &lt;size&gt; &lt;out.png&gt;": the picture the extension gives
/// for a file, saved as a PNG. "… preview &lt;Petal.Shell.dll&gt; &lt;file&gt; &lt;width&gt; &lt;height&gt; &lt;out.png&gt;": the
/// preview pane made for it, drawn as a picture. Exits 0 with a picture, 2 without one, 1 on an error (written out).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args[0] == "preview")
                return Preview(args);
            var shell = Assembly.LoadFrom(args[0]);
            // As Windows' process does through the handler's class, the libraries beside it are found by name.
            shell.GetType("Petal.Shell.Libraries", throwOnError: true).GetMethod("Guard", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, null);
            var read = shell.GetType("Petal.Shell.Previews", throwOnError: true).GetMethod("Read", BindingFlags.Static | BindingFlags.Public);
            using (var stream = File.OpenRead(args[1]))
            using (var picture = (Bitmap)read.Invoke(null, new object[] { stream, int.Parse(args[2]) }))
            {
                if (picture == null)
                    return 2;
                picture.Save(args[3], ImageFormat.Png);
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int Preview(string[] args)
    {
        var shell = Assembly.LoadFrom(args[1]);
        var type = shell.GetType("Petal.Shell.PreviewHandler", throwOnError: true);
        // Its class's own start, as Windows makes one: the libraries beside it are found by name.
        var handler = Activator.CreateInstance(type);
        type.GetMethod("InitializeWith").Invoke(handler, new object[] { File.ReadAllBytes(args[2]) });
        using (var picture = (Bitmap)type.GetMethod("Snapshot").Invoke(handler, new object[] { int.Parse(args[3]), int.Parse(args[4]) }))
            picture.Save(args[5], ImageFormat.Png);
        type.GetMethod("Unload").Invoke(handler, null);
        return 0;
    }
}
