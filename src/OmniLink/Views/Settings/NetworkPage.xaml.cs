using System.Windows;
using System.Windows.Controls;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

public partial class NetworkPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    private bool _init = true;
    public bool IsDirty { get; private set; }
    public NetworkPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        Loaded += (_, _) => LoadFromConfig();
    }

    public string TitleKey => "settings.section.network";
    public string IconSymbol => "Globe24";

    public void LoadFromConfig()
    {
        _init = true;
        var cfg = Host.Config;
        KvmTcpPortBox.Text = cfg.KvmTcpPort.ToString();
        KvmUdpPortBox.Text = cfg.KvmUdpPort.ToString();
        DiscoveryPortBox.Text = cfg.DiscoveryPort.ToString();
        PlaybackRtpPortBox.Text = cfg.PlaybackRtpPort.ToString();
        AllowlistBox.Text = string.Join(", ", cfg.AllowedSubnets ?? new List<string>());
        PortErrorText.Visibility = Visibility.Collapsed;
        AllowlistErrorText.Visibility = Visibility.Collapsed;
        _init = false;
        IsDirty = false;
    }

    public void RestoreDefaults()
    {
        var cfg = Host.Config;
        cfg.KvmTcpPort = OmniProtocol.KvmTcpPort;
        cfg.KvmUdpPort = OmniProtocol.KvmUdpPort;
        cfg.DiscoveryPort = OmniProtocol.DiscoveryPort;
        cfg.PlaybackRtpPort = OmniProtocol.PlaybackRtpPort;
        cfg.AllowedSubnets.Clear();
        LoadFromConfig();
        Host.SaveConfig();
        Host.SetBanner(Loc.T("banner.defaults.network", "คืนค่าเริ่มต้นหน้า Network แล้ว"));
    }

    private void OnPortChanged(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        var cfg = Host.Config;
        var okTcp = TryPort(KvmTcpPortBox.Text, OmniProtocol.KvmTcpPort, out var tcp);
        var okUdp = TryPort(KvmUdpPortBox.Text, OmniProtocol.KvmUdpPort, out var udp);
        var okDisc = TryPort(DiscoveryPortBox.Text, OmniProtocol.DiscoveryPort, out var disc);
        var okRtp = TryPort(PlaybackRtpPortBox.Text, OmniProtocol.PlaybackRtpPort, out var rtp);
        var allParsed = okTcp && okUdp && okDisc && okRtp;
        var unique = !allParsed || new[] { tcp, udp, disc, rtp }.Distinct().Count() == 4;

        PortErrorText.Text = !allParsed
            ? Loc.T("settings.ports.err.format", "พอร์ตต้องเป็นตัวเลข 1024-65535 — ช่องที่กรอกผิดจะยังใช้ค่าเดิม")
            : !unique
                ? Loc.T("settings.ports.err.dup", "พอร์ตแต่ละตัวต้องไม่ซ้ำกัน — ยังไม่บันทึกจนกว่าจะแก้ให้ไม่ซ้ำ")
                : "";
        PortErrorText.Visibility = allParsed && unique ? Visibility.Collapsed : Visibility.Visible;
        if (!allParsed || !unique) return;

        cfg.KvmTcpPort = tcp;
        cfg.KvmUdpPort = udp;
        cfg.DiscoveryPort = disc;
        cfg.PlaybackRtpPort = rtp;
        Host.SaveConfig();
    }

    private static bool TryPort(string? text, int fallback, out int port)
    {
        port = fallback;
        return int.TryParse(text?.Trim(), out var v) && v is >= OmniLimits.PortMin and <= OmniLimits.PortMax && (port = v) == v;
    }

    private void OnAllowlistChanged(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        var raw = AllowlistBox.Text.Trim();
        var entries = raw.Split(',', '\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var bad = entries.FirstOrDefault(t => !System.Net.IPAddress.TryParse(t.Split('/')[0], out _));
        AllowlistErrorText.Text = string.IsNullOrEmpty(bad)
            ? ""
            : Loc.T("settings.allowlist.err", "รายการนี้ไม่ใช่ IP/CIDR (จะไม่ถูกบันทึก):", bad);
        AllowlistErrorText.Visibility = string.IsNullOrEmpty(bad) ? Visibility.Collapsed : Visibility.Visible;
        if (bad is not null) return;

        Host.Config.AllowedSubnets = entries;
        Host.SaveConfig();
    }
}
