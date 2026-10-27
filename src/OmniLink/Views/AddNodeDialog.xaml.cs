using System.Windows;
using OmniLink.Core;

namespace OmniLink.Views;

/// <summary>
/// B1: จับคู่เครื่องแบบกรอก IP เอง (สไตล์ Deskflow) — ใช้ได้แม้ broadcast discovery ไม่ทำงาน
/// (VPN เช่น ZeroTier/Radmin, เครื่องหลายการ์ดเน็ต) — flow การจับคู่ใช้ PIN + fingerprint TOFU เดิมทุกอย่าง
/// </summary>
public partial class AddNodeDialog : Window
{
    public DiscoveredNode? TargetNode { get; private set; }
    public string Pin { get; private set; } = "";

    public AddNodeDialog()
    {
        InitializeComponent();
        IpBox.Focus();
    }

    private void OnPairClick(object sender, RoutedEventArgs e)
    {
        var ip = IpBox.Text.Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            FeedbackText.Text = Loc.T("dialog.add.err.ip", "IP ไม่ถูกต้อง — ตัวอย่าง: 192.168.99.2");
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            FeedbackText.Text = Loc.T("dialog.add.err.port", "Port ต้องเป็นตัวเลข 1-65535");
            return;
        }

        var pin = PinBox.Text.Trim();
        if (string.IsNullOrEmpty(pin))
        {
            FeedbackText.Text = Loc.T("dialog.add.err.pin", "กรุณาใส่ PIN 6 หลักที่แสดงบนจอเครื่องลูก");
            return;
        }

        var position = (PositionCombo.SelectedIndex switch
        {
            1 => "left",
            2 => "top",
            3 => "bottom",
            _ => "right",
        });
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? $"Manual-{ip}" : NameBox.Text.Trim();

        // uuid ปลอมเชิงระบุ — จับคู่สำเร็จแล้ว host จะใช้ uuid จริงจาก pair_accept แทนเสมอ
        TargetNode = new DiscoveredNode(
            Uuid: $"manual-{ip.Replace('.', '-')}",
            Name: name,
            Os: "Linux",
            Ip: ip,
            Port: port,
            Width: 1920,
            Height: 1080,
            Paired: false,
            Version: "?");
        Pin = pin;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
