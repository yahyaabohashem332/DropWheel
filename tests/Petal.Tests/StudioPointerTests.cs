using System.Diagnostics;
using System.Runtime.InteropServices;
using Petal.App.Screen.Studio;
using Petal.Core.Recording;

namespace Petal.Tests;

/// <summary>
/// The studio's pointer, 120 times a second however Windows keeps its clock. Since Windows 11 a program's request
/// for finer ticks may be ignored once none of its windows can be seen, and in a run of these tests, whose windows
/// come and go, it was for most of the run: a 1 ms sleep took a whole 15.6 ms tick, and the loop the sampler had
/// before came round 64 times a second. Windows is told here to ignore this process's requests while the pointer
/// is sampled, so the test meets that clock whatever the tests before it left; nothing else runs meanwhile.
/// </summary>
[Collection("Timing")]
public sealed class StudioPointerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "petal-studio-pointer-" + Guid.NewGuid().ToString("N")[..8]);

    public StudioPointerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void The_pointer_is_sampled_about_120_times_a_second_even_on_a_coarse_clock()
    {
        // Only where the pointer is and its shape: no picture of the screen is taken.
        var area = new Petal.App.Interop.NativeMethods.RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 };
        IgnoreTimerRequests(true);
        try
        {
            var clock = Stopwatch.StartNew();
            var capture = new StudioCapture(area, clock, _dir);
            capture.Start();
            Thread.Sleep(1000);
            capture.Stop();
            Assert.InRange(capture.Pointer.Count, 90, 135);
            Assert.All(capture.Pointer, p => Assert.True(p.Kind != PointerKind.Other || p.Shape >= 0));
            Assert.True(capture.Pointer.Zip(capture.Pointer.Skip(1)).All(pair => pair.Second.Time >= pair.First.Time));
            Assert.Empty(capture.Typing);
        }
        finally
        {
            IgnoreTimerRequests(false);
        }
    }

    /// <summary>
    /// Windows told to ignore this process's requests for finer ticks (true), or to decide for itself again, as it
    /// does unless told (false). Where Windows doesn't know the request, nothing changes.
    /// </summary>
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
