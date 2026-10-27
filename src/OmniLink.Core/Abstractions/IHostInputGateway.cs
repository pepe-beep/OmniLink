using OmniLink.Core.Strategies;

namespace OmniLink.Core.Abstractions;

/// <summary>ข้อความเมาส์ระดับกลาง — gateway แปลงจาก message ของ OS เจ้าของเครื่องมาให้ (ไม่มี WM_* หลุดออกนอก interface)</summary>
public enum MouseInputMessage
{
    Move,
    LeftDown,
    LeftUp,
    MiddleDown,
    MiddleUp,
    RightDown,
    RightUp,
    XDown,
    XUp,
    Wheel,
    HWheel,
    /// <summary>ข้อความเมาส์ชนิดอื่นที่ engine ไม่สนใจ (คงไว้เพื่อ semantics เดิม: ส่งต่อแต่ไม่มี case รองรับ)</summary>
    Other,
}

/// <summary>
/// เหตุการณ์คีย์บอร์ดจาก hook ของ OS — ค่าบูลีนแยกชัดเจนแทน WM_KEYDOWN/WM_SYSKEYDOWN ของ Win32
/// X11Keycode: gateway ของ X11 แนบ keycode จริงมาเสมอ (engine ส่งต่อได้เลยโดยไม่ผ่านการแปลง Win32→X11),
/// ฝั่ง Windows = 0 (engine แปลงจาก VkCode/ScanCode ตามเดิม)
/// </summary>
public readonly record struct KeyHookData(uint VkCode, uint ScanCode, bool IsExtended, bool IsInjected, bool IsDown, bool IsUp, uint X11Keycode = 0);

/// <summary>เหตุการณ์เมาส์จาก hook ของ OS (struct ล้วน — ต้องไม่จอง heap บน hot path 1000Hz)</summary>
public readonly record struct MouseHookData(MouseInputMessage Message, int X, int Y, uint MouseData, uint Time);

/// <summary>
/// v2.0 Platform SPI: ประตูเดียวของ "อินพุตฝั่งเครื่อง host" — hook คีย์บอร์ด/เมาส์, เคอร์เซอร์,
/// ขอบเขตจอ, ภาษาปัจจุบันของเครื่อง และการตรวจสุขภาพ hook
/// Windows impl = WindowsHostInput (LL hook + message pump) · Linux impl = X11 (poll + XGrab/XTest)
/// เหตุการณ์ทั้งหมดถูกเรียกบน hook thread ของ gateway — handler ต้องเร็วและห้ามโยน exception ออกมา
/// (gateway กลืน exception เองเพื่อกัน OS ถอด hook)
/// </summary>
public interface IHostInputGateway
{
    /// <summary>เริ่มทำงาน (ติด hook + เธรด pump ของ OS) — onKey/onMouse คืน true = กลืน event ไม่ส่งต่อให้ OS</summary>
    void Start(Func<KeyHookData, bool>? onKey, Func<MouseHookData, bool> onMouse);

    /// <summary>หยุดทำงาน (ถอด hook + จบเธรด pump) — idempotent</summary>
    void Stop();

    /// <summary>Pre-JIT ตัว callback ของ gateway ก่อนติด hook (กัน first-event timeout จากการคอมไพล์ช้า)</summary>
    void Warmup();

    /// <summary>watchdog ตามจังหวะที่ผู้ใช้เรียก (เดิมทุก 3 วิ): ตรวจ hook ตาย + probe + rehook เอง</summary>
    void RunHealthCheck();

    /// <summary>อ่านพิกัดเคอร์เซอร์ปัจจุบัน (false = OS ให้ไม่ได้)</summary>
    bool GetCursorPos(out int x, out int y);

    /// <summary>วาร์ปเคอร์เซอร์ไปตำแหน่ง (ใช้ทั้งจุดยึดระหว่างคุม remote และวางเคอร์เซอร์ตอนกลับ host)</summary>
    void SetCursorPos(int x, int y);

    /// <summary>ขอบเขตจอทุกจอของเครื่องนี้ (virtual desktop)</summary>
    ScreenBounds[] GetMonitors();

    /// <summary>โทเคนของหน้าต่าง foreground ปัจจุบัน (เทียนว่าเปลี่ยนหน้าต่างหรือยัง — ค่าเฉพาะ OS)</summary>
    long GetForegroundToken();

    /// <summary>กลุ่มภาษาปัจจุบันของหน้าต่าง foreground (0 = EN, 1 = TH)</summary>
    int GetForegroundLangGroup();

    /// <summary>ขอให้ OS สลับภาษาของหน้าต่าง foreground ไปกลุ่มที่ระบุ</summary>
    void RequestLangChange(int group);

    /// <summary>มีหน้าต่างหน้าจอเต็ม (exclusive fullscreen) ทำงานอยู่หรือไม่</summary>
    bool IsForegroundFullscreen();

    /// <summary>คำอธิบายหน้าต่าง foreground เพื่อ forensic log (ชื่อโปรเซส + title)</summary>
    string DescribeForegroundApp();
}

/// <summary>
/// Gateway เปล่าสำหรับ unit test / headless ที่ไม่มีอินพุต — ทุกอย่าง no-op,
/// จอเดียว 1920×1080, ภาษา EN — โหมด edge-switch จะไม่ทำงานแต่โครงเครื่องยนต์ทั้งหมดยังเดินได้
/// </summary>
public sealed class NullHostInputGateway : IHostInputGateway
{
    public void Start(Func<KeyHookData, bool>? onKey, Func<MouseHookData, bool> onMouse) { }
    public void Stop() { }
    public void Warmup() { }
    public void RunHealthCheck() { }
    public bool GetCursorPos(out int x, out int y) { x = 0; y = 0; return false; }
    public void SetCursorPos(int x, int y) { }
    public ScreenBounds[] GetMonitors() => new[] { new ScreenBounds(0, 0, 1920, 1080) };
    public long GetForegroundToken() => 0;
    public int GetForegroundLangGroup() => 0;
    public void RequestLangChange(int group) { }
    public bool IsForegroundFullscreen() => false;
    public string DescribeForegroundApp() => "(null-gateway)";
}
