namespace OmniLink.Core;

/// <summary>
/// สถานะ switching ทั้งหมดของระบบ — เจ้าของเดียวในอนาคตคือ InputPipeline (godclass-K6 ทางเลือก B)
/// K6a: รวมฟิลด์กระจัดกระจายจาก KvmHostService มาไว้ที่เดียว (semantics เดิม — access ตรง,
/// volatile คงเดิม, Interlocked/Volatile.Read ref ได้เพราะเป็น public field)
/// ข้อจำกัดจริง: LL keyboard hook ต้องตัดสินใจ synchronous → hook thread ยังเป็นผู้เขียน
/// ฟิลด์ที่ hook ตัดสินเอง (ModMask/WinKey sequence) — pipeline จะเป็นเจ้าของส่วนที่เหลือ
/// </summary>
internal sealed class SwitchingState
{
    // ── เดิมจาก K4 (LANG) ──
    /// <summary>ภาษาของ host ก่อนเริ่มควบคุมเครื่องลูก</summary>
    public int SavedHostLangBeforeClient;
    /// <summary>tick ที่ห้าม watcher poll ภาษา</summary>
    public long IgnoreHostLangPollUntilTick;

    // ── เดิมจาก K6a (HOOK/EDGE/MON) ──
    /// <summary>เครื่องที่กำลังควบคุมอยู่ (null = อยู่บน host)</summary>
    public string? ActiveNodeId;

    /// <summary>A2: ล็อกเมาส์ไว้จอปัจจุบัน (Scroll Lock toggle) — panic/unlock ยัง override เสมอ</summary>
    public volatile bool LockedToScreen;

    /// <summary>สถานะ modifier ค้างของฝั่ง remote (bit mask)</summary>
    public byte ModMask;
    public bool WinHeld;

    /// <summary>จุดกึ่งกลางจอขวาสุด (จุดคืนเคอร์เซอร์ตอน edge return)</summary>
    public int AnchorX = 960;
    public int AnchorY = 540;

    /// <summary>tick ล่าสุดที่กลับมา host (smart mic standby อ่าน)</summary>
    public long ReturnToHostTick;
    /// <summary>เมาส์อยู่ใกล้ขอบ (adaptive ping อ่าน)</summary>
    public volatile bool IsNearEdge;

    /// <summary>C3: 0 = ว่าง, 1 = มี edge-switch กำลังทำงาน (Interlocked gate)</summary>
    public int EdgeSwitchBusy;

    public long LastFsBlockLogTick; // throttle log การบล็อกเพราะ fullscreen
    public long LastLockLogTick;

    // win-key / grave swallow state machine (hook thread เท่านั้น)
    public bool WinSwallowedOnRemote;
    public bool WinForwardedToRemote;
    public bool WinConsumedByCombo;
    public bool GraveSwallowedDown;
}
