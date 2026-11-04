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
        new(ResizeImage, "RESIZE", "Resize & Rotate", "resize", true, ToolSelection.SingleOrBatch, "Resized"),
        new(Watermark, "WATERMARK", "Add Watermark", "stamp", true, ToolSelection.SingleOrBatch, "Watermarked"),
        new(CopyText, "TEXT · QR", "Copy Text or QR Code", "textSelect", false, ToolSelection.Single, ""),
        new(ProtectPdf, "PASSWORD", "Password", "lock", true, ToolSelection.SingleOrBatch, "Protected"),
        new(NumberPages, "NUMBERS", "Add Page Numbers", "pageNumbers", true, ToolSelection.SingleOrBatch, "Numbered"),
        new(SignPdf, "SIGN", "Sign PDF", "signature", true, ToolSelection.Single, "Signed"),
        // Forms filled in, words retyped, marks and comments added, and what must go really taken out.
        new(EditPdf, "EDIT", "Edit PDF", "pencil", true, ToolSelection.Single, "Edited", ToolOutputKind.FixedFormat, "pdf"),
        new(ExtractImages, "IMAGES", "Extract Images", "images", false, ToolSelection.SingleOrBatch, "Images", ToolOutputKind.Folder),
        new(RemoveBackground, "REMOVE BG", "Remove Background", "eraser", true, ToolSelection.SingleOrBatch, "No Background"),
        new(Transcribe, "SUBTITLES", "Transcribe", "captions", true, ToolSelection.SingleOrBatch, "Subtitled"),
        // PDFs each get a text layer; pictures become the pages of one searchable PDF.
        new(MakeSearchable, "OCR", "Make Searchable", "scanSearch", false, ToolSelection.SingleOrBatch, "Searchable", ToolOutputKind.FixedFormat, "pdf"),
        new(ReadAloud, "LISTEN", "Read Aloud", "bookAudio", true, ToolSelection.Single, "Audio", ToolOutputKind.FixedFormat, "mp3"),
        new(RemoveNoise, "DENOISE", "Remove Noise", "micVocal", true, ToolSelection.SingleOrBatch, "Clean"),
        new(SeparateVocals, "VOCALS", "Separate Vocals", "music", true, ToolSelection.SingleOrBatch, "Stems", ToolOutputKind.Folder),
        new(EraseObject, "ERASE", "Erase Object", "brush", true, ToolSelection.Single, "Erased"),
        new(TranslateSubtitles, "TRANSLATE", "Translate Subtitles", "languages", true, ToolSelection.SingleOrBatch, "Translated"),
        // A Word, PowerPoint, Excel or PDF file in another language, its formatting kept.
        new(TranslateDocument, "TRANSLATE", "Translate Document", "languages", true, ToolSelection.SingleOrBatch, "Translated"),
        new(TranslatePicture, "TRANSLATE", "Translate Picture", "languages", true, ToolSelection.SingleOrBatch, "Translated"),
        new(ExportLayers, "LAYERS", "Export Layers", "layers", true, ToolSelection.SingleOrBatch, "Layers", ToolOutputKind.Folder),
        new(SlimPhotoshop, "HISTORY", "Remove Photoshop History", "history", false, ToolSelection.SingleOrBatch, "Slim"),
        new(FileDetails, "INFO", "File Info", "info", true, ToolSelection.Single, ""),
        new(CollectFiles, "COLLECT", "Collect Files", "folderInput", true, ToolSelection.Single, "Collected", ToolOutputKind.Folder),
        new(SavePreview, "PREVIEW", "Save Preview", "fileImage", false, ToolSelection.SingleOrBatch, "Preview", ToolOutputKind.FixedFormat, "png"),
        new(Palette, "PALETTE", "Colour Palette", "palette", true, ToolSelection.Single, ""),
        new(ExtractColours, "COLOURS", "Extract Colours", "dropper", true, ToolSelection.Single, "Colours", ToolOutputKind.FixedFormat, "ase"),
        // A dropped folder as a whole.
        new(OrganizeFolder, "ORGANIZE", "Organize Folder", "folderTree", true, ToolSelection.Folder, ""),
        new(FolderPdf, "ONE PDF", "Folder to PDF", "fileStack", true, ToolSelection.Folder, "", ToolOutputKind.FixedFormat, "pdf"),
        new(ContactSheet, "CONTACT SHEET", "Contact Sheet", "galleryThumbnails", true, ToolSelection.Folder, "Contact Sheet", ToolOutputKind.FixedFormat, "pdf"),
        new(FolderReport, "REPORT", "Folder Report", "fileSpreadsheet", true, ToolSelection.Folder, "Report", ToolOutputKind.FixedFormat, "xlsx"),
        new(GatherFiles, "GATHER", "Gather Files", "folders", true, ToolSelection.Folder, "Gathered", ToolOutputKind.Folder),
        new(RemovePauses, "PAUSES", "Remove Pauses", "scissors", true, ToolSelection.SingleOrBatch, "No pauses"),
        new(AnimatedCaptions, "CAPTION", "Animated Captions", "captions", true, ToolSelection.SingleOrBatch, "Captioned"),
        new(Reframe, "VERTICAL", "Vertical Video", "smartphone", true, ToolSelection.SingleOrBatch, "Vertical"),
        // A long video's best moments, each a short clip, in a new folder.
        new(MakeShorts, "SHORTS", "Make Shorts", "shorts", true, ToolSelection.Single, "Shorts", ToolOutputKind.Folder),
        // The speech said again in the other language (Arabic and English), in the speakers' own voices; named for that language.
        new(Dub, "DUB", "Dub", "dub", true, ToolSelection.Single, "Dubbed", ToolOutputKind.FixedFormat, "mp4"),
        // A recording cut by taking words out of what was said.
        new(EditByText, "EDIT BY TEXT", "Edit by Text", "textCut", true, ToolSelection.Single, "Edited"),
        // Questions about one document or recording, answered from its own words with the page or the minute.
        new(AskFile, "ASK FILE", "Ask a File", "fileQuestion", true, ToolSelection.Single, ""),
        // A video made cinematic: zooms, a background and frame, cuts and speed-ups (a recording's pointer drawn anew).
        new(RecordingStudio, "STUDIO", "Recording Studio", "cinematic", true, ToolSelection.Single, "Cinematic"),
        new(DescribePicture, "DESCRIBE", "Describe Picture", "eye", true, ToolSelection.Single, ""),
        new(TableFromImage, "TABLE", "Table to Excel", "sheet", false, ToolSelection.SingleOrBatch, "Table", ToolOutputKind.FixedFormat, "xlsx"),
        // Exactly two files: two versions of a document, or two pictures.
        new(CompareFiles, "COMPARE", "Compare Files", "fileDiff", false, ToolSelection.MultipleOnly, "Compared", ToolOutputKind.FixedFormat, "html"),
    ];

    private static readonly FrozenDictionary<ToolId, ToolInfo> ById = All.ToFrozenDictionary(t => t.Id);

    public static ToolInfo Get(ToolId id) => ById[id];

    // Wheel order for each family, clockwise from the top. Single and multi-file lists differ.
    private static readonly ToolId[] ImageSingle = [Compress, Metadata, EditPhoto, AnnotatePhoto, RemoveBackground, EraseObject, TranslatePicture, AddBackground, CropImage, ResizeImage, Watermark, CopyText, RedactPhoto, DescribePicture, TableFromImage, ExtractColours];
    private static readonly ToolId[] ImageMulti = [Compress, Metadata, ResizeImage, CropImage, Watermark, RemoveBackground, TranslatePicture, CreatePdf, MakeSearchable, CreateCollage, TableFromImage];
    private static readonly ToolId[] VectorMulti = [CreatePdf, CreateCollage];
    private static readonly ToolId[] AudioSingle = [Compress, Metadata, Transcribe, EditByText, AskFile, NormalizeAudio, RemoveNoise, RemovePauses, SeparateVocals, AudioVisualizer, TrimAudio, AudioChannels, BleepAudio];
    private static readonly ToolId[] AudioMulti = [Compress, Metadata, Transcribe, NormalizeAudio, RemoveNoise, RemovePauses, SeparateVocals];
    private static readonly ToolId[] VideoSingle = [Compress, Metadata, MakeShorts, Dub, EditByText, RecordingStudio, AskFile, Transcribe, AnimatedCaptions, TrimVideo, CropVideo, Reframe, RemovePauses, RemoveNoise, ChangeSpeed, VideoSnapshots, RemoveAudio, SplitVideo, RedactVideo, Watermark];
    private static readonly ToolId[] VideoMulti = [Compress, Metadata, Transcribe, AnimatedCaptions, Reframe, RemoveNoise, RemovePauses, RemoveAudio, JoinVideos, Watermark];
    private static readonly ToolId[] SubtitleTools = [TranslateSubtitles];
    private static readonly ToolId[] GifTools = [Compress, Metadata, TrimVideo, CropVideo, ChangeSpeed];
    private static readonly ToolId[] PdfSingle = [EditPdf, Compress, Metadata, AskFile, TranslateDocument, SplitPdf, OrganizePdf, ProtectPdf, NumberPages, Watermark, SignPdf, MakeSearchable, ReadAloud, ExtractImages];
    private static readonly ToolId[] DocumentTools = [AskFile, ReadAloud];
    private static readonly ToolId[] TranslatedDocumentTools = [AskFile, TranslateDocument, ReadAloud];
    private static readonly ToolId[] TranslatedSheetTools = [AskFile, TranslateDocument];
    private static readonly ToolId[] PdfMulti = [Compress, Metadata, TranslateDocument, SplitPdf, MergePdfs, ProtectPdf, NumberPages, Watermark, MakeSearchable, ExtractImages];
    private static readonly ToolId[] ArchiveTools = [ExtractArchive];
    private static readonly ToolId[] PhotoshopSingle = [ExportLayers, FileDetails, ExtractColours, SlimPhotoshop];
    private static readonly ToolId[] PhotoshopMulti = [ExportLayers, SlimPhotoshop, CreatePdf, CreateCollage];

    /// <summary>Read-only pictures: Photoshop, camera RAW, Illustrator, EPS and SVG.</summary>
    private static ToolId[] VectorTools(IReadOnlyCollection<FileFormat> formats, bool multi)
    {
        var kinds = formats.Distinct().ToList();
        if (kinds.All(f => f == Formats.Formats.Psd || f == Formats.Formats.Psb))
            return multi ? PhotoshopMulti : PhotoshopSingle;
        if (multi)
            return VectorMulti;
        var only = kinds[0];
        if (only == Formats.Formats.Raw)
            return [FileDetails, ExtractColours];
        if (only == Formats.Formats.Ai)
            return [FileDetails, CollectFiles, ExtractColours];
        if (only == Formats.Formats.Eps)
            return [SavePreview, FileDetails];
        return [ExtractColours];
    }

    /// <summary>Design and video projects: what is inside, previews, and gathering their linked files.</summary>
    private static ToolId[] ProjectTools(IReadOnlyCollection<FileFormat> formats, bool multi)
    {
        var kinds = formats.Distinct().ToList();
        if (multi)
            return kinds.All(f => f == Formats.Formats.Indd || f == Formats.Formats.Xd) ? [SavePreview] : [];
        var only = kinds[0];
        if (only == Formats.Formats.Indd)
            return [FileDetails, CollectFiles, SavePreview];
        if (only == Formats.Formats.Idml)
            return [FileDetails, CollectFiles];
        if (only == Formats.Formats.Xd)
            return [SavePreview, FileDetails];
        return [CollectFiles];
    }

    /// <summary>
    /// The tools offered for a dropped folder: what works on the folder as a whole, then the batch tools
    /// for its main kind of file (<paramref name="mainFormats"/>), each run over every such file inside.
    /// Tools that join a selection into one file give way to Folder to PDF and Contact Sheet; archives
    /// anywhere inside can be extracted.
    /// </summary>
    public static IReadOnlyList<ToolInfo> ForFolder(IReadOnlyCollection<FileFormat> mainFormats, bool pictures, bool pdfs, bool archives)
    {
        var ids = new List<ToolId> { OrganizeFolder };
        if (pictures || pdfs)
            ids.Add(FolderPdf);
        if (pictures)
            ids.Add(ContactSheet);
        ids.Add(FolderReport);
        ids.Add(GatherFiles);
        if (mainFormats.Count > 0)
        {
            // Always as a batch, even when every file has the same format.
            IReadOnlyCollection<FileFormat> batch = mainFormats.Count == 1 ? [mainFormats.First(), mainFormats.First()] : mainFormats;
            foreach (var tool in ToolsFor(batch))
            {
                if (tool.Selection != ToolSelection.SingleOrBatch || ids.Contains(tool.Id))
                    continue;
                // A folder of pictures becomes one searchable PDF through Folder to PDF.
                if (tool.Id == MakeSearchable && pictures && mainFormats.All(f => f.Family is FormatFamily.Image or FormatFamily.Vector))
                    continue;
                ids.Add(tool.Id);
            }
        }
        if (archives && !ids.Contains(ExtractArchive))
            ids.Add(ExtractArchive);
        return ids.Select(Get).ToList();
    }

    /// <summary>Whether a batch tool offered for a folder works on a file of this format.</summary>
    public static bool Takes(ToolInfo tool, FileFormat format) =>
        ToolsFor([format, format]).Contains(tool) || ToolsFor([format]).Contains(tool);

    /// <summary>The tools offered on the tools wheel for a selection of files.</summary>
    public static IReadOnlyList<ToolInfo> ToolsFor(IReadOnlyCollection<FileFormat> formats)
    {
        if (formats.Count == 0)
            return [];
        var families = formats.Select(f => f.Family == FormatFamily.Vector ? FormatFamily.Image : f.Family).Distinct().ToList();
        // Two versions of a document compare whatever each is saved as (a PDF against its Word original).
        if (families.Count != 1)
            return Comparable(formats) ? [Get(CompareFiles)] : [];

        var multi = formats.Count > 1;
        bool hasVector = formats.Any(f => f.Family == FormatFamily.Vector);
        bool onlyVector = formats.All(f => f.Family == FormatFamily.Vector);
        bool onlyGif = formats.All(f => f == Formats.Formats.Gif);

        ToolId[] ids = families[0] switch
        {
            FormatFamily.Image when onlyVector => VectorTools(formats, multi),
            FormatFamily.Image when hasVector => multi ? VectorMulti : [],
            FormatFamily.Image => multi ? ImageMulti : ImageSingle,
            FormatFamily.Audio => multi ? AudioMulti : AudioSingle,
            FormatFamily.Video when onlyGif => multi ? [Metadata] : GifTools,
            FormatFamily.Video when formats.Any(f => f == Formats.Formats.Gif) => multi ? [Metadata] : [],
            FormatFamily.Video => multi ? VideoMulti : VideoSingle,
            FormatFamily.Pdf => multi ? PdfMulti : PdfSingle,
            FormatFamily.Archive => ArchiveTools,
            FormatFamily.Document or FormatFamily.Text or FormatFamily.Ebook => Translated(formats, TranslatedDocumentTools, multi ? [] : DocumentTools),
            FormatFamily.Slides or FormatFamily.Spreadsheet => Translated(formats, TranslatedSheetTools, multi ? [] : [AskFile]),
            FormatFamily.Subtitle => SubtitleTools,
            FormatFamily.Project => ProjectTools(formats, multi),
            FormatFamily.Palette => multi ? [] : [Palette],
            _ => [],
        };
        var tools = ids.Select(Get).ToList();
        if (Comparable(formats))
            tools.Add(Get(CompareFiles));
        return tools;
    }

    /// <summary>The kinds of Office file Translate Document takes, with their formatting.</summary>
    private static bool Translatable(FileFormat format) =>
        format == Formats.Formats.Docx || format == Formats.Formats.Odt || format == Formats.Formats.Rtf || format == Formats.Formats.Pptx || format == Formats.Formats.Xlsx;

    /// <summary>The tools for files that can all be translated, of which several files together get only the translation; otherwise the others.</summary>
    private static ToolId[] Translated(IReadOnlyCollection<FileFormat> formats, ToolId[] offered, ToolId[] others) =>
        !formats.All(Translatable) ? others : formats.Count > 1 ? [TranslateDocument] : offered;

    /// <summary>Two documents with words in them, or two pictures (not drawings): what Compare Files takes.</summary>
    public static bool Comparable(IReadOnlyCollection<FileFormat> formats)
    {
        if (formats.Count != 2)
            return false;
        static bool Document(FileFormat f) => f.Family is FormatFamily.Pdf or FormatFamily.Document or FormatFamily.Text or FormatFamily.Ebook;
        return formats.All(Document) || formats.All(f => f.Family == FormatFamily.Image);
    }
}
