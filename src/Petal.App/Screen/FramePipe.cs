using System.IO;
using System.Runtime.InteropServices;

namespace Petal.App.Screen;

/// <summary>
/// A recording's frames on their way to FFmpeg, written by a thread of their own so that taking them never waits for
/// FFmpeg. Once its first frames are in, FFmpeg reads nothing while Windows starts the graphics card's H.264 encoder:
/// 0.5–1.1 s at 1280×720, 1.3–2.4 s with the camera's video starting too, more with the card busy (0.1 s for Windows'
/// software encoder), and none of FFmpeg's options change that. Written as they were taken, every recording began with
/// its first picture held that long while the sound went on. Frames wait here meanwhile, each in memory of its own that
/// goes back to Windows once it is written: up to three seconds of them, in no more than a quarter of the memory free
/// and 768 MB. Past that the last one goes again, as when the PC falls behind.
/// </summary>
internal sealed unsafe class FramePipe : IDisposable
{
    /// <summary>Puts the next frame into <paramref name="into"/>; false when there is none yet.</summary>
    public delegate bool Fill(Span<byte> into);

    private readonly Stream _input;
    private readonly int _size, _most;
    private readonly object _gate = new();
    // Frames to write, in order; Zero stands for the frame before, once more.
    private readonly Queue<IntPtr> _waiting = new();
    private readonly Stack<IntPtr> _spare = new();
    private readonly Thread _writer;
    private int _frames;
    private bool _closing, _abandoned, _ended;
    private volatile bool _broken;

    /// <param name="most">How many frames may wait; by default what <see cref="MostFor"/> allows.</param>
    public FramePipe(Stream input, int frameBytes, int fps, string name, int? most = null)
    {
        _input = input;
        _size = frameBytes;
        _most = Math.Max(1, most ?? MostFor(frameBytes, fps));
        _writer = new Thread(Write) { IsBackground = true, Name = name };
        _writer.Start();
    }

    /// <summary>FFmpeg stopped taking frames: it went away.</summary>
    public bool Broken => _broken;

    private int _held;

    /// <summary>Frames' memory not yet given back to Windows (for the tests).</summary>
    internal int Held
    {
        get
        {
            lock (_gate)
                return _held;
        }
    }

    /// <summary>
    /// Three seconds of frames, in no more than a quarter of the memory free now and 768 MB with the five in hand (one
    /// being taken, one being written, the one before it and two spare); two at least.
    /// </summary>
    internal static int MostFor(int frameBytes, int fps)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        long free = GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPhys : 1L << 30;
        long budget = Math.Min(768L << 20, free / 4);
        return (int)Math.Clamp(budget / Math.Max(1, frameBytes) - 5, 2, 3L * fps);
    }

    /// <summary>
    /// The next frame, put by <paramref name="fill"/> into memory of the pipe's. While as many frames wait as may, the one
    /// before goes again instead, and <paramref name="fill"/> isn't asked. False when there was nothing to send yet.
    /// </summary>
    public bool Send(Fill fill)
    {
        IntPtr buffer;
        lock (_gate)
        {
            if (_ended)
                return true;
            if (_frames >= _most)
            {
                _waiting.Enqueue(IntPtr.Zero);
                Monitor.PulseAll(_gate);
                return true;
            }
            buffer = _spare.Count > 0 ? _spare.Pop() : Take();
        }
        bool filled = false;
        try
        {
            filled = fill(new Span<byte>((void*)buffer, _size));
        }
        finally
        {
            lock (_gate)
            {
                if (filled && !_ended)
                {
                    _waiting.Enqueue(buffer);
                    _frames++;
                    Monitor.PulseAll(_gate);
                }
                else
                {
                    Release(buffer);
                }
            }
        }
        return filled;
    }

    /// <summary>The frame sent last, once more.</summary>
    public void Again()
    {
        lock (_gate)
        {
            if (_ended)
                return;
            _waiting.Enqueue(IntPtr.Zero);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Writes what waits and waits for it to go, for <paramref name="most"/> at most; true when all went.</summary>
    public bool Finish(TimeSpan most)
    {
        lock (_gate)
        {
            _closing = true;
            Monitor.PulseAll(_gate);
        }
        return _writer.Join(most) && !_broken;
    }

    /// <summary>What still waits is let go unwritten (a recording thrown away); nothing waits for FFmpeg to take it.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _closing = _abandoned = true;
            Monitor.PulseAll(_gate);
        }
        _writer.Join(TimeSpan.FromSeconds(1));
    }

    private void Write()
    {
        IntPtr last = IntPtr.Zero;
        try
        {
            while (true)
            {
                IntPtr next;
                lock (_gate)
                {
                    while (_waiting.Count == 0 && !_closing)
                        Monitor.Wait(_gate);
                    if (_abandoned || _waiting.Count == 0)
                        return;
                    next = _waiting.Dequeue();
                    if (next != IntPtr.Zero)
                        _frames--;
                }
                if (next == IntPtr.Zero)
                {
                    if (last != IntPtr.Zero)
                        _input.Write(new ReadOnlySpan<byte>((void*)last, _size));
                    continue;
                }
                try
                {
                    _input.Write(new ReadOnlySpan<byte>((void*)next, _size));
                }
                finally
                {
                    if (last != IntPtr.Zero)
                        lock (_gate)
                            Release(last);
                    last = next;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // FFmpeg went away; the recording says so when it ends.
            _broken = true;
        }
        finally
        {
            // Nothing more is written: every frame's memory goes back.
            lock (_gate)
            {
                _ended = true;
                if (last != IntPtr.Zero)
                    Free(last);
                while (_waiting.Count > 0)
                    if (_waiting.Dequeue() is var p && p != IntPtr.Zero)
                        Free(p);
                while (_spare.Count > 0)
                    Free(_spare.Pop());
                _frames = 0;
            }
        }
    }

    // All three below are called holding _gate.
    private IntPtr Take()
    {
        _held++;
        return (IntPtr)NativeMemory.Alloc((nuint)_size);
    }

    /// <summary>A frame written or not wanted: two are kept for the next frames, the rest go back to Windows at once.</summary>
    private void Release(IntPtr buffer)
    {
        if (!_ended && _spare.Count < 2)
            _spare.Push(buffer);
        else
            Free(buffer);
    }

    private void Free(IntPtr buffer)
    {
        NativeMemory.Free((void*)buffer);
        _held--;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
