using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace OmniLink.Core;

/// <summary>
/// v1.9: tray menu สร้างจากคลังภาษา (Loc.T) และ rebuild ได้ทันทีเมื่อผู้ใช้สลับภาษา UI
/// (เดิม hardcode ไทยล้วน — UI-L5 ตระกูลเดียวกับที่เคยแก้ langGroup)
/// </summary>
internal sealed class OmniTrayService : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly Action _openDashboard;
    private readonly Action _calibrateEn;
    private readonly Action _calibrateTh;
    private readonly Action _resetAudio;
    private readonly Action _exitApp;
    private bool _lastRemoteActive;
    private int _lastLangGroup;

    public OmniTrayService(
        Action openDashboard,
        Action calibrateEn,
        Action calibrateTh,
        Action resetAudio,
        Action exitApp)
    {
        _openDashboard = openDashboard;
        _calibrateEn = calibrateEn;
        _calibrateTh = calibrateTh;
        _resetAudio = resetAudio;
        _exitApp = exitApp;

        _tray = new TaskbarIcon
        {
            ToolTipText = Loc.T("tray.tooltip", "OmniLink — LAN KVM & Audio Hub"),
            Icon = OmniIconBuilder.GetTrayIcon(false, 0),
        };

        RebuildMenu();
        _tray.TrayLeftMouseUp += (_, _) => _openDashboard();
    }

    /// <summary>สร้าง context menu ใหม่จากภาษาปัจจุบัน — เรียกตอน ctor และตอน LocalizationService แจ้งเปลี่ยนภาษา</summary>
    public void RebuildMenu()
    {
        var menu = new ContextMenu();

        var itemOpen = new MenuItem { Header = Loc.T("tray.open", "เปิดหน้าจัดการ OmniLink") };
        itemOpen.Click += (_, _) => _openDashboard();

        var itemEn = new MenuItem { Header = Loc.T("tray.kb.eng", "ซิงค์คีย์บอร์ดทุกเครื่อง -> ENG (US)") };
        itemEn.Click += (_, _) => _calibrateEn();

        var itemTh = new MenuItem { Header = Loc.T("tray.kb.th", "ซิงค์คีย์บอร์ดทุกเครื่อง -> TH (ไทย)") };
        itemTh.Click += (_, _) => _calibrateTh();

        var itemReset = new MenuItem { Header = Loc.T("tray.reset", "รีเซ็ตระบบเสียง Stream + Mic") };
        itemReset.Click += (_, _) => _resetAudio();

        var itemExit = new MenuItem { Header = Loc.T("tray.exit", "ออกจากโปรแกรม") };
        itemExit.Click += (_, _) => _exitApp();

        menu.Items.Add(itemOpen);
        menu.Items.Add(new Separator());
        menu.Items.Add(itemEn);
        menu.Items.Add(itemTh);
        menu.Items.Add(new Separator());
        menu.Items.Add(itemReset);
        menu.Items.Add(new Separator());
        menu.Items.Add(itemExit);

        _tray.ContextMenu = menu;
    }

    public void UpdateState(bool remoteActive, int langGroup, string activeNodeText)
    {
        try
        {
            // UI-L5 fix: ใช้ค่า langGroup ที่ส่งมาให้จริง — tray icon ต้องสะท้อน TH/EN
            if (remoteActive != _lastRemoteActive || langGroup != _lastLangGroup)
            {
                _lastRemoteActive = remoteActive;
                _lastLangGroup = langGroup;
                _tray.Icon = OmniIconBuilder.GetTrayIcon(remoteActive, langGroup);
            }
            var tip = $"OmniLink — {activeNodeText}";
            if (_tray.ToolTipText != tip)
            {
                _tray.ToolTipText = tip;
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _tray.Dispose();
    }
}
