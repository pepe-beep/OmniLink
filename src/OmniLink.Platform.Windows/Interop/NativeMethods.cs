using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OmniLink.Interop;

internal static partial class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_XBUTTONUP = 0x020C;
    public const int WM_MOUSEHWHEEL = 0x020E;

    public const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    public const uint WM_SETICON = 0x0080;
    public const nint ICON_SMALL = 0;
    public const nint ICON_BIG = 1;

    public const nint HKL_EN_US = 0x04090409;
    public const nint HKL_TH_TH = 0x041E041E;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    public delegate nint LowLevelKeyboardProc(int nCode, nuint wParam, nint lParam);
    public delegate nint LowLevelMouseProc(int nCode, nuint wParam, nint lParam);
    public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public static readonly nint HOOK_PROBE_MAGIC = new nint(0x4F4D4E49); // "OMNI"

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, nint dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    // ─── Fullscreen detection (บล็อกสลับจอเมื่อมีแอปหน้าจอเต็ม) ───
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern nint GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern nint GetShellWindow();

    private static readonly object FsGate = new();
    private static bool _fsCached;
    private static long _fsCacheTick;

    /// <summary>เช็คว่าหน้าต่าง rect ครอบจอเต็มจอ (สำหรับ unit test)</summary>
    internal static bool IsRectFullScreen(RECT wnd, RECT mon, int tol = 2)
    {
        return wnd.Left <= mon.Left + tol &&
               wnd.Top <= mon.Top + tol &&
               wnd.Right >= mon.Right - tol &&
               wnd.Bottom >= mon.Bottom - tol &&
               (wnd.Right - wnd.Left) > 0 &&
               (wnd.Bottom - wnd.Top) > 0;
    }

    /// <summary>
    /// หน้าต่างเบื้องหน้าเป็นหน้าจอเต็ม (เกม borderless/วิดีโอ) หรือไม่ — cache 400ms
    /// ใช้ใน mouse hook จึงต้องถูกมาก (Win32 calls ไม่กี่ตัว) + แยก desktop/shell ออก
    /// </summary>
    public static bool IsForegroundFullscreen()
    {
        lock (FsGate)
        {
            var now = Environment.TickCount64;
            if (now - _fsCacheTick < 400)
            {
                return _fsCached;
            }
            _fsCacheTick = now;
            _fsCached = ComputeIsForegroundFullscreen();
            return _fsCached;
        }
    }

    private static bool ComputeIsForegroundFullscreen()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0) return false;
            if (hwnd == GetDesktopWindow() || hwnd == GetShellWindow()) return false;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return false;

            if (!GetWindowRect(hwnd, out var wr)) return false;
            var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hmon == 0) return false;
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(hmon, ref mi)) return false;

            return IsRectFullScreen(wr, mi.rcMonitor);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    // Phase 5 forensics: ชื่อหน้าต่าง foreground ตอน hook ตาย (หา trigger ของ BUG-1)
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern nint GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(nint hWnd);

    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(nint Pipe, out uint ServerProcessId);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>ดึงขนาด Physical RAM ทั้งหมดของเครื่องเป็นไบต์ (ใช้ตรวจจับเครื่องสเปกต่ำ เช่น Acer E5-411 แรม 2GB)</summary>
    public static ulong GetTotalPhysicalMemoryBytes()
    {
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                return mem.ullTotalPhys;
            }
        }
        catch { }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessWorkingSetSize(nint hProcess, nuint dwMinimumWorkingSetSize, nuint dwMaximumWorkingSetSize);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(nint hProcess);

    /// <summary>PROCESS_BASIC_INFORMATION (ทุกฟิลด์เป็น pointer-size ตาม padding จริงของทั้ง x86/x64)</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(nint processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    /// <summary>คืน PID ของ parent process (หรือ -1 ถ้าอ่านไม่ได้)</summary>
    public static int GetParentProcessId(Process p)
    {
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(p.Handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            return status == 0 ? (int)pbi.InheritedFromUniqueProcessId : -1;
        }
        catch
        {
            return -1;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint dpiContextA, nint dpiContextB);

    /// <summary>ตรวจ PerMonitorV2 จริงตาม TRAPS.md (DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4)</summary>
    public static bool IsPerMonitorV2()
    {
        try
        {
            var context = GetThreadDpiAwarenessContext();
            return context != 0 && AreDpiAwarenessContextsEqual(context, new nint(-4));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// คืนหน่วยความจำ Physical Working Set ตาม TRAPS-AND-PITFALLS.md กับดักที่ 7
    /// สั่ง GC Aggressive แล้วเรียก EmptyWorkingSet และ SetProcessWorkingSetSize คืน RAM สู่ Windows ทำให้กิน RAM เพียง ~10-25MB
    /// fullGc=false: ข้าม blocking GC (ใช้กับ periodic trim — blocking GC จะ suspend hook thread ซึ่งเสี่ยงโดน LowLevelHooksTimeout)
    /// </summary>
    public static void TrimWorkingSet(bool fullGc = true)
    {
        try
        {
            if (fullGc)
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            using var self = Process.GetCurrentProcess();
            var handle = self.Handle;
            EmptyWorkingSet(handle);
            SetProcessWorkingSetSize(handle, unchecked((nuint)(-1)), unchecked((nuint)(-1)));
        }
        catch
        {
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconExW(
        string lpszFile,
        int nIconIndex,
        out nint phiconLarge,
        out nint phiconSmall,
        uint nIcons);

    private static nint _cachedLargeIcon = nint.Zero;
    private static nint _cachedSmallIcon = nint.Zero;
    private static readonly object _iconLock = new();

    /// <summary>
    /// กำหนดไอคอนจริงของแอปพลิเคชัน (ทั้งไอคอนใหญ่และเล็ก) ให้กับ HWND โดยตรงผ่าน WM_SETICON
    /// ป้องกัน Windows Shell ตกเป็นไอคอน placeholder ขาวฟ้า (อ้างอิง WindowInterop.ApplyAppIcon ใน AppGroupLauncher)
    /// </summary>
    public static void ApplyAppIcon(nint hWnd)
    {
        if (hWnd == nint.Zero) return;

        lock (_iconLock)
        {
            if (_cachedLargeIcon == nint.Zero && _cachedSmallIcon == nint.Zero)
            {
                try
                {
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    {
                        ExtractIconExW(exe, 0, out _cachedLargeIcon, out _cachedSmallIcon, 1);
                    }
                }
                catch
                {
                }

                if (_cachedLargeIcon == nint.Zero && _cachedSmallIcon == nint.Zero)
                {
                    try
                    {
                        var sri = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/OmniLink;component/Assets/app.ico"));
                        if (sri?.Stream is not null)
                        {
                            using var icon = new System.Drawing.Icon(sri.Stream);
                            var cloned = (System.Drawing.Icon)icon.Clone();
                            _cachedLargeIcon = cloned.Handle;
                            _cachedSmallIcon = cloned.Handle;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (_cachedSmallIcon != nint.Zero)
            {
                SendMessage(hWnd, WM_SETICON, ICON_SMALL, _cachedSmallIcon);
            }
            if (_cachedLargeIcon != nint.Zero)
            {
                SendMessage(hWnd, WM_SETICON, ICON_BIG, _cachedLargeIcon);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    public const uint WM_QUIT = 0x0012;
    public const uint PM_NOREMOVE = 0x0000;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, uint Msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // C2: clipboard format listener — OS แจ้งเมื่อ clipboard เปลี่ยน (เบา ไม่ใช่ polling)
    public const uint WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(nint hwnd);

    // GroupA-M: MMCSS (avrt.dll) — จดทะเบียน thread กับ scheduler ของงาน latency-critical
    private static nint _mmcssHandle;
    public static nint LastMmcssHandle => _mmcssHandle;

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristicsW(string taskName, out uint taskIndex);

    [DllImport("avrt.dll")]
    private static extern bool AvRevertMmThreadCharacteristics_(nint avrtHandle);

    public static bool AvSetMmThreadCharacteristics(string taskName, out uint taskIndex)
    {
        _mmcssHandle = AvSetMmThreadCharacteristicsW(taskName, out taskIndex);
        return _mmcssHandle != 0;
    }

    public static void AvRevertMmThreadCharacteristics(nint avrtHandle)
    {
        if (avrtHandle != 0) AvRevertMmThreadCharacteristics_(avrtHandle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);
}
