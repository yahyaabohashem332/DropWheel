using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using ImageMagick.Formats;
using Petal.Core.Formats;
using Petal.Core.Jobs;
using Petal.Core.Util;
using F = Petal.Core.Formats.Formats;

namespace Petal.Engines.Imaging;

/// <param name="FullCanvas">Each layer on a transparent picture the size of the document, in its place; otherwise cropped to the layer.</param>
/// <param name="IncludeHidden">Also save the layers that are switched off.</param>
public sealed record LayerExportOptions(bool FullCanvas = false, bool IncludeHidden = false);

/// <summary>Photoshop documents: the flat picture, layers as files, and slimming the file.</summary>
public static partial class PhotoshopTools
{
    /// <summary>The document as one picture: the flat copy Photoshop saves, or the visible layers combined when it saved none.</summary>
    internal static MagickImage LoadComposite(string path, FileFormat? format)
    {
        var psd = PsdFile.Read(path);
        if (psd.HasComposite || psd.FrameCount == 0)
        {
            using var stream = ImageLoader.OpenRead(path);
            var image = new MagickImage(stream, ReadSettings(format, single: true));
            ApplyProfile(image, psd);
            return image;
        }
        using var frames = ReadLayers(path, format, psd);
        return Flatten(psd, frames);
    }

    /// <summary>The flat picture first, then every layer that has pixels, bottom first.</summary>
    internal static MagickImageCollection ReadLayers(string path, FileFormat? format, PsdFile? psd = null)
    {
        psd ??= PsdFile.Read(path);
        Stream stream = ImageLoader.OpenRead(path);
        // For files without a flat picture, ImageMagick merges the layers itself while reading, and
        // that path can corrupt memory. Petal combines the layers on its own, so ImageMagick is told
        // the flat picture is there (a one-byte change while reading; the file is untouched).
        if (!psd.HasComposite && psd.CompositeFlagOffset is long flag)
            stream = new ByteOverrideStream(stream, flag, 1);
        using (stream)
            return new MagickImageCollection(stream, ReadSettings(format, single: false));
    }

    private static MagickReadSettings ReadSettings(FileFormat? format, bool single)
    {
        var settings = new MagickReadSettings
        {
            Format = format == F.Psb ? MagickFormat.Psb : MagickFormat.Psd,
            // The colour profile belongs to the document; give it to every layer too.
            Defines = new PsdReadDefines { ReplicateProfile = true },
        };
        if (single)
        {
            settings.FrameIndex = 0;
            settings.FrameCount = 1;
        }
        return settings;
    }

    private static void ApplyProfile(MagickImage image, PsdFile psd)
    {
        if (image.GetColorProfile() is null && psd.IccProfile is { Length: > 0 } icc)
            image.SetProfile(new ColorProfile(icc));
    }

    /// <summary>The visible layers combined, for files saved without a flat copy.</summary>
    private static MagickImage Flatten(PsdFile psd, MagickImageCollection frames)
    {
        var canvas = new MagickImage(MagickColors.Transparent, (uint)psd.Width, (uint)psd.Height);
        foreach (var layer in psd.Layers)
        {
            if (layer.FrameIndex < 0 || layer.Hidden || layer.FrameIndex + 1 >= frames.Count)
                continue;
            using var pixels = Prepare((MagickImage)frames[layer.FrameIndex + 1]);
            canvas.Composite(pixels, frames[layer.FrameIndex + 1].Page.X, frames[layer.FrameIndex + 1].Page.Y, CompositeOperator.Over);
        }
        ApplyProfile(canvas, psd);
        return canvas;
    }

    /// <summary>A copy of a layer's pixels. ImageMagick has already applied the layer's opacity and mask.</summary>
    private static MagickImage Prepare(MagickImage frame)
    {
        var image = (MagickImage)frame.Clone();
        image.ResetPage();
        return image;
    }

