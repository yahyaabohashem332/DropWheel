using System.Diagnostics;
using System.Globalization;
using Petal.App.Screen;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>
/// A recording's frames are taken when they are due, however Windows keeps its clock. The screen's and the camera's
/// frames used to wait with Thread.Sleep, and a recording had finer ticks only because WPF asks for them while the
/// bar's red dot blinks; with Windows ignoring that (<see cref="CoarseClock"/>), frames were taken 7.7 ms late on
/// average and up to 15.5 ms, 31 or 47 ms apart instead of 33. Nothing else runs meanwhile.
/// </summary>
[Collection("Timing")]
public sealed class RecordingPaceTests(ITestOutputHelper output)
{
    private const int Fps = 30;

    /// <summary>Runs the recorder's frame loop for a while: when each frame was taken against its moment, and how many went again.</summary>
    private static (List<double> Late, List<long> Taken, long Again, double Seconds) Record(TimeSpan length, Action<long>? taking = null)
    {
        var late = new List<double>();
        var taken = new List<long>();
        long again = 0;
        var clock = Stopwatch.StartNew();
        bool stop = false;
        // As the recorder's own threads run.
        var loop = new Thread(() => FramePace.Run(clock, Fps, () => Volatile.Read(ref stop),
            take: n =>
            {
                late.Add(clock.Elapsed.TotalMilliseconds - n * 1000.0 / Fps);
                taken.Add(n);
                taking?.Invoke(n);
                return true;
            },
            again: () => again++)) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        loop.Start();
        Thread.Sleep(length);
        Volatile.Write(ref stop, true);
        loop.Join();
        return (late, taken, again, clock.Elapsed.TotalSeconds);
    }

    [Fact]
    public void Frames_are_taken_when_they_are_due_even_on_a_coarse_clock()
    {
        List<double> late;
        long again;
        double seconds;
        using (new CoarseClock())
            (late, _, again, seconds) = Record(TimeSpan.FromSeconds(2));
        var sorted = late.OrderBy(l => l).ToList();
        string said = string.Create(CultureInfo.InvariantCulture,
            $"{late.Count} taken, {again} again; late by {late.Average():0.00} ms on average, 90% within {sorted[(int)(sorted.Count * 0.9)]:0.00}, at most {sorted[^1]:0.00}");
        output.WriteLine(said);
        // Every frame's moment came...
        Assert.InRange(late.Count + again, (long)(seconds * Fps) - 1, (long)(seconds * Fps) + 2);
        // ...and its frame was taken then, not at the next tick of the clock.
        Assert.True(late.Average() < 3, said);
        Assert.True(late.Count(l => l > 6) <= late.Count / 10, said);
    }

    [Fact]
    public void A_frame_that_takes_long_is_sent_again_for_the_time_it_took_so_the_video_keeps_time()
    {
        // Frame 10 takes 200 ms, as the first frames do while FFmpeg starts the graphics card's encoder.
        var (_, taken, again, seconds) = Record(TimeSpan.FromSeconds(1), n => { if (n == 10) Thread.Sleep(200); });
        // As many frames as the time holds: those taken, and the last one again while the PC was behind...
        Assert.InRange(taken.Count + again, (long)(seconds * Fps) - 1, (long)(seconds * Fps) + 2);
        // ...frame 10 standing for the 200 ms it took (six frames' time), and the next taken the one due when it was done.
        Assert.Equal(Enumerable.Range(0, 11).Select(i => (long)i), taken.Take(11));
        Assert.InRange(taken[11] - 11, 4, 7);
        Assert.True(taken.Zip(taken.Skip(1)).All(p => p.Second > p.First));
    }
}
