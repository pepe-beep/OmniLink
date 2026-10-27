namespace OmniLink.Core.Abstractions;

/// <summary>
/// v2.0 Platform SPI: ปุ่มหนีฉุกเฉินของเครื่องที่ "กำลังถูกคุม" (โหมด sub) —
/// บทเรียน input-leap/barrier จาก node agent: เครื่องที่ถูกกักเมาส์ต้องหนีได้ด้วยคีย์บอร์ดจริงของตัวเองเสมอ
/// (Ctrl+Alt+Shift+Q — ตัวเดียวกับ node agent) · Windows = RegisterHotKey · Linux = XGrabKey
/// </summary>
public interface IEscapeHotkeyHook : IDisposable
{
    /// <summary>เริ่มดักปุ่ม (idempotent) — callback ถูกเรียกบนเธรดของ hook ต้องเร็วและไม่โยน</summary>
    void Start(Action onTrigger);

    void Stop();
}

/// <summary>ไม่ดักปุ่มจริง — สำหรับ test/แพลตฟอร์มที่ยังไม่มี implementation</summary>
public sealed class NullEscapeHotkeyHook : IEscapeHotkeyHook
{
    public Action? OnTriggered { get; private set; }

    public void Start(Action onTrigger) => OnTriggered = onTrigger;

    public void Stop() => OnTriggered = null;

    public void Dispose() { }
}
