using System.Windows;
using System.Windows.Controls;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

public partial class SwitchingPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    private bool _init = true;
    public bool IsDirty { get; private set; }
    public SwitchingPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        Loaded += (_, _) => LoadFromConfig();
    }

    public string TitleKey => "settings.section.switching";
    public string IconSymbol => "ArrowsBidirectional24";

    public void LoadFromConfig()
    {
        _init = true;
        var cfg = Host.Config;
        SwitchDelayBox.Text = cfg.SwitchDelayMs.ToString();
        DoubleTapBox.Text = cfg.DoubleTapMs.ToString();
        CornerSizeBox.Text = cfg.CornerSize.ToString();
        CornerTLBox.IsChecked = cfg.CornerTopLeft;
        CornerTRBox.IsChecked = cfg.CornerTopRight;
        CornerBLBox.IsChecked = cfg.CornerBottomLeft;
        CornerBRBox.IsChecked = cfg.CornerBottomRight;
        ChkLockToScreen.IsChecked = cfg.LockToScreenEnabled;
        ChkBlockFullscreen.IsChecked = cfg.BlockSwitchOnFullscreen;
        ChkGraveSwitch.IsChecked = cfg.GraveSwitchEnabled;
        ErrorText.Visibility = Visibility.Collapsed;
        _init = false;
        IsDirty = false;
    }

    public void RestoreDefaults()
    {
        var cfg = Host.Config;
        cfg.SwitchDelayMs = OmniLimits.SwitchDelayDefault;
        cfg.DoubleTapMs = OmniLimits.DoubleTapDefault;
        cfg.CornerSize = OmniLimits.CornerDefault;
        cfg.CornerTopLeft = cfg.CornerTopRight = cfg.CornerBottomLeft = cfg.CornerBottomRight = true;
        cfg.LockToScreenEnabled = true;
        cfg.BlockSwitchOnFullscreen = true;
        cfg.GraveSwitchEnabled = true;
        LoadFromConfig();
        Host.UpdateKvmStrategies();
        Host.SaveConfig();
        Host.SetBanner(Loc.T("banner.defaults.switching", "คืนค่าเริ่มต้นหน้า Switching แล้ว"));
    }

    private void OnBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        var cfg = Host.Config;
        var okDelay = int.TryParse(SwitchDelayBox.Text.Trim(), out var delay);
        var okTap = int.TryParse(DoubleTapBox.Text.Trim(), out var tap);
        var okCorner = int.TryParse(CornerSizeBox.Text.Trim(), out var corner);
        if (okDelay) cfg.SwitchDelayMs = OmniLimits.ClampInt(delay, OmniLimits.SwitchDelayMin, OmniLimits.SwitchDelayMax);
        if (okTap) cfg.DoubleTapMs = OmniLimits.ClampInt(tap, OmniLimits.DoubleTapMin, OmniLimits.DoubleTapMax);
        if (okCorner) cfg.CornerSize = OmniLimits.ClampInt(corner, OmniLimits.CornerMin, OmniLimits.CornerMax);
        ShowHint(!(okDelay && okTap && okCorner));
        Host.UpdateKvmStrategies();
        Host.SaveConfig();
    }

    private void ShowHint(bool invalid)
    {
        ErrorText.Text = Loc.T("settings.hint.number", "กรอกตัวเลขเท่านั้น — ค่าที่เกินช่วงจะถูกจำกัดให้อยู่ในช่วงที่ปลอดภัยอัตโนมัติ");
        ErrorText.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        var cfg = Host.Config;
        cfg.CornerTopLeft = CornerTLBox.IsChecked == true;
        cfg.CornerTopRight = CornerTRBox.IsChecked == true;
        cfg.CornerBottomLeft = CornerBLBox.IsChecked == true;
        cfg.CornerBottomRight = CornerBRBox.IsChecked == true;
        cfg.LockToScreenEnabled = ChkLockToScreen.IsChecked == true;
        cfg.BlockSwitchOnFullscreen = ChkBlockFullscreen.IsChecked == true;
        cfg.GraveSwitchEnabled = ChkGraveSwitch.IsChecked == true;
        Host.UpdateKvmStrategies();
        Host.SaveConfig();
    }
}
