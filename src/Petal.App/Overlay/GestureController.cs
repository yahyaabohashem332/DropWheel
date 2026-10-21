using System.Windows.Threading;
using Petal.App.Input;
using Petal.App.Settings;
using static Petal.App.Interop.NativeMethods;

namespace Petal.App.Overlay;

/// <summary>
/// Turns raw input into wheel commands. A drag is "press, then move past the system drag
/// threshold". Tapping exactly one mapped modifier during that drag opens its wheel; tapping
/// another switches wheels in place. Releasing the key does nothing: the wheel stays until the drop.
/// Pressing the key just before the drag starts also counts.
/// </summary>
public sealed class GestureController : IDisposable
{
    private readonly GlobalInputHook _hook = new();
    private readonly WheelWindow _wheel;
    private readonly Dispatcher _ui;
    private readonly int _thresholdX = Math.Max(4, GetSystemMetrics(SM_CXDRAG));
    private readonly int _thresholdY = Math.Max(4, GetSystemMetrics(SM_CYDRAG));

    // Hook-thread state.
    private bool _pressed;
    private bool _dragging;
    private bool _wheelRequested;
    private ScreenPoint _pressPoint;
    private ScreenPoint _cursor;
    private WheelMode? _pending;
    private readonly HashSet<ModifierKey> _held = [];

    public GestureController(WheelWindow wheel, Dispatcher ui)
    {
        _wheel = wheel;
        _ui = ui;
        _hook.LeftButtonDown += OnDown;
        _hook.LeftButtonUp += OnUp;
        _hook.MouseMoved += OnMove;
        _hook.ModifierDown += OnModifierDown;
        _hook.ModifierUp += k => _held.Remove(k);
        _hook.EscapePressed += OnEscape;
        _hook.SideButtonDown = OnSideButton;
        _hook.SideButtonUp = button => Trigger(button);
        _hook.WheelTurned = OnWheelTurned;
    }

    /// <summary>The side button chosen in Settings went down, at this point.</summary>
    public Action<ScreenPoint>? MouseTriggerPressed { get; set; }

    private static bool Trigger(int button) =>
        !Settings.Paused && Settings.MouseButton switch
        {
            Wheels.MouseTrigger.Back => button == 1,
            Wheels.MouseTrigger.Forward => button == 2,
            _ => false,
        };

    private bool OnSideButton(int button, ScreenPoint at)
    {
        if (!Trigger(button) || _pressed)
            return false;
        _ui.BeginInvoke(DispatcherPriority.Send, () => MouseTriggerPressed?.Invoke(at));
        return true;
    }

    /// <summary>While a dragged file's wheel is open, the mouse wheel goes to the next wheel instead of scrolling the window below.</summary>
    private bool OnWheelTurned(int delta)
    {
        if (!_dragging || !_wheelRequested || !Settings.ScrollSwitchesWheels)
            return false;
        _ui.BeginInvoke(DispatcherPriority.Send, () => _wheel.Step(delta < 0 ? 1 : -1));
        return true;
    }

    public void Start() => _hook.Start();

    public bool IsDragging => Volatile.Read(ref _dragging);

    private static AppSettings Settings => SettingsStore.Current;

    private WheelMode? ModeFor(ModifierKey key)
    {
        var s = Settings;
        if (key == s.ConvertKey) return WheelMode.Conversions;
        if (key == s.ToolsKey) return WheelMode.Tools;
        if (key == s.ActionsKey) return WheelMode.Actions;
        return null;
    }

    private void OnDown(ScreenPoint p)
    {
        _pressed = true;
        _dragging = false;
        _wheelRequested = false;
        _pressPoint = p;
        _cursor = p;
        // A key already held when the press starts counts as a request, as if tapped now.
        _pending = _held.Count == 1 ? ModeFor(_held.First()) : null;
        _ui.BeginInvoke(DispatcherPriority.Send, _wheel.BeginSession);
    }

    private void OnMove(ScreenPoint p)
    {
        _cursor = p;
        if (!_pressed || _dragging)
            return;
        if (Math.Abs(p.X - _pressPoint.X) > _thresholdX || Math.Abs(p.Y - _pressPoint.Y) > _thresholdY)
        {
            _dragging = true;
            if (_pending is { } mode)
                RequestOpen(mode);
        }
    }

    private void OnUp(ScreenPoint p)
    {
        _pressed = false;
        _dragging = false;
        _pending = null;
        if (_wheelRequested)
            _ui.BeginInvoke(DispatcherPriority.Send, _wheel.PointerReleased);
        _wheelRequested = false;
    }

    private void OnModifierDown(ModifierKey key)
    {
        if (!_held.Add(key))
            return; // auto-repeat
        if (_held.Count != 1 || Settings.Paused || ModeFor(key) is not { } mode)
            return;
        if (!_pressed)
            return;
        if (_dragging)
            RequestOpen(mode);
        else
            _pending = mode;
    }

    private void RequestOpen(WheelMode mode)
    {
        _wheelRequested = true;
        var at = _cursor;
        _ui.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (_wheel.IsOpen)
                _wheel.SwitchMode(mode);
            else
                _wheel.Open(at, mode);
        });
    }

    private void OnEscape()
    {
        _pending = null;
        if (_wheelRequested)
            _ui.BeginInvoke(DispatcherPriority.Send, _wheel.Cancel);
        _wheelRequested = false;
    }

    public void Dispose() => _hook.Dispose();
}
