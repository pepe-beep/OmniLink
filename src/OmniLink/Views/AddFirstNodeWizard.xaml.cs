using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniLink.Core;
using OmniLink.Views.Settings;

namespace OmniLink.Views;

/// <summary>
/// v2.1 onboarding: wizard "เพิ่มเครื่องแรกของคุณ" 3 ขั้น —
/// ขั้น 1 เลือกชนิดเครื่องข้างๆ (Windows / Linux+SSH / พร้อมแล้ว)
/// ขั้น 2 เตรียมตัวติดตั้งตามชนิด (เปิดโฟลเดอร์ตัวติดตั้ง / deploy ผ่าน SSH)
/// ขั้น 3 สแกน + ใส่ PIN จับคู่ (reuse ScanNetworkAsync + PairingFlow — ตรรกะเดิม 100%)
/// </summary>
public partial class AddFirstNodeWizard : Window
{
    private readonly ISettingsHost _host;
    private DiscoveredNode? _selected;
    private int _step = 1;
    private string _choice = "";

    public sealed record FoundRow(DiscoveredNode Node, string Title, string Detail);
    private readonly List<FoundRow> _rows = new();

    public AddFirstNodeWizard(ISettingsHost host)
    {
        InitializeComponent();

        _host = host;
        WinModeNote.Text = NodeInstallers.Mode == "portable"
            ? Loc.T("wizard.win.mode.portable", "โฟลเดอร์นี้มีไฟล์ OmniLink-Node-Win-Portable.zip — คัดลอกไฟล์นี้ไปเครื่องนั้นได้เลย (มี Python ฝัง ไม่ต้องลงอะไรเพิ่ม)")
            : Loc.T("wizard.win.mode.manual", "build นี้ไม่มี zip แบบพกพา — ใช้ install_node.bat ในโฟลเดอร์นี้แทน (เครื่องปลายทางต้องมี Python 3.8+)");
        SshUserBox.Text = "psl440";
    }

    // ─── ขั้น 1: เลือกชนิด ───

    private void OnChooseWindows(object sender, RoutedEventArgs e) => Choose("windows");
    private void OnChooseLinux(object sender, RoutedEventArgs e) => Choose("linux");
    private void OnChooseReady(object sender, RoutedEventArgs e) => Choose("ready");

    private void Choose(string choice)
    {
        _choice = choice;
        GoStep(2);
    }

    // ─── ขั้น 2: เตรียมตามชนิด ───

