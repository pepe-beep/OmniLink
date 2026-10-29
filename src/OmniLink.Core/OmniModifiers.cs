using System.Text.Json.Serialization;

namespace OmniLink.Core;

/// <summary>
/// v1.9 C2: แม็ปปุ่ม modifier ต่อเครื่อง (แบบ Deskflow ComputerSettings) —
/// source slot → target slot เช่น "alt" → "altgr" (คนที่ใช้ layout หลายประเทศ) หรือ → "none" (ปิดปุ่มนั้นสำหรับเครื่องนี้)
/// หมายเหตุ: มี 5 slot จริง (shift/ctrl/alt/super/altgr) — แผนเดิมบอก 6 รวม "meta" แต่ X11/OmniLink protocol
/// ไม่มี keycode meta แยก (meta = Mod4 = super ตามธรรมเนียม X11) จึงตัดออกพร้อมเหตุผล
/// แปลงฝั่ง host ก่อนส่งเท่านั้น — agent/protocol ไม่แตะ, ไม่แตะ INV-2/INV-11 (keyspace ไทยไม่ใช่ modifier)
/// </summary>
public static class OmniModifiers
{
    public const string Shift = "shift";
    public const string Ctrl = "ctrl";
    public const string Alt = "alt";
    public const string Super = "super";
    public const string AltGr = "altgr";
    public const string None = "none";

    /// <summary>slot ทั้งหมดที่ใช้เป็น source/target ได้ (ไม่รวม none สำหรับ source)</summary>
    public static readonly string[] Sources = { Shift, Ctrl, Alt, Super, AltGr };
    public static readonly string[] Targets = { Shift, Ctrl, Alt, Super, AltGr, None };

    /// <summary>X11 keycodes ของ modifier (ตรงกับ OmniProtocol.Win32ToX11Keycode)</summary>
    public const int LShift = 50, RShift = 62, LCtrl = 37, RCtrl = 105, LAlt = 64, RAlt = 108, LSuper = 133, RSuper = 134;

    /// <summary>keycode → source slot (altgr = RAlt เท่านั้น ตามธรรมเนียม X11)</summary>
    public static string? SlotOf(int x11Kc) => x11Kc switch
    {
        LShift or RShift => Shift,
        LCtrl or RCtrl => Ctrl,
        LAlt => Alt,
        RAlt => AltGr,
        LSuper or RSuper => Super,
        _ => null,
    };

    /// <summary>keycode แทนของ target slot (ใช้ปุ่มฝั่งซ้ายเป็นตัวแทน)</summary>
    public static int KeycodeOf(string slot) => slot switch
    {
        Shift => LShift,
        Ctrl => LCtrl,
        Alt => LAlt,
        Super => LSuper,
        AltGr => RAlt,
        _ => 0,
    };

    public static bool IsValidTarget(string? slot) => slot is not null && Targets.Contains(slot);

    /// <summary>
    /// แปลง keycode ของ modifier ตามแผนที่ของเครื่องปลายทาง — คีย์ปกติ (non-modifier) ผ่านทันทีไม่แตะ
    /// (INV-2: keyspace ไทยเช่น XK_Thai_fofan = 0x0DBF ไม่ตรงกับ modifier ใด → ไม่มีทางถูกแปลง)
    /// target "none" คืน 0 = caller ข้ามการส่ง (กลืนปุ่มนั้นทิ้งสำหรับเครื่องนี้)
    /// </summary>
    public static int RemapKeycode(int x11Kc, IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0) return x11Kc;
        var slot = SlotOf(x11Kc);
        if (slot is null) return x11Kc;
        if (!map.TryGetValue(slot, out var target) || !IsValidTarget(target) || target == slot) return x11Kc;
        return target == None ? 0 : KeycodeOf(target);
    }
}

/// <summary>v1.9 C1: 1 action ของกฎ hotkey — type + node id (เฉพาะ switch_node)</summary>
public sealed class HotkeyActionConfig
{
    public const string SwitchNode = "switch_node";
    public const string ToggleLock = "toggle_lock";
    public const string ToggleStream = "toggle_stream";
    public const string ToggleMic = "toggle_mic";
    public const string CalibrateEn = "calibrate_en";
    public const string CalibrateTh = "calibrate_th";
    public const string HistoryPicker = "history_picker";

    public static readonly string[] AllTypes = { SwitchNode, ToggleLock, ToggleStream, ToggleMic, CalibrateEn, CalibrateTh, HistoryPicker };

    [JsonPropertyName("type")]
    public string Type { get; set; } = SwitchNode;

    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    public HotkeyActionConfig() { }

    public HotkeyActionConfig(string type, string? nodeId = null)
    {
        Type = type;
        NodeId = nodeId;
    }
}

/// <summary>v1.9 C1: กฎ hotkey 1 แถว — ปุ่มลัด 1 ชุด → ทำหลาย action ต่อกด</summary>
public sealed class HotkeyRuleConfig
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = ""; // canonical เช่น "Ctrl+Alt+F1"

    [JsonPropertyName("actions")]
    public List<HotkeyActionConfig> Actions { get; set; } = new();
}
