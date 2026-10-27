using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

/// <summary>
/// หน้า Security &amp; Pairing — ย้าย flow จับคู่ทั้งก้อน (scan → เลือกเป้า → PIN → TOFU fingerprint) จาก MainWindow เดิม
/// มาไว้ที่นี่หมด + เพิ่มตาราง fingerprint รายเครื่อง (ดู/เพิกถอน = บังคับจับคู่ TOFU ใหม่)
/// </summary>
public partial class SecurityPairingPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    public bool IsDirty => false;
    public sealed record FingerprintRow(string NodeId, string Title, string Fingerprint);

    public sealed record DiscoveredRow(DiscoveredNode Node, string Name, string DetailLine, string BadgeText, Brush BadgeBg, Brush BadgeFg);

    private readonly List<DiscoveredRow> _discoveredRows = new();
    private DiscoveredNode? _selectedDiscoveredNode;
    private DiscoveredNode? _lastDiscoveredNode;

    public SecurityPairingPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        Loaded += (_, _) => LoadFromConfig();
    }

    public string TitleKey => "settings.section.security";
    public string IconSymbol => "ShieldCheckmark24";

    public void LoadFromConfig()
    {
        RefreshPairingUi();
    }

    public void RestoreDefaults() { /* หน้านี้ไม่มีค่าปรับ — จับคู่/เพิกถอนทำเป็นรายการ */ }

    /// <summary>เรียกจาก host ทุก tick เมื่อ Settings tab เปิดอยู่ — รื้อรายการเมื่อข้อมูลเปลี่ยนเท่านั้น</summary>
    public void RefreshPairingUi()
    {
        var fpRows = Host.Config.Nodes
            .Select(n => new FingerprintRow(n.NodeId, n.DisplayName, string.IsNullOrWhiteSpace(n.CertFingerprintSha256)
                ? Loc.T("settings.fp.none", "(ยังไม่มี fingerprint — host จะปฏิเสธการเชื่อมจนกว่าจะจับคู่ใหม่)")
                : "SHA-256 " + n.CertFingerprintSha256))
            .ToList();
        var unchanged = FingerprintList.ItemsSource is List<FingerprintRow> cur &&
                        cur.Count == fpRows.Count && !cur.Where((r, i) => !r.Equals(fpRows[i])).Any();
        if (!unchanged)
        {
            FingerprintList.ItemsSource = fpRows;
            FpEmptyText.Visibility = fpRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var policy = string.Join("\n", Host.Config.Nodes.Select(n =>
            $"• {n.DisplayName} — {Loc.T($"policy.{OmniPolicy.Normalize(n.Policy)}", OmniPolicy.Normalize(n.Policy))}"));
        PolicySummaryText.Text = Host.Config.Nodes.Count == 0 ? Loc.T("settings.policy.empty", "ยังไม่มีเครื่องที่จับคู่ไว้") : policy;

        if (_lastDiscoveredNode is not null)
        {
            UpdatePairButtonLabel(_lastDiscoveredNode);
        }
    }

    /// <summary>เรียกจาก discovery callback (มาจาก background thread — host ส่งผ่าน Dispatcher มาให้แล้ว)</summary>
    public void OnLiveNodeDiscovered(DiscoveredNode node)
    {
        if (_discoveredRows.Any(r => r.Node.Ip == node.Ip && r.Node.Uuid == node.Uuid)) return;
        _discoveredRows.Add(MakeDiscoveredRow(node));
        RebindDiscoveredList();
        DiscoveryCountText.Text = Loc.T("settings.pairing.count", "พบ {0} เครื่อง", _discoveredRows.Count);
    }

    /// <summary>เริ่ม scan ใหม่ (เรียกจากปุ่มบนหน้า หรือ host.NavigateToPairingAndScan)</summary>
    public void StartNetworkScan()
    {
        BtnScanNetwork.IsEnabled = false;
        BtnScanNetwork.Content = Loc.T("settings.pairing.scanning", "กำลังสแกน...");
        DiscoveryStatusText.Text = Loc.T("settings.pairing.status.scanning", "กำลัง broadcast หา OmniLink agent ใน subnet...");
        _discoveredRows.Clear();
        DiscoveredNodesListBox.ItemsSource = null;
        DiscoveryCountText.Text = Loc.T("settings.pairing.searching", "กำลังค้นหา...");

        Task.Run(async () =>
        {
            var nodes = await Host.Discovery.ScanNetworkAsync(3000);
            _ = Host.Self.Dispatcher.BeginInvoke(() =>
            {
                _discoveredRows.Clear();
                foreach (var node in nodes)
                {
                    _discoveredRows.Add(MakeDiscoveredRow(node));
                }
                RebindDiscoveredList();
                DiscoveryCountText.Text = Loc.T("settings.pairing.count", "พบ {0} เครื่อง", _discoveredRows.Count);
                DiscoveryStatusText.Text = _discoveredRows.Count > 0
                    ? Loc.T("settings.pairing.status.found", "พบ {0} เครื่อง — เลือกเครื่องที่ต้องการจับคู่", _discoveredRows.Count)
                    : Loc.T("settings.pairing.status.none", "ไม่มีเครื่องตอบกลับ — ตรวจว่า OmniLink agent รันอยู่บนเครื่องลูก");
                BtnScanNetwork.IsEnabled = true;
                BtnScanNetwork.Content = Loc.T("settings.pairing.scan", "Broadcast Discovery");
                if (_discoveredRows.Count > 0 && DiscoveredNodesListBox.SelectedIndex < 0)
                {
                    DiscoveredNodesListBox.SelectedIndex = 0;
                }
            });
        });
    }

    private DiscoveredRow MakeDiscoveredRow(DiscoveredNode node)
    {
        var isPaired = Host.Config.Nodes.Any(n => n.Ip == node.Ip || n.NodeId == node.Uuid);
        var badgeBg = isPaired ? MakeFrozen(0x30, 0x00, 0xF5, 0xA0) : MakeFrozen(0x30, 0x00, 0xD2, 0xFF);
        var badgeFg = isPaired ? MakeFrozen(0xFF, 0x00, 0xF5, 0xA0) : MakeFrozen(0xFF, 0x00, 0xD2, 0xFF);
        var badgeText = isPaired ? Loc.T("badge.paired", "จับคู่แล้ว") : Loc.T("badge.ready", "พร้อมจับคู่");
        var detail = $"{node.Os} • {node.Ip}:{node.Port} • {node.Width}x{node.Height} • v{node.Version}" +
                     (string.IsNullOrEmpty(node.Zone) ? "" : $" • {node.Zone}");
        return new DiscoveredRow(node, node.Name, detail, badgeText, badgeBg, badgeFg);
    }

    private void RebindDiscoveredList()
    {
        DiscoveredNodesListBox.ItemsSource = null;
        DiscoveredNodesListBox.ItemsSource = _discoveredRows;
    }

    private static SolidColorBrush MakeFrozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnScanNetworkClick(object sender, RoutedEventArgs e) => StartNetworkScan();

    private void OnDiscoveredNodeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiscoveredNodesListBox.SelectedItem is not DiscoveredRow row) return;
        _selectedDiscoveredNode = row.Node;
        _lastDiscoveredNode = row.Node;
        PairingTargetNodeLabel.Text = Loc.T("settings.pairing.target", "เป้าหมาย: {0} ({1}) — จอ {2}x{3}",
            row.Node.Name, row.Node.Ip, row.Node.Width, row.Node.Height);
        PairingFeedbackText.Text = "";
        UpdatePairButtonLabel(row.Node);
    }

    private void UpdatePairButtonLabel(DiscoveredNode node)
    {
        var isPaired = Host.Config.Nodes.Any(n => n.Ip == node.Ip || n.NodeId == node.Uuid);
        BtnPairDevice.Content = isPaired
            ? Loc.T("settings.pairing.repair", "จับคู่ใหม่ / อัปเดตเครื่อง")
            : Loc.T("settings.pairing.pair", "จับคู่และเชื่อมต่อ");
    }

    private void OnPairDeviceClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDiscoveredNode is null)
        {
            ShowPairFeedback(Loc.T("settings.pairing.err.target", "เลือกเครื่องจากรายการด้านบนก่อน"), error: true);
            return;
        }
        var pin = PairingPinBox.Text.Trim();
        if (string.IsNullOrEmpty(pin))
        {
            ShowPairFeedback(Loc.T("settings.pairing.err.pin", "กรอก PIN 6 หลักที่แสดงบนจอเครื่องลูกก่อน"), error: true);
            return;
        }
        BtnPairDevice.IsEnabled = false;
        ShowPairFeedback(Loc.T("settings.pairing.connecting", "กำลังเชื่อมต่อ {0} ({1}) และตรวจ PIN...", _selectedDiscoveredNode.Name, _selectedDiscoveredNode.Ip), error: false);
        RunPairingFlow(_selectedDiscoveredNode, pin, onDone: () => BtnPairDevice.IsEnabled = true);
    }

    /// <summary>B1 flow เดิม — v2.1 ย้ายไป PairingFlow กลาง (wizard ใช้ร่วม) ตรรกะเดิม 100%</summary>
    private void RunPairingFlow(DiscoveredNode targetNode, string pin, Action? onDone = null)
    {
        PairingFlow.Run(Host, targetNode, pin,
            feedback: (msg, error) => ShowPairFeedback(msg, error),
            onDone: _ => onDone?.Invoke());
    }

    private void ShowPairFeedback(string text, bool error)
    {
        PairingFeedbackText.Foreground = new SolidColorBrush(error ? Color.FromRgb(0xFF, 0x3B, 0x5C) : Color.FromRgb(0x00, 0xD2, 0xFF));
        PairingFeedbackText.Text = text;
    }

    private void OnAddNodeManuallyClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AddNodeDialog { Owner = Host.Self };
        if (dialog.ShowDialog() != true || dialog.TargetNode is null) return;
        ShowPairFeedback(Loc.T("settings.pairing.connecting.manual", "กำลังเชื่อมต่อ {0}:{1} (เพิ่มเอง) และตรวจ PIN...", dialog.TargetNode.Ip, dialog.TargetNode.Port), error: false);
        RunPairingFlow(dialog.TargetNode, dialog.Pin);
    }

    /// <summary>v1.9: เพิกถอน fingerprint รายเครื่อง — host จะปฏิเสธการเชื่อมจนกว่าจะ TOFU ใหม่ (ตามกลไกเดิมของ v1.6)</summary>
    private void OnRevokeFingerprintClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FingerprintRow row })
        {
            Revoke(row);
        }
    }

    private void Revoke(FingerprintRow row)
    {
        var node = Host.Config.Nodes.FirstOrDefault(n => n.NodeId == row.NodeId);
        if (node is null) return;
        var confirm = MessageBox.Show(Host.Self,
            Loc.T("settings.fp.revoke.confirm", "ลบ fingerprint ของ '{0}'?\n\nhost จะปฏิเสธการเชื่อมต่อเครื่องนี้ทันที จนกว่าจะจับคู่ใหม่ (TOFU)", node.DisplayName),
            Loc.T("settings.fp.revoke.title", "ยืนยันการเพิกถอน fingerprint"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        node.CertFingerprintSha256 = "";
        Host.SaveConfig();
        RefreshPairingUi();
        Host.SetBanner(Loc.T("banner.fp.revoked", "เพิกถอน fingerprint ของ {0} แล้ว — ต้องจับคู่ใหม่ก่อนใช้งาน", node.DisplayName));
    }
}
