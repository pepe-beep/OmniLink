using System.Runtime.InteropServices;
using OmniLink.Core;
using OmniLink.Core.Abstractions;

namespace OmniLink.Platform.Windows;

/// <summary>
/// v2.0 โหมด sub บน Windows: ฉีดอินพุตด้วย SendInput (เมาส์ absolute/relative/ปุ่ม/ล้อ + คีย์)
/// การแปลง X11 keycode → VK เป็น inverse ของ OmniProtocol.Win32ToX11Keycode:
/// keycode พิเศษ (modifier/ล้อ/ฯลฯ) ใช้ตารางกลับ · extended scan → MapVirtualKey(ext) · ที่เหลือ scan=kc-8
/// </summary>
public sealed class WindowsNodeInputInjector : INodeInputInjector
{
    private readonly object _lock = new();
    private readonly HashSet<int> _heldKeys = new();
    private readonly HashSet<int> _heldButtons = new();
    private int _curX;
    private int _curY;

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEKEYBDINPUT mk;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEKEYBDINPUT
    {
        // MOUSEINPUT
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
        // KEYBDINPUT (overlay — ใช้ FieldOffset ไม่ได้ใน struct เดียวแบบ sequential ง่ายๆ จึงแยก path ตาม type)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT2
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT2[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
    private const uint XBUTTON1 = 1, XBUTTON2 = 2;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint MAPVK_VSC_TO_VK = 1, MAPVK_VSC_TO_VK_EX = 3;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    #endregion

    public int ScreenWidth => GetSystemMetrics(SM_CXSCREEN);
    public int ScreenHeight => GetSystemMetrics(SM_CYSCREEN);

    public void RefreshDimensions() { } // GetSystemMetrics อ่านสดทุกครั้งอยู่แล้ว

    public (int X, int Y) QueryPointer()
    {
        if (GetCursorPos(out var p)) return (p.X, p.Y);
        lock (_lock) { return (_curX, _curY); }
    }

    public void MoveAbsolute(int x, int y)
    {
        lock (_lock)
        {
            _curX = Math.Clamp(x, 0, Math.Max(0, ScreenWidth - 1));
            _curY = Math.Clamp(y, 0, Math.Max(0, ScreenHeight - 1));
        }
        MoveAbsoluteReal(_curX, _curY);
    }

    private void MoveAbsoluteReal(int x, int y)
    {
        var w = Math.Max(1, ScreenWidth);
        var h = Math.Max(1, ScreenHeight);
        var nx = (int)Math.Clamp(x, 0, w - 1) * 65535 / Math.Max(1, w - 1);
        var ny = (int)Math.Clamp(y, 0, h - 1) * 65535 / Math.Max(1, h - 1);
        SendMouseAbs(nx, ny);
    }

    private static void SendMouseAbs(int nx, int ny)
    {
        var input = new INPUT2
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = 0x8001 /*ABSOLUTE|MOVE*/ } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT2>());
    }

    public (int X, int Y) MoveRelative(int dx, int dy)
    {
        int nx, ny;
        lock (_lock)
        {
            _curX += dx;
            _curY += dy;
            _curX = Math.Clamp(_curX, 0, Math.Max(0, ScreenWidth - 1));
            _curY = Math.Clamp(_curY, 0, Math.Max(0, ScreenHeight - 1));
            nx = _curX; ny = _curY;
        }
        // เหมือน agent: ใช้ absolute จาก tracked (กัน relative acceleration ของบางแอป)
        MoveAbsoluteReal(nx, ny);
        return (nx, ny);
    }