    /// <summary>
    /// Saves each layer as a PNG named after it ("03 Header - Logo.png"), numbered from the top of
    /// the Layers panel, into <paramref name="folder"/>. Returns the files written.
    /// </summary>
    public static Task<IReadOnlyList<string>> ExportLayersAsync(string input, string folder, LayerExportOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var format = ImageFormats.Detect(input);
            var psd = PsdFile.Read(input);
            using var frames = ReadLayers(input, format, psd);
            progress?.Report(0.3);
            if (frames.Count - 1 != psd.FrameCount)
                throw new InvalidDataException("The layers of this file couldn't be matched to their names.");
            var colourProfile = frames[0].GetColorProfile() ?? (psd.IccProfile is { Length: > 0 } icc ? new ColorProfile(icc) : null);
            // Top of the panel first, like Photoshop lists them.
            var layers = psd.Layers.Where(l => l.FrameIndex >= 0 && (options.IncludeHidden || !l.Hidden)).Reverse().ToList();
            if (layers.Count == 0)
                throw new InvalidOperationException(options.IncludeHidden ? "This file has no layers with pixels." : "Every layer with pixels is hidden. Include hidden layers to save them.");
            Directory.CreateDirectory(folder);
            var written = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int digits = Math.Max(2, layers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            for (int i = 0; i < layers.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var layer = layers[i];
                var frame = (MagickImage)frames[layer.FrameIndex + 1];
                using var pixels = Prepare(frame);
                if (pixels.GetColorProfile() is null && colourProfile is not null)
                    pixels.SetProfile(colourProfile);
                ImageLoader.ToSrgb(pixels);
                MagickImage output = pixels;
                MagickImage? canvas = null;
                if (options.FullCanvas)
                {
                    canvas = new MagickImage(MagickColors.Transparent, (uint)psd.Width, (uint)psd.Height);
                    canvas.Composite(pixels, frame.Page.X, frame.Page.Y, CompositeOperator.Copy);
                    output = canvas;
                }
                try
                {
                    var name = FileName(layer, i + 1, digits);
                    var path = OutputNaming.Unique(folder, name, "png", p => used.Contains(p) || File.Exists(p));
                    used.Add(path);
                    output.Strip(); // the profile was applied; a layer needs no camera or history data
                    output.Write(path, MagickFormat.Png);
                    written.Add(path);
                }
                finally
                {
                    canvas?.Dispose();
                }
                progress?.Report(0.3 + 0.7 * (i + 1) / layers.Count);
            }
            return written;
        }, cancellationToken);

    /// <summary>"03 Header - Logo": the panel position, the groups, and the layer's name, made safe for a file name.</summary>
    internal static string FileName(PsdLayer layer, int number, int digits)
    {
        var parts = layer.Group.Append(layer.Name).Select(Clean).Where(p => p.Length > 0).ToList();
        var name = parts.Count > 0 ? string.Join(" - ", parts) : "Layer";
        if (name.Length > 120)
            name = name[..120].TrimEnd();
        return number.ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture) + " " + name;
    }

    private static string Clean(string part)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(part.Length);
        foreach (char c in part)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? ' ' : c);
        return Spaces().Replace(sb.ToString(), " ").Trim().TrimEnd('.');
    }

    /// <summary>How many earlier files Photoshop recorded inside this one (its "document ancestors").</summary>
    public static int HistoryEntries(PsdFile psd) => psd.Xmp is { Length: > 0 } xmp ? HistoryEntries(Encoding.UTF8.GetString(xmp)) : 0;

    internal static int HistoryEntries(string xmp)
    {
        var m = Ancestors().Match(xmp);
        return m.Success ? ListItem().Matches(m.Value).Count : 0;
    }

    /// <summary>
    /// A copy without the list of every file ever pasted or placed into the document, which
    /// Photoshop keeps adding to and which can make a file many times larger than its pictures.
    /// Layers and pixels are untouched. Returns the sizes before and after.
    /// </summary>
    public static async Task<(long Before, long After, int Entries)> RemoveHistoryAsync(string input, string output, CancellationToken cancellationToken = default)
    {
        int entries = HistoryEntries(PsdFile.Read(input));
        if (entries == 0)
            throw new InvalidOperationException("This file has no Photoshop history to remove.");
        if (File.Exists(output))
            File.Delete(output);
        var result = await ExifToolProcess.RunAsync(["-m", "-q", "-XMP-photoshop:DocumentAncestors=", "-o", output, input], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("The history couldn't be removed: " + result.StandardError.Trim().Split('\n').LastOrDefault());
        return (new FileInfo(input).Length, new FileInfo(output).Length, entries);
    }

    /// <summary>
    /// A copy to share: without where and on what it was made, who made it, and Photoshop's
    /// history (EXIF, GPS, IPTC, XMP). The colour profile, layers and pixels are untouched.
    /// </summary>
    public static async Task CleanForSharingAsync(string input, string output, CancellationToken cancellationToken = default)
    {
        if (File.Exists(output))
            File.Delete(output);
        var result = await ExifToolProcess.RunAsync(["-m", "-q", "-all=", "--icc_profile:all", "-o", output, input], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("The file couldn't be cleaned: " + result.StandardError.Trim().Split('\n').LastOrDefault());
    }

    [GeneratedRegex(@"<photoshop:DocumentAncestors>.*?</photoshop:DocumentAncestors>", RegexOptions.Singleline)]
    private static partial Regex Ancestors();

    [GeneratedRegex(@"<rdf:li[\s>/]")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>A read-only stream that shows one byte with another value, leaving the file as it is.</summary>
internal sealed class ByteOverrideStream(Stream inner, long offset, byte value) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offsetInBuffer, int count) => Read(buffer.AsSpan(offsetInBuffer, count));

    public override int Read(Span<byte> buffer)
    {
        long start = inner.Position;
        int n = inner.Read(buffer);
        if (offset >= start && offset < start + n)
            buffer[(int)(offset - start)] = value;
        return n;
    }

    public override long Seek(long position, SeekOrigin origin) => inner.Seek(position, origin);
    public override void Flush() { }
    public override void SetLength(long length) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offsetInBuffer, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
