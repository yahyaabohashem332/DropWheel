using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Petal.App.Shell;

/// <summary>What the clipboard holds, turned into something the wheel can take.</summary>
public abstract record ClipboardContent
{
    /// <summary>Files copied in File Explorer, or a picture or text saved to a file.</summary>
    public sealed record Files(IReadOnlyList<string> Paths) : ClipboardContent;

    /// <summary>A single web address: it becomes a QR code.</summary>
    public sealed record Link(string Url) : ClipboardContent;
}

/// <summary>
/// The quick shortcut with nothing selected: the clipboard's files, its picture (saved as a PNG in
/// Pictures\Screenshots, where Windows keeps screenshots) or its text (saved in Documents\Petal).
/// </summary>
public static class ClipboardSource
{
    public static ClipboardContent? Take()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                if (files.Count > 0)
                    return new ClipboardContent.Files(files);
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                return new ClipboardContent.Files([SaveImage(image)]);
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (text.Length == 0)
                    return null;
                if (IsLink(text))
                    return new ClipboardContent.Link(text);
                return new ClipboardContent.Files([SaveText(text)]);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException)
        {
            // Another program holds the clipboard, or the folder can't be written.
            Log.Error(ex, "Reading the clipboard");
        }
        return null;
    }

    public static bool IsLink(string text) =>
        !text.Contains('\n') && !text.Contains(' ')
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "mailto" or "tel";

    private static string Stamp => DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);

    private static string SaveImage(BitmapSource image)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        Directory.CreateDirectory(folder);
        var path = Petal.Core.Jobs.OutputNaming.Unique(folder, $"Clipboard {Stamp}", "png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        return path;
    }

    private static string SaveText(string text)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Petal");
        Directory.CreateDirectory(folder);
        var path = Petal.Core.Jobs.OutputNaming.Unique(folder, $"Clipboard {Stamp}", "txt");
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
