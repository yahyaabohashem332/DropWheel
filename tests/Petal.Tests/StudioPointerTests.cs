using System.Diagnostics;
using Petal.App.Screen.Studio;
using Petal.Core.Recording;

namespace Petal.Tests;

/// <summary>
/// The studio's pointer, 120 times a second however Windows keeps its clock. In a run of these tests Windows ignored
/// the process's requests for finer ticks for most of the run, and the loop the sampler had before came round 64 times
/// a second; here the pointer is sampled on that coarse clock (<see cref="CoarseClock"/>), whatever the tests before it
/// left. Nothing else runs meanwhile.
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
        using (new CoarseClock())
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
    }
}
