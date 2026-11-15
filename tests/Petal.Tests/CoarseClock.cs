using System.Runtime.InteropServices;

namespace Petal.Tests;

/// <summary>
/// Windows' coarse clock, at will. Since Windows 11 a program's request for finer ticks may be ignored once none of
/// its windows can be seen, and in a run of these tests, whose windows come and go, it was for most of the run: a 1 ms
/// sleep took a whole 15.6 ms tick. While one of these is held Windows is told to ignore this process's requests, so a
/// test meets that clock whatever the tests before it left; once it is disposed Windows decides for itself again, as it
/// does unless told. Where Windows doesn't know the request, nothing changes.
/// </summary>
internal sealed class CoarseClock : IDisposable
{
    public CoarseClock() => IgnoreTimerRequests(true);

    public void Dispose() => IgnoreTimerRequests(false);

    private static void IgnoreTimerRequests(bool ignore)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1, // PROCESS_POWER_THROTTLING_CURRENT_VERSION
            ControlMask = ignore ? IgnoreTimerResolution : 0,
            StateMask = ignore ? IgnoreTimerResolution : 0,
        };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version, ControlMask, StateMask;
    }

    private const int ProcessPowerThrottling = 4;
    private const uint IgnoreTimerResolution = 0x4; // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE state, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
