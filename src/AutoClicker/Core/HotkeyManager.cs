using System.Windows.Interop;

namespace AutoClicker.Core;

/// <summary>
/// Global hotkeys via RegisterHotKey on a hidden message-only window. This is
/// not a keyboard hook: Windows hands us only the registered combination.
/// Must be created on the UI thread; <see cref="Pressed"/> fires there.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _names = new();
    private int _nextId = 1;

    /// <summary>Argument: the name passed to <see cref="Register"/>.</summary>
    public event EventHandler<string>? Pressed;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("AutoClickerHotkeys")
        {
            ParentWindow = HWND_MESSAGE,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a hotkey; false if another program already owns it.</summary>
    public bool Register(string name, HotkeyBinding binding)
    {
        if (binding.VirtualKey == 0) return false;
        int id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, binding.Modifiers | Native.MOD_NOREPEAT, (uint)binding.VirtualKey))
            return false;
        _names[id] = name;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _names.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _names.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _names.TryGetValue(wParam.ToInt32(), out var name))
        {
            handled = true;
            Pressed?.Invoke(this, name);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
