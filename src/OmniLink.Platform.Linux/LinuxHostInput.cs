using System.Runtime.InteropServices;
using System.Threading;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Strategies;

namespace OmniLink.Platform.Linux;

/// <summary>
/// v2.0: ประตูอินพุตฝั่ง Linux (X11) — ชุด P/Invoke และหลักการทั้งหมดมาจาก
/// node_agent/omnilink_agent.py (X11Controller) ที่พิสูจน์แล้วบน L440:
/// - XInitThreads ก่อนแตะอะไร + ใช้ display เดียวภายใต้ lock
/// - XQueryPointer/XTestFakeMotionEvent สำหรับเคอร์เซอร์
/// - XkbGetState + XkbKeycodeToKeysym(38, group) == 0x0DBF ตรวจกลุ่มภาษา TH/EN (hardware-verified)
/// - XkbLockGroup(dpy, 0x0100=XkbUseCoreKbd, group) สลับภาษา
///
/// สองสถานะตามคำตอบของ onMouse (เซมานติกเดียวกับ LL hook ฝั่ง Windows — service เป็นเจ้าของคำตอบ):
/// - ปกติ (false): poll XQueryPointer ~125Hz ส่งเฉพาะ Move ให้ engine เช็คขอบ
/// - คุม remote (true): XGrabPointer+XGrabKeyboard บน root — อินพุตทั้งหมดมาที่เรา
///   (engine คำนวณ delta จาก absolute แล้ว warp กลับจุดยึดเองผ่าน SetCursorPos — เหมือน Windows)
/// </summary>
public sealed class LinuxHostInput : IHostInputGateway
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";

    private const uint XkbUseCoreKbd = 0x0100;
    private const int GrabSuccess = 0;
    private const int GrabModeAsync = 1;

    private const int EvKeyPress = 2, EvKeyRelease = 3, EvButtonPress = 4, EvButtonRelease = 5, EvMotionNotify = 6;

    // event mask มาตรฐานของ X11
    private const long PointerMotionMask = 1 << 6, ButtonPressMask = 1 << 2, ButtonReleaseMask = 1 << 3;

    #region X event structs (LayoutKind.Sequential ตามหัวข้อ Xutil.h — x86_64)

    [StructLayout(LayoutKind.Sequential)]
    private struct XAnyEvent
    {
        public int type;
        public ulong serial;
        public int send_event;
        public nint display;
        public ulong window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XKeyEvent
    {
        public XAnyEvent any;
        public ulong root, subwindow, time;
        public int x, y, x_root, y_root;
        public uint state;
        public uint keycode;
        public int same_screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XButtonEvent
    {
        public XAnyEvent any;
        public ulong root, subwindow, time;
        public int x, y, x_root, y_root;
        public uint state;
        public uint button;
        public int same_screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XMotionEvent
    {
        public XAnyEvent any;
        public ulong root, subwindow, time;
        public int x, y, x_root, y_root;
        public uint state;
        public byte is_hint;
        public int same_screen;
    }

    #endregion

    #region P/Invoke (ล็อกชุดเดียวกับ X11Controller ของ agent)

    [DllImport(LibX11)] private static extern int XInitThreads();
    [DllImport(LibX11)] private static extern nint XOpenDisplay(string? name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(nint dpy);
    [DllImport(LibX11)] private static extern ulong XDefaultRootWindow(nint dpy);
    [DllImport(LibX11)] private static extern int XDisplayWidth(nint dpy, int screen);
    [DllImport(LibX11)] private static extern int XDisplayHeight(nint dpy, int screen);
    [DllImport(LibX11)] private static extern int XFlush(nint dpy);
    [DllImport(LibX11)] private static extern int XSync(nint dpy, int discard);
    [DllImport(LibX11)] private static extern int XPending(nint dpy);
    [DllImport(LibX11)] private static extern int XNextEvent(nint dpy, nint eventReturn);
    [DllImport(LibX11)] private static extern int XSelectInput(nint dpy, ulong window, long mask);

    [DllImport(LibX11)] private static extern bool XQueryPointer(nint dpy, ulong root,
        out ulong rootRet, out ulong childRet, out int rootX, out int rootY,
        out int winX, out int winY, out uint mask);

    [DllImport(LibXtst)] private static extern int XTestFakeMotionEvent(nint dpy, int screen, int x, int y, ulong delay);

    [DllImport(LibX11)] private static extern int XGrabPointer(nint dpy, ulong grabWindow, bool ownerEvents,
        long eventMask, int pointerMode, int keyboardMode, ulong confineTo, ulong cursor, ulong time);
    [DllImport(LibX11)] private static extern int XUngrabPointer(nint dpy, ulong time);
    [DllImport(LibX11)] private static extern int XGrabKeyboard(nint dpy, ulong grabWindow, bool ownerEvents,
        int pointerMode, int keyboardMode, ulong time);
    [DllImport(LibX11)] private static extern int XUngrabKeyboard(nint dpy, ulong time);

    [DllImport(LibX11)] private static extern int XkbUseExtension(nint dpy, ref int maj, ref int min);
    [DllImport(LibX11)] private static extern bool XkbGetState(nint dpy, uint device, byte[] stateReturn);
    [DllImport(LibX11)] private static extern ulong XkbKeycodeToKeysym(nint dpy, byte keycode, int group, int level);
    [DllImport(LibX11)] private static extern bool XkbLockGroup(nint dpy, uint device, uint group);

    #endregion

    private readonly object _lock = new();
    private nint _dpy;
    private ulong _root;
    private int _screenW, _screenH;

    private Func<KeyHookData, bool>? _onKey;
    private Func<MouseHookData, bool>? _onMouse;
    private Thread? _loopThread;
    private volatile bool _running;
    private bool _grabbed;
    private long _lastEventTick = Environment.TickCount64;

    public LinuxHostInput(string display = ":0")
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Environment.SetEnvironmentVariable("DISPLAY", display);
        }
        lock (_lock)
        {
            XInitThreads(); // บทเรียน agent: ต้องเรียกก่อนใช้ X จากหลายเธรด
            _dpy = XOpenDisplay(null);
            if (_dpy == 0) _dpy = XOpenDisplay(display);
            if (_dpy == 0)
            {
                OmniLog.Warn($"[linux-input] เปิด X display '{display}' ไม่ได้ — edge-switch ใช้ไม่ได้ (engine อื่นยังเดินได้)");
                return;
            }
            _root = XDefaultRootWindow(_dpy);
            _screenW = XDisplayWidth(_dpy, 0);
            _screenH = XDisplayHeight(_dpy, 0);
            var maj = 1; var min = 0;
            XkbUseExtension(_dpy, ref maj, ref min);
            XSync(_dpy, 0);
            OmniLog.Write("linux-input", $"X11 พร้อม {_screenW}x{_screenH} (XKB v{maj}.{min})");
        }
    }

    public void Start(Func<KeyHookData, bool>? onKey, Func<MouseHookData, bool> onMouse)
    {
        _onKey = onKey;
        _onMouse = onMouse;
        if (_dpy == 0 || _loopThread is { IsAlive: true }) return;
        _running = true;
        _loopThread = new Thread(InputLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "OmniLink_LinuxInput",
        };
        _loopThread.Start();
    }

    public void Stop()
    {
        _running = false;
        ReleaseGrab();
    }

    public void Warmup() { }

    /// <summary>watchdog — เธรด input ตาย = เกิดใหม่ (เทียบเคียง rehook ของ Windows)</summary>
    public void RunHealthCheck()
    {
        if (!_running || Environment.TickCount64 - Interlocked.Read(ref _lastEventTick) < 30000) { }
        if (_running && _loopThread is not { IsAlive: true })
        {
            OmniLog.Warn("[linux-input] เธรด input ตาย — restart");
            _loopThread = null;
            if (_onMouse is not null) Start(_onKey, _onMouse);
        }
    }

    public bool GetCursorPos(out int x, out int y)
    {
        lock (_lock)
        {
            if (_dpy != 0 && XQueryPointer(_dpy, _root, out _, out _, out x, out y, out _, out _, out _))
            {
                return true;
            }
            x = 0; y = 0;
            return false;
        }
    }

    public void SetCursorPos(int x, int y)
    {
        lock (_lock)
        {
            if (_dpy == 0) return;
            XTestFakeMotionEvent(_dpy, -1, x, y, 0);
            XFlush(_dpy);
        }
    }

    public ScreenBounds[] GetMonitors()
    {
        lock (_lock)
        {
            // v1: จอเดียว (screen 0) — multi-monitor ผ่าน Xinerama เป็นขั้นถัดไป
            return _dpy == 0
                ? new[] { new ScreenBounds(0, 0, 1920, 1080) }
                : new[] { new ScreenBounds(0, 0, _screenW, _screenH) };
        }
    }

    /// <summary>X11 ไม่มีแนวคิด "หน้าต่าง foreground" ระดับภาษา (XKB group เป็น global) — คืน 0 คงที่ = watcher ไม่ตีความเป็นการสลับหน้าต่าง</summary>
    public long GetForegroundToken() => 0;

    public int GetForegroundLangGroup()
    {
        lock (_lock)
        {
            if (_dpy == 0) return 0;
            // เหมือน agent เป๊ะ: group จาก XkbStateRec → keysym ของปุ่ม 38 ('a' vs 'ฟ')
            var st = new byte[18];
            if (!XkbGetState(_dpy, XkbUseCoreKbd, st)) return 0;
            var group = st[0];
            var ks = XkbKeycodeToKeysym(_dpy, 38, group, 0);
            return ks == 0x0DBF ? 1 : 0;
        }
    }

    public void RequestLangChange(int group)
    {
        lock (_lock)
        {
            if (_dpy == 0) return;
            XkbLockGroup(_dpy, XkbUseCoreKbd, (uint)(group == 1 ? 1 : 0));
            XFlush(_dpy);
        }
    }

    /// <summary>v1: ยังไม่ตรวจ fullscreen ผ่าน EWMH — คืน false (BlockSwitchOnFullscreen ไม่ทำงานบน Linux รอบแรก)</summary>
    public bool IsForegroundFullscreen() => false;

    public string DescribeForegroundApp() => "(linux)";

    /// <summary>
    /// ถาม engine ว่ากำลังคุม remote อยู่ไหม — ส่ง event Other (service ไม่สนใจเนื้อหา แต่คืนค่า
    /// ActiveNodeId จาก handler เสมอ ตามเซมานติกของ OnMouseHook) = probe ฟรีไม่กระทบ state
    /// </summary>
    private bool EngineCapturing() => _onMouse?.Invoke(new MouseHookData(MouseInputMessage.Other, 0, 0, 0, 0)) ?? false;

    private void InputLoop()
    {
        var buf = Marshal.AllocHGlobal(256); // ใหญ่กว่า XEvent ที่ใหญ่สุด (XConfigureEvent ~96B) — กันเขียนทับ
        var lastX = -1; var lastY = -1;
        try
        {
            while (_running)
            {
                try
                {
                    Interlocked.Exchange(ref _lastEventTick, Environment.TickCount64);

                    if (!_grabbed)
                    {
                        // ── โหมดปกติ: poll เคอร์เซอร์เบาๆ (agent ใช้ pattern เดียวกัน — XQueryPointer ถูกมาก) ──
                        // (ไม่ยิง probe ถาม engine ตอน idle — การเข้าโหมดคุมจับได้จากคำตอบของ Move อยู่แล้ว
                        //  ทั้งจาก edge-switch และสูตรลัด ซึ่งจะจับ grab ในรอบถัดไป)
                        if (GetCursorPos(out var x, out var y) && (x != lastX || y != lastY))
                        {
                            lastX = x; lastY = y;
                            var sw = _onMouse?.Invoke(new MouseHookData(MouseInputMessage.Move, x, y, 0, 0)) ?? false;
                            if (sw)
                            {
                                GrabAll(); // engine เพิ่งสลับเข้า remote (รอบนี้จับเลย ไม่รอรอบหน้า)
                                lastX = lastY = -1;
                            }
                        }
                        Thread.Sleep(16); // ~60Hz — เพียงพอสำหรับ edge detection และเบามาก
                    }
                    else
                    {
                        // ── โหมดคุม remote: ถาม engine ว่ายังคุมอยู่ไหม (edge return → ปลด grab) แล้วอ่าน event จาก grab ──
                        if (!EngineCapturing())
                        {
                            ReleaseGrab();
                            continue;
                        }
                        int pending;
                        lock (_lock) { pending = _dpy == 0 ? 0 : XPending(_dpy); }
                        if (pending == 0)
                        {
                            Thread.Sleep(10); // release detection ≤10ms — ต่ำพอสำหรับ edge return
                            continue;
                        }
                        while (pending-- > 0)
                        {
                            lock (_lock)
                            {
                                XNextEvent(_dpy, buf);
                            }
                            DispatchXEvent(buf);
                        }
                    }
                }
                catch (Exception ex)
                {
                    OmniLog.Warn($"[linux-input] loop warning: {ex.Message}");
                    Thread.Sleep(200);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            ReleaseGrab();
        }
    }

    private unsafe void DispatchXEvent(nint buf)
    {
        var type = (*(XAnyEvent*)buf).type;
        switch (type)
        {
            case EvMotionNotify:
            {
                var m = *(XMotionEvent*)buf;
                // ส่ง absolute ตรงๆ — engine คำนวณ delta เทียบจุดยึดของมันเอง แล้ว warp กลับเอง
                // ผ่าน SetCursorPos (เหมือน Windows ที่ service ยิง SetCursorPos ทุก move)
                _onMouse?.Invoke(new MouseHookData(MouseInputMessage.Move, m.x_root, m.y_root, 0, 0));
                break;
            }
            case EvButtonPress:
            case EvButtonRelease:
            {
                var b = *(XButtonEvent*)buf;
                var down = type == EvButtonPress;
                var (msg, data) = b.button switch
                {
                    1 => (down ? MouseInputMessage.LeftDown : MouseInputMessage.LeftUp, 0u),
                    2 => (down ? MouseInputMessage.MiddleDown : MouseInputMessage.MiddleUp, 0u),
                    3 => (down ? MouseInputMessage.RightDown : MouseInputMessage.RightUp, 0u),
                    8 => (down ? MouseInputMessage.XDown : MouseInputMessage.XUp, 1u << 16),
                    9 => (down ? MouseInputMessage.XDown : MouseInputMessage.XUp, 2u << 16),
                    // ล้อ X11: 4=ขึ้น 5=ลง 6=ซ้าย 7=ขวา — แปลงเป็น delta ±120 บน 16 บิตสูงเหมือน Windows
                    4 => (MouseInputMessage.Wheel, 120u << 16),
                    5 => (MouseInputMessage.Wheel, unchecked((uint)(-120 << 16))),
                    7 => (MouseInputMessage.HWheel, 120u << 16),
                    6 => (MouseInputMessage.HWheel, unchecked((uint)(-120 << 16))),
                    _ => (MouseInputMessage.Other, 0u),
                };
                _onMouse?.Invoke(new MouseHookData(msg, 0, 0, data, 0));
                break;
            }
            case EvKeyPress:
            case EvKeyRelease:
            {
                var k = *(XKeyEvent*)buf;
                var down = type == EvKeyPress;
                var vk = KeycodeToNeutralVk((int)k.keycode);
                // X11Keycode แนบมาเสมอ — engine ใช้ตรงๆ โดยไม่ผ่านการแปลง Win32→X11
                _onKey?.Invoke(new KeyHookData(vk, (uint)Math.Max(0, k.keycode - 8), IsExtendedKey((int)k.keycode), false, down, !down, (uint)k.keycode));
                break;
            }
        }
    }

    private void GrabAll()
    {
        lock (_lock)
        {
            if (_dpy == 0 || _grabbed) return;
            // ไม่ warp ที่นี่ — engine จัดจุดยึดเองผ่าน SetCursorPos ตอน SwitchToNode ไปแล้ว
            var gp = XGrabPointer(_dpy, _root, false,
                PointerMotionMask | ButtonPressMask | ButtonReleaseMask,
                GrabModeAsync, GrabModeAsync, 0, 0, 0);
            var gk = XGrabKeyboard(_dpy, _root, false, GrabModeAsync, GrabModeAsync, 0);
            XFlush(_dpy);
            _grabbed = gp == GrabSuccess && gk == GrabSuccess;
            if (_grabbed)
            {
                // Gate3 บั๊กจริง: motion ที่ค้างใน queue "ก่อน" grab (จากการ warp ของ engine และ
                // เมาส์จริงที่ขอบ) จะกลายเป็น relative delta ผีทันทีที่เริ่ม dispatch — ทิ้งให้สิ้นเปลือง
                XSync(_dpy, 1); // discard:=True
                var drain = new byte[256];
                while (XPending(_dpy) > 0)
                {
                    XNextEvent(_dpy, Marshal.AllocHGlobal(256)); // placeholder — ใช้ buffer จริงด้านล่าง
                    break;
                }
                {
                    var buf = Marshal.AllocHGlobal(256);
                    try
                    {
                        while (XPending(_dpy) > 0)
                        {
                            XNextEvent(_dpy, buf);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                OmniLog.Write("linux-input", "grab pointer+keyboard สำเร็จ (กำลังคุมเครื่องอื่น — อินพุตเครื่องนี้ถูกกัก, drained)");
            }
            else
            {
                XUngrabPointer(_dpy, 0);
                XUngrabKeyboard(_dpy, 0);
                XFlush(_dpy);
                OmniLog.Warn($"[linux-input] grab ไม่สำเร็จ (ptr={gp} kbd={gk}) — ปลดทั้งคู่กันติดค้าง");
            }
        }
    }

    private void ReleaseGrab()
    {
        lock (_lock)
        {
            if (_dpy == 0 || !_grabbed) return;
            XUngrabKeyboard(_dpy, 0);
            XUngrabPointer(_dpy, 0);
            XFlush(_dpy);
            _grabbed = false;
            OmniLog.Write("linux-input", "ปลด grab (กลับมาใช้เครื่องนี้ปกติ)");
        }
    }

    /// <summary>แปลง X keycode → keysym → VK กลาง: Latin-1 keysym ตรงกับ VK โดยตรง, modifier พิเศษใช้ตาราง (บทเรียน: X keycode = evdev + 8)</summary>
    private uint KeycodeToNeutralVk(int keycode)
    {
        ulong ks;
        lock (_lock)
        {
            ks = _dpy == 0 ? 0 : XkbKeycodeToKeysym(_dpy, (byte)keycode, 0, 0);
        }
        return ks switch
        {
            0xFFEB => 0x5B, // Super_L → VK_LWIN
            0xFFEC => 0x5C, // Super_R → VK_RWIN
            0xFFE1 => 0xA0, // Shift_L → VK_LSHIFT
            0xFFE2 => 0xA1, // Shift_R
            0xFFE3 => 0xA2, // Control_L → VK_LCONTROL
            0xFFE4 => 0xA3, // Control_R
            0xFFE9 => 0xA4, // Alt_L → VK_LMENU
            0xFFEA => 0xA5, // Alt_R
            0xFF14 => 0x91, // Scroll_Lock → VK_SCROLL
            >= 0x20 and <= 0xFF => (uint)ks, // Latin-1 (Space กดคีย์อักษรตรงกรณีทั่วไป) = VK เดียวกัน
            _ => (uint)(ks & 0xFF), // BackSpace 0xFF08→0x08 = VK_BACK, Tab 0xFF09→0x09 = VK_TAB, Return→0x0D ✓
        };
    }

    private static bool IsExtendedKey(int keycode) => keycode is 105 or 108 or 62 or 114; // RCtrl/RAlt/RShift/Right
}
