using System.Diagnostics;
using System.Text.Json;

namespace AutoClicker.Core;

/// <summary>
/// One auto-press loop on its own high-priority thread with ~1 ms precision.
/// Settings are snapshotted at Start, so editing them while running is safe;
/// changes take effect on the next Start.
///
/// Threading: <see cref="Changed"/> fires on the worker thread — marshal to the
/// dispatcher before touching UI. <see cref="Count"/> and <see cref="ActualCps"/>
/// are safe to poll from any thread (a 100–200 ms DispatcherTimer is plenty).
/// </summary>
public abstract class Runner
{
    private readonly object _gate = new();
    private readonly Queue<long> _recent = new();
    private ManualResetEventSlim _stop = new(true);
    private Thread? _thread;
    private IntPtr _target;
    private long _count;
    private volatile bool _running;
    private volatile bool _paused;

    /// <summary>Supplies the LockToWindow flag at start.</summary>
    internal Func<bool> LockToWindow { get; set; } = () => true;

    public bool IsRunning => _running;

    /// <summary>True while running but holding off because another window is in front.</summary>
    public bool IsPaused => _paused;

    /// <summary>Presses sent since the last Start.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>Measured presses per second over the last ~2 s (0 if idle).</summary>
    public double ActualCps
    {
        get
        {
            lock (_gate)
            {
                Trim(Stopwatch.GetTimestamp());
                if (_recent.Count < 2) return 0;
                double span = (_recent.Last() - _recent.Peek()) / (double)Stopwatch.Frequency;
                return span <= 0 ? 0 : (_recent.Count - 1) / span;
            }
        }
    }

    /// <summary>Raised on start, stop and pause/resume. Worker thread.</summary>
    public event EventHandler? Changed;

    public void Toggle()
    {
        if (_running) Stop(); else Start();
    }

    public void Start()
    {
        if (_running) return;
        Snapshot();
        _target = IntPtr.Zero;
        Interlocked.Exchange(ref _count, 0);
        lock (_gate) _recent.Clear();
        _stop = new ManualResetEventSlim(false);
        _running = true;
        _paused = false;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = GetType().Name };
        _thread.Start(_stop);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (!_running) return;
        _stop.Set();
        // The loop releases anything held in its finally block; give it a moment.
        _thread?.Join(500);
    }

    protected abstract TimingSettings Timing { get; }
    protected abstract void Snapshot();
    protected abstract void Press(ManualResetEventSlim stop);
    protected abstract void HoldDown();
    protected abstract void ReleaseAll();

    private void Loop(object? state)
    {
        var stop = (ManualResetEventSlim)state!;
        var t = Timing;
        bool lockToWindow = LockToWindow();
        Native.timeBeginPeriod(1);
        try
        {
            if (!Wait(stop, t.StartDelayMs)) return;

            if (t.Mode == RunMode.Hold) RunHold(stop, lockToWindow);
            else RunRepeat(stop, t, lockToWindow);
        }
        finally
        {
            ReleaseAll();
            Native.timeEndPeriod(1);
            _paused = false;
            _running = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RunRepeat(ManualResetEventSlim stop, TimingSettings t, bool lockToWindow)
    {
        long freq = Stopwatch.Frequency;
        long next = Stopwatch.GetTimestamp();
        while (!stop.IsSet && (t.Limit <= 0 || Count < t.Limit))
        {
            if (!CanSend(lockToWindow))
            {
                SetPaused(true);
                if (!Wait(stop, 20)) return;
                next = Stopwatch.GetTimestamp();
                continue;
            }
            SetPaused(false);

            Press(stop);
            Interlocked.Increment(ref _count);
            long now = Stopwatch.GetTimestamp();
            lock (_gate) { _recent.Enqueue(now); Trim(now); }

            double period = t.IntervalMs + (t.RandomMs > 0 ? Random.Shared.Next(-t.RandomMs, t.RandomMs + 1) : 0);
            period = Math.Max(period, t.HoldMs + 1);
            period = Math.Max(period, 1);
            next += (long)(period * freq / 1000.0);
            // If we fell far behind (system stall), don't burst to catch up.
            if (next < now) next = now;
            if (!WaitUntil(stop, next)) return;
        }
    }

    private void RunHold(ManualResetEventSlim stop, bool lockToWindow)
    {
        bool held = false;
        while (!stop.IsSet)
        {
            bool can = CanSend(lockToWindow);
            SetPaused(!can);
            if (can && !held) { HoldDown(); held = true; Interlocked.Exchange(ref _count, 1); }
            else if (!can && held) { ReleaseAll(); held = false; }
            if (!Wait(stop, 20)) return;
        }
    }

    /// <summary>
    /// Never send into our own windows; with LockToWindow, only into the window
    /// that was in front at the first press.
    /// </summary>
    private bool CanSend(bool lockToWindow)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid == (uint)Environment.ProcessId) return false;
        if (!lockToWindow) return true;
        if (_target == IntPtr.Zero) _target = fg;
        return fg == _target;
    }

    private void SetPaused(bool value)
    {
        if (_paused == value) return;
        _paused = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Trim(long now)
    {
        long window = Stopwatch.Frequency * 2;
        while (_recent.Count > 0 && now - _recent.Peek() > window) _recent.Dequeue();
        while (_recent.Count > 200) _recent.Dequeue();
    }

    /// <summary>Waits <paramref name="ms"/> ms; false if stop was requested.</summary>
    protected static bool Wait(ManualResetEventSlim stop, double ms)
    {
        if (ms <= 0) return !stop.IsSet;
        return WaitUntil(stop, Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0));
    }

    /// <summary>Sleeps coarsely, then spins the last ~1.5 ms for precision.</summary>
    protected static bool WaitUntil(ManualResetEventSlim stop, long deadline)
    {
        long freq = Stopwatch.Frequency;
        while (true)
        {
            if (stop.IsSet) return false;
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0) return true;
            double ms = remaining * 1000.0 / freq;
            if (ms > 2.0)
            {
                if (stop.Wait((int)(ms - 1.5))) return false;
            }
            else
            {
                Thread.SpinWait(50);
            }
        }
    }

    protected static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}

