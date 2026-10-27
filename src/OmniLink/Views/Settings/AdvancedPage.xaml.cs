using System.Windows;
using System.Windows.Controls;
using OmniLink.Core;
using OmniLink.Interop;

namespace OmniLink.Views.Settings;

public partial class AdvancedPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    private bool _init = true;
    public bool IsDirty { get; private set; }
    public AdvancedPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        Loaded += (_, _) => LoadFromConfig();
    }

    public string TitleKey => "settings.section.advanced";
    public string IconSymbol => "Wrench24";

    public void LoadFromConfig()
    {
        _init = true;
        var cfg = Host.Config;
        ChkLowMemoryMode.IsChecked = cfg.LowMemoryMode;
        ChkLowMemSoftwareRender.IsChecked = cfg.LowMemSoftwareRender;
        ChkLowMemThreadPool.IsChecked = cfg.LowMemThreadPoolCap;
        ChkLowMemGcBatch.IsChecked = cfg.LowMemGcBatch;
        PlaybackLatencyBox.Text = cfg.PlaybackLatencyMs.ToString();
        UdpBufferBox.Text = cfg.UdpSocketBufferBytes.ToString();
        ChkAdaptivePing.IsChecked = cfg.AdaptivePingEnabled;
        PingActiveBox.Text = cfg.PingActiveMs.ToString();
        PingIdleBox.Text = cfg.PingIdleMs.ToString();
        PingTrayBox.Text = cfg.PingTrayMs.ToString();
        ChkAutoEngGuard.IsChecked = cfg.AutoEngHostOnClient;
        ChkStopDeskflow.IsChecked = cfg.StopDeskflowOnStart;
        ChkDiagnostics.IsChecked = cfg.DiagnosticsEnabled;
        ChkSubMode.IsChecked = cfg.SubModeEnabled;
        UpdateLowMemStatus();
        _init = false;
        IsDirty = false;
    }

    public void ApplyLanguage() => UpdateLowMemStatus();

    public void RestoreDefaults()
    {
        var cfg = Host.Config;
        cfg.LowMemoryMode = false;
        cfg.LowMemSoftwareRender = true;
        cfg.LowMemThreadPoolCap = true;
        cfg.LowMemGcBatch = true;
        cfg.PlaybackLatencyMs = OmniLimits.PlaybackLatencyDefault;
        cfg.UdpSocketBufferBytes = OmniLimits.UdpBufferDefault;
        cfg.AdaptivePingEnabled = true;
        cfg.PingActiveMs = OmniLimits.PingActiveDefault;
        cfg.PingIdleMs = OmniLimits.PingIdleDefault;
        cfg.PingTrayMs = OmniLimits.PingTrayDefault;
        cfg.AutoEngHostOnClient = true;
        cfg.StopDeskflowOnStart = true;
        cfg.DiagnosticsEnabled = true;
        LoadFromConfig();
        Host.SaveConfig();
        Host.SetBanner(Loc.T("banner.defaults.advanced", "คืนค่าเริ่มต้นหน้า Advanced แล้ว (บางค่ามีผลหลังรีสตาร์ต)"));
    }

    private void OnBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        var cfg = Host.Config;
        if (int.TryParse(PlaybackLatencyBox.Text.Trim(), out var latency))
        {
            cfg.PlaybackLatencyMs = OmniLimits.ClampInt(latency, OmniLimits.PlaybackLatencyMin, OmniLimits.PlaybackLatencyMax);
        }
        if (int.TryParse(UdpBufferBox.Text.Trim(), out var buf))
        {
            cfg.UdpSocketBufferBytes = OmniLimits.ClampInt(buf, (int)OmniLimits.UdpBufferMin, (int)OmniLimits.UdpBufferMax);
        }
        if (int.TryParse(PingActiveBox.Text.Trim(), out var pa))
        {
            cfg.PingActiveMs = OmniLimits.ClampInt(pa, OmniLimits.PingActiveMin, OmniLimits.PingActiveMax);
        }
        if (int.TryParse(PingIdleBox.Text.Trim(), out var pi))
        {
            cfg.PingIdleMs = OmniLimits.ClampInt(pi, OmniLimits.PingIdleMin, OmniLimits.PingIdleMax);
        }
        if (int.TryParse(PingTrayBox.Text.Trim(), out var pt))
        {
            cfg.PingTrayMs = OmniLimits.ClampInt(pt, OmniLimits.PingTrayMin, OmniLimits.PingTrayMax);
        }
        Host.SaveConfig();
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        var cfg = Host.Config;
        cfg.LowMemoryMode = ChkLowMemoryMode.IsChecked == true;
        cfg.LowMemSoftwareRender = ChkLowMemSoftwareRender.IsChecked == true;
        cfg.LowMemThreadPoolCap = ChkLowMemThreadPool.IsChecked == true;
        cfg.LowMemGcBatch = ChkLowMemGcBatch.IsChecked == true;
        cfg.AdaptivePingEnabled = ChkAdaptivePing.IsChecked == true;
        cfg.AutoEngHostOnClient = ChkAutoEngGuard.IsChecked == true;
        cfg.StopDeskflowOnStart = ChkStopDeskflow.IsChecked == true;
        cfg.DiagnosticsEnabled = ChkDiagnostics.IsChecked == true;
        OmniLog.SetDiagnostics(cfg.DiagnosticsEnabled);
        UpdateLowMemStatus();
        Host.SaveConfig();
    }

    private void OnSubModeChanged(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        var cfg = Host.Config;
        var want = ChkSubMode.IsChecked == true;
        if (want)
        {
            // godclass-F3: node agent กับ sub-mode ใช้พอร์ต 24801/24802 ร่วมกัน — ห้ามเปิดพร้อมกันชัดเจน
            var block = SubModeGuard.GetBlockReason();
            if (block is not null)
            {
                _init = true;
                ChkSubMode.IsChecked = false; // กลับกล่องก่อน ไม่งั้น handler วนซ้ำ
                _init = false;
                MessageBox.Show(Window.GetWindow(this), block, "OmniLink — Sub-mode",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateSubModeStatus();
                return;
            }
        }
        if (want == cfg.SubModeEnabled && Host is MainWindow mw0 && mw0.SubModeRunning == want)
        {
            UpdateSubModeStatus();
            return;
        }
        cfg.SubModeEnabled = want;
        if (Host is MainWindow mw)
        {
            if (want) mw.StartSubMode();
            else mw.StopSubMode();
        }
        IsDirty = true;
        Host.SaveConfig();
        UpdateSubModeStatus();
    }

    private void UpdateSubModeStatus()
    {
        if (Host is MainWindow mw && mw.SubServer is { } sub)
        {
            SubModePinText.Text = Loc.T("sub.toggle.pin", "PIN จับคู่: {0} · Fingerprint: {1}..", sub.PairingPin, sub.CertFingerprint[..Math.Min(12, sub.CertFingerprint.Length)]);
        }
        else
        {
            SubModePinText.Text = "";
        }
    }

    /// <summary>สถานะจริงของ Low-Mem profile (ย้ายจาก MainWindow เดิม — localized)</summary>
    private void UpdateLowMemStatus()
    {
        if (LowMemStatusText is null) return;
        var totalRam = NativeMethods.GetTotalPhysicalMemoryBytes();
        var isLowRam = totalRam > 0 && totalRam <= 4_294_967_296UL;
        LowMemStatusText.Text = isLowRam
            ? Loc.T("settings.res.status.auto", "● ACTIVE (auto — RAM ≤ 4GB, ปิดไม่ได้ มีผลหลังรีสตาร์ต)")
            : ChkLowMemoryMode.IsChecked == true
                ? Loc.T("settings.res.status.manual", "● ACTIVE (manual — มีผลหลังรีสตาร์ต)")
                : Loc.T("settings.res.status.off", "○ OFF (RAM > 4GB · เปิดด้วย checkbox นี้ มีผลหลังรีสตาร์ต)");
        LowMemStatusText.Foreground = isLowRam || ChkLowMemoryMode.IsChecked == true
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x27, 0xAE, 0x60))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x93, 0xA3));
    }
}
