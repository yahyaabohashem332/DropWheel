using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Petal.App.Input;
using Petal.App.Wheels;
using Petal.Core.Recording;
using SkiaSharp;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Screen.Studio;

/// <summary>
/// What a cinematic recording keeps beside its pictures: the pointer 120 times a second with its shape (Windows'
/// own shapes by name, any other taken from the screen once), each key typed with where the text cursor was, and
/// the shortcuts pressed. Typed letters are never kept — only that a key went down — so passwords stay private.
/// </summary>
internal sealed class StudioCapture
{
    private readonly RECT _area;
    private readonly Stopwatch _clock;
    private readonly string _folder;
    private readonly ConcurrentQueue<(double Time, int Key)> _keys = new();
    private readonly HashSet<int> _held = [];
    private readonly Dictionary<IntPtr, int> _shapeOf = [];
    private Thread? _thread;
    private volatile bool _stopping;
    private Action<int, bool>? _onKey;

    public List<PointerSample> Pointer { get; } = [];

    public List<KeySample> Keys { get; } = [];

    public List<TypingSample> Typing { get; } = [];

    public List<PointerShape> Shapes { get; } = [];

    public StudioCapture(RECT area, Stopwatch clock, string folder)
    {
        _area = area;
        _clock = clock;
        _folder = folder;
    }

