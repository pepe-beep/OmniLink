using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Strategies;
using OmniLink.Interop;

namespace OmniLink.Platform.Windows;

/// <summary>
/// v2.0 (ย้ายจาก KvmHostService แบบพฤติกรรมเดิม 100%): ประตูอินพุตฝั่ง Windows —
/// Low-Level Keyboard/Mouse Hook + message pump thread + watchdog rehook + เคอร์เซอร์ +
/// จอ + ภาษา (HKL) + foreground forensics — ทุกกับดัก Win32 อยู่ในไฟล์นี้ไฟล์เดียว
/// (ดู docs/war-room/TRAPS-AND-PITFALLS.md ก่อนแตะ hook เสมอ)
/// </summary>
public sealed class WindowsHostInput : IHostInputGateway
{
    private const uint WM_USER_REHOOK = 0x0400 + 101;

    private nint _kbHook;
    private nint _msHook;
    private readonly NativeMethods.LowLevelKeyboardProc _kbProcDelegate;
    private readonly NativeMethods.LowLevelMouseProc _msProcDelegate;
    private Func<KeyHookData, bool>? _onKey;
    private Func<MouseHookData, bool>? _onMouse;

    private Thread? _hookThread;
    private uint _hookThreadId;

    private long _lastHookEventTick = Environment.TickCount64;
    private long _msEventCount; // Stage A: จำนวน mouse event ที่ hook รับ
    private long _kbEventCount; // Stage A: จำนวน keyboard event ที่ hook รับ
    private long _lastRehookTick = 0;
    private long _rehookCount;

    public WindowsHostInput()
    {
        _kbProcDelegate = KeyboardHookCallback;
        _msProcDelegate = MouseHookCallback;
    }

