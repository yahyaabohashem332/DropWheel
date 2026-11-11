using System.Runtime.InteropServices;

namespace Petal.Engines.Media.Foundation;

/// <summary>One stream of a file as Windows' Media Foundation sees it, before anything is decoded.</summary>
internal sealed record WindowsStream(uint Index, MediaStreamKind Kind, Guid Subtype, int Width, int Height, int SampleRate, int Channels);

/// <summary>What a decoded video stream looks like: the frame Windows hands over and the part of it that is the picture.</summary>
internal sealed record WindowsVideoFormat(int FrameWidth, int FrameHeight, int Left, int Top, int Width, int Height, int Stride, bool TenBit,
    int AspectNumerator, int AspectDenominator, double FrameRate);

internal sealed record WindowsAudioFormat(int SampleRate, int Channels);

/// <summary>
/// A file opened with Windows' Media Foundation Source Reader: Windows' own decoders give its pictures as NV12 (P010
/// for more than 8 bits) and its sound as 32-bit float, the codecs DropWheel's FFmpeg doesn't carry.
/// </summary>
internal sealed class WindowsReader : IDisposable
{
    private readonly IMFSourceReader _reader;
    private readonly string _path;
    private bool _disposed;

    /// <summary>A second reader of the same file, for reading streams as the file holds them: a stream read so can't
    /// be decoded after.</summary>
    private WindowsReader? _raw;

    private WindowsReader(IMFSourceReader reader, string path, IReadOnlyList<WindowsStream> streams, TimeSpan duration)
    {
        _reader = reader;
        _path = path;
        Streams = streams;
        Duration = duration;
    }

    public IReadOnlyList<WindowsStream> Streams { get; }

    public TimeSpan Duration { get; }

