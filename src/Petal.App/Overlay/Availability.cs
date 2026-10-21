using System.IO;
using Petal.App.Localization;
using Petal.Core.Actions;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Models;
using Petal.Core.Tools;

namespace Petal.App.Overlay;

/// <summary>
/// Petals that can't do their job yet stay on the wheel, dim, and say why in the centre: an AI tool whose
/// model hasn't been downloaded (choosing it still opens its window, where the download is), Open in VS Code
/// without VS Code, or a conversion that needs a free helper program.
/// </summary>
public static class Availability
{
    public static WheelItem Of(WheelItem item) => item.Payload switch
    {
        ToolInfo tool when MissingModel(tool) is { } why => item with { Unavailable = why, Blocked = false },
        FileActionInfo { Id: FileActionId.OpenInCode } when CodeEditor.Path is null => item with { Unavailable = "VS Code isn't installed", Blocked = true },
        _ => item,
    };

    /// <summary>"Needs a 224 MB download first" for an AI tool whose model isn't here yet; null when it is.</summary>
    public static string? MissingModel(ToolInfo tool) => tool.Id switch
    {
        ToolId.RemoveBackground when !Petal.Engines.Imaging.BackgroundRemover.IsReady => Download(ModelCatalog.Background),
        ToolId.Transcribe when !new[] { ModelCatalog.WhisperFast, ModelCatalog.WhisperBalanced, ModelCatalog.WhisperAccurate }.Any(ModelStore.IsAvailable) => Download(ModelCatalog.WhisperFast),
        ToolId.RemoveNoise when !Petal.Engines.Media.VoiceCleaner.IsReady => Download(ModelCatalog.DeepFilter),
        ToolId.SeparateVocals when !Petal.Engines.Media.StemSplitter.IsReady => Download(ModelCatalog.Demucs),
        ToolId.EraseObject when !Petal.Engines.Imaging.ObjectEraser.IsReady => Download(ModelCatalog.Lama),
        ToolId.TranslateSubtitles when !Petal.Recipes.Translation.Translator.IsReady => Download([.. Petal.Recipes.Translation.Translator.Models]),
        _ => null,
    };

    private static string Download(params ModelInfo[] models) =>
        L.F("Needs a {0} download first", Util.Sizes.Format(models.Where(m => !ModelStore.IsAvailable(m)).Sum(m => m.Size)));

    /// <summary>
    /// Conversions these files could have but this PC can't do now, as dim petals that say what's missing:
    /// EPS and older Illustrator files need Ghostscript, slides need Office or LibreOffice.
    /// </summary>
    public static IEnumerable<WheelItem> MissingConversions(IReadOnlyCollection<FileFormat> formats, ConverterRegistry converters)
    {
        if (formats.Count == 0)
            return [];
        var available = converters.AvailableTargets(formats);
        var missing = ConversionMatrix.TargetsFor(formats).Where(t => !available.Contains(t) && !formats.All(f => f == t)).ToList();
        if (missing.Count == 0)
            return [];
        string why = formats.Any(f => f == Formats.Eps || f == Formats.Ai) && !Petal.Core.Util.Ghostscript.IsAvailable
            ? "Needs Ghostscript, which is free"
            : formats.Any(f => f.Family == FormatFamily.Slides)
                ? "Needs Microsoft Office or LibreOffice"
                : "Not available on this PC";
        return missing.Select(t => WheelItem.From(t) with { Unavailable = why, Blocked = true });
    }
}

/// <summary>Visual Studio Code, where it is installed for this user or for everyone.</summary>
public static class CodeEditor
{
    private static string? _path;
    private static bool _looked;

    public static string? Path
    {
        get
        {
            if (!_looked)
            {
                _path = Find();
                _looked = true;
            }
            return _path;
        }
    }

    /// <summary>Looks again, e.g. after VS Code was installed.</summary>
    public static void Forget() => _looked = false;

    private static string? Find()
    {
        foreach (var candidate in new[]
        {
            @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe",
            @"%ProgramFiles%\Microsoft VS Code\Code.exe",
            @"%ProgramFiles(x86)%\Microsoft VS Code\Code.exe",
        })
        {
            var path = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(path))
                return path;
        }
        // On the PATH as bin\code.cmd beside Code.exe.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var cmd = System.IO.Path.Combine(dir.Trim().Trim('"'), "code.cmd");
                if (File.Exists(cmd) && Directory.GetParent(System.IO.Path.GetDirectoryName(cmd)!) is { } root
                    && File.Exists(System.IO.Path.Combine(root.FullName, "Code.exe")))
                    return System.IO.Path.Combine(root.FullName, "Code.exe");
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }
}
