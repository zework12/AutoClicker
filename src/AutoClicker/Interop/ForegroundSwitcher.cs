using AutoClicker.Core;

namespace AutoClicker.Interop;

/// <summary>
/// Lends the foreground to the menu while it is open and hands it straight back
/// to whatever window had it before — in practice, the game.
/// </summary>
internal sealed class ForegroundSwitcher
{
    private IntPtr _previous;

    public void Take(IntPtr menu)
    {
        var current = Native.GetForegroundWindow();

        // Don't overwrite the remembered game with one of our own windows.
        if (current != IntPtr.Zero && current != menu)
        {
            Native.GetWindowThreadProcessId(current, out var pid);
            if (pid != (uint)Environment.ProcessId) _previous = current;
        }

        // Allowed here because a hotkey press counts as the last input event, so
        // Windows lets us change the foreground window.
        UiNative.SetForegroundWindow(menu);
    }

    public void Release()
    {
        if (_previous != IntPtr.Zero && UiNative.IsWindow(_previous))
            UiNative.SetForegroundWindow(_previous);

        _previous = IntPtr.Zero;
    }
}
