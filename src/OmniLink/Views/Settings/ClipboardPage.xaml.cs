using System.Windows;
using System.Windows.Controls;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

public partial class ClipboardPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    private bool _init = true;
    public bool IsDirty { get; private set; }
    public ClipboardPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        Loaded += (_, _) => LoadFromConfig();
    }

    public string TitleKey => "settings.section.clipboard";
    public string IconSymbol => "ClipboardPaste24";

    public void LoadFromConfig()
    {
        _init = true;
        var cfg = Host.Config;
        ChkClipboardSync.IsChecked = cfg.EnableClipboard;
        ChkClipPush.IsChecked = cfg.ClipboardPushToNode;
        ChkClipAccept.IsChecked = cfg.ClipboardAcceptFromNode;
        ClipMaxBytesBox.Text = cfg.ClipboardMaxBytes.ToString();
        ErrorText.Visibility = Visibility.Collapsed;
        _init = false;
        IsDirty = false;
    }

    public void RestoreDefaults()
    {
        var cfg = Host.Config;
        cfg.EnableClipboard = true;
        cfg.ClipboardPushToNode = true;
        cfg.ClipboardAcceptFromNode = true;
        cfg.ClipboardMaxBytes = OmniLimits.ClipMaxDefault;
        LoadFromConfig();
        Host.UpdateKvmStrategies();
        Host.SaveConfig();
        Host.SetBanner(Loc.T("banner.defaults.clipboard", "คืนค่าเริ่มต้นหน้า Clipboard แล้ว"));
    }

    private void OnBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        if (int.TryParse(ClipMaxBytesBox.Text.Trim(), out var v))
        {
            Host.Config.ClipboardMaxBytes = OmniLimits.ClampInt(v, OmniLimits.ClipMaxMin, OmniLimits.ClipMaxMax);
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorText.Text = Loc.T("settings.clip.err", "ต้องเป็นตัวเลข 1,000 - 5,000,000 bytes — ช่องที่กรอกผิดจะยังใช้ค่าเดิม");
            ErrorText.Visibility = Visibility.Visible;
        }
        Host.SaveConfig();
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        var cfg = Host.Config;
        cfg.EnableClipboard = ChkClipboardSync.IsChecked == true;
        cfg.ClipboardPushToNode = ChkClipPush.IsChecked == true;
        cfg.ClipboardAcceptFromNode = ChkClipAccept.IsChecked == true;
        Host.UpdateKvmStrategies();
        Host.SaveConfig();
    }
}
