namespace OmniLink.Core;

/// <summary>
/// สถานะวงจรชีวิตการเชื่อมต่อแบบ Symmetric Dual-Sided Adaptive State Machine (ADR-005)
/// เพื่อประหยัด CPU, ทรัพยากรเน็ตเวิร์ก และแบตเตอรี่บนแล็ปท็อป
/// </summary>
public enum ConnectionLifecycleState
{
    /// <summary>
    /// เชื่อมต่ออยู่ (Ping 1.5s) หรือเพิ่งหลุดการเชื่อมต่อ (&lt; 30s: retry ทุก 2s)
    /// </summary>
    Hot,

    /// <summary>
    /// ขาดการติดต่อนาน 30s - 5 นาที (หน่วงรอบ retry เป็น 20-30s)
    /// </summary>
    Cold,

    /// <summary>
    /// ขาดการติดต่อนานเกิน 5 นาที (หยุด retry 100%, รอ Reverse Knock UDP หรือปุ่มกดปลุก)
    /// </summary>
    Freeze
}
