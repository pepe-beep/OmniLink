using System.Windows;
using System.Windows.Controls;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

public partial class AudioPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    private bool _init = true;
    public bool IsDirty { get; private set; }
    public AudioPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        Loaded += (_, _) => LoadFromConfig();
    }

    public string TitleKey => "settings.section.audio";
    public string IconSymbol => "Speaker224";

    public void LoadFromConfig()
    {
        _init = true;
        var cfg = Host.Config;
        ChkAutoStartStream.IsChecked = cfg.AutoStartStream;
        ChkAutoStartMic.IsChecked = cfg.AutoStartMic;
        ChkSmartAudioStandby.IsChecked = cfg.SmartAudioStandby;
        ChkSmartMicFocus.IsChecked = cfg.SmartMicFollowFocus;
        AudioIdleTimeoutBox.Text = cfg.AudioIdleTimeoutSec.ToString();
        MicIdleTimeoutBox.Text = cfg.MicIdleTimeoutSec.ToString();
        ErrorText.Visibility = Visibility.Collapsed;
        _init = false;
        IsDirty = false;
    }

    public void RestoreDefaults()
    {
        var cfg = Host.Config;
        cfg.AutoStartStream = true;
        cfg.AutoStartMic = false;
        cfg.SmartAudioStandby = true;
        cfg.SmartMicFollowFocus = true;
        cfg.AudioIdleTimeoutSec = OmniLimits.AudioIdleDefault;
        cfg.MicIdleTimeoutSec = OmniLimits.MicIdleDefault;
        LoadFromConfig();
        Host.SaveConfig();
        Host.SetBanner(Loc.T("banner.defaults.audio", "คืนค่าเริ่มต้นหน้า Audio แล้ว"));
    }

    private void OnBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        var okA = int.TryParse(AudioIdleTimeoutBox.Text.Trim(), out var a);
        var okM = int.TryParse(MicIdleTimeoutBox.Text.Trim(), out var m);
        if (okA) Host.Config.AudioIdleTimeoutSec = OmniLimits.ClampInt(a, OmniLimits.AudioIdleMin, OmniLimits.AudioIdleMax);
        if (okM) Host.Config.MicIdleTimeoutSec = OmniLimits.ClampInt(m, OmniLimits.MicIdleMin, OmniLimits.MicIdleMax);
        ErrorText.Visibility = okA && okM ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = Loc.T("settings.hint.number", "กรอกตัวเลขเท่านั้น — ค่าที่เกินช่วงจะถูกจำกัดให้อยู่ในช่วงที่ปลอดภัยอัตโนมัติ");
        Host.SaveConfig();
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        var cfg = Host.Config;
        cfg.AutoStartStream = ChkAutoStartStream.IsChecked == true;
        cfg.AutoStartMic = ChkAutoStartMic.IsChecked == true;
        cfg.SmartAudioStandby = ChkSmartAudioStandby.IsChecked == true;
        cfg.SmartMicFollowFocus = ChkSmartMicFocus.IsChecked == true;
        Host.SaveConfig();
    }
}
