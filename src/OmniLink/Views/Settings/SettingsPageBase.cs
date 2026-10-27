namespace OmniLink.Views.Settings;

/// <summary>
/// สัญญาของหน้า Settings ย่อยทุกหน้า:
/// - LoadFromConfig() เติมค่าจาก config ลง controls (ตั้ง guard _init กัน handler ยิงเอง)
/// - controls เขียนค่ากลับ config แบบ live (clamp ตาม OmniLimits) + host.SaveConfig()
/// - RestoreDefaults() คืนค่าเฉพาะหน้าตัวเอง, ApplyLanguage() วาดข้อความที่เขียนด้วยโค้ดใหม่เมื่อสลับภาษา
/// หมายเหตุ: หน้าเป็น partial จาก XAML root UserControl — จึงใช้ interface แทน base class
/// (WPF generated partial ล็อค base เป็น root element เสมอ ทำให้สืบทอด class อื่นไม่ได้)
/// </summary>
public interface ISettingsPage
{
    /// <summary>key ภาษาของชื่อหน้า (โชว์บน rail ซ้าย เช่น "settings.section.general")</summary>
    string TitleKey { get; }

    /// <summary>ชื่อ Wpf.Ui symbol ของหน้า (ต้องผ่าน SymbolValidationTests)</summary>
    string IconSymbol { get; }

    void LoadFromConfig();

    /// <summary>คืนค่าหน้านี้เป็นค่าโรงงาน (เขียนลง config + controls ทันที)</summary>
    void RestoreDefaults();

    /// <summary>true เมื่อผู้ใช้แก้ control ใดๆ ตั้งแต่โหลดหน้าล่าสุด — ปุ่ม "ยกเลิกการแก้ไข" ใช้ตัวนี้
    /// (หน้านี้ live-apply: ค่าที่แก้ถูกบันทึกแล้ว IsDirty ใช้บอกว่า "มีอะไรเปลี่ยนจากค่าที่โหลด" ให้ปุ่มย้อนกลับ)</summary>
    bool IsDirty { get; }

    /// <summary>เรียกเมื่อสลับภาษา — rebuild ข้อความที่ตั้งด้วยโค้ด</summary>
    void ApplyLanguage()
    {
    }
}
