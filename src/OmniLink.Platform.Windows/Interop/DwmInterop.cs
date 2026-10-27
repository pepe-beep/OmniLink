using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OmniLink.Interop;

/// <summary>
/// DWM ของ Windows 11: มุมโค้ง + dark mode attribute
/// v2.0: ระบบ backdrop/วัสดุหน้าต่าง (Mica/Acrylic/Aero Glass/Accent) ถูกถอนออกทั้งหมดตามคำสั่งผู้ใช้ —
/// หน้าต่างของแอปเป็นสีทึบตลอด (ประหยัดพลังงาน + ไม่มีกับดัก Inactive Solid Fallback) เหลือเพียงธีมสีภายในของแอป
/// อ้างอิงสถาปัตยกรรมจาก AppGroupLauncher (DwmInterop.cs + TRAPS.md ข้อ 28 และ 120)
/// </summary>
internal static partial class DwmInterop
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    public enum Corner : int
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    public static bool SetRoundedCorners(nint hwnd, bool round)
    {
        var value = round ? (int)Corner.Round : (int)Corner.DoNotRound;
        return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, in value, sizeof(int)) == 0;
    }

    public static bool SetImmersiveDarkMode(nint hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, in value, sizeof(int)) == 0;
    }

    public static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value != 0;
            }
        }
        catch
        {
        }
        return false;
    }
}
