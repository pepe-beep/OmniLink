namespace OmniLink.Core.Abstractions;

/// <summary>
/// Abstraction สำหรับการเข้าถึงระบบ Clipboard ของระบบปฏิบัติการ (DIP / Information Expert)
/// แยกภาระ Win32/WPF Clipboard API ออกจาก UI Thread และ Controllers
/// </summary>
public interface IClipboardBridge
{
    /// <summary>
    /// อ่านข้อความตัวอักษรจาก Clipboard (คืน null หากไม่มีหรือไม่สามารถเข้าถึงได้)
    /// </summary>
    string? GetText();

    /// <summary>
    /// เขียนข้อความตัวอักษรลง Clipboard
    /// </summary>
    bool SetText(string text);

    /// <summary>
    /// อ่านรูปภาพในรูปแบบไบต์ PNG จาก Clipboard
    /// ให้ความสำคัญกับ Native PNG data format ก่อนเพื่อหลีกเลี่ยง bug กลับหัวของ WPF DIB
    /// </summary>
    bool TryGetPng(out byte[] pngBytes);

    /// <summary>
    /// เขียนรูปภาพไบต์ PNG ลง Clipboard
    /// </summary>
    bool SetPng(byte[] pngBytes);

    /// <summary>
    /// ตรวจสอบว่ามีรายการไฟล์อยู่ใน Clipboard หรือไม่ (CF_HDROP)
    /// </summary>
    bool ContainsFileDropList();

    /// <summary>
    /// อ่านรายชื่อไฟล์จาก Clipboard (คืน null หากไม่มี)
    /// isCut จะเป็นจริงหากเป็นการ Cut (Preferred DropEffect = DROPEFFECT_MOVE: 2)
    /// </summary>
    System.Collections.Generic.IReadOnlyList<string>? GetFileDropList(out bool isCut);

    /// <summary>
    /// เขียนรายชื่อไฟล์ลง Clipboard พร้อมระบุว่าเป็น Copy (1) หรือ Cut (2)
    /// </summary>
    bool SetFileDropList(System.Collections.Generic.IReadOnlyList<string> paths, bool isCut);
}