    public void Start(Func<KeyHookData, bool>? onKey, Func<MouseHookData, bool> onMouse)
    {
        _onKey = onKey;
        _onMouse = onMouse;
        if (_hookThread is null)
        {
            _hookThread = new Thread(HookThreadLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "OmniLink_LLHook_Pump",
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();
        }
    }

    public void Stop()
    {
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, 0, 0);
            _hookThreadId = 0;
        }
    }

    /// <summary>Pre-JIT ตัว hook callback ของฝั่งนี้ก่อนติด hook (กัน first-event timeout จากการคอมไพล์ช้า)</summary>
    public void Warmup()
    {
        try
        {
            var kbMethod = typeof(WindowsHostInput).GetMethod(nameof(KeyboardHookCallback), BindingFlags.NonPublic | BindingFlags.Instance);
            if (kbMethod != null) RuntimeHelpers.PrepareMethod(kbMethod.MethodHandle);
            var msMethod = typeof(WindowsHostInput).GetMethod(nameof(MouseHookCallback), BindingFlags.NonPublic | BindingFlags.Instance);
            if (msMethod != null) RuntimeHelpers.PrepareMethod(msMethod.MethodHandle);
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[hook] gateway Pre-JIT warmup warning: {ex.Message}");
        }
    }

    #region Hook installation + message pump

    private void InstallHooks(nint hMod)
    {
        UninstallHooks();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _kbHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _kbProcDelegate, 0, 0);
            if (_kbHook == 0 && hMod != 0)
            {
                _kbHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _kbProcDelegate, hMod, 0);
            }
            if (_kbHook != 0) break;
            OmniLog.Error($"[hook] SetWindowsHookEx(KEYBOARD_LL) ล้มเหลว err={Marshal.GetLastWin32Error()} attempt={attempt}/3");
            Thread.Sleep(300 * attempt);
        }
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _msHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _msProcDelegate, 0, 0);
            if (_msHook == 0 && hMod != 0)
            {
                _msHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _msProcDelegate, hMod, 0);
            }
            if (_msHook != 0) break;
            OmniLog.Error($"[hook] SetWindowsHookEx(MOUSE_LL) ล้มเหลว err={Marshal.GetLastWin32Error()} attempt={attempt}/3");
            Thread.Sleep(300 * attempt);
        }
        if (_kbHook == 0 || _msHook == 0)
        {
            OmniLog.Error("[hook] ติดตั้ง Low-Level Hook ไม่สำเร็จ — โหมด KVM ใช้ไม่ได้ (UI และระบบเสียงยังทำงาน)");
        }
        else
        {
            OmniLog.Info($"[hook] Low-Level Hooks พร้อมทำงาน (kb={_kbHook != 0}, ms={_msHook != 0})");

            // F2: เพิ่มงบเวลา callback เป็น 5000ms (default ~300ms; Win10 1709+ cap ที่ 1000ms)
            // เขียนเฉพาะเมื่อยังไม่มีค่า — ไม่ทับที่ผู้ใช้ตั้งเอง
            try
            {
                using var dk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                if (dk?.GetValue("LowLevelHooksTimeout") is null)
                {
                    dk?.SetValue("LowLevelHooksTimeout", 5000, Microsoft.Win32.RegistryValueKind.DWord);
                    OmniLog.Write("hook", "ตั้ง LowLevelHooksTimeout=5000 (HKCU) — มีผลกับ hook ที่ติดตั้งใหม่");
                }
                else
                {
                    // Phase 5 forensics: ค่าปัจจุบันสำคัญกับการวินิจฉัย hook ตาย (ถ้าต่ำกว่า 3000 = เสี่ยงสูง)
                    OmniLog.Write("hook", $"LowLevelHooksTimeout ปัจจุบัน = {dk.GetValue("LowLevelHooksTimeout")} (มีผู้ตั้งไว้แล้ว — ไม่ทับ)");
                }
            }
            catch (Exception ex)
            {
                OmniLog.Warn("[hook] set LowLevelHooksTimeout failed: " + ex.Message);
            }
        }
        Interlocked.Exchange(ref _lastHookEventTick, Environment.TickCount64);
    }

    private void UninstallHooks()
    {
        if (_kbHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_kbHook);
            _kbHook = 0;
        }
        if (_msHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_msHook);
            _msHook = 0;
        }
    }

    private void HookThreadLoop()
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        var hMod = NativeMethods.GetModuleHandleW(null);

        // Win32 Trap: บังคับให้ OS สร้าง Message Queue ให้ worker thread นี้ก่อนเรียก SetWindowsHookEx
        NativeMethods.PeekMessageW(out _, 0, 0, 0, NativeMethods.PM_NOREMOVE);

        // C4: ติดตั้ง hook พร้อม retry + log — ห้ามล้มเงียบเพราะเมาส์จะหลุดการควบคุมโดยผู้ใช้ไม่รู้สาเหตุ
        InstallHooks(hMod);

        // C4: GetMessage คืน -1 เมื่อ error — ต้องแยกจาก 0 (WM_QUIT) ไม่งั้น loop จะวนรัวไร้ปลาย
        int ret;
        while ((ret = NativeMethods.GetMessageW(out var msg, 0, 0, 0)) != 0)
        {
            if (ret == -1)
            {
                OmniLog.Error("[hook] GetMessageW คืน -1 — หยุด message pump ของ hook thread");
                break;
            }

            if (msg.message == WM_USER_REHOOK)
            {
                OmniLog.Warn("[hook] ได้รับสัญญาณ WM_USER_REHOOK จาก Watchdog — กำลังติดตั้ง Hook ใหม่...");
                InstallHooks(hMod);
                continue;
            }

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        UninstallHooks();
    }

    /// <summary>
    /// BUG-1 Watchdog (เดิม CheckHookHealth ใน KvmHostService): ตรวจจับกรณี Windows แอบตัด
    /// Low-Level Hooks ทิ้งเงียบๆ (Silent Hook Drop) — ผู้ใช้กดคีย์/ขยับเมาส์จริง (GetLastInputInfo)
    /// แต่ Hook เงียบเกิน 15000ms → ยิง Probe → ไม่ตอบ = PostThreadMessage ให้ rehook
    /// </summary>
    public void RunHealthCheck()
    {
        try
        {
            if (_hookThreadId == 0) return;

            var lii = new NativeMethods.LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>();
            if (!NativeMethods.GetLastInputInfo(ref lii)) return;

            var now = Environment.TickCount64;
            var currentTick32 = unchecked((uint)now);
            var idleMs = currentTick32 - lii.dwTime;

            // ผู้ใช้มีการกดปุ่มหรือขยับเมาส์บนเครื่องจริงต่อเนื่อง (idleMs <= 1000ms)
            if (idleMs <= 1000)
            {
                var hookStaleMs = now - Interlocked.Read(ref _lastHookEventTick);
                // แต่ Hook Callback เงียบสนิทเกิน 15000ms
                if (hookStaleMs > 15000)
                {
                    // ส่ง Probe ทดสอบก่อนว่า Hook มีชีวิตอยู่จริงหรือไม่ (ป้องกัน False Positive จาก UIPI / Admin Window)
                    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_MOVE, 0, 0, 0, NativeMethods.HOOK_PROBE_MAGIC);
                    Thread.Sleep(50); // รอให้ Message Loop ประมวลผล Probe

                    var probeElapsed = Environment.TickCount64 - Interlocked.Read(ref _lastHookEventTick);
                    if (probeElapsed < 100)
                    {
                        // Hook ยังทำงานและตอบสนอง 100%! ที่เงียบไปเกิดจากหน้าต่างที่กำลังใช้งานอยู่มีสิทธิ์สูงกว่า (UIPI)
                        return;
                    }

                    // Probe ไม่ตอบสนอง = Hook ถูก Windows ปลดทิ้งจริงจาก timeout หรือระบบตื่นจาก sleep
                    var rehookElapsed = now - Interlocked.Read(ref _lastRehookTick);
                    if (rehookElapsed > 30000) // Cooldown 30 วินาที ป้องกัน flood
                    {
                        Interlocked.Exchange(ref _lastRehookTick, now);
                        var rehookCount = Interlocked.Increment(ref _rehookCount);
                        // Phase 5 forensics (R-1): เก็บ foreground app ไว้หา pattern ว่าแอปใดทำ hook ตาย
                        OmniLog.Warn($"[hook-watchdog] ตรวจพบ Hook หลุดจากระบบจริง! (Probe ไม่ตอบสนอง, User active idleMs={idleMs}ms, Hook เงียบมา {hookStaleMs}ms, foreground={DescribeForegroundApp()}, rehook ครั้งที่ {rehookCount}) ส่งคำสั่ง Re-hook...");
                        NativeMethods.PostThreadMessageW(_hookThreadId, WM_USER_REHOOK, 0, 0);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OmniLog.Error($"[hook-watchdog] ข้อผิดพลาด: {ex.Message}");
        }
    }

    #endregion

    #region Hook callbacks (hot path — ห้าม allocation หนัก/ห้ามโยน exception)

    private nint KeyboardHookCallback(int nCode, nuint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                Interlocked.Exchange(ref _lastHookEventTick, Environment.TickCount64);
                Interlocked.Increment(ref _kbEventCount);
                NativeMethods.KBDLLHOOKSTRUCT kb;
                unsafe
                {
                    kb = *(NativeMethods.KBDLLHOOKSTRUCT*)lParam;
                }
                var isInjected = (kb.flags & 0x10) != 0;

                if (!isInjected && _onKey is not null)
                {
                    var msg = (uint)(wParam & 0xFFFFFFFF);
                    var isDown = msg is (uint)NativeMethods.WM_KEYDOWN or (uint)NativeMethods.WM_SYSKEYDOWN;
                    var isUp = msg is (uint)NativeMethods.WM_KEYUP or (uint)NativeMethods.WM_SYSKEYUP;

                    var swallow = _onKey(new KeyHookData(kb.vkCode, kb.scanCode, (kb.flags & 0x01) != 0, isInjected, isDown, isUp, 0));
                    if (swallow)
                    {
                        return 1;
                    }
                }
            }
        }
        catch
        {
        }
        return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private nint MouseHookCallback(int nCode, nuint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            unsafe
            {
                var ms = *(NativeMethods.MSLLHOOKSTRUCT*)lParam;
                if (ms.dwExtraInfo == NativeMethods.HOOK_PROBE_MAGIC)
                {
                    Interlocked.Exchange(ref _lastHookEventTick, Environment.TickCount64);
                    return 1; // กลืน Probe สังเคราะห์ทิ้ง ไม่ส่งต่อให้แอปอื่นหรือ worker queue
                }

                Interlocked.Exchange(ref _lastHookEventTick, Environment.TickCount64);
                Interlocked.Increment(ref _msEventCount);

                var swallow = _onMouse is not null &&
                    _onMouse(new MouseHookData(MapMouseMessage((uint)wParam), ms.pt.X, ms.pt.Y, ms.mouseData, ms.time));
                if (swallow)
                {
                    return 1;
                }
            }
        }
        return NativeMethods.CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    private static MouseInputMessage MapMouseMessage(uint msg) => msg switch
    {
        (uint)NativeMethods.WM_MOUSEMOVE => MouseInputMessage.Move,
        (uint)NativeMethods.WM_LBUTTONDOWN => MouseInputMessage.LeftDown,
        (uint)NativeMethods.WM_LBUTTONUP => MouseInputMessage.LeftUp,
        (uint)NativeMethods.WM_RBUTTONDOWN => MouseInputMessage.RightDown,
        (uint)NativeMethods.WM_RBUTTONUP => MouseInputMessage.RightUp,
        (uint)NativeMethods.WM_MBUTTONDOWN => MouseInputMessage.MiddleDown,
        (uint)NativeMethods.WM_MBUTTONUP => MouseInputMessage.MiddleUp,
        (uint)NativeMethods.WM_MOUSEWHEEL => MouseInputMessage.Wheel,
        (uint)NativeMethods.WM_XBUTTONDOWN => MouseInputMessage.XDown,
        (uint)NativeMethods.WM_XBUTTONUP => MouseInputMessage.XUp,
        (uint)NativeMethods.WM_MOUSEHWHEEL => MouseInputMessage.HWheel,
        _ => MouseInputMessage.Other,
    };

    #endregion

    #region Cursor / monitors / language / foreground

    public bool GetCursorPos(out int x, out int y)
    {
        if (NativeMethods.GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }

    public void SetCursorPos(int x, int y) => NativeMethods.SetCursorPos(x, y);

    public ScreenBounds[] GetMonitors()
    {
        var list = new List<ScreenBounds>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint hMon, nint hdc, ref NativeMethods.RECT rc, nint data) =>
        {
            list.Add(new ScreenBounds(rc.Left, rc.Top, rc.Right, rc.Bottom));
            return true;
        }, 0);
        return list.ToArray();
    }

    public long GetForegroundToken() => NativeMethods.GetForegroundWindow();

    public int GetForegroundLangGroup()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        var tid = hwnd != 0 ? NativeMethods.GetWindowThreadProcessId(hwnd, out _) : 0;
        var hkl = NativeMethods.GetKeyboardLayout(tid);
        var langId = (int)(hkl & 0xFFFF);
        return langId == 0x041E ? 1 : 0;
    }

    public void RequestLangChange(int group)
    {
        var hkl = group == 1 ? NativeMethods.HKL_TH_TH : NativeMethods.HKL_EN_US;
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd != 0)
        {
            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, 0, hkl);
        }
    }

    public bool IsForegroundFullscreen() => NativeMethods.IsForegroundFullscreen();

    /// <summary>Phase 5 forensics: ชื่อโปรเซส + title ของหน้าต่าง foreground ณ ตอนนี้ (fail-safe ทุกจุด)</summary>
    public string DescribeForegroundApp()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == 0) return "(none)";
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            var title = new System.Text.StringBuilder(192);
            NativeMethods.GetWindowTextW(hwnd, title, 192);
            var procName = "";
            try { procName = Process.GetProcessById((int)pid)?.ProcessName ?? ""; } catch { }
            return $"{procName}(pid={pid})'{title}'";
        }
        catch
        {
            return "?";
        }
    }

    #endregion
}
