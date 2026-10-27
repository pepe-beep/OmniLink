using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

public partial class LoggingPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    private bool _init = true;
    public bool IsDirty { get; private set; }
    public LoggingPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        Loaded += (_, _) =>
        {
            LoadFromConfig();
            LogPathText.Text = OmniPaths.LogsDir;
        };
    }

    public string TitleKey => "settings.section.logging";
    public string IconSymbol => "DocumentBulletList24";

    public void LoadFromConfig()
    {
        _init = true;
        var cfg = Host.Config;
        LevelCombo.SelectedIndex = cfg.LogLevel switch
        {
            "OFF" => 0,
            "ERROR" => 1,
            "WARN" => 2,
            _ => 3,
        };
        LogMaxKbBox.Text = (cfg.LogMaxBytes / 1024).ToString();
        ErrorText.Visibility = Visibility.Collapsed;
        _init = false;
        IsDirty = false;
    }

    public void RestoreDefaults()
    {
        var cfg = Host.Config;
        cfg.LogLevel = "INFO";
        cfg.LogMaxBytes = OmniLimits.LogKbDefault * 1024;
        LoadFromConfig();
        Host.ApplyLogLevelFromConfig();
        OmniLog.SetMaxLogBytes(cfg.LogMaxBytes);
        Host.SaveConfig();
        Host.SetBanner(Loc.T("banner.defaults.logging", "คืนค่าเริ่มต้นหน้า Logging แล้ว"));
    }

    private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        Host.Config.LogLevel = LevelCombo.SelectedIndex switch
        {
            0 => "OFF",
            1 => "ERROR",
            2 => "WARN",
            _ => "INFO",
        };
        Host.ApplyLogLevelFromConfig();
        Host.SaveConfig();
    }

    private void OnBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        IsDirty = true;
        if (int.TryParse(LogMaxKbBox.Text.Trim(), out var kb) && kb is >= OmniLimits.LogKbMin and <= OmniLimits.LogKbMax)
        {
            Host.Config.LogMaxBytes = kb * 1024;
            OmniLog.SetMaxLogBytes(Host.Config.LogMaxBytes);
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorText.Text = Loc.T("settings.log.err", "ต้องเป็นตัวเลข 500 - 50,000 KB — ช่องที่กรอกผิดจะยังใช้ค่าเดิม");
            ErrorText.Visibility = Visibility.Visible;
        }
        Host.SaveConfig();
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(OmniPaths.LogsDir);
            Process.Start(new ProcessStartInfo { FileName = OmniPaths.LogsDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Host.SetBanner(Loc.T("banner.log.openfail", "เปิดโฟลเดอร์ log ไม่สำเร็จ: ") + ex.Message);
        }
    }
}
