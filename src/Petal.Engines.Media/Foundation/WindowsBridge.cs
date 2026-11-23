using System.IO.Pipes;
using System.Runtime.InteropServices;
using Petal.Engines.Media.Internal;

namespace Petal.Engines.Media.Foundation;

/// <summary>
/// A stream of a file to decode through Windows, with what FFprobe knows of it. <paramref name="Lead"/> is where
/// FFmpeg puts the stream's first packet: before 0 for sound whose encoder's lead-in the file says to skip.
/// <paramref name="TenBit"/>: a picture of more than 8 bits, which comes through as P010. <paramref name="TellTale"/>:
/// where the file has more than one stream of the kind, bytes of a packet that only this one holds.
/// </summary>
internal sealed record BridgeTrack(MediaStreamInfo Stream, int Nth, TimeSpan? Lead = null, bool TenBit = false, byte[]? TellTale = null);

/// <summary>
/// What to decode of a file: the tracks, from where, and for how long. <paramref name="Copy"/>: a copy of the file's
/// streams in a container Windows opens, made for the bridge and deleted with it.
/// </summary>
internal sealed record BridgeRequest(string Path, IReadOnlyList<BridgeTrack> Tracks, TimeSpan Start, TimeSpan? Duration, string? Copy = null);

/// <summary>
/// Decodes streams of a file with Windows' own decoders and hands them to FFmpeg as raw pictures and sound, through a
/// named pipe FFmpeg reads like any input. This is how DropWheel reads H.264, HEVC, AAC, WMV and the other formats
/// whose patents its FFmpeg doesn't license: Windows decodes them under Microsoft's licence.
/// </summary>
internal sealed class WindowsBridge : IAsyncDisposable
{
    private readonly BridgeRequest _request;
    private readonly string _pipeName = "petal-windows-" + Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    private WindowsBridge(BridgeRequest request)
    {
        _request = request;
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        _thread = new Thread(Run) { IsBackground = true, Name = "Windows decoding" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>The pipe FFmpeg opens: "\\.\pipe\…".</summary>
    public string PipePath => @"\\.\pipe\" + _pipeName;

    /// <summary>What went wrong while decoding, if anything did; FFmpeg closing the pipe early isn't a failure.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The options to put before "-i" and the pipe for this input.</summary>
    public IReadOnlyList<string> InputArgs
    {
        get
        {
            var args = new List<string> { "-f", "matroska" };
            var video = _request.Tracks.Select((t, i) => (t, i)).Where(x => x.t.Stream.Kind == MediaStreamKind.Video).ToList();
            for (var v = 0; v < video.Count; v++)
            {
                var clockwise = video[v].t.Stream.Rotation;
                if (clockwise != 0)
                    args.AddRange([$"-display_rotation:v:{v}", Inv.I((360 - clockwise) % 360)]);
            }
            args.AddRange(["-i", PipePath]);
            return args;
        }
    }

    /// <summary>Opens the file through Windows and decodes its first frames, so that a file Windows can't read fails here.</summary>
    public static async Task<WindowsBridge> StartAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        var bridge = new WindowsBridge(request);
        bridge._thread.Start();
        try
        {
            await bridge._ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return bridge;
        }
        catch
        {
            await bridge.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Track
    {
        public required BridgeTrack Source { get; init; }

        public required uint Stream { get; init; }

        public required int Number { get; init; }

        public WindowsVideoFormat? Video { get; set; }

        public WindowsAudioFormat? Audio { get; set; }

        public IMFSample? Pending { get; set; }

        public long PendingTime { get; set; }

        public bool Ended { get; set; }

        /// <summary>For sound: where the next sample's first frame falls, in frames from the start.</summary>
        public long FramesWritten { get; set; }

        public long FirstFrame { get; set; } = -1;

        /// <summary>Added to Windows' times to give FFmpeg's.</summary>
        public long Shift { get; set; }

        /// <summary>Windows' time of the track's first sample, read before decoding starts.</summary>
        public long? FirstTime { get; set; }

        /// <summary>Sound too short to send on its own, held to go with what follows it; and the frame it starts at.</summary>
        public byte[]? Carry { get; set; }

        public long CarryAt { get; set; }
    }

    /// <summary>
    /// The fewest frames of sound sent in one block. A cut leaves only the end of the frame it falls in, a few samples,
    /// and an encoder that takes its block size from the first frame it gets (FFmpeg 9's FLAC) refuses a frame that small.
    /// </summary>
    private const int ShortestBlock = 1024;

    private void Run()
    {
        WindowsReader? reader = null;
        var tracks = new List<Track>();
        try
        {
            reader = WindowsReader.Open(_request.Copy ?? _request.Path);
            // Which of Windows' streams each one is, all found before any is set to be decoded: telling streams apart
            // reads them as the file holds them.
            var taken = new HashSet<uint>();
            var found = new List<(BridgeTrack Wanted, WindowsStream Stream)>();
            foreach (var wanted in _request.Tracks)
            {
                var stream = reader.Find(wanted.Stream, wanted.Nth, wanted.TellTale, taken)
                    ?? throw new MediaException(WindowsMedia.CantRead(_request.Path, wanted.Stream));
                taken.Add(stream.Index);
                found.Add((wanted, stream));
            }
            var number = 1;
            foreach (var (wanted, stream) in found)
            {
                var track = new Track { Source = wanted, Stream = stream.Index, Number = number++ };
                try
                {
                    if (wanted.Stream.Kind == MediaStreamKind.Video)
                        track.Video = reader.SelectVideo(stream.Index, wanted.TenBit);
                    else
                        track.Audio = reader.SelectAudio(stream.Index);
                }
                catch (WindowsMediaException e)
                {
                    throw new MediaException(WindowsMedia.CantRead(_request.Path, wanted.Stream), e);
                }
                tracks.Add(track);
            }

            // Windows doesn't skip the lead-in an AAC encoder leaves (the file's edit list says how long it is):
            // where FFmpeg puts the first packet before 0, Windows' times are moved to match.
            var shifted = false;
            foreach (var track in tracks.Where(t => t.Source.Lead is not null))
            {
                var (sample, time, _) = reader.Read(track.Stream);
                if (sample is not null)
                {
                    Marshal.ReleaseComObject(sample);
                    track.Shift = track.Source.Lead!.Value.Ticks - time;
                    track.FirstTime = time;
                    shifted = true;
                }
            }
            // A file whose clock doesn't start at 0 (a transport stream starts at 1.4 s, which Windows gives as 0.7):
            // FFmpeg counts from the file's own start, so Windows' times are counted from its first sample.
            foreach (var track in tracks.Where(t => t.Source.Lead is null))
            {
                var (sample, time, _) = reader.Read(track.Stream);
                if (sample is not null)
                {
                    Marshal.ReleaseComObject(sample);
                    track.FirstTime = time;
                    shifted = true;
                }
            }
            var origin = tracks.Where(t => t.FirstTime is not null).Select(t => t.FirstTime!.Value).DefaultIfEmpty(0).Min();
            if (origin > 0)
                foreach (var track in tracks.Where(t => t.Source.Lead is null))
                    track.Shift = -origin;

            var start = _request.Start.Ticks;
            // Where Windows can't seek, reading from the beginning and dropping what comes before does the same.
            if (start > 0 || shifted)
                reader.Seek(TimeSpan.FromTicks(Math.Max(0, start - Math.Max(0, tracks.Max(t => t.Shift)))));
            var end = _request.Duration is { } d ? start + d.Ticks : long.MaxValue;

            // The first sample of each track settles its format (a decoder may change it once it has seen the stream).
            foreach (var track in tracks)
            {
                Advance(reader, track, start, end);
                if (track.Video is not null)
                    track.Video = reader.VideoFormat(track.Stream);
                if (track.Audio is not null)
                    track.Audio = reader.AudioFormat(track.Stream);
            }
            _ready.TrySetResult();

            try
            {
                _pipe.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using var output = new BufferedStream(_pipe, 1 << 20);
            var writer = new MatroskaWriter(output);
            writer.WriteHeader(tracks.Select(MatroskaTrackOf).ToList(), DurationSeconds(reader));
            // Nothing after the place asked for (a time past the end): FFmpeg's reader opens a stream only once it has
            // met a cluster, so an empty one lets it open this one, which then ends, as the file itself would.
            if (tracks.All(t => t.Pending is null))
                writer.WriteEmptyCluster();
            var frame = Array.Empty<byte>();
            while (!_stop.IsCancellationRequested)
            {
                var next = tracks.Where(t => t.Pending is not null).MinBy(t => t.PendingTime);
                if (next is null)
                    break;
                if (next.Video is { } video)
                    WriteVideo(writer, next, video, start, ref frame);
                else
                    WriteAudio(writer, next, next.Audio!, start, end);
                Release(next);
                Advance(reader, next, start, end);
            }
            foreach (var track in tracks)
                Flush(writer, track);
            output.Flush();
        }
        catch (IOException) when (_ready.Task.IsCompleted)
        {
            // FFmpeg closed the pipe: it has what it wanted (a single frame, "-t", "-shortest").
        }
        catch (ObjectDisposedException) when (_ready.Task.IsCompleted)
        {
        }
        catch (Exception e)
        {
            var failure = e is MediaException ? e : new MediaException(WindowsMedia.CantRead(_request.Path, null), e);
            Failure = failure;
            _ready.TrySetException(failure);
        }
        finally
        {
            foreach (var track in tracks)
                Release(track);
            reader?.Dispose();
            try
            {
                _pipe.Dispose();
            }
            catch (IOException)
            {
                // The other end is already gone.
            }
            _done.TrySetResult();
        }
    }

    private double DurationSeconds(WindowsReader reader)
    {
        var total = reader.Duration.TotalSeconds - _request.Start.TotalSeconds;
        if (_request.Duration is { } d)
            total = Math.Min(total, d.TotalSeconds);
        return Math.Max(0, total);
    }

    private static MatroskaTrack MatroskaTrackOf(Track track)
    {
        if (track.Video is { } v)
        {
            var displayWidth = (int)Math.Round(v.Width * (double)v.AspectNumerator / v.AspectDenominator);
            var stream = track.Source.Stream;
            var fps = stream.FrameRate > 0 ? stream.FrameRate : v.FrameRate;
            return new MatroskaVideoTrack(track.Number, v.Width, v.Height, Math.Max(1, displayWidth), v.Height,
                v.TenBit ? MatroskaWriter.Yuv420P10 : MatroskaWriter.Fourcc("NV12"), Colour.Of(stream), fps);
        }
        return new MatroskaAudioTrack(track.Number, track.Audio!.SampleRate, track.Audio.Channels);
    }

    /// <summary>Reads a track's next sample, dropping what comes before the start; marks it ended past the end.</summary>
    private static void Advance(WindowsReader reader, Track track, long start, long end)
    {
        while (true)
        {
            var (sample, time, flags) = reader.Read(track.Stream);
            time += track.Shift;
            if ((flags & MF.ReaderCurrentTypeChanged) != 0)
            {
                if (track.Video is not null)
                    track.Video = Settled(track.Video, reader.VideoFormat(track.Stream));
                else
                    track.Audio = reader.AudioFormat(track.Stream);
            }
            if (sample is null)
            {
                track.Pending = null;
                track.Ended = true;
                return;
            }
            sample.GetSampleDuration(out var duration);
            if (time >= end)
            {
                Marshal.ReleaseComObject(sample);
                track.Pending = null;
                track.Ended = true;
                return;
            }
            // A picture shown before the start, or sound that ends before it.
            var before = track.Video is not null ? time < start - 10_000 : time + Math.Max(duration, 1) <= start;
            if (before)
            {
                Marshal.ReleaseComObject(sample);
                continue;
            }
            track.Pending = sample;
            track.PendingTime = time;
            return;
        }
    }

    /// <summary>A size change mid-stream keeps the size FFmpeg was told; what is new is cropped or padded to it.</summary>
    private static WindowsVideoFormat Settled(WindowsVideoFormat before, WindowsVideoFormat now) =>
        now with { Width = before.Width, Height = before.Height };

    private static void Release(Track track)
    {
        if (track.Pending is { } sample)
            Marshal.ReleaseComObject(sample);
        track.Pending = null;
    }

    private static unsafe void WriteVideo(MatroskaWriter writer, Track track, WindowsVideoFormat format, long start, ref byte[] frame)
    {
        var sample = track.Pending!;
        int w = format.Width, h = format.Height;
        int cw = (w + 1) / 2, ch = (h + 1) / 2;
        var size = format.TenBit ? w * h * 2 + cw * ch * 4 : w * h + cw * 2 * ch;
        if (frame.Length != size)
            frame = new byte[size];

        MF.Check(sample.GetBufferByIndex(0, out var buffer), "Reading a picture");
        try
        {
            var data = IntPtr.Zero;
            var pitch = 0;
            var twoD = buffer as IMF2DBuffer;
            var locked2D = twoD is not null && twoD.Lock2D(out data, out pitch) >= 0;
            if (!locked2D)
            {
                MF.Check(buffer.Lock(out data, out _, out _), "Reading a picture");
                pitch = format.Stride;
            }
            try
            {
                var luma = (byte*)data;
                var chroma = luma + (long)pitch * format.FrameHeight;
                // Rows and columns past the frame Windows gave are left black rather than read.
                int rows = Math.Min(h, format.FrameHeight - format.Top), cols = Math.Min(w, format.FrameWidth - format.Left);
                fixed (byte* outBase = frame)
                {
                    if (!format.TenBit)
                    {
                        if (rows < h || cols < w)
                        {
                            new Span<byte>(outBase, w * h).Clear();
                            new Span<byte>(outBase + w * h, cw * 2 * ch).Fill(128);
                        }
                        for (var y = 0; y < rows; y++)
                            new ReadOnlySpan<byte>(luma + (long)(format.Top + y) * pitch + format.Left, cols).CopyTo(new Span<byte>(outBase + y * w, cols));
                        int chromaRows = Math.Min(ch, (format.FrameHeight - format.Top + 1) / 2), chromaBytes = Math.Min(cw * 2, (cols + 1) / 2 * 2);
                        var uv = outBase + w * h;
                        for (var y = 0; y < chromaRows; y++)
                            new ReadOnlySpan<byte>(chroma + (long)(format.Top / 2 + y) * pitch + format.Left / 2 * 2, chromaBytes).CopyTo(new Span<byte>(uv + y * cw * 2, chromaBytes));
                    }
                    else
                    {
                        // P010 keeps 10 bits at the top of 16; FFmpeg's yuv420p10le wants them at the bottom, U and V apart.
                        var y16 = (ushort*)outBase;
                        var u16 = (ushort*)(outBase + w * h * 2);
                        var v16 = u16 + cw * ch;
                        if (rows < h || cols < w)
                        {
                            new Span<ushort>(y16, w * h).Clear();
                            new Span<ushort>(u16, cw * ch * 2).Fill(512);
                        }
                        for (var y = 0; y < rows; y++)
                        {
                            var src = (ushort*)(luma + (long)(format.Top + y) * pitch) + format.Left;
                            var dst = y16 + y * w;
                            for (var x = 0; x < cols; x++)
                                dst[x] = (ushort)(src[x] >> 6);
                        }
                        int chromaRows = Math.Min(ch, (format.FrameHeight - format.Top + 1) / 2), chromaCols = Math.Min(cw, (cols + 1) / 2);
                        for (var y = 0; y < chromaRows; y++)
                        {
                            var src = (ushort*)(chroma + (long)(format.Top / 2 + y) * pitch) + format.Left / 2 * 2;
                            for (var x = 0; x < chromaCols; x++)
                            {
                                u16[y * cw + x] = (ushort)(src[2 * x] >> 6);
                                v16[y * cw + x] = (ushort)(src[2 * x + 1] >> 6);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (locked2D)
                    twoD!.Unlock2D();
                else
                    buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
        writer.WriteBlock(track.Number, Math.Max(0, track.PendingTime - start) / 10, frame);
    }

    private static unsafe void WriteAudio(MatroskaWriter writer, Track track, WindowsAudioFormat format, long start, long end)
    {
        var sample = track.Pending!;
        var frameBytes = 4 * format.Channels;
        MF.Check(sample.ConvertToContiguousBuffer(out var buffer), "Reading sound");
        try
        {
            MF.Check(buffer.Lock(out var data, out _, out var length), "Reading sound");
            try
            {
                var frames = (long)length / frameBytes;
                var rate = format.SampleRate;
                // Where this sample falls, in frames from the start.
                var at = (long)Math.Round((track.PendingTime - start) * (double)rate / TimeSpan.TicksPerSecond);
                long skip = 0;
                if (at < 0)
                {
                    skip = Math.Min(frames, -at);
                    at = 0;
                }
                if (end != long.MaxValue)
                {
                    var last = (long)Math.Round((end - start) * (double)rate / TimeSpan.TicksPerSecond);
                    frames = Math.Min(frames, skip + Math.Max(0, last - at));
                }
                if (track.FirstFrame < 0)
                {
                    track.FirstFrame = at;
                    track.FramesWritten = at;
                }
                // Sound is kept continuous: a gap Windows leaves is filled with silence, an overlap is dropped.
                var expected = track.FramesWritten;
                if (at > expected + rate / 100)
                {
                    var silence = new byte[(int)Math.Min(at - expected, rate) * frameBytes];
                    while (expected < at)
                    {
                        var count = (int)Math.Min(at - expected, silence.Length / frameBytes);
                        Emit(writer, track, expected, silence.AsSpan(0, count * frameBytes), frameBytes, rate);
                        expected += count;
                    }
                }
                else if (at < expected)
                {
                    skip = Math.Min(frames, skip + (expected - at));
                }
                if (frames > skip)
                {
                    var bytes = new ReadOnlySpan<byte>((byte*)data + skip * frameBytes, (int)((frames - skip) * frameBytes));
                    Emit(writer, track, expected, bytes, frameBytes, rate);
                    expected += frames - skip;
                }
                track.FramesWritten = expected;
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

    /// <summary>
    /// Sends sound that starts at frame <paramref name="at"/>, after any held back before it (it follows on from that).
    /// A piece shorter than <see cref="ShortestBlock"/> is held to go with the next.
    /// </summary>
    private static void Emit(MatroskaWriter writer, Track track, long at, ReadOnlySpan<byte> bytes, int frameBytes, int rate)
    {
        if (track.Carry is { } carry)
        {
            var joined = new byte[carry.Length + bytes.Length];
            carry.CopyTo(joined, 0);
            bytes.CopyTo(joined.AsSpan(carry.Length));
            at = track.CarryAt;
            track.Carry = null;
            bytes = joined;
        }
        if (bytes.Length / frameBytes < ShortestBlock)
        {
            track.Carry = bytes.ToArray();
            track.CarryAt = at;
            return;
        }
        writer.WriteBlock(track.Number, at * 1_000_000 / rate, bytes);
    }

    /// <summary>Sends what is still held back: the stream has ended.</summary>
    private static void Flush(MatroskaWriter writer, Track track)
    {
        if (track.Carry is { } carry && track.Audio is { } audio)
            writer.WriteBlock(track.Number, track.CarryAt * 1_000_000 / audio.SampleRate, carry);
        track.Carry = null;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            _pipe.Dispose();
        }
        catch (IOException)
        {
        }
        if (_thread.IsAlive)
        {
            try
            {
                await _done.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A decoder that doesn't return: the thread is a background one and goes with the process.
            }
        }
        if (_request.Copy is { } copy && !_thread.IsAlive)
        {
            try
            {
                File.Delete(copy);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>FFprobe's colour names as the numbers ITU-T H.273 gives them, for the Matroska colour element.</summary>
    internal static class Colour
    {
        public static MatroskaColour? Of(MediaStreamInfo stream)
        {
            var colour = new MatroskaColour(Matrix(stream.ColorSpace), Range(stream.ColorRange), Transfer(stream.ColorTransfer), Primaries(stream.ColorPrimaries));
            return colour is { Matrix: 0, Range: 0, Transfer: 0, Primaries: 0 } ? null : colour;
        }

        private static int Range(string? name) => name switch { "tv" => 1, "pc" => 2, _ => 0 };

        private static int Matrix(string? name) => name switch
        {
            "gbr" => 0, "bt709" => 1, "fcc" => 4, "bt470bg" => 5, "smpte170m" => 6, "smpte240m" => 7, "ycgco" => 8,
            "bt2020nc" => 9, "bt2020c" => 10, "smpte2085" => 11, "ictcp" => 14, _ => 0,
        };

        private static int Transfer(string? name) => name switch
        {
            "bt709" => 1, "gamma22" => 4, "gamma28" => 5, "smpte170m" => 6, "smpte240m" => 7, "linear" => 8,
            "iec61966-2-4" => 11, "bt1361e" => 12, "iec61966-2-1" => 13, "bt2020-10" => 14, "bt2020-12" => 15,
            "smpte2084" => 16, "smpte428" => 17, "arib-std-b67" => 18, _ => 0,
        };

        private static int Primaries(string? name) => name switch
        {
            "bt709" => 1, "bt470m" => 4, "bt470bg" => 5, "smpte170m" => 6, "smpte240m" => 7, "film" => 8, "bt2020" => 9,
            "smpte428" => 10, "smpte431" => 11, "smpte432" => 12, "jedec-p22" => 22, _ => 0,
        };
    }
}
