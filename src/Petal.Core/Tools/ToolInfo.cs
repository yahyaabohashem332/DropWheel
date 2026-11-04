namespace Petal.Core.Tools;

public enum ToolId
{
    Compress,
    Metadata,
    EditPhoto,
    AnnotatePhoto,
    AddBackground,
    CropImage,
    RedactPhoto,
    CreatePdf,
    CreateCollage,
    NormalizeAudio,
    TrimAudio,
    AudioChannels,
    AudioVisualizer,
    BleepAudio,
    RemoveAudio,
    TrimVideo,
    CropVideo,
    ChangeSpeed,
    VideoSnapshots,
    SplitVideo,
    RedactVideo,
    JoinVideos,
    SplitPdf,
    OrganizePdf,
    MergePdfs,
    ExtractArchive,
    ResizeImage,
    Watermark,
    CopyText,
    ProtectPdf,
    NumberPages,
    SignPdf,
    EditPdf,
    ExtractImages,
    RemoveBackground,
    Transcribe,
    MakeSearchable,
    ReadAloud,
    RemoveNoise,
    SeparateVocals,
    EraseObject,
    TranslateSubtitles,
    ExportLayers,
    SlimPhotoshop,
    FileDetails,
    CollectFiles,
    SavePreview,
    Palette,
    ExtractColours,
    OrganizeFolder,
    FolderPdf,
    ContactSheet,
    FolderReport,
    GatherFiles,
    RemovePauses,
    AnimatedCaptions,
    Reframe,
    TableFromImage,
    CompareFiles,
    DescribePicture,
    MakeShorts,
    Dub,
    EditByText,
    AskFile,
    RecordingStudio,
    TranslateDocument,
    TranslatePicture,
    IdPhoto,
    StudyNotes,
    AutoBlur,
    TraceToSvg,
}

/// <summary>How a tool names what it saves next to the original.</summary>
public enum ToolOutputKind
{
    /// <summary>A file with the same extension as the input.</summary>
    SameFormat,
    /// <summary>A file with a fixed extension, see <see cref="ToolInfo.OutputExtension"/>.</summary>
    FixedFormat,
    /// <summary>A new folder holding several files.</summary>
    Folder,
}

/// <param name="WheelTitle">Short upper-case caption under the icon on the wheel.</param>
/// <param name="Icon">Key of the icon in the app's icon set.</param>
/// <param name="HasWindow">Opens a tool window to choose settings before running.</param>
/// <param name="Selection">Whether the tool works on one file, a selection, or only a selection.</param>
/// <param name="OutputSuffix">Appended to the base name, e.g. "photo Compressed.jpg".</param>
public sealed record ToolInfo(
    ToolId Id,
    string WheelTitle,
    string Name,
    string Icon,
    bool HasWindow,
    ToolSelection Selection,
    string OutputSuffix,
    ToolOutputKind OutputKind = ToolOutputKind.SameFormat,
    string? OutputExtension = null);

public enum ToolSelection
{
    /// <summary>Exactly one file.</summary>
    Single,
    /// <summary>One file or a batch, each processed on its own.</summary>
    SingleOrBatch,
    /// <summary>Two or more files combined into one result.</summary>
    MultipleOnly,
    /// <summary>A dropped folder, taken as a whole.</summary>
    Folder,
}
