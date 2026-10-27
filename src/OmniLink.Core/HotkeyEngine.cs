namespace OmniLink.Core;

/// <summary>
/// ย้ายออกจาก KvmHostService (godclass-K5): ดัชนีสูตรลัด (vk → กฎ) — build นอก hook thread
/// สลับทั้งก้อนด้วย Volatile ให้ hook thread อ่าน lock-free · ส่วน dispatch (RunHotkeyActions)
/// ที่เรียกข้าม EDGE/LANG คงอยู่ที่ orchestrator (KvmHostService)
/// </summary>
internal sealed class HotkeyEngine
{
    private Dictionary<uint, List<(int Mask, List<HotkeyActionConfig> Actions)>>? _index;

    public Dictionary<uint, List<(int Mask, List<HotkeyActionConfig> Actions)>>? Index =>
        Volatile.Read(ref _index);

    /// <summary>เรียกเมื่อ hotkeys ใน config เปลี่ยน (UI save) — rebuild นอก hook thread</summary>
    public void Reload(OmniConfig config)
    {
        var idx = new Dictionary<uint, List<(int Mask, List<HotkeyActionConfig> Actions)>>();
        foreach (var rule in config.Hotkeys)
        {
            if (!HotkeyKey.TryParse(rule.Key, out var vk, out var mask, out var canonical)) continue;
            if (rule.Actions.Count == 0) continue;
            rule.Key = canonical; // เก็บ canonical กลับลง config ให้ UI แสดงตรงกัน
            if (!idx.TryGetValue(vk, out var list))
            {
                idx[vk] = list = new List<(int, List<HotkeyActionConfig>)>();
            }
            list.Add((mask, rule.Actions));
        }
        Volatile.Write(ref _index, idx);
        OmniLog.Write("hotkey", $"hotkey index rebuilt: {idx.Values.Sum(l => l.Count)} rule(s)");
    }
}