public sealed class MouseRunner : Runner
{
    private readonly Func<MouseSettings> _source;
    private MouseSettings _s = new();
    private volatile bool _down;

    public MouseRunner(Func<MouseSettings> source) => _source = source;

    protected override TimingSettings Timing => _s;
    protected override void Snapshot() => _s = Clone(_source());

    protected override void Press(ManualResetEventSlim stop)
    {
        if (_s.Position == ClickPosition.Fixed) InputSender.MoveTo(_s.FixedX, _s.FixedY);
        Click(stop);
        if (_s.DoubleClick && !stop.IsSet)
        {
            Wait(stop, Math.Max(_s.HoldMs, 1));
            Click(stop);
        }
    }

    private void Click(ManualResetEventSlim stop)
    {
        InputSender.MouseButton(_s.Button, true);
        _down = true;
        Wait(stop, _s.HoldMs);
        InputSender.MouseButton(_s.Button, false);
        _down = false;
    }

    protected override void HoldDown()
    {
        if (_s.Position == ClickPosition.Fixed) InputSender.MoveTo(_s.FixedX, _s.FixedY);
        InputSender.MouseButton(_s.Button, true);
        _down = true;
    }

    protected override void ReleaseAll()
    {
        if (!_down) return;
        InputSender.MouseButton(_s.Button, false);
        _down = false;
    }
}

public sealed class KeyRunner : Runner
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    private readonly Func<KeySettings> _source;
    private KeySettings _s = new();
    private volatile bool _down;

    public KeyRunner(Func<KeySettings> source) => _source = source;

    protected override TimingSettings Timing => _s;
    protected override void Snapshot() => _s = Clone(_source());

    protected override void Press(ManualResetEventSlim stop)
    {
        Down();
        Wait(stop, _s.HoldMs);
        ReleaseAll();
    }

    protected override void HoldDown() => Down();

    private void Down()
    {
        if (_s.Ctrl) InputSender.Key(VK_CONTROL, true);
        if (_s.Shift) InputSender.Key(VK_SHIFT, true);
        if (_s.Alt) InputSender.Key(VK_MENU, true);
        InputSender.Key(_s.VirtualKey, true);
        _down = true;
    }

    protected override void ReleaseAll()
    {
        if (!_down) return;
        InputSender.Key(_s.VirtualKey, false);
        if (_s.Alt) InputSender.Key(VK_MENU, false);
        if (_s.Shift) InputSender.Key(VK_SHIFT, false);
        if (_s.Ctrl) InputSender.Key(VK_CONTROL, false);
        _down = false;
    }
}

