using System.Windows;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

/// <summary>
/// ทางผ่านจากหน้า Settings ย่อยกลับไปหา MainWindow/services — หน้าห้ามแตะ service ตรงๆ นอก interface นี้
/// (กันหน้า page รื้อ state ของ host โดยไม่รู้เรื่อง logging/refresh เหมือนโค้ดเดิม)
/// </summary>
public interface ISettingsHost
{
    OmniConfig Config { get; }
    KvmHostService Kvm { get; }
    OmniDiscoveryService Discovery { get; }
    Window Self { get; }

    /// <summary>เซฟ config แบบ debounce + เขียนบน background thread เสมอ (กฎ UI-H4: ห้าม I/O บน UI thread)</summary>
    void SaveConfig();
    void SetBanner(string message);
    void RefreshUi();
    void UpdateKvmStrategies();
    void ApplyThemeNow();
    void ApplyLogLevelFromConfig();
    void ExitApp();

    // Danger Zone — ทำผ่าน host เพราะต้องแตะ live sessions
    void ClearAllPairings();
    void ClearAllowlist();
    void FactoryReset();

    /// <summary>พาผู้ใช้ไปหน้า Security &amp; Pairing แล้วเริ่ม scan ให้เลย (ใช้จากปุ่ม Scan LAN บนแท็บ Devices)</summary>
    void NavigateToPairingAndScan();
}