    public void Start()
    {
        if (GlobalInputHook.Current is { } hook)
        {
            // The hook's own thread must be let go at once: the key is only noted here.
            _onKey = (vk, down) => _keys.Enqueue((_clock.Elapsed.TotalSeconds, down ? vk : -vk));
            hook.KeyChanged += _onKey;
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "Petal studio pointer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        if (_onKey is not null && GlobalInputHook.Current is { } hook)
            hook.KeyChanged -= _onKey;
        _onKey = null;
        _thread?.Join(2000);
        Drain();
    }

    private void Run()
    {
        // Windows' clock ticks every 15.6 ms unless asked for finer while this runs.
        TimeBeginPeriod(1);
        try
        {
            double next = 0;
            while (!_stopping)
            {
                double now = _clock.Elapsed.TotalSeconds;
                if (now < next)
                {
                    Thread.Sleep(1);
                    continue;
                }
                next = now + 1 / 120.0;
                Sample(now);
                Drain();
            }
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    private void Sample(double time)
    {
        var info = new ScreenNative.CURSORINFO { cbSize = Marshal.SizeOf<ScreenNative.CURSORINFO>() };
        if (!ScreenNative.GetCursorInfo(ref info))
            return;
        bool showing = (info.flags & ScreenNative.CURSOR_SHOWING) != 0;
        int x = info.ptScreenPos.X, y = info.ptScreenPos.Y;
        bool inside = showing && x >= _area.Left && x < _area.Right && y >= _area.Top && y < _area.Bottom;
        var kind = PointerKinds.Of(info.hCursor);
        int shape = kind == PointerKind.Other ? ShapeOf(info.hCursor) : -1;
        if (kind == PointerKind.Other && shape < 0)
            kind = PointerKind.Arrow;
        lock (Pointer)
            Pointer.Add(new PointerSample(Math.Round(time, 4), x - _area.Left, y - _area.Top, inside, kind, shape));
    }

    /// <summary>The number of a shape not Windows' own, taken from the screen the first time it is seen (-1 when it can't be).</summary>
    private int ShapeOf(IntPtr cursor)
    {
        if (cursor == IntPtr.Zero)
            return -1;
        if (_shapeOf.TryGetValue(cursor, out int known))
            return known;
        int index = -1;
        try
        {
            if (PointerArt.CaptureNative(cursor) is { } taken)
            {
                index = Shapes.Count;
                var file = $"pointer{index}.png";
                using (var data = taken.Image.Encode(SKEncodedImageFormat.Png, 100))
                using (var stream = File.Create(Path.Combine(_folder, file)))
                    data.SaveTo(stream);
                Shapes.Add(new PointerShape(file, taken.HotX, taken.HotY, taken.Image.Width, taken.Image.Height));
                taken.Image.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            index = -1;
        }
        _shapeOf[cursor] = index;
        return index;
    }

    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    /// <summary>The keys noted since last time: shortcuts by name, typing by when and where.</summary>
    private void Drain()
    {
        while (_keys.TryDequeue(out var k))
        {
            int vk = Math.Abs(k.Key);
            if (k.Key < 0)
            {
                _held.Remove(vk);
                continue;
            }
            bool repeat = !_held.Add(vk);
            if (IsModifier(vk) || repeat)
                continue;
            bool ctrl = _held.Overlaps([0x11, 0xA2, 0xA3]), alt = _held.Overlaps([0x12, 0xA4, 0xA5]);
            bool shift = _held.Overlaps([0x10, 0xA0, 0xA1]), win = _held.Overlaps([0x5B, 0x5C]);
            var key = KeyInterop.KeyFromVirtualKey(vk);
            bool special = key is >= Key.F1 and <= Key.F24 or Key.Escape or Key.PrintScreen;
            if (ctrl || alt || win || special)
            {
                var parts = new List<string>();
                if (ctrl) parts.Add("Ctrl");
                if (alt) parts.Add("Alt");
                if (shift) parts.Add("Shift");
                if (win) parts.Add("Win");
                parts.Add(KeySender.NameOf(key));
                lock (Keys)
                    Keys.Add(new KeySample(Math.Round(k.Time, 3), string.Join(" + ", parts)));
                continue;
            }
            var (placed, cx, cy) = Caret();
            lock (Typing)
                Typing.Add(new TypingSample(Math.Round(k.Time, 3), cx, cy, placed));
        }
    }

    /// <summary>Where the text cursor is in the recording, when the app in front uses Windows' own and it is inside the area.</summary>
    private (bool Placed, double X, double Y) Caret()
    {
        var front = GetForegroundWindow();
        if (front == IntPtr.Zero)
            return (false, 0, 0);
        uint thread = GetWindowThreadProcessId(front, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.hwndCaret == IntPtr.Zero)
            return (false, 0, 0);
        var point = new POINT { X = info.rcCaret.Left, Y = (info.rcCaret.Top + info.rcCaret.Bottom) / 2 };
        if (!ClientToScreen(info.hwndCaret, ref point))
            return (false, 0, 0);
        if (point.X < _area.Left || point.X >= _area.Right || point.Y < _area.Top || point.Y >= _area.Bottom)
            return (false, 0, 0);
        return (true, point.X - _area.Left, point.Y - _area.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref POINT point);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint period);
}

/// <summary>Windows' own pointer shapes, known by the handles Windows hands every app for them.</summary>
internal static class PointerKinds
{
    private static readonly Dictionary<IntPtr, PointerKind> Known = Load();

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    private static Dictionary<IntPtr, PointerKind> Load()
    {
        var map = new Dictionary<IntPtr, PointerKind>();
        foreach (var (id, kind) in new[]
        {
            (32512, PointerKind.Arrow), (32513, PointerKind.Text), (32514, PointerKind.Wait), (32515, PointerKind.Cross),
            (32642, PointerKind.SizeNWSE), (32643, PointerKind.SizeNESW), (32644, PointerKind.SizeWE), (32645, PointerKind.SizeNS),
            (32646, PointerKind.Move), (32648, PointerKind.No), (32649, PointerKind.Hand), (32650, PointerKind.Busy),
        })
        {
            var handle = LoadCursor(IntPtr.Zero, id);
            if (handle != IntPtr.Zero)
                map.TryAdd(handle, kind);
        }
        return map;
    }

    public static PointerKind Of(IntPtr cursor) => Known.TryGetValue(cursor, out var kind) ? kind : PointerKind.Other;
}
