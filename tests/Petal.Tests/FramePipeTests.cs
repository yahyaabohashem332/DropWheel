using System.Diagnostics;
using Petal.App.Screen;

namespace Petal.Tests;

/// <summary>
/// A recording's frames on their way to FFmpeg: taken on time even while FFmpeg reads nothing (as for the first 0.5 to
/// 2.4 s, while Windows starts the graphics card's encoder), in order, within their memory, and let go of.
/// </summary>
public sealed class FramePipeTests
{
    /// <summary>FFmpeg's input as the tests see it: each frame's first byte, in order. It can read nothing for a while, or go away.</summary>
    private sealed class Ffmpeg : Stream
    {
        public readonly List<byte> Frames = [];
        public readonly ManualResetEventSlim Reading = new(true);
        public TimeSpan FirstWait;
        public bool Gone;
        public volatile bool Writing;
        private int _writes;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writing = true;
            if (Gone)
                throw new IOException("FFmpeg went away");
            if (_writes++ == 0)
                Thread.Sleep(FirstWait);
            Reading.Wait();
            lock (Frames)
                Frames.Add(buffer[0]);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Frame n: every byte n.</summary>
    private static void Send(FramePipe pipe, byte n) => pipe.Send(into =>
    {
        into.Fill(n);
        return true;
    });

    [Fact]
    public void Frames_reach_FFmpeg_in_order_and_again_is_the_one_before()
    {
        var ffmpeg = new Ffmpeg();
        using (var pipe = new FramePipe(ffmpeg, 64, 30, "test"))
        {
            Send(pipe, 1);
            Send(pipe, 2);
            pipe.Again();
            Send(pipe, 3);
            pipe.Again();
            pipe.Again();
            Assert.True(pipe.Finish(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, pipe.Held);
        }
        Assert.Equal(new byte[] { 1, 2, 2, 3, 3, 3 }, ffmpeg.Frames);
    }

    [Fact]
    public void Taking_a_frame_never_waits_while_FFmpeg_starts_its_encoder()
    {
        // FFmpeg takes the first frame and then reads nothing for 0.7 s.
        var ffmpeg = new Ffmpeg { FirstWait = TimeSpan.FromMilliseconds(700) };
        using var pipe = new FramePipe(ffmpeg, 320 * 180 * 4, 30, "test");
        var longest = TimeSpan.Zero;
        for (byte n = 0; n < 30; n++)
        {
            var one = Stopwatch.StartNew();
            Send(pipe, n);
            if (one.Elapsed > longest)
                longest = one.Elapsed;
            Thread.Sleep(33);
        }
        // Written straight in, a frame would have waited the 0.7 s.
        Assert.True(longest < TimeSpan.FromMilliseconds(300), longest.ToString());
        Assert.True(pipe.Finish(TimeSpan.FromSeconds(10)));
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (byte)i), ffmpeg.Frames);
        Assert.Equal(0, pipe.Held);
    }

    [Fact]
    public void Past_its_memory_the_last_frame_goes_again_so_the_video_keeps_time()
    {
        var ffmpeg = new Ffmpeg();
        ffmpeg.Reading.Reset();
        using (var pipe = new FramePipe(ffmpeg, 64, 30, "test", most: 4))
        {
            Send(pipe, 0);
            Assert.True(SpinWait.SpinUntil(() => ffmpeg.Writing, 5000));
            for (byte n = 1; n < 10; n++)
                Send(pipe, n);
            // Frame 0 being written and four waiting: no more memory than that.
            Assert.Equal(5, pipe.Held);
            ffmpeg.Reading.Set();
            Assert.True(pipe.Finish(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, pipe.Held);
        }
        // Frames 5 to 9 found no room and stood as frame 4 again: ten frames for ten.
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 4, 4, 4, 4, 4 }, ffmpeg.Frames);
    }

    [Fact]
    public void When_FFmpeg_goes_away_the_pipe_says_so_and_lets_go_of_its_frames()
    {
        var ffmpeg = new Ffmpeg { Gone = true };
        using var pipe = new FramePipe(ffmpeg, 64, 30, "test");
        Send(pipe, 0);
        Assert.True(SpinWait.SpinUntil(() => pipe.Broken, 5000));
        Send(pipe, 1);
        pipe.Again();
        Assert.False(pipe.Finish(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, pipe.Held);
    }

    [Fact]
    public void A_recording_thrown_away_doesnt_wait_for_FFmpeg_to_take_its_frames()
    {
        var ffmpeg = new Ffmpeg();
        ffmpeg.Reading.Reset();
        var pipe = new FramePipe(ffmpeg, 64, 30, "test");
        for (byte n = 0; n < 5; n++)
            Send(pipe, n);
        Assert.True(SpinWait.SpinUntil(() => ffmpeg.Writing, 5000));
        var clock = Stopwatch.StartNew();
        pipe.Dispose();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), clock.Elapsed.ToString());
        // FFmpeg is stopped: the frame it held ends, the rest are let go unwritten.
        ffmpeg.Reading.Set();
        Assert.True(SpinWait.SpinUntil(() => pipe.Held == 0, 5000));
        Assert.Equal(new byte[] { 0 }, ffmpeg.Frames);
    }

    [Fact]
    public void Three_seconds_of_frames_may_wait_within_the_memory_free()
    {
        // 1280×720 at 30 a second; a frame of 3840×2160 much less, in no more than 768 MB.
        Assert.InRange(FramePipe.MostFor(1280 * 720 * 4, 30), 2, 90);
        Assert.InRange(FramePipe.MostFor(3840 * 2160 * 4, 24) * 3840L * 2160 * 4, 2 * 3840L * 2160 * 4, 768L << 20);
    }
}
