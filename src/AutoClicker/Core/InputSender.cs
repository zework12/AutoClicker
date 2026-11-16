using static AutoClicker.Core.Native;

namespace AutoClicker.Core;

/// <summary>Builds and sends SendInput packets for one mouse button or one key.</summary>
internal static class InputSender
{
    public static void MouseButton(MouseButtonKind button, bool down)
    {
        uint flag = (button, down) switch
        {
            (MouseButtonKind.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (MouseButtonKind.Left, false) => MOUSEEVENTF_LEFTUP,
            (MouseButtonKind.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (MouseButtonKind.Right, false) => MOUSEEVENTF_RIGHTUP,
            (MouseButtonKind.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_MIDDLEUP,
        };
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi.dwFlags = flag;
        SendInput(1, [input], InputSize);
    }

    /// <summary>Moves the cursor to physical screen pixel (x, y) across all monitors.</summary>
    public static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1);
        int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);

        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi.dx = (int)Math.Round((x - vx) * 65535.0 / vw);
        input.u.mi.dy = (int)Math.Round((y - vy) * 65535.0 / vh);
        input.u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        SendInput(1, [input], InputSize);
    }

    /// <summary>Relative mouse move in mouse counts — what raw-input games read as camera movement.</summary>
    public static void MoveBy(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi.dx = dx;
        input.u.mi.dy = dy;
        input.u.mi.dwFlags = MOUSEEVENTF_MOVE;
        SendInput(1, [input], InputSize);
    }

    // Keys whose scan code needs the E0 prefix (arrows, Ins/Del/Home/End/PgUp/PgDn, right Ctrl/Alt, etc.).
    private static readonly HashSet<int> Extended =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home arrows
        0x2D, 0x2E,                                     // Insert Delete
        0x5B, 0x5C, 0x5D,                               // Win keys, Apps
        0x6F, 0x90,                                     // Numpad divide, NumLock
        0xA3, 0xA5,                                     // RControl, RMenu
    ];

    /// <summary>
    /// Sends a key by scan code. Games that read DirectInput/raw input only see
    /// scan codes, so this works in far more of them than a virtual-key event.
    /// </summary>
    public static void Key(int vk, bool down)
    {
        uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC_EX);
        uint flags = KEYEVENTF_SCANCODE;
        if (Extended.Contains(vk) || (scan & 0xFF00) == 0xE000) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;

        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk = 0;
        input.u.ki.wScan = (ushort)(scan & 0xFF);
        input.u.ki.dwFlags = flags;
        SendInput(1, [input], InputSize);
    }
}
