using AutoClicker.Core;

namespace AutoClicker.Views;

/// <summary>Human-readable names for Win32 virtual keys and hotkey combinations.</summary>
internal static class KeyNames
{
    private static readonly Dictionary<int, string> Named = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause",
        [0x14] = "Caps Lock", [0x1B] = "Esc", [0x20] = "Пробел",
        [0x21] = "Page Up", [0x22] = "Page Down", [0x23] = "End", [0x24] = "Home",
        [0x25] = "←", [0x26] = "↑", [0x27] = "→", [0x28] = "↓",
        [0x2C] = "Print Screen", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x5B] = "Win", [0x5C] = "Правый Win", [0x5D] = "Menu",
        [0x6A] = "Num *", [0x6B] = "Num +", [0x6D] = "Num −", [0x6E] = "Num .", [0x6F] = "Num /",
        [0x90] = "Num Lock", [0x91] = "Scroll Lock",
        [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt",
        [0xA0] = "Левый Shift", [0xA1] = "Правый Shift",
        [0xA2] = "Левый Ctrl", [0xA3] = "Правый Ctrl",
        [0xA4] = "Левый Alt", [0xA5] = "Правый Alt",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/",
        [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
    };

    public static string Key(int vk)
    {
        if (vk == 0) return "не задана";
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return $"Num {vk - 0x60}";
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        return Named.TryGetValue(vk, out var name) ? name : $"Клавиша {vk:X2}";
    }

    public static string Combo(int vk, uint modifiers)
    {
        if (vk == 0) return Key(0);
        var parts = new List<string>(4);
        if ((modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(Key(vk));
        return string.Join(" + ", parts);
    }

    public static string Combo(HotkeyBinding binding) => Combo(binding.VirtualKey, binding.Modifiers);

    /// <summary>The key the keyboard runner presses, with its Ctrl/Shift/Alt companions.</summary>
    public static string Combo(KeySettings key)
    {
        uint mods = (key.Ctrl ? Native.MOD_CONTROL : 0) | (key.Shift ? Native.MOD_SHIFT : 0) | (key.Alt ? Native.MOD_ALT : 0);
        return Combo(key.VirtualKey, mods);
    }
}
