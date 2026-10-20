using Petal.Core.Jobs;
using Petal.Engines.Files.Archives;
using Petal.Engines.Files.Subtitles;
using Petal.Engines.Imaging;

namespace Petal.App;

/// <summary>Registers every converter. More specific engines go first so they win.</summary>
public static class EngineSetup
{
    /// <summary>
    /// ImageMagick's OpenCL start-up costs seconds on the first image and rarely helps; turn it off
    /// and load the native library in the background so the first conversion is instant.
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
        Task.Run(() =>
        {
            try
            {
                using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 8, 8);
                image.Write(System.IO.Stream.Null, ImageMagick.MagickFormat.Png);
            }
            catch
            {
                // Warm-up is best effort.
            }
        });
    }

    public static ConverterRegistry CreateConverters()
    {
        WarmUp();
        Tools.Windows.ImageToolSetup.Register();
        Tools.Windows.PdfToolSetup.Register();
        var converters = new ConverterRegistry();
        converters.Register(new ImageConverter());
        converters.Register(new Petal.Engines.Media.MediaConverter());
        converters.Register(new ArchiveConverter());
        converters.Register(new SubtitleConverter());
        Petal.Engines.Documents.DocumentEngine.Register(converters);
        return converters;
    }
}
