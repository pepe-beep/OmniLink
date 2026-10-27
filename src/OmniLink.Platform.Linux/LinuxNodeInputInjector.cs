using System.Runtime.InteropServices;
using OmniLink.Core;
using OmniLink.Core.Abstractions;

namespace OmniLink.Platform.Linux;

/// <summary>
/// v2.0 โหมด sub บน Linux: ฉีดอินพุตผ่าน XTest — P/Invoke ชุดเดียวกับ X11Controller ของ node agent
/// (ยืนยันแล้วบน L440) เปิด display connection ของตัวเองแยกจาก host-gateway (แต่ละ connection
/// thread-safe ในตัวเอง — pattern เดียวกับ hotkey loop ของ agent)
/// </summary>
public sealed class LinuxNodeInputInjector : INodeInputInjector
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";

    [DllImport(LibX11)] private static extern nint XOpenDisplay(string? name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(nint dpy);
    [DllImport(LibX11)] private static extern ulong XDefaultRootWindow(nint dpy);
    [DllImport(LibX11)] private static extern int XDisplayWidth(nint dpy, int screen);
    [DllImport(LibX11)] private static extern int XDisplayHeight(nint dpy, int screen);
    [DllImport(LibX11)] private static extern int XFlush(nint dpy);
    [DllImport(LibX11)] private static extern bool XQueryPointer(nint dpy, ulong root,
        out ulong rootRet, out ulong childRet, out int rootX, out int rootY,
        out int winX, out int winY, out uint mask);
    [DllImport(LibXtst)] private static extern int XTestFakeMotionEvent(nint dpy, int screen, int x, int y, ulong delay);
    [DllImport(LibXtst)] private static extern int XTestFakeButtonEvent(nint dpy, uint button, int down, ulong delay);
    [DllImport(LibXtst)] private static extern int XTestFakeKeyEvent(nint dpy, uint keycode, int down, ulong delay);

    private readonly object _lock = new();
    private nint _dpy;
    private int _w, _h;
    private int _curX, _curY;
    private readonly HashSet<int> _heldKeys = new();
    private readonly HashSet<int> _heldButtons = new();

    private static readonly (byte Bit, int Keycode)[] ModMap =
    {
        (OmniProtocol.ModLShift, 50),
        (OmniProtocol.ModRShift, 62),
        (OmniProtocol.ModLCtrl, 37),
        (OmniProtocol.ModRCtrl, 105),
        (OmniProtocol.ModLAlt, 64),
        (OmniProtocol.ModRAlt, 108),
        (OmniProtocol.ModLSuper, 133),
        (OmniProtocol.ModRSuper, 134),
    };

    public LinuxNodeInputInjector(string display = ":0")
    {
        lock (_lock)
        {
            _dpy = XOpenDisplay(null);
            if (_dpy == 0) _dpy = XOpenDisplay(display);
            if (_dpy == 0)
            {
                OmniLog.Warn("[sub-linux] เปิด X display ไม่ได้ — การฉีดอินพุตใช้ไม่ได้ (รับคำสั่งได้แต่ไม่มีผล)");
                return;
            }
            _w = XDisplayWidth(_dpy, 0);
            _h = XDisplayHeight(_dpy, 0);
            _curX = _w / 2;
            _curY = _h / 2;
        }
    }

    public int ScreenWidth { get { lock (_lock) return _w; } }
    public int ScreenHeight { get { lock (_lock) return _h; } }

    public void RefreshDimensions()
    {
        lock (_lock)
        {
            if (_dpy == 0) return;
            _w = XDisplayWidth(_dpy, 0);
            _h = XDisplayHeight(_dpy, 0);
        }
    }

    public (int X, int Y) QueryPointer()
    {
        lock (_lock)
        {
            if (_dpy != 0 && XQueryPointer(_dpy, XDefaultRootWindow(_dpy), out _, out _, out var x, out var y, out _, out _, out _))
            {
                _curX = x; _curY = y; // PY-F11: sync tracked กับจริง
                return (x, y);
            }
            return (_curX, _curY);
        }
    }

    public void MoveAbsolute(int x, int y)
    {
        lock (_lock)
        {
            if (_dpy == 0) return;
            var cx = Math.Clamp(x, 0, Math.Max(0, _w - 1));
            var cy = Math.Clamp(y, 0, Math.Max(0, _h - 1));
            _curX = cx; _curY = cy;
            XTestFakeMotionEvent(_dpy, -1, cx, cy, 0);
            XFlush(_dpy);
        }
    }

    public (int X, int Y) MoveRelative(int dx, int dy)
    {
        lock (_lock)
        {
            if (_dpy == 0) return (_curX, _curY);
            var nx = Math.Clamp(_curX + dx, 0, Math.Max(0, _w - 1));
            var ny = Math.Clamp(_curY + dy, 0, Math.Max(0, _h - 1));
            _curX = nx; _curY = ny;
            XTestFakeMotionEvent(_dpy, -1, nx, ny, 0);
            XFlush(_dpy);
            return (nx, ny);
        }
    }

    public void MouseButton(int button, bool down)
    {
        lock (_lock)
        {
            if (_dpy == 0) return;
            if (down) _heldButtons.Add(button); else _heldButtons.Remove(button);
            XTestFakeButtonEvent(_dpy, (uint)button, down ? 1 : 0, 0);
            XFlush(_dpy);
        }
    }

    public void MouseWheel(int wx, int wy)
    {
        lock (_lock)
        {
            if (_dpy == 0) return;
            // X11: 4=ขึ้น 5=ลง 6=ซ้าย 7=ขวา (เหมือน agent — กี่ notch ตาม delta/120)
            if (wy != 0)
            {
                var btn = wy > 0 ? 4u : 5u;
                var steps = Math.Clamp(Math.Abs(wy) / 120, 1, 10);
                for (var i = 0; i < steps; i++)
                {
                    XTestFakeButtonEvent(_dpy, btn, 1, 0);
                    XTestFakeButtonEvent(_dpy, btn, 0, 0);
                }
            }
            if (wx != 0)
            {
                var btn = wx > 0 ? 7u : 6u;
                var steps = Math.Clamp(Math.Abs(wx) / 120, 1, 10);
                for (var i = 0; i < steps; i++)
                {
                    XTestFakeButtonEvent(_dpy, btn, 1, 0);
                    XTestFakeButtonEvent(_dpy, btn, 0, 0);
                }
            }
            XFlush(_dpy);
        }
    }

    public void KeyEvent(int x11Keycode, bool down)
    {
        if (x11Keycode < 8 || x11Keycode > 255) return;
        lock (_lock)
        {
            if (_dpy == 0) return;
            if (down) _heldKeys.Add(x11Keycode); else _heldKeys.Remove(x11Keycode);
            XTestFakeKeyEvent(_dpy, (uint)x11Keycode, down ? 1 : 0, 0);
            XFlush(_dpy);
        }
    }

    public void SyncModifiers(byte modMask)
    {
        lock (_lock)
        {
            if (_dpy == 0) return;
            var flushed = false;
            foreach (var (bit, kc) in ModMap)
            {
                var shouldBeDown = (modMask & bit) != 0;
                if (!shouldBeDown && _heldKeys.Contains(kc))
                {
                    XTestFakeKeyEvent(_dpy, (uint)kc, 0, 0);
                    _heldKeys.Remove(kc);
                    flushed = true;
                }
            }
            if (flushed) XFlush(_dpy);
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            if (_dpy == 0) return;
            foreach (var kc in _heldKeys.ToList())
            {
                XTestFakeKeyEvent(_dpy, (uint)kc, 0, 0);
            }
            _heldKeys.Clear();
            foreach (var (_, kc) in ModMap)
            {
                XTestFakeKeyEvent(_dpy, (uint)kc, 0, 0);
            }
            foreach (var btn in _heldButtons.ToList())
            {
                XTestFakeButtonEvent(_dpy, (uint)btn, 0, 0);
            }
            _heldButtons.Clear();
            XFlush(_dpy);
        }
    }

    public void Dispose()
    {
        ReleaseAll();
        lock (_lock)
        {
            if (_dpy != 0)
            {
                XCloseDisplay(_dpy);
                _dpy = 0;
            }
        }
    }
}
