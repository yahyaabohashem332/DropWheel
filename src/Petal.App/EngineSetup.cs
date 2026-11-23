using Petal.Core.Jobs;
using Petal.Engines.Files.Archives;
using Petal.Engines.Files.Subtitles;
using Petal.Engines.Imaging;

namespace Petal.App;

/// <summary>Registers every converter. More specific engines go first so they win.</summary>
public static class EngineSetup
{
    private static int _warmed;

    /// <summary>
    /// ImageMagick's OpenCL start-up costs seconds on the first image and rarely helps; turn it off.
    /// Then, once, a few seconds after start and at low priority, load what the first conversion would
    /// otherwise wait for: ImageMagick's native library, PDFium and its map of the installed fonts
    /// (about five seconds before the first PDF page), and FFmpeg and ExifTool, read from disk.
    /// </summary>
    public static void WarmUp()
    {
        try
        {
            ImageMagick.OpenCL.IsEnabled = false;
        }
        catch
        {
            // Older builds without OpenCL support.
        }
        if (Interlocked.Exchange(ref _warmed, 1) == 1)
            return;
        new Thread(() =>
        {
            Thread.Sleep(3000);
            Try(() =>
            {
                using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 8, 8);
                image.Write(System.IO.Stream.Null, ImageMagick.MagickFormat.Png);
            });
            Try(Petal.Engines.Documents.DocumentEngine.WarmUp);
            if (Petal.Core.Util.ToolLocator.Ffmpeg is { } ffmpeg)
                Try(() => Petal.Core.Util.ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-version"]).GetAwaiter().GetResult());
            if (Petal.Core.Util.ToolLocator.ExifTool is { } exifTool)
                Try(() => Petal.Core.Util.ProcessRunner.RunAsync(exifTool, ["-ver"]).GetAwaiter().GetResult());
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Petal warm-up" }.Start();

        static void Try(Action step)
        {
            try
            {
                step();
            }
            catch
            {
                // Warm-up is best effort.
            }
        }
    }

    public static ConverterRegistry CreateConverters()
    {
        WarmUp();
        Tools.Windows.ImageToolSetup.Register();
        Tools.Windows.PdfToolSetup.Register();
        Tools.Windows.AdobeToolSetup.Register();
        Tools.Windows.FolderToolSetup.Register();
        Tools.Windows.WorkToolSetup.Register();
        var converters = new ConverterRegistry();
        converters.Register(new ImageConverter());
        converters.Register(new PaletteConverter());
        converters.Register(new Petal.Engines.Media.MediaConverter());
        converters.Register(new ArchiveConverter());
        converters.Register(new Petal.Engines.Files.Models.ModelConverter());
        converters.Register(new Petal.Engines.Files.Drawings.DrawingConverter());
        converters.Register(new SubtitleConverter());
        converters.Register(new Petal.Engines.Files.Fonts.FontConverter());
        converters.Register(new Petal.Engines.Documents.IWork.IWorkConverter());
        Petal.Engines.Documents.DocumentEngine.Register(converters);
        return converters;
    }
}
