using System;

namespace OmniLink.Core;

// godclass-K7e: แยกจาก OmniProtocol.cs (move-verbatim) — ตารางนี้ใช้กับ Windows-hook path เท่านั้น
// (Linux hostd ไม่มีทางเรียก: hook ของ Linux มาจาก Platform.Linux) —
// การดึงออกจาก Core แบบสมบูรณ์ต้องเพิ่ม method บน IHostInputGateway (บันทึกไว้เป็นงานอนาคต)
public static partial class OmniProtocol
{
    public static int Win32ToX11Keycode(uint vkCode, uint scanCode, bool isExtended)
    {
        switch (vkCode)
        {
            case 0xA0: return 50;  // VK_LSHIFT
            case 0xA1: return 62;  // VK_RSHIFT
            case 0xA2: return 37;  // VK_LCONTROL
            case 0xA3: return 105; // VK_RCONTROL
            case 0xA4: return 64;  // VK_LMENU
            case 0xA5: return 108; // VK_RMENU
            case 0x5B: return 133; // VK_LWIN
            case 0x5C: return 134; // VK_RWIN
            case 0x13: return 127; // VK_PAUSE
            case 0x90: return 77;  // VK_NUMLOCK
            case 0x91: return 78;  // VK_SCROLL
            case 0xAD: return 121; // VK_VOLUME_MUTE
            case 0xAE: return 122; // VK_VOLUME_DOWN
            case 0xAF: return 123; // VK_VOLUME_UP
        }

        if (isExtended)
        {
            switch (scanCode)
            {
                case 0x1C: return 104; // KP_Enter
                case 0x1D: return 105; // Control_R
                case 0x35: return 106; // KP_Divide
                case 0x37: return 107; // Print
                case 0x38: return 108; // Alt_R
                case 0x47: return 110; // Home
                case 0x48: return 111; // Up
                case 0x49: return 112; // Prior (PageUp)
                case 0x4B: return 113; // Left
                case 0x4D: return 114; // Right
                case 0x4F: return 115; // End
                case 0x50: return 116; // Down
                case 0x51: return 117; // Next (PageDown)
                case 0x52: return 118; // Insert
                case 0x53: return 119; // Delete
                case 0x5B: return 133; // Super_L
                case 0x5C: return 134; // Super_R
                case 0x5D: return 135; // Menu
            }
        }

        if (scanCode >= 1 && scanCode <= 88)
        {
            return (int)scanCode + 8;
        }

        return 0;
    }
}
