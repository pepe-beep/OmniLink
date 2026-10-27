namespace OmniLink.Core.Abstractions;

/// <summary>
/// v2.0 Platform SPI: ตัวฉีดอินพุตของ "โหมด sub" (เครื่องนี้ถูกคุมโดย host ตัวอื่น) —
/// เป็นคู่ตรงข้ามของ IHostInputGateway (เครื่องนี้คุมคนอื่น)
/// Windows impl = SendInput · Linux impl = XTest (ชุดเดียวกับ node agent)
/// ทุกเมธอดต้อง thread-safe ภายในตัวเองและเร็ว (ถูกเรียกจาก UDP hot path)
/// </summary>
public interface INodeInputInjector : IDisposable
{
    int ScreenWidth { get; }
    int ScreenHeight { get; }

    /// <summary>อ่านขนาดจอใหม่ (resolution เปลี่ยน / เพิ่งสร้าง)</summary>
    void RefreshDimensions();

    /// <summary>ตำแหน่งเคอร์เซอร์ปัจจุบัน (พิกัด root — ใช้ทั้งจุดวางตอน enter_screen และคำนวณ norm ตอน edge_return)</summary>
    (int X, int Y) QueryPointer();

    /// <summary>วาร์ปเคอร์เซอร์แบบ absolute (เก็บ tracked position ให้ด้วย)</summary>
    void MoveAbsolute(int x, int y);

    /// <summary>ขยับเคอร์เซอร์แบบ relative — คืนตำแหน่งใหม่ตามที่ track ไว้ (อาจถูก clamp ที่ขอบจอ)</summary>
    (int X, int Y) MoveRelative(int dx, int dy);

    /// <summary>ปุ่มเมาส์ (1=ซ้าย 2=กลาง 3=ขวา 8/9=X)</summary>
    void MouseButton(int button, bool down);

    /// <summary>ล้อเลื่อน (wx/wy เป็น delta สะสมแบบ Windows: ±120 ต่อ notch)</summary>
    void MouseWheel(int wx, int wy);

    /// <summary>คีย์ (X11 keycode 8-255 — ตามโปรโตคอลเดิมของ node agent)</summary>
    void KeyEvent(int x11Keycode, bool down);

    /// <summary>ปรับสถานะ modifier ให้ตรง mask (ปล่อยปุ่มที่ค้างเกิน — กัน stuck keys เวลา packet หล่น)</summary>
    void SyncModifiers(byte modMask);

    /// <summary>ปล่อยปุ่ม/ปุ่มเมาส์ทั้งหมดที่ตัวเองกดค้างไว้</summary>
    void ReleaseAll();
}
