using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Interop;
using OmniLink.ViewModels;
using OmniLink.Views.Settings;

namespace OmniLink.Views;

// godclass-M5: ย้ายมาจาก MainWindow.xaml.cs แบบ move-verbatim (partial class)
// กลุ่ม SET+SUB: settings 10 หน้า + rail + ภาษา/ธีม + danger zone + sub-mode lifecycle
public partial class MainWindow
{
    private readonly List<ISettingsPage> _settingsPages = new();
    private SecurityPairingPage? _securityPage;

    // ================= v1.9 SETTINGS PAGES (แทน popup Advanced) =================

    private void BuildSettingsPages()
    {
        _settingsPages.Clear();
        _settingsPages.Add(new GeneralPage(this));
        _settingsPages.Add(new SwitchingPage(this));
        _settingsPages.Add(new HotkeysPage(this));
        _settingsPages.Add(new NetworkPage(this));
        _securityPage = new SecurityPairingPage(this);
        _settingsPages.Add(_securityPage);
        _settingsPages.Add(new AudioPage(this));
        _settingsPages.Add(new ClipboardPage(this));
        _settingsPages.Add(new LoggingPage(this));
        _settingsPages.Add(new AdvancedPage(this));
        _settingsPages.Add(new DangerPage(this));

        RebuildSettingsRail();
        SettingsSectionList.SelectedIndex = 0;
    }

    private void RebuildSettingsRail()
    {
        var selected = SettingsSectionList.SelectedIndex;
        SettingsSectionList.ItemsSource = _settingsPages
            .Select(p => new SettingsSectionRow(p.IconSymbol, Loc.T(p.TitleKey)))
            .ToList();
        if (selected >= 0 && selected < _settingsPages.Count)
        {
            SettingsSectionList.SelectedIndex = selected;
        }
    }

    private void OnSettingsSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = SettingsSectionList.SelectedIndex;
        if (idx < 0 || idx >= _settingsPages.Count) return;
        var page = _settingsPages[idx];
        SettingsPageContent.Content = page;
        page.LoadFromConfig();
        UpdateSettingsFooterButtons();
    }

    private void OnRestorePageClick(object sender, RoutedEventArgs e)
    {
        if (SettingsPageContent.Content is ISettingsPage page)
        {
            page.RestoreDefaults();
            UpdateSettingsFooterButtons();
        }
    }

    /// <summary>ยกเลิกการแก้ไข = โหลดค่าจาก config กลับลง controls ใหม่ (ค่าที่ apply ไปแล้วก่อนหน้านี้ยังอยู่ใน config เดิมจนกว่าจะแก้ใหม่)</summary>
    private void OnResetPageClick(object sender, RoutedEventArgs e)
    {
        if (SettingsPageContent.Content is ISettingsPage page)
        {
            page.LoadFromConfig();
            UpdateSettingsFooterButtons();
            SetBanner(Loc.T("banner.reset.page", "ยกเลิกการแก้ไขหน้านี้แล้ว — กลับไปใช้ค่าที่โหลดล่าสุด"));
        }
    }

    internal void UpdateSettingsFooterButtons()
    {
        if (BtnResetPage is null) return;
        BtnResetPage.IsEnabled = SettingsPageContent.Content is ISettingsPage { IsDirty: true };
    }

    private void OnUiLanguageChanged()
    {
        RebuildSettingsRail();
        foreach (var page in _settingsPages)
        {
            page.ApplyLanguage();
        }
        try { _tray?.RebuildMenu(); } catch { }
        RefreshUiState();
    }

    // ================= ISettingsHost =================

    OmniConfig ISettingsHost.Config => _config;
    IKvmHost ISettingsHost.Kvm => _kvm;
    OmniDiscoveryService ISettingsHost.Discovery => _discovery;
    Window ISettingsHost.Self => this;

    public void SaveConfig() => ScheduleConfigSave();

    public void UpdateKvmStrategies() => _kvm.UpdateStrategies();

    /// <summary>v2.0 เฟส 3: เปิดโหมด sub (ถูกคุมได้) — wire กันลูป host↔host ผ่าน probe</summary>
    public void StartSubMode()
    {
        if (_subServer is not null) return;
        var sub = new OmniLink.Core.NodeAgentServer(
            _config,
            new OmniLink.Platform.Windows.WindowsNodeInputInjector(),
            _hostInput,
            _clipboardBridge,
            new OmniLink.Platform.Windows.WindowsEscapeHotkey());
        sub.OnStatus = msg => Dispatcher.BeginInvoke(() => SetBanner(msg));
        _kvm.SubActiveFocusProbe = () => sub.ActiveFocus;
        _subServer = sub;
        sub.Start();
        RefreshUiState();
    }

    public void StopSubMode()
    {
        _kvm.SubActiveFocusProbe = null;
        _subServer?.Dispose();
        _subServer = null;
        SetBanner(Loc.T("sub.stopped", "ปิดโหมด sub แล้ว"));
        RefreshUiState();
    }

    public bool SubModeRunning => _subServer is not null;

    public OmniLink.Core.NodeAgentServer? SubServer => _subServer;

    public void ApplyThemeNow()
    {
        OmniLink.Themes.ThemeService.Apply(_config.ThemeMode);
        ApplyTheme();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) ApplyWindowVisual(hwnd);
    }

    public void ApplyLogLevelFromConfig() => OmniLog.SetLevel(_config.LogLevel);

    public void ExitApp() => ExitApplication();

    public void ClearAllPairings()
    {
        foreach (var n in _config.Nodes.ToList())
        {
            _kvm.RemoveNode(n.NodeId);
        }
        _kvm.ReloadLayout();
        RefreshUiState();
        SetBanner(Loc.T("banner.danger.pairings", "ล้างการจับคู่ทั้งหมดแล้ว — จับคู่ใหม่ผ่านหน้า Security & Pairing"));
    }

    public void ClearAllowlist()
    {
        _config.AllowedSubnets.Clear();
        ScheduleConfigSave();
        SetBanner(Loc.T("banner.danger.allowlist", "ล้าง allowlist แล้ว (โหมดอนุญาตทุกที่ — deploy ใหม่เพื่อบังคับ agent)"));
    }

    public void FactoryReset()
    {
        _config.ResetToDefaultsInPlace();
        LocalizationService.Initialize(_config.UiLanguage);
        var save = Task.Run(() => { try { _config.Save(); } catch { } }); // UI-H4: ไม่เขียนไฟล์บน UI thread
        OmniLog.SetMaxLogBytes(_config.LogMaxBytes);
        RefreshUiState();
        SetBanner(Loc.T("banner.danger.factory", "คืนค่าโรงงานแล้ว — แอปจะปิดใน 3 วินาที เปิดใหม่ได้จาก Desktop"));
        _ = Task.Run(async () =>
        {
            await save;
            await Task.Delay(3000);
            _ = Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
        });
    }

    public void NavigateToPairingAndScan()
    {
        MainTabs.SelectedIndex = 3;
        var idx = _settingsPages.IndexOf(_securityPage!);
        if (idx >= 0)
        {
            SettingsSectionList.SelectedIndex = idx;
        }
        _securityPage?.StartNetworkScan();
    }

}
