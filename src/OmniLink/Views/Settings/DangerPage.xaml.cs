using System.Windows;
using System.Windows.Controls;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

/// <summary>
/// Danger Zone — ย้ายจาก popup Advanced เดิม: ทุกปุ่มยืนยันผ่าน MessageBox + backup config อัตโนมัติก่อนทำ
/// (backup เก็บเป็น omnilink_config.json.backup-yyyyMMdd-HHmmss เหมือนเดิมทุกอย่าง)
/// </summary>
public partial class DangerPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    public bool IsDirty => false;
    public DangerPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
    }

    public string TitleKey => "settings.section.danger";
    public string IconSymbol => "Warning24";

    public void LoadFromConfig()
    {
        // ไม่มี control ที่ผูกค่า config — ปุ่มล้วน
    }

    public void RestoreDefaults() { /* ใช้ปุ่ม Factory Reset เท่านั้น */ }

    private bool ConfirmDanger(string message)
    {
        var result = MessageBox.Show(Host.Self,
            message + "\n\n" + Loc.T("danger.backup.note", "ระบบจะสำรอง config อัตโนมัติก่อนดำเนินการ\nดำเนินการต่อหรือไม่?"),
            Loc.T("danger.title", "ยืนยัน — Danger Zone"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private string BackupConfig()
    {
        var backupPath = OmniPaths.ConfigPath + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (System.IO.File.Exists(OmniPaths.ConfigPath))
        {
            System.IO.File.Copy(OmniPaths.ConfigPath, backupPath, overwrite: true);
        }
        return backupPath;
    }

    private void OnClearPairingsClick(object sender, RoutedEventArgs e)
    {
        var count = Host.Config.Nodes.Count;
        if (count == 0)
        {
            MessageBox.Show(Host.Self, Loc.T("danger.pairings.none", "ไม่มี node ที่จับคู่ไว้"),
                Loc.T("settings.section.danger", "Danger Zone"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ConfirmDanger(Loc.T("danger.pairings.confirm", "ลบการจับคู่ทั้งหมด {0} เครื่องถาวร (host จะตัดการเชื่อมต่อทุกเครื่องทันที)?", count))) return;

        var backup = BackupConfig();
        Host.ClearAllPairings();
        OmniLog.Warn($"[danger] clear all pairings ({count}) — config backup: {backup}");
        MessageBox.Show(Host.Self,
            Loc.T("danger.done", "เสร็จแล้ว (backup: {0})", System.IO.Path.GetFileName(backup)),
            Loc.T("settings.section.danger", "Danger Zone"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnClearAllowlistClick(object sender, RoutedEventArgs e)
    {
        if ((Host.Config.AllowedSubnets?.Count ?? 0) == 0)
        {
            MessageBox.Show(Host.Self, Loc.T("danger.allowlist.none", "Allowlist ว่างอยู่แล้ว (โหมดอนุญาตทุกที่)"),
                Loc.T("settings.section.danger", "Danger Zone"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ConfirmDanger(Loc.T("danger.allowlist.confirm", "ลบ allowlist ทั้งหมด? (agent จะกลับไปโหมดอนุญาตทุกที่ — มีผลหลัง redeploy)"))) return;

        var backup = BackupConfig();
        Host.ClearAllowlist();
        OmniLog.Warn("[danger] clear allowlist — config backup: " + backup);
        MessageBox.Show(Host.Self,
            Loc.T("danger.done", "เสร็จแล้ว (backup: {0})", System.IO.Path.GetFileName(backup)),
            Loc.T("settings.section.danger", "Danger Zone"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnFactoryResetClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDanger(Loc.T("danger.factory.confirm", "คืนค่าโรงงาน = ล้าง node/token/allowlist/การตั้งค่าทั้งหมด\nแอปจะปิดตัวหลังคืนค่า (เปิดใหม่ได้จาก Desktop)"))) return;

        var backup = BackupConfig();
        OmniLog.Warn("[danger] FACTORY RESET — config backup: " + backup);
        Host.FactoryReset();
    }
}
