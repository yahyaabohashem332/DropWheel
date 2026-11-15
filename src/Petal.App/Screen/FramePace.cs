using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Petal.App.Screen;

/// <summary>
/// A recording's frames at a steady rate on its clock: frame n is due n/fps seconds in and is taken then, and while
/// the PC is a whole frame behind (one took long to take or to hand on), the last one goes again for the time that
/// went by, so the video keeps time with the sound.
/// </summary>
internal static class FramePace
{
    /// <summary>
    /// Until <paramref name="stopping"/>: <paramref name="take"/> takes frame n when it is due (false when there is
    /// nothing to take yet, as before the camera's first picture: it is asked again a frame later), and
    /// <paramref name="again"/> sends the last frame once more.
    /// </summary>
    public static void Run(Stopwatch clock, int fps, Func<bool> stopping, Func<long, bool> take, Action again)
    {
        using var wait = new PreciseWait();
        double frameMs = 1000.0 / fps;
        long sent = 0;
        while (!stopping())
        {
            double due = sent * frameMs;
            if (clock.Elapsed.TotalMilliseconds < due)
            {
                wait.Until(clock, due);
                continue;
            }
            if (!take(sent))
            {
                wait.Until(clock, clock.Elapsed.TotalMilliseconds + frameMs);
                continue;
            }
            sent++;
            while (!stopping() && sent * frameMs < clock.Elapsed.TotalMilliseconds - frameMs)
            {
                again();
                sent++;
            }
        }
    }
}

/// <summary>
/// Waits until a moment on a stopwatch, to within a millisecond or so, however Windows keeps its clock. For a program
/// that asks for nothing finer, Windows' clock ticks every 15.6 ms, and since Windows 11 a request for finer may be
/// ignored while none of the program's windows can be seen; a sleep then lasts until a tick, up to 15.6 ms longer than
/// asked. (A recording had finer ticks only because WPF asks for them while an animation runs, and the bar's red dot
/// blinks; told to ignore that, Windows made its frames 7.7 ms late on average.) A high-resolution waitable timer,
/// which Windows has from 10 1803 on, keeps its own time; without one this sleeps as before. One for each thread that
/// waits.
/// </summary>
internal sealed class PreciseWait : IDisposable
{
    private readonly IntPtr _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);

    /// <summary>Sleeps until the clock reads <paramref name="moment"/> (milliseconds), for 50 ms at most so a wish to stop is seen.</summary>
    public void Until(Stopwatch clock, double moment)
    {
        double ms = Math.Min(moment - clock.Elapsed.TotalMilliseconds, 50);
        if (ms <= 0)
            return;
        long due = -(long)Math.Max(1, ms * 10_000);
        if (_timer != IntPtr.Zero && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(_timer, 100);
        else
            Thread.Sleep(Math.Max(1, (int)ms));
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero)
            CloseHandle(_timer);
    }

    private const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr argument, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