public sealed class CameraRunner : Runner
{
    private readonly Func<CameraSettings> _source;
    private CameraSettings _s = new();
    // Offset moved "out" but not yet moved back; undone on stop/pause.
    private int _ox, _oy;

    public CameraRunner(Func<CameraSettings> source) => _source = source;

    protected override TimingSettings Timing => _s;

    protected override void Snapshot()
    {
        _s = Clone(_source());
        _s.Mode = RunMode.Repeat;
    }

    protected override void Press(ManualResetEventSlim stop)
    {
        int sx = _s.RandomDirection && Random.Shared.Next(2) == 0 ? -1 : 1;
        int sy = _s.RandomDirection && Random.Shared.Next(2) == 0 ? -1 : 1;
        int dx = _s.Axis == CameraAxis.Vertical ? 0 : sx * _s.DistancePx;
        int dy = _s.Axis == CameraAxis.Horizontal ? 0 : sy * _s.DistancePx;

        if (!Glide(stop, dx, dy)) return;
        if (!Wait(stop, _s.HoldMs)) return;
        Glide(stop, -dx, -dy);
    }

    /// <summary>Spreads a move over MoveMs in ~5 ms steps. False if stopped midway.</summary>
    private bool Glide(ManualResetEventSlim stop, int dx, int dy)
    {
        int steps = Math.Max(1, _s.MoveMs / 5);
        double stepMs = _s.MoveMs / (double)steps;
        int doneX = 0, doneY = 0;
        for (int i = 1; i <= steps; i++)
        {
            int tx = (int)Math.Round(dx * i / (double)steps);
            int ty = (int)Math.Round(dy * i / (double)steps);
            InputSender.MoveBy(tx - doneX, ty - doneY);
            _ox += tx - doneX;
            _oy += ty - doneY;
            doneX = tx;
            doneY = ty;
            if (i < steps && !Wait(stop, stepMs)) return false;
        }
        return true;
    }

    protected override void HoldDown() { }

    protected override void ReleaseAll()
    {
        // Put the camera back where it was if we were stopped mid-nudge.
        InputSender.MoveBy(-_ox, -_oy);
        _ox = 0;
        _oy = 0;
    }
}

public sealed class RandomMoveRunner : Runner
{
    private readonly Func<RandomMoveSettings> _source;
    private RandomMoveSettings _s = new();
    // Current offset from where the mouse was at start; undone on stop/pause.
    private double _ox, _oy;
    private int _sentX, _sentY;

    public RandomMoveRunner(Func<RandomMoveSettings> source) => _source = source;

    protected override TimingSettings Timing => _s;

    protected override void Snapshot()
    {
        _s = Clone(_source());
        _s.Mode = RunMode.Repeat;
        _s.HoldMs = 0;
        _s.Limit = 0;
    }

    protected override void Press(ManualResetEventSlim stop)
    {
        // Random target inside the circle (sqrt for an even spread over the area).
        double angle = Random.Shared.NextDouble() * Math.PI * 2;
        double r = Math.Sqrt(Random.Shared.NextDouble()) * _s.RadiusPx;
        double tx = Math.Cos(angle) * r;
        double ty = _s.Vertical ? Math.Sin(angle) * r * 0.4 : 0;

        int moveMs = Math.Max(20, (int)(_s.MoveMs * (0.5 + Random.Shared.NextDouble())));
        int steps = Math.Max(1, moveMs / 5);
        double stepMs = moveMs / (double)steps;
        double fromX = _ox, fromY = _oy;

        for (int i = 1; i <= steps; i++)
        {
            // Ease in-out so moves start and end softly.
            double t = i / (double)steps;
            double e = t * t * (3 - 2 * t);
            _ox = fromX + (tx - fromX) * e;
            _oy = fromY + (ty - fromY) * e;
            SendOffset();
            if (i < steps && !Wait(stop, stepMs)) return;
        }
    }

    /// <summary>Sends the whole-count difference between where we are and where we should be.</summary>
    private void SendOffset()
    {
        int x = (int)Math.Round(_ox), y = (int)Math.Round(_oy);
        InputSender.MoveBy(x - _sentX, y - _sentY);
        _sentX = x;
        _sentY = y;
    }

    protected override void HoldDown() { }

    protected override void ReleaseAll()
    {
        InputSender.MoveBy(-_sentX, -_sentY);
        _ox = _oy = 0;
        _sentX = _sentY = 0;
    }
}
