using ImageMagick;
using Petal.Engines.Imaging.Heif;

namespace Petal.Engines.Imaging;

/// <summary>
/// HEIC encoder. The pixels are coded by Windows' HEVC encoder (the graphics card's, or the HEVC Video
/// Extensions'), through FFmpeg, and wrapped in a HEIF container here: DropWheel carries no HEVC encoder of
/// its own. Up to ~8.9 MP is one coded image; larger photos become a grid of 512 px tiles exactly like iPhone
/// HEICs, which keeps every tile within HEVC level limits that phone and Windows decoders enforce.
/// </summary>
internal static class HeifWriter
{
    internal const int TileSize = 512;
    internal const long SingleImageMaxPixels = 8_912_896; // HEVC level 5.1 picture size
    /// <summary>Graphics cards' encoders refuse very small pictures; the padding is hidden by the crop.</summary>
    private const int MinCodedSize = 256;

    public static bool IsAvailable => FfmpegStillEncoder.CanEncodeHevc;

    public static async Task EncodeAsync(MagickImage image, Stream output, ImageWriteOptions options, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new NotSupportedException(FfmpegStillEncoder.NoHevcText);

        MagickImage work = image;
        bool owned = false;
        try
        {
            if (work.HasAlpha || work.Orientation is not (Orientation.TopLeft or Orientation.Undefined))
            {
                work = (MagickImage)image.Clone();
                owned = true;
                ImageWriter.FlattenOnWhite(work);
                ImageLoader.Orient(work);
            }
            await EncodeUprightAsync(work, output, options, cancellationToken);
        }
        finally
        {
            if (owned)
                work.Dispose();
        }
    }

    private static async Task EncodeUprightAsync(MagickImage image, Stream output, ImageWriteOptions options, CancellationToken cancellationToken)
    {
        int width = (int)image.Width, height = (int)image.Height;
        bool grid = (long)width * height > SingleImageMaxPixels;

        int codedW, codedH, columns = 1, rows = 1;
        if (grid)
        {
            codedW = codedH = TileSize;
            columns = (width + TileSize - 1) / TileSize;
            rows = (height + TileSize - 1) / TileSize;
            if (columns > 256 || rows > 256)
                throw new NotSupportedException("Image is too large for HEIC");
        }
        else
        {
            // 4:2:0 needs even dimensions and the encoders a few dozen pixels; the padding is hidden again
            // with a clean-aperture crop.
            codedW = Math.Max(MinCodedSize, width + (width & 1));
            codedH = Math.Max(MinCodedSize, height + (height & 1));
        }

        // HEIC pixels are always coded as colour, so only an RGB profile describes them (not a grey one).
        var icc = options.KeepColorProfile && image.GetColorProfile() is { ColorSpace: ColorSpace.sRGB } profile ? profile.ToByteArray() : null;
        // The graphics card's encoder first; should it refuse (busy, or a size it won't take), Windows' software one.
        byte[]? annexB = null;
        var modes = FfmpegStillEncoder.HevcModes;
        for (var i = 0; annexB is null; i++)
        {
            try
            {
                using var frames = new TileFrameStream(image, codedW, codedH, columns, rows);
                annexB = await FfmpegStillEncoder.EncodeHevcAsync(frames, codedW, codedH, options.Quality, options.HeicPreset,
                    signalSrgb: icc is null, cancellationToken, modes[i]);
            }
            catch (Petal.Core.Util.ExternalToolException) when (i + 1 < modes.Count && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        var stream = HevcBitstream.Parse(annexB);
        var spec = new HeifImageSpec
        {
            Width = width,
            Height = height,
            CodedWidth = codedW,
            CodedHeight = codedH,
            Columns = columns,
            Rows = rows,
            HvcC = stream.BuildHvcC(),
            Pictures = stream.Pictures,
            Exif = options.KeepMetadata ? MetadataHygiene.ExifForEmbedding(image) : null,
            Xmp = options.KeepMetadata ? image.GetXmpProfile()?.ToByteArray() : null,
            Icc = icc,
            // With an ICC profile the profile defines the colours; only the YCbCr matrix is signalled.
            ColourPrimaries = (ushort)(icc is null ? 1 : 2),
            TransferCharacteristics = (ushort)(icc is null ? 13 : 2),
            // The matrix the pixels were converted with (Windows' encoder leaves it out of the stream).
            MatrixCoefficients = 1,
        };
        HeifMuxer.Write(output, spec);
    }

    /// <summary>
    /// Streams the image to FFmpeg as raw RGB24 frames, one per tile (or one padded frame),
    /// reading a band of rows at a time so large photos aren't copied whole.
    /// </summary>
    private sealed class TileFrameStream : Stream
    {
        private readonly IPixelCollection<byte> _pixels;
        private readonly int _frameW, _frameH, _columns, _rows, _width, _height;
        private int _frame;
        private byte[] _current = [];
        private int _offset;
        private byte[]? _band;
        private int _bandRow = -1, _bandHeight;

        public TileFrameStream(MagickImage image, int frameW, int frameH, int columns, int rows)
        {
            _pixels = image.GetPixels();
            _frameW = frameW;
            _frameH = frameH;
            _columns = columns;
            _rows = rows;
            _width = (int)image.Width;
            _height = (int)image.Height;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => (long)_frameW * _frameH * 3 * _columns * _rows;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset >= _current.Length)
            {
                if (_frame >= _columns * _rows)
                    return 0;
                _current = BuildFrame(_frame++);
                _offset = 0;
            }
            int n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        private byte[] BuildFrame(int index)
        {
            int col = index % _columns, row = index / _columns;
            int x0 = col * _frameW, y0 = row * _frameH;
            EnsureBand(row, y0);
            var band = _band!;
            var frame = new byte[_frameW * _frameH * 3];
            int validW = Math.Min(_frameW, _width - x0);
            for (int y = 0; y < _frameH; y++)
            {
                // Rows and columns past the edge repeat the last real pixel so chroma doesn't bleed.
                int srcY = Math.Min(y, _bandHeight - 1);
                var src = band.AsSpan((srcY * _width + x0) * 3, validW * 3);
                var dst = frame.AsSpan(y * _frameW * 3, _frameW * 3);
                src.CopyTo(dst);
                if (validW < _frameW)
                {
                    var last = src[^3..];
                    for (int x = validW; x < _frameW; x++)
                        last.CopyTo(dst[(x * 3)..]);
                }
            }
            return frame;
        }

        private void EnsureBand(int row, int y0)
        {
            if (_bandRow == row)
                return;
            _bandHeight = Math.Min(_frameH, _height - y0);
            _band = _pixels.ToByteArray(0, y0, (uint)_width, (uint)_bandHeight, PixelMapping.RGB)
                ?? throw new InvalidOperationException("Could not read pixels");
            _bandRow = row;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _pixels.Dispose();
            base.Dispose(disposing);
        }
    }
}