    public static WindowsReader Open(string path)
    {
        MediaFoundationRuntime.Ensure();
        MF.Check(MF.MFCreateAttributes(out var attributes, 1), "MFCreateAttributes");
        IMFSourceReader reader;
        try
        {
            // The video processor turns whatever a decoder gives into NV12 or P010, and weaves interlaced fields.
            attributes.SetUINT32(MF.EnableAdvancedVideoProcessing, 1);
            var hr = MF.MFCreateSourceReaderFromURL(path, attributes, out reader);
            if (hr < 0)
                throw new WindowsMediaException("Opening the file", hr);
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }

        var streams = new List<WindowsStream>();
        for (uint i = 0; ; i++)
        {
            var hr = reader.GetNativeMediaType(i, 0, out var type);
            if (hr == MF.InvalidStreamNumber)
                break;
            if (hr < 0)
            {
                streams.Add(new WindowsStream(i, MediaStreamKind.Unknown, Guid.Empty, 0, 0, 0, 0));
                continue;
            }
            try
            {
                type.GetMajorType(out var major);
                type.GetGUID(MF.Subtype, out var subtype);
                if (major == MF.MediaTypeVideo)
                {
                    type.GetUINT64(MF.FrameSize, out var size);
                    var (w, h) = MF.Split(size);
                    streams.Add(new WindowsStream(i, MediaStreamKind.Video, subtype, (int)w, (int)h, 0, 0));
                }
                else if (major == MF.MediaTypeAudio)
                {
                    type.GetUINT32(MF.AudioSampleRate, out var rate);
                    type.GetUINT32(MF.AudioChannels, out var channels);
                    streams.Add(new WindowsStream(i, MediaStreamKind.Audio, subtype, 0, 0, (int)rate, (int)channels));
                }
                else
                {
                    streams.Add(new WindowsStream(i, MediaStreamKind.Unknown, subtype, 0, 0, 0, 0));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(type);
            }
        }
        reader.SetStreamSelection(MF.AllStreams, 0);

        var duration = TimeSpan.Zero;
        if (reader.GetPresentationAttribute(MF.MediaSource, MF.PresentationDuration, out var value) >= 0 && value.Type == PropVariant.VtUi8)
            duration = TimeSpan.FromTicks((long)value.ULong);
        return new WindowsReader(reader, path, streams, duration);
    }

    /// <summary>
    /// The stream FFprobe calls <paramref name="stream"/>, the <paramref name="nth"/> of its kind, leaving out those in
    /// <paramref name="taken"/>. Windows doesn't keep a file's order of streams (it lists an MP4's sound tracks last
    /// first, before its picture), so where there are several of a kind, <paramref name="tellTale"/>, bytes of a packet
    /// only that stream holds, says which it is.
    /// </summary>
    public WindowsStream? Find(MediaStreamInfo stream, int nth, byte[]? tellTale, IReadOnlySet<uint> taken)
    {
        var ofKind = Streams.Where(s => s.Kind == stream.Kind).ToList();
        if (ofKind.Count > 1 && tellTale is not null)
        {
            var raw = _raw ??= Open(_path);
            var holders = ofKind.Where(s => !taken.Contains(s.Index) && raw.Holds(s.Index, tellTale)).ToList();
            if (holders.Count == 1)
                return holders[0];
        }
        if (ofKind.ElementAtOrDefault(nth) is { } same && !taken.Contains(same.Index))
            return same;
        return ofKind.FirstOrDefault(s => !taken.Contains(s.Index));
    }

    /// <summary>Whether one of a stream's first samples, as the file holds them (not decoded), has <paramref name="bytes"/> in it.</summary>
    private bool Holds(uint stream, byte[] bytes, int samples = 150)
    {
        if (_reader.GetNativeMediaType(stream, 0, out var native) < 0)
            return false;
        try
        {
            if (_reader.SetCurrentMediaType(stream, IntPtr.Zero, native) < 0)
                return false;
        }
        finally
        {
            Marshal.ReleaseComObject(native);
        }
        _reader.SetStreamSelection(MF.AllStreams, 0);
        _reader.SetStreamSelection(stream, 1);
        Seek(TimeSpan.Zero);
        try
        {
            for (var i = 0; i < samples; i++)
            {
                var (sample, _, _) = Read(stream);
                if (sample is null)
                    return false;
                try
                {
                    if (Contains(sample, bytes))
                        return true;
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }
            return false;
        }
        catch (WindowsMediaException)
        {
            return false;
        }
        finally
        {
            _reader.SetStreamSelection(stream, 0);
        }
    }

    private static unsafe bool Contains(IMFSample sample, byte[] bytes)
    {
        if (sample.ConvertToContiguousBuffer(out var buffer) < 0)
            return false;
        try
        {
            if (buffer.Lock(out var data, out _, out var length) < 0)
                return false;
            try
            {
                return new ReadOnlySpan<byte>((byte*)data, (int)length).IndexOf(bytes) >= 0;
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>Asks Windows to decode a video stream into NV12, or P010 when the source has more than 8 bits.</summary>
    public WindowsVideoFormat SelectVideo(uint stream, bool tenBit)
    {
        MF.Check(MF.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            type.SetGUID(MF.MajorType, MF.MediaTypeVideo);
            type.SetGUID(MF.Subtype, tenBit ? MF.P010 : MF.Nv12);
            var hr = _reader.SetCurrentMediaType(stream, IntPtr.Zero, type);
            if (hr < 0 && tenBit)
            {
                // A decoder that gives 8 bits only.
                type.SetGUID(MF.Subtype, MF.Nv12);
                tenBit = false;
                hr = _reader.SetCurrentMediaType(stream, IntPtr.Zero, type);
            }
            if (hr < 0)
                throw new WindowsMediaException("Decoding the video", hr);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
        MF.Check(_reader.SetStreamSelection(stream, 1), "Selecting the video");
        return VideoFormat(stream);
    }

    /// <summary>The decoded video's format now: Windows may settle it only once the first frames are decoded.</summary>
    public WindowsVideoFormat VideoFormat(uint stream)
    {
        MF.Check(_reader.GetCurrentMediaType(stream, out var type), "Reading the video format");
        try
        {
            type.GetGUID(MF.Subtype, out var subtype);
            type.GetUINT64(MF.FrameSize, out var size);
            var (w, h) = MF.Split(size);
            int left = 0, top = 0, width = (int)w, height = (int)h;
            // A decoder works in whole blocks (1088 lines for 1080): the aperture says which part is the picture.
            if ((Aperture(type, MF.MinimumDisplayAperture) ?? Aperture(type, MF.GeometricAperture)) is { } area)
            {
                left = Math.Clamp((int)area.OffsetX.Value, 0, (int)w);
                top = Math.Clamp((int)area.OffsetY.Value, 0, (int)h);
                width = Math.Clamp(area.Width, 1, (int)w - left);
                height = Math.Clamp(area.Height, 1, (int)h - top);
            }
            var tenBit = subtype == MF.P010;
            var stride = type.GetUINT32(MF.DefaultStride, out var s) >= 0 ? Math.Abs((int)s) : (int)w * (tenBit ? 2 : 1);
            uint parN = 1, parD = 1;
            if (type.GetUINT64(MF.PixelAspectRatio, out var par) >= 0)
                (parN, parD) = MF.Split(par);
            double fps = 0;
            if (type.GetUINT64(MF.FrameRate, out var rate) >= 0 && MF.Split(rate) is var (num, den) && den > 0)
                fps = (double)num / den;
            return new WindowsVideoFormat((int)w, (int)h, left, top, width, height, stride, tenBit,
                (int)Math.Max(1, parN), (int)Math.Max(1, parD), fps);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static MfVideoArea? Aperture(IMFMediaType type, Guid key)
    {
        var size = Marshal.SizeOf<MfVideoArea>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (type.GetBlob(key, buffer, (uint)size, out var written) < 0 || written < size)
                return null;
            var area = Marshal.PtrToStructure<MfVideoArea>(buffer);
            return area.Width > 0 && area.Height > 0 ? area : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Asks Windows to decode an audio stream into 32-bit float, at the stream's own rate and channels.</summary>
    public WindowsAudioFormat SelectAudio(uint stream)
    {
        MF.Check(MF.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            type.SetGUID(MF.MajorType, MF.MediaTypeAudio);
            type.SetGUID(MF.Subtype, MF.AudioFloat);
            type.SetUINT32(MF.AudioBitsPerSample, 32);
            var hr = _reader.SetCurrentMediaType(stream, IntPtr.Zero, type);
            if (hr < 0)
                throw new WindowsMediaException("Decoding the sound", hr);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
        MF.Check(_reader.SetStreamSelection(stream, 1), "Selecting the sound");
        return AudioFormat(stream);
    }

    public WindowsAudioFormat AudioFormat(uint stream)
    {
        MF.Check(_reader.GetCurrentMediaType(stream, out var type), "Reading the sound format");
        try
        {
            type.GetUINT32(MF.AudioSampleRate, out var rate);
            type.GetUINT32(MF.AudioChannels, out var channels);
            return new WindowsAudioFormat((int)rate, (int)channels);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    /// <summary>Goes to the key frame at or before a time; the frames before the time itself are for the caller to drop.</summary>
    public bool Seek(TimeSpan position)
    {
        var at = PropVariant.FromLong(position.Ticks);
        return _reader.SetCurrentPosition(Guid.Empty, at) >= 0;
    }

    /// <summary>The next sample of a stream: null at its end. Flags say whether its format changed on the way.</summary>
    public (IMFSample? Sample, long Time, uint Flags) Read(uint stream)
    {
        for (var empty = 0; ; empty++)
        {
            // Windows answering with neither a sample nor an end, over and over, would otherwise never end.
            if (empty > 10_000)
                throw new WindowsMediaException("Decoding", unchecked((int)0x80004005));
            var hr = _reader.ReadSample(stream, 0, out _, out var flags, out var time, out var sample);
            if (hr < 0)
                throw new WindowsMediaException("Decoding", hr);
            if ((flags & MF.ReaderError) != 0)
                throw new WindowsMediaException("Decoding", unchecked((int)0x80004005));
            if (sample is not null)
                return (sample, time, flags);
            if ((flags & MF.ReaderEndOfStream) != 0)
                return (null, time, flags);
            // A stream tick (a gap in a sparse stream) or a format change without a sample: read on.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _raw?.Dispose();
        Marshal.ReleaseComObject(_reader);
    }
}
