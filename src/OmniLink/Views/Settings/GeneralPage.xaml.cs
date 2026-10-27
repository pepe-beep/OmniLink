using System.Windows;
using System.Windows.Controls;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

public partial class GeneralPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    private bool _init = true;
    public bool IsDirty { get; private set; }
    public GeneralPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Loaded += (_, _) => LoadFromConfig();
    }

    public string TitleKey => "settings.section.general";
    public string IconSymbol => "Settings24";

    private void OnLanguageChanged()
    {
        // ชื่อรายการ combo เป็น DynamicResource (เปลี่ยนเอง) — ที่ต้องทำคือกัน index เพี้ยน
    }

    public void LoadFromConfig()
    {
        _init = true;
        var cfg = Host.Config;
        LangCombo.SelectedIndex = cfg.UiLanguage switch { "th-TH" => 1, "en-US" => 2, _ => 0 };
        ThemeCombo.SelectedIndex = Math.Clamp(cfg.ThemeMode, 0, 2);
        CloseActionCombo.SelectedIndex = string.Equals(cfg.CloseButtonAction, "exit", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ChkAutoStartKvm.IsChecked = cfg.AutoStartKvm;
        RadioGlobalCalibrated.IsChecked = cfg.LanguageMode == "global_calibrated";
        RadioTargetOnly.IsChecked = cfg.LanguageMode != "global_calibrated";
        ChkPreventSleep.IsChecked = cfg.PreventSleep;
        _init = false;
        IsDirty = false;
    }

    public void RestoreDefaults()
    {
        var cfg = Host.Config;
        cfg.UiLanguage = "auto";
        cfg.ThemeMode = 0;
        cfg.CloseButtonAction = "minimize_to_tray";
        cfg.AutoStartKvm = true;
        cfg.LanguageMode = "global_calibrated";
        cfg.PreventSleep = false;
        OmniLink.Core.PreventSleepService.SetEnabled(false);
        LoadFromConfig();
        ApplyLanguageSetting();
        Host.ApplyThemeNow();
        Host.SaveConfig();
        Host.SetBanner(Loc.T("banner.defaults.general", "คืนค่าเริ่มต้นหน้า General แล้ว"));
    }

    private void ApplyLanguageSetting()
    {
        LocalizationService.SetLanguage(Host.Config.UiLanguage);
        Host.SaveConfig();
    }

    private void OnLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        Host.Config.UiLanguage = LangCombo.SelectedIndex switch { 1 => "th-TH", 2 => "en-US", _ => "auto" };
        ApplyLanguageSetting();
        Host.SetBanner(Loc.T("banner.lang.changed", "เปลี่ยนภาษาหน้าจอเรียบร้อยแล้ว"));
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        Host.Config.ThemeMode = Math.Clamp(ThemeCombo.SelectedIndex, 0, 2);
        Host.ApplyThemeNow();
        Host.SaveConfig();
        Host.SetBanner(Loc.T("banner.theme.changed", "เปลี่ยนธีมเรียบร้อยแล้ว"));
    }

    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        Host.Config.CloseButtonAction = CloseActionCombo.SelectedIndex == 1 ? "exit" : "minimize_to_tray";
        Host.SaveConfig();
    }

    private void OnAutoStartKvmChanged(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        Host.Config.AutoStartKvm = ChkAutoStartKvm.IsChecked == true;
        Host.SaveConfig();
        Host.SetBanner(ChkAutoStartKvm.IsChecked == true
            ? Loc.T("banner.autokvm.on", "โหมด KVM จะทำงานตั้งแต่เปิดโปรแกรมครั้งถัดไป")
            : Loc.T("banner.autokvm.off", "โหมด KVM จะไม่ทำงานตอนเปิดโปรแกรมครั้งถัดไป"));
    }

    private void OnPreventSleepChanged(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        var on = ChkPreventSleep.IsChecked == true;
        Host.Config.PreventSleep = on;
        OmniLink.Core.PreventSleepService.SetEnabled(on); // มีผลทันที ไม่ต้องรีสตาร์ต
        Host.SaveConfig();
        Host.SetBanner(on
            ? Loc.T("banner.sleep.on", "เปิดกันเครื่อง sleep แล้ว — เครื่องนี้จะไม่เข้าโหมด sleep ขณะโปรแกรมทำงาน")
            : Loc.T("banner.sleep.off", "ปิดกัน sleep แล้ว — เครื่องเข้า sleep ได้ตามค่า Windows"));
    }

    private void OnKbSyncChanged(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        Host.Config.LanguageMode = RadioGlobalCalibrated.IsChecked == true ? "global_calibrated" : "target_only";
        Host.SaveConfig();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Host.ExitApp();
}
