using System.Runtime.InteropServices;
using System.Threading;
using OmniLink.Core;
using OmniLink.Core.Abstractions;

namespace OmniLink.Platform.Windows;

/// <summary>
/// ปุ่มหนีฉุกเฉินบน Windows (โหมด sub): RegisterHotKey Ctrl+Alt+Shift+Q —
/// ใช้ hotkey ของ OS โดยตรง (ไม่ต้องง้อ LL hook ของ host-role ที่กำลังกลืนปุ่มอยู่)
/// เธรดนี้ถือ message queue ของตัวเองตามกฎ Win32 (RegisterHotKey ต้องมี message pump)
/// </summary>
public sealed class WindowsEscapeHotkey : IEscapeHotkeyHook
{
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;
    private const uint WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4F4D; // "OM"

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
    }

    private const uint WM_QUIT = 0x0012;
    private Thread? _thread;
    private volatile bool _running;
    private Action? _onTrigger;

    public void Start(Action onTrigger)
    {
        _onTrigger = onTrigger;
        if (_thread is { IsAlive: true }) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "OmniSub_Escape" };
        _thread.SetApartmentState(ApartmentState.STA); // ต้องมี message queue ของเธรดตัวเอง
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_thread is not null)
        {
            try { PostThreadMessageW((uint)_thread.ManagedThreadId, WM_QUIT, 0, 0); } catch { }
        }
    }

    private void Loop()
    {
        var tid = (uint)Environment.CurrentManagedThreadId;
        try
        {
            // 0x51 = 'Q'
            if (!RegisterHotKey(nint.Zero, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT, 0x51))
            {
                OmniLog.Warn("[sub] RegisterHotKey(Ctrl+Alt+Shift+Q) ไม่สำเร็จ — ทางหนีจะพึ่งปุ่ม panic ของ host เครื่องคุมเท่านั้น");
                return;
            }
            OmniLog.Write("sub", "Ctrl+Alt+Shift+Q พร้อม (RegisterHotKey)");
            while (_running)
            {
                var r = GetMessageW(out var msg, nint.Zero, 0, 0);
                if (r <= 0) break; // 0 = WM_QUIT, -1 = error
                if (msg.message == WM_HOTKEY && msg.wParam == HotkeyId)
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
            try { UnregisterHotKey(nint.Zero, HotkeyId); } catch { }
            _ = tid;
        }
    }

    public void Dispose() => Stop();
}