    private void OnOpenInstallersFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = NodeInstallers.FolderToOpen,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[wizard] เปิดโฟลเดอร์ตัวติดตั้งไม่ได้: " + ex.Message);
        }
    }

    private void OnDeployLinux(object sender, RoutedEventArgs e)
    {
        var ip = SshIpBox.Text.Trim();
        var user = SshUserBox.Text.Trim();
        if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(user))
        {
            DeployStatusText.Text = Loc.T("wizard.linux.err.fields", "กรอก IP และชื่อผู้ใช้ SSH ก่อน");
            return;
        }
        DeployStatusText.Text = Loc.T("wizard.linux.deploying", "กำลังติดตั้งผ่าน SSH (อาจใช้ ~10 วินาที)...");
        Task.Run(() =>
        {
            // ใช้กลไก deploy เดิม (version handshake + autostart ครบ) — งานหนักห้ามบน UI thread
            var cfg = new NodeConfig { NodeId = $"node-{ip.Replace('.', '-')}", Ip = ip, SshUser = user, Enabled = true };
            var (ok, msg) = _host.Kvm.DeployRemoteAgent(cfg);
            _ = Dispatcher.BeginInvoke(() =>
            {
                DeployStatusText.Text = (ok ? "✅ " : "❌ ") + msg;
                if (ok)
                {
                    Task.Delay(900).ContinueWith(_ => Dispatcher.BeginInvoke(() => GoScanNow()));
                }
            });
        });
    }

    private void OnGoScan(object sender, RoutedEventArgs e) => GoScanNow();

    // ─── ขั้น 3: สแกน + จับคู่ ───

    private void GoScanNow() => GoStep(3);

    private void OnRescan(object sender, RoutedEventArgs e) => RunScan();

    private void RunScan()
    {
        ScanStatusText.Text = Loc.T("settings.pairing.scanning", "กำลังสแกน...");
        _rows.Clear();
        FoundList.ItemsSource = null;
        Task.Run(async () =>
        {
            var nodes = await _host.Discovery.ScanNetworkAsync(3000);
            _ = Dispatcher.BeginInvoke(() =>
            {
                // หนึ่งเครื่อง (uuid เดียว) หลาย NIC = เลือกแถวเดียว (อันแรกที่ตอบ) — กดผิดแถว ZeroTier ทำ TLS พัง
                var seenUuid = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in nodes)
                {
                    var key = string.IsNullOrEmpty(n.Uuid) ? n.Ip : n.Uuid;
                    if (!seenUuid.Add(key)) continue;
                    _rows.Add(new FoundRow(n, n.Name, $"{n.Os} • {n.Ip} • จอ {n.Width}x{n.Height}"));
                }
                FoundList.ItemsSource = _rows;
                ScanStatusText.Text = _rows.Count > 0
                    ? Loc.T("settings.pairing.count", "พบ {0} เครื่อง", _rows.Count)
                    : Loc.T("wizard.scan.none", "ยังไม่พบ — ตรวจว่า agent รันอยู่ที่เครื่องนั้นแล้วกดสแกนใหม่");
                if (_rows.Count > 0) FoundList.SelectedIndex = 0;
            });
        });
    }

    private void OnFoundSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FoundList.SelectedItem is not FoundRow row) return;
        _selected = row.Node;
        TargetLabel.Text = Loc.T("settings.pairing.target", "เป้าหมาย: {0} ({1}) — จอ {2}x{3}",
            row.Node.Name, row.Node.Ip, row.Node.Width, row.Node.Height);
    }

    private void OnManualAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new AddNodeDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.TargetNode is null) return;
        _selected = dialog.TargetNode;
        PinBox.Text = dialog.Pin;
        TargetLabel.Text = Loc.T("settings.pairing.target", "เป้าหมาย: {0} ({1}) — จอ {2}x{3}",
            _selected.Name, _selected.Ip, _selected.Width, _selected.Height);
        if (!string.IsNullOrEmpty(dialog.Pin)) OnPairClick(sender, e);
    }

    private void OnPairClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            Show("", true, Loc.T("settings.pairing.err.target", "เลือกเครื่องจากรายการด้านบนก่อน"));
            return;
        }
        var pin = PinBox.Text.Trim();
        if (string.IsNullOrEmpty(pin))
        {
            Show("", true, Loc.T("settings.pairing.err.pin", "กรอก PIN 6 หลักที่แสดงบนจอเครื่องลูกก่อน"));
            return;
        }
        BtnWizardPair.IsEnabled = false;
        Show("", false, Loc.T("settings.pairing.connecting", "กำลังเชื่อมต่อ {0} ({1}) และตรวจ PIN...", _selected.Name, _selected.Ip));
        PairingFlow.Run(_host, _selected, pin,
            feedback: (msg, error) => Show("", error, msg),
            onDone: success =>
            {
                BtnWizardPair.IsEnabled = true;
                if (success)
                {
                    // จับคู่สำเร็จ — ปิด wizard ให้ผู้ใช้เห็น node โผล่บนแผนที่ทันที
                    DialogResult = true;
                    Close();
                }
            });
    }

    private void Show(string _unused, bool error, string msg)
    {
        FeedbackText.Foreground = new SolidColorBrush(error ? Color.FromRgb(0xFF, 0x3B, 0x5C) : Color.FromRgb(0x00, 0xD2, 0xFF));
        FeedbackText.Text = msg;
    }

    // ─── สลับขั้น ───

    private void GoStep(int step)
    {
        _step = step;
        Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        WinPanel.Visibility = step == 2 && _choice == "windows" ? Visibility.Visible : Visibility.Collapsed;
        LinuxPanel.Visibility = step == 2 && _choice == "linux" ? Visibility.Visible : Visibility.Collapsed;
        ReadyPanel.Visibility = step == 2 && _choice == "ready" ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;
        StepBadge.Text = $"{step} / 3";
        TitleText.Text = Loc.T(step switch
        {
            2 => "wizard.title2",
            3 => "wizard.title3",
            _ => "wizard.title",
        }, step switch
        {
            2 => "เตรียมเครื่องข้างๆ",
            3 => "จับคู่กับเครื่องใหม่",
            _ => "เพิ่มเครื่องแรกของคุณ",
        });
        SubtitleText.Text = Loc.T(step switch
        {
            2 => "wizard.subtitle2",
            3 => "wizard.subtitle3",
            _ => "wizard.subtitle",
        }, step switch
        {
            2 => "ทำตามคำแนะนำสั้นๆ แล้วกดไปขั้นถัดไป",
            3 => "สแกนหาเครื่อง → เลือก → ใส่ PIN จากจอเครื่องนั้น",
            _ => "เลือกว่าคอมพิวเตอร์ข้างๆ เครื่องนี้เป็นระบบอะไร",
        });

        if (step == 3 && _rows.Count == 0)
        {
            RunScan();
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => GoStep(_step == 3 ? 2 : 1);

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
