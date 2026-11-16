namespace AutoClicker.Core;

public enum MouseButtonKind { Left, Right, Middle }

/// <summary>Repeat = press/release every interval; Hold = keep pressed until stopped.</summary>
public enum RunMode { Repeat, Hold }

public enum ClickPosition { Cursor, Fixed }

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Timings shared by the mouse and keyboard runners. All values in milliseconds.</summary>
public abstract class TimingSettings
{
    /// <summary>Period between the starts of two presses. Min 1.</summary>
    public double IntervalMs { get; set; } = 100;

    /// <summary>Each interval is shifted by a random value in [-RandomMs, +RandomMs].</summary>
    public int RandomMs { get; set; } = 0;

    /// <summary>How long the button/key stays down per press. 0 = down and up back to back.</summary>
    public int HoldMs { get; set; } = 10;

    /// <summary>Pause after Start before the first press.</summary>
    public int StartDelayMs { get; set; } = 0;

    /// <summary>Stop after this many presses. 0 = until stopped.</summary>
    public int Limit { get; set; } = 0;

    public RunMode Mode { get; set; } = RunMode.Repeat;

    /// <summary>UI only: the menu shows the interval as presses per second instead of ms. The engine ignores it.</summary>
    public bool IntervalAsRate { get; set; }
}

public sealed class MouseSettings : TimingSettings
{
    public MouseButtonKind Button { get; set; } = MouseButtonKind.Left;
    public bool DoubleClick { get; set; }
    public ClickPosition Position { get; set; } = ClickPosition.Cursor;

    /// <summary>Physical screen pixels (the app is per-monitor DPI aware).</summary>
    public int FixedX { get; set; }
    public int FixedY { get; set; }
}

public sealed class KeySettings : TimingSettings
{
    /// <summary>Win32 virtual-key code of the key to press. Default: E (0x45).</summary>
    public int VirtualKey { get; set; } = 0x45;

    /// <summary>Modifiers held together with the key.</summary>
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }

    public KeySettings() { IntervalMs = 500; HoldMs = 30; }
}

public enum CameraAxis { Horizontal, Vertical, Both }

/// <summary>
/// Periodic small camera nudge: a relative mouse move out and straight back,
/// so the view ends where it started. Mode/HoldMs are not used; HoldMs is the
/// pause between "out" and "back".
/// </summary>
public sealed class CameraSettings : TimingSettings
{
    /// <summary>How far to move, in mouse counts (what games read as raw input).</summary>
    public int DistancePx { get; set; } = 40;

    /// <summary>How long each half of the move takes; spread over ~5 ms steps so it looks smooth.</summary>
    public int MoveMs { get; set; } = 150;

    public CameraAxis Axis { get; set; } = CameraAxis.Horizontal;

    /// <summary>Pick left/right (up/down) at random each time instead of always the same side.</summary>
    public bool RandomDirection { get; set; } = true;

    public CameraSettings() { IntervalMs = 30_000; RandomMs = 5_000; HoldMs = 100; }
}

/// <summary>
/// Random mouse wander: smooth moves to random points inside a circle around
/// where it started, with random pauses. Stays bounded and returns to the
/// start on stop. IntervalMs/RandomMs = pause between moves; HoldMs, Mode and
/// Limit are not used.
/// </summary>
public sealed class RandomMoveSettings : TimingSettings
{
    /// <summary>Radius of the area the mouse wanders in, in mouse counts.</summary>
    public int RadiusPx { get; set; } = 120;

    /// <summary>Average duration of one move; each move varies it by ±50%.</summary>
    public int MoveMs { get; set; } = 300;

    /// <summary>Allow vertical movement too (scaled down to 40% so the view does not swing up and down much).</summary>
    public bool Vertical { get; set; } = true;

    public RandomMoveSettings() { IntervalMs = 700; RandomMs = 500; HoldMs = 0; }
}

/// <summary>A global hotkey: Win32 virtual-key plus MOD_* flags (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8).</summary>
public sealed class HotkeyBinding
{
    public int VirtualKey { get; set; }
    public uint Modifiers { get; set; }

    public HotkeyBinding() { }
    public HotkeyBinding(int vk, uint mods = 0) { VirtualKey = vk; Modifiers = mods; }
}

public sealed class OverlaySettings
{
    public bool Visible { get; set; } = true;
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopRight;
    public double Opacity { get; set; } = 0.9;
}

public sealed class AppSettings
{
    public MouseSettings Mouse { get; set; } = new();
    public KeySettings Keyboard { get; set; } = new();
    public CameraSettings Camera { get; set; } = new();
    public RandomMoveSettings RandomMove { get; set; } = new();

    /// <summary>
    /// Send input only while the window that was in front at the first press
    /// is still in front. If the user alt-tabs away, runners pause (and release
    /// anything held) instead of clicking into other programs.
    /// </summary>
    public bool LockToWindow { get; set; } = true;

    public HotkeyBinding MouseHotkey { get; set; } = new(0x75);          // F6
    public HotkeyBinding KeyboardHotkey { get; set; } = new(0x76);       // F7
    public HotkeyBinding MenuHotkey { get; set; } = new(0x77);           // F8
    public HotkeyBinding CameraHotkey { get; set; } = new(0x78);         // F9
    public HotkeyBinding RandomMoveHotkey { get; set; } = new(0x79);     // F10

    public OverlaySettings Overlay { get; set; } = new();

    /// <summary>UI only: the menu page that was open last (0 mouse … 4 settings). The engine ignores it.</summary>
    public int MenuPage { get; set; }
}
