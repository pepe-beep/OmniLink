using System.Globalization;

namespace OmniLink.Core;

/// <summary>
/// v1.9 C1: แปลง/จับคู่ hotkey string แบบ canonical ("Ctrl+Alt+F1") — pure logic ทดสอบได้ไม่ต้องมี hook
/// ข้อจำกัด v1: ใช้ได้เฉพาะ Ctrl/Alt/Shift (ห้าม Win เพราะโค้ดจัดการ Win ค้างไว้ทั้งฟอร์มเพื่อกัน Start Menu/Activities — ให้ conflict ได้เป๊ะต้องแตะ logic ชุดนั้น)
/// จับคู่ฝั่ง hook เป็น lookup (vk, modMask) → actions แบบ O(1) — ห้ามทำงานหนักใน LL hook (INV-5)
/// </summary>
public static class HotkeyKey
{
    /// <summary>ปุ่ม trigger ที่รับได้: F1-F24, A-Z, 0-9, Space, Backspace, Tab, Enter, Esc, Home/End/PageUp/PageDown, ลูกศร, Print/Pause</summary>
    public static readonly IReadOnlyDictionary<string, uint> NamedKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D, ["esc"] = 0x1B,
        ["home"] = 0x24, ["end"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["print"] = 0x2C, ["pause"] = 0x13,
        ["context"] = 0x5D, // Menu key
    };

    /// <summary>แยกส่วนประกอบ — ผิดรูปแบบคืน false (ไม่โยน)</summary>
    public static bool TryParse(string? text, out uint vk, out int modMask, out string canonical)
    {
        vk = 0;
        modMask = 0;
        canonical = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var wantCtrl = false;
        var wantAlt = false;
        var wantShift = false;
        string? keyPart = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    if (wantCtrl) return false;
                    wantCtrl = true;
                    break;
                case "alt":
                    if (wantAlt) return false;
                    wantAlt = true;
                    break;
                case "shift":
                    if (wantShift) return false;
                    wantShift = true;
                    break;
                case "win" or "super" or "meta":
                    return false; // v1 ไม่รับ Win (ดูหัวคลาส)
                default:
                    if (keyPart is not null) return false; // ปุ่ม trigger ได้ปุ่มเดียว
                    keyPart = part;
                    break;
            }
        }

        if (keyPart is null) return false;
        if (!TryKeyToVk(keyPart, out vk)) return false;
        if (!wantCtrl && !wantAlt && !wantShift) return false; // ต้องมี modifier อย่างน้อย 1 — กันแย่งปุ่มพิมพ์ธรรมดา

        if (wantCtrl) modMask |= OmniProtocol.ModLCtrl | OmniProtocol.ModRCtrl;
        if (wantAlt) modMask |= OmniProtocol.ModLAlt | OmniProtocol.ModRAlt;
        if (wantShift) modMask |= OmniProtocol.ModLShift | OmniProtocol.ModRShift;

        canonical = Canonicalize(wantCtrl, wantAlt, wantShift, vk);
        return true;
    }

    /// <summary>
    /// จับคู่ state ปัจจุบันของ hook — mask ของกฎเป็น "บิทคู่ L|R ต่อ modifier" (จาก TryParse)
    /// ผ่านเมื่อ current มีอย่างน้อยหนึ่งฝั่งของทุก modifier ที่กฎต้องการ (กดเกิน เช่น เผลอชิด Shift ก็ยังตรง)
    /// </summary>
    public static bool Matches(int ruleMask, int currentModMask)
    {
        const int shiftPair = OmniProtocol.ModLShift | OmniProtocol.ModRShift;
        const int ctrlPair = OmniProtocol.ModLCtrl | OmniProtocol.ModRCtrl;
        const int altPair = OmniProtocol.ModLAlt | OmniProtocol.ModRAlt;
        if ((ruleMask & shiftPair) != 0 && (currentModMask & shiftPair) == 0) return false;
        if ((ruleMask & ctrlPair) != 0 && (currentModMask & ctrlPair) == 0) return false;
        if ((ruleMask & altPair) != 0 && (currentModMask & altPair) == 0) return false;
        return true;
    }

    public static string Canonicalize(bool ctrl, bool alt, bool shift, uint vk)
    {
        var text = VkToName(vk);
        if (text is null) return "";
        var s = "";
        if (ctrl) s += "Ctrl+";
        if (alt) s += "Alt+";
        if (shift) s += "Shift+";
        return s + text;
    }

    /// <summary>ชื่อ canonical ของ vk (คืน null = เป็น trigger ไม่ได้)</summary>
    public static string? VkToName(uint vk)
    {
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        return NamedKeys.FirstOrDefault(kv => kv.Value == vk).Key is { } name && name.Length > 0
            ? char.ToUpperInvariant(name[0]) + name[1..]  // pageup → PageUp (คงตัวพิมพ์เดิมของตัวอักษรหลังตัวแรก)
            : null;
    }

    private static bool TryKeyToVk(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
            return false;
        }
        if (key.Length is 2 or 3 && key[0] is 'F' or 'f' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            return true;
        }
        return NamedKeys.TryGetValue(key, out vk);
    }

    /// <summary>
    /// ตรวจชนกับของในระบบ — คืนรายการ conflict codes (แปลงเป็นข้อความที่ UI layer):
    /// duplicate:<key> = กฎซ้ำกันเอง, reserved.scrolllock:<key>, reserved.emergency:<key>
    /// </summary>
    public static List<string> FindConflicts(IReadOnlyList<HotkeyRuleConfig> rules)
    {
        var conflicts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Key)) continue;
            if (!seen.Add(rule.Key))
            {
                conflicts.Add("duplicate:" + rule.Key);
                continue;
            }
            if (!TryParse(rule.Key, out var vk, out var mask, out _)) continue;

            // Scroll Lock toggle (vk 0x91 ตามลำพัง)
            if (vk == 0x91) conflicts.Add("reserved.scrolllock:" + rule.Key);
            // ฉุกเฉิน Ctrl+Alt+Backspace
            if (vk == 0x08 && (mask & (OmniProtocol.ModLCtrl | OmniProtocol.ModLAlt)) != 0)
            {
                conflicts.Add("reserved.emergency:" + rule.Key);
            }
        }
        return conflicts;
    }
}