    public void MouseButton(int button, bool down)
    {
        lock (_lock)
        {
            if (down) _heldButtons.Add(button); else _heldButtons.Remove(button);
        }
        var (flags, data) = button switch
        {
            1 => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
            2 => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
            3 => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
            8 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
            9 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
            _ => (0u, 0u),
        };
        if (flags == 0) return;
        var input = new INPUT2
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT2>());
    }

    public void MouseWheel(int wx, int wy)
    {
        // Windows ใช้ delta ต่อ notch โดยตรง (±120) — ส่งทีเดียวตามที่ได้รับ
        if (wy != 0)
        {
            var input = new INPUT2 { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)(int)wy) } } };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT2>());
        }
        if (wx != 0)
        {
            var input = new INPUT2 { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_HWHEEL, mouseData = unchecked((uint)(int)wx) } } };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT2>());
        }
    }

    public void KeyEvent(int x11Keycode, bool down)
    {
        if (x11Keycode < 8 || x11Keycode > 255) return;
        var (vk, extended) = X11KeycodeToVk(x11Keycode);
        if (vk == 0) return;
        lock (_lock)
        {
            if (down) _heldKeys.Add(x11Keycode); else _heldKeys.Remove(x11Keycode);
        }
        var flags = down ? 0u : KEYEVENTF_KEYUP;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT2
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT2>());
    }

    /// <summary>ปล่อย modifier ที่ค้างเกิน mask (เหมือน sync_modifiers ของ agent — กัน stuck keys)</summary>
    public void SyncModifiers(byte modMask)
    {
        var expected = new List<(int keycode, bool down)>
        {
            (50, (modMask & OmniProtocol.ModLShift) != 0),
            (62, (modMask & OmniProtocol.ModRShift) != 0),
            (37, (modMask & OmniProtocol.ModLCtrl) != 0),
            (105, (modMask & OmniProtocol.ModRCtrl) != 0),
            (64, (modMask & OmniProtocol.ModLAlt) != 0),
            (108, (modMask & OmniProtocol.ModRAlt) != 0),
            (133, (modMask & OmniProtocol.ModLSuper) != 0),
            (134, (modMask & OmniProtocol.ModRSuper) != 0),
        };
        lock (_lock)
        {
            foreach (var (kc, shouldBeDown) in expected)
            {
                var isDown = _heldKeys.Contains(kc);
                if (!shouldBeDown && isDown)
                {
                    KeyEvent(kc, false);
                }
            }
        }
    }

    public void ReleaseAll()
    {
        List<int> keys;
        List<int> buttons;
        lock (_lock)
        {
            keys = _heldKeys.ToList();
            _heldKeys.Clear();
            buttons = _heldButtons.ToList();
            _heldButtons.Clear();
        }
        foreach (var kc in keys.Concat(new[] { 50, 62, 37, 105, 64, 108, 133, 134 }))
        {
            KeyEvent(kc, false);
        }
        foreach (var b in buttons)
        {
            MouseButton(b, false);
        }
    }

    /// <summary>inverse ของ OmniProtocol.Win32ToX11Keycode — modifier พิเศษตามตาราง, ext scan ตามชุด, ที่เหลือ scan+8</summary>
    private static (ushort Vk, bool Extended) X11KeycodeToVk(int kc) => kc switch
    {
        50 => (0xA0, false),   // VK_LSHIFT
        62 => (0xA1, false),   // VK_RSHIFT
        37 => (0xA2, false),   // VK_LCONTROL
        105 => (0xA3, true),   // VK_RCONTROL (ext)
        64 => (0xA4, false),   // VK_LMENU
        108 => (0xA5, true),   // VK_RMENU (ext)
        133 => (0x5B, false),  // VK_LWIN
        134 => (0x5C, false),  // VK_RWIN
        127 => (0x13, false),  // VK_PAUSE
        77 => (0x90, false),   // VK_NUMLOCK
        78 => (0x91, false),   // VK_SCROLL
        121 => (0xAD, false),  // Volume mute
        122 => (0xAE, false),  // Volume down
        123 => (0xAF, false),  // Volume up
        104 => (0x0D, true),   // KP_Enter
        106 => (0x6F, true),   // KP_Divide
        107 => (0x2A, true),   // Print
        110 => (0x24, true),   // Home
        111 => (0x26, true),   // Up
        112 => (0x21, true),   // PageUp
        113 => (0x25, true),   // Left
        114 => (0x27, true),   // Right
        115 => (0x23, true),   // End
        116 => (0x28, true),   // Down
        117 => (0x22, true),   // PageDown
        118 => (0x2D, true),   // Insert
        119 => (0x2E, true),   // Delete
        135 => (0x5D, false),  // Menu
        >= 9 and <= 96 => ((ushort)MapVirtualKeyW((uint)(kc - 8), MAPVK_VSC_TO_VK), false),
        _ => (0, false),
    };

    private static void SendMouseEvent(uint flags, uint mouseData)
    {
        var input = new INPUT2
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT2>());
    }

    public void Dispose() => ReleaseAll();
}
