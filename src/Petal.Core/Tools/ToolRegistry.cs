using System.Collections.Frozen;
using Petal.Core.Formats;
using static Petal.Core.Tools.ToolId;

namespace Petal.Core.Tools;

public static class ToolRegistry
{
    public static readonly IReadOnlyList<ToolInfo> All =
    [
        new(Compress, "COMPRESS", "Compress", "compress", true, ToolSelection.SingleOrBatch, "Compressed"),
        new(Metadata, "METADATA", "Metadata", "tag", true, ToolSelection.SingleOrBatch, "No Metadata"),
        new(EditPhoto, "EDIT", "Edit Photo", "sliders", true, ToolSelection.Single, "Edited"),
        new(AnnotatePhoto, "ANNOTATE", "Annotate Photo", "pen", true, ToolSelection.Single, "Annotated", ToolOutputKind.FixedFormat, "png"),
        new(AddBackground, "ADD BG", "Add Background", "frame", true, ToolSelection.Single, "Background"),
        new(CropImage, "CROP", "Crop Image", "crop", true, ToolSelection.SingleOrBatch, "Cropped"),
        new(RedactPhoto, "REDACT", "Redact Photo", "eyeOff", true, ToolSelection.Single, "Redacted"),
        new(CreatePdf, "CREATE PDF", "Create PDF", "filePlus", false, ToolSelection.MultipleOnly, "", ToolOutputKind.FixedFormat, "pdf"),
        new(CreateCollage, "COLLAGE", "Create Collage", "collage", true, ToolSelection.MultipleOnly, "Collage", ToolOutputKind.FixedFormat, "png"),
        new(NormalizeAudio, "NORMALIZE", "Normalize Volume", "audioLines", true, ToolSelection.SingleOrBatch, "Normalized"),
        new(TrimAudio, "TRIM", "Trim Audio", "scissors", true, ToolSelection.Single, "Trimmed"),
        new(AudioChannels, "CHANNELS", "Convert Audio Channels", "speakers", true, ToolSelection.Single, "Stereo"),
        new(AudioVisualizer, "VISUALIZE", "Audio Visualizer", "waveform", true, ToolSelection.Single, "Visualizer", ToolOutputKind.FixedFormat, "mp4"),
        new(BleepAudio, "BLEEP", "Bleep Audio", "bleep", true, ToolSelection.Single, "Bleeped"),
        new(RemoveAudio, "MUTE", "Remove Audio", "volumeOff", false, ToolSelection.SingleOrBatch, "Muted"),
        new(TrimVideo, "TRIM", "Trim Video", "scissors", true, ToolSelection.Single, "Trimmed"),
        new(CropVideo, "CROP", "Crop Video", "crop", true, ToolSelection.Single, "Cropped"),
        new(ChangeSpeed, "SPEED", "Change Video Speed", "gauge", true, ToolSelection.Single, "Speed"),
        new(VideoSnapshots, "SNAPSHOT", "Video Snapshots", "camera", true, ToolSelection.Single, "Snapshot", ToolOutputKind.FixedFormat, "png"),
        new(SplitVideo, "SPLIT", "Split Video", "film", true, ToolSelection.Single, "Clips", ToolOutputKind.Folder),
        new(RedactVideo, "REDACT", "Redact Video", "eyeOff", true, ToolSelection.Single, "Redacted"),
        new(JoinVideos, "JOIN", "Join Videos", "filmStack", true, ToolSelection.MultipleOnly, "Joined", ToolOutputKind.FixedFormat, "mp4"),
        new(SplitPdf, "SPLIT", "Split PDF", "split", false, ToolSelection.SingleOrBatch, "Pages", ToolOutputKind.Folder),
        new(OrganizePdf, "ORGANIZE", "Organize PDF", "grid", true, ToolSelection.Single, "Organized", ToolOutputKind.FixedFormat, "pdf"),
        new(MergePdfs, "MERGE PDF", "Merge PDFs", "copy", true, ToolSelection.MultipleOnly, "Merged", ToolOutputKind.FixedFormat, "pdf"),
        new(ExtractArchive, "EXTRACT", "Extract Archive", "archive", false, ToolSelection.SingleOrBatch, "", ToolOutputKind.Folder),
        new(ReadCode, "READ QR", "Read QR Code", "qr", false, ToolSelection.Single, ""),
    ];

    private static readonly FrozenDictionary<ToolId, ToolInfo> ById = All.ToFrozenDictionary(t => t.Id);

    public static ToolInfo Get(ToolId id) => ById[id];

    // Wheel order for each family, clockwise from the top. Single and multi-file lists differ.
    private static readonly ToolId[] ImageSingle = [Compress, Metadata, EditPhoto, AnnotatePhoto, AddBackground, CropImage, RedactPhoto, ReadCode];
    private static readonly ToolId[] ImageMulti = [Compress, Metadata, CropImage, CreatePdf, CreateCollage];
    private static readonly ToolId[] VectorMulti = [CreatePdf, CreateCollage];
    private static readonly ToolId[] AudioSingle = [Compress, Metadata, NormalizeAudio, AudioVisualizer, TrimAudio, AudioChannels, BleepAudio];
    private static readonly ToolId[] AudioMulti = [Compress, Metadata, NormalizeAudio];
    private static readonly ToolId[] VideoSingle = [Compress, Metadata, RemoveAudio, TrimVideo, CropVideo, ChangeSpeed, VideoSnapshots, SplitVideo, RedactVideo];
    private static readonly ToolId[] VideoMulti = [Compress, Metadata, RemoveAudio, JoinVideos];
    private static readonly ToolId[] GifTools = [Compress, Metadata, TrimVideo, CropVideo, ChangeSpeed];
    private static readonly ToolId[] PdfSingle = [Compress, Metadata, SplitPdf, OrganizePdf];
    private static readonly ToolId[] PdfMulti = [Compress, Metadata, SplitPdf, MergePdfs];
    private static readonly ToolId[] ArchiveTools = [ExtractArchive];

    /// <summary>The tools offered on the tools wheel for a selection of files.</summary>
    public static IReadOnlyList<ToolInfo> ToolsFor(IReadOnlyCollection<FileFormat> formats)
    {
        if (formats.Count == 0)
            return [];
        var families = formats.Select(f => f.Family == FormatFamily.Vector ? FormatFamily.Image : f.Family).Distinct().ToList();
        if (families.Count != 1)
            return [];

        var multi = formats.Count > 1;
        bool hasVector = formats.Any(f => f.Family == FormatFamily.Vector);
        bool onlyVector = formats.All(f => f.Family == FormatFamily.Vector);
        bool onlyGif = formats.All(f => f == Formats.Formats.Gif);

        ToolId[] ids = families[0] switch
        {
            FormatFamily.Image when onlyVector => multi ? VectorMulti : [],
            FormatFamily.Image when hasVector => multi ? VectorMulti : [],
            FormatFamily.Image => multi ? ImageMulti : ImageSingle,
            FormatFamily.Audio => multi ? AudioMulti : AudioSingle,
            FormatFamily.Video when onlyGif => multi ? [Metadata] : GifTools,
            FormatFamily.Video when formats.Any(f => f == Formats.Formats.Gif) => multi ? [Metadata] : [],
            FormatFamily.Video => multi ? VideoMulti : VideoSingle,
            FormatFamily.Pdf => multi ? PdfMulti : PdfSingle,
            FormatFamily.Archive => ArchiveTools,
            _ => [],
        };
        return ids.Select(Get).ToList();
    }
}
