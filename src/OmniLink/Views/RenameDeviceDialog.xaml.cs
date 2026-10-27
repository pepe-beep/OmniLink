using System.Windows;
using System.Windows.Input;
using OmniLink.Core;

namespace OmniLink.Views;

public partial class RenameDeviceDialog : Window
{
    private readonly NodeConfig _nodeConfig;

    public bool WasChanged { get; private set; }

    public RenameDeviceDialog(NodeConfig nodeConfig)
    {
        InitializeComponent();
        _nodeConfig = nodeConfig;

        var detectedHost = !string.IsNullOrWhiteSpace(_nodeConfig.Hostname)
            ? _nodeConfig.Hostname
            : (!string.IsNullOrWhiteSpace(_nodeConfig.NodeId) ? _nodeConfig.NodeId : "Unknown");

        DetectedHostnameText.Text = detectedHost;
        DeviceNameBox.Text = _nodeConfig.DisplayName;
        DeviceNameBox.Focus();
        DeviceNameBox.SelectAll();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var text = DeviceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            // UI-L6 fix: ค่าว่าง = ไม่ยอมรับ (เดิม reset เงียบๆ แต่ยังรายงาน "เปลี่ยนชื่อสำเร็จ" — หลอกผู้ใช้)
            return;
        }
        if (text.Equals(_nodeConfig.Hostname, System.StringComparison.OrdinalIgnoreCase))
        {
            _nodeConfig.ResetName();
        }
        else
        {
            _nodeConfig.SetCustomName(text);
        }

        WasChanged = true;
        DialogResult = true;
        Close();
    }

    private void OnResetHostnameClick(object sender, RoutedEventArgs e)
    {
        _nodeConfig.ResetName();
        DeviceNameBox.Text = _nodeConfig.DisplayName;
        WasChanged = true;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnDeviceNameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSaveClick(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            OnCancelClick(sender, e);
        }
    }
}
