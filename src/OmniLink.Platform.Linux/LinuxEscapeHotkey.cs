using System.Runtime.InteropServices;
using System.Threading;
using OmniLink.Core;
using OmniLink.Core.Abstractions;

namespace OmniLink.Platform.Linux;

/// <summary>
/// ปุ่มหนีฉุกเฉินบน Linux (โหมด sub): XGrabKey Ctrl+Alt+Shift+Q —
/// คัดลอกจาก start_escape_hotkey ของ node agent ที่พิสูจน์แล้วทุกบรรทัด
/// (grab ทุก combination ของ lock/num/mod5 เพราะ modifier state ของผู้ใช้เปลี่ยนได้)
/// </summary>
public sealed class LinuxEscapeHotkey : IEscapeHotkeyHook
{
    private const string LibX11 = "libX11.so.6";
    private const uint ShiftMask = 1 << 0, LockMask = 1 << 1, ControlMask = 1 << 2;
    private const uint Mod1Mask = 1 << 3, Mod2Mask = 1 << 4, Mod5Mask = 1 << 7;
    private const int KeyPress = 2;

    [DllImport(LibX11)] private static extern nint XOpenDisplay(string? name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(nint dpy);
    [DllImport(LibX11)] private static extern ulong XDefaultRootWindow(nint dpy);
    [DllImport(LibX11)] private static extern int XGrabKey(nint dpy, int keycode, uint modifiers, ulong grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(LibX11)] private static extern int XSelectInput(nint dpy, ulong window, long mask);
    [DllImport(LibX11)] private static extern int XSync(nint dpy, int discard);
    [DllImport(LibX11)] private static extern int XNextEvent(nint dpy, byte[] eventReturn);

    private Thread? _thread;
    private volatile bool _running;
    private Action? _onTrigger;

    public void Start(Action onTrigger)
    {
        _onTrigger = onTrigger;
        if (_thread is { IsAlive: true }) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "OmniSub_Escape" };
        _thread.Start();
    }

    public void Stop() => _running = false;

    private void Loop()
    {
        nint dpy = 0;
        try
        {
            dpy = XOpenDisplay(null);
            if (dpy == 0)
            {
                OmniLog.Warn("[sub] เปิด display สำหรับ escape hotkey ไม่ได้ — ทางหนีใช้ keyboard hook ของ host-role แทนไม่ได้ (ต้องพึ่งปุ่มจาก host)");
                return;
            }
            var root = XDefaultRootWindow(dpy);
            var mods = ControlMask | Mod1Mask | ShiftMask; // Ctrl+Alt+Shift
            var grabbed = 0;
            foreach (var extra in new uint[]
                     {
                         0, LockMask, Mod2Mask, LockMask | Mod2Mask, Mod5Mask, Mod5Mask | LockMask,
                         Mod5Mask | Mod2Mask, Mod5Mask | LockMask | Mod2Mask,
                     })
            {
                XGrabKey(dpy, 24, mods | extra, root, 0, 1, 1); // keycode 24 = Q (evdev)
                grabbed++;
            }
            XSelectInput(dpy, root, KeyPress);
            XSync(dpy, 0);
            OmniLog.Write("sub", $"Ctrl+Alt+Shift+Q พร้อม ({grabbed} modifier combos)");

            var ev = new byte[256];
            while (_running)
            {
                XNextEvent(dpy, ev); // block — ตายเมื่อ display ปิดหรือ process จบ (background thread)
                var type = BitConverter.ToInt32(ev, 0);
                if (type == KeyPress)
                {
                    OmniLog.Write("sub", "ฉุกเฉิน! Ctrl+Alt+Shift+Q — สั่งคืนการคุม");
                    try { _onTrigger?.Invoke(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[sub] escape hotkey loop: " + ex.Message);
        }
        finally
        {
            if (dpy != 0)
            {
                try { XCloseDisplay(dpy); } catch { }
            }
        }
    }

    public void Dispose() => Stop();
}
