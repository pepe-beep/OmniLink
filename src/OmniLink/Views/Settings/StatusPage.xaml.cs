using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OmniLink.Core;
using OmniLink.Core.Clipboard;
using OmniLink.Core.Abstractions;

namespace OmniLink.Views.Settings;

/// <summary>
/// A5: หน้า "สถานะระบบ" — observe จากของจริง (process/process นับ rehook/nodes/config)
/// read-only ไม่มี config · refresh ตอน Loaded + ปุ่ม + timer 2 วิขณะ visible
/// </summary>
public partial class StatusPage : UserControl, ISettingsPage
{
    private readonly ISettingsHost Host;
    private readonly DispatcherTimer? _timer;

    public string TitleKey => "settings.section.status";
    public string IconSymbol => "Info24";
    public bool IsDirty => false;

    public StatusPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshAll();
        Loaded += (_, _) => { RefreshAll(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void LoadFromConfig() => RefreshAll();

    public void RestoreDefaults() { /* read-only — ไม่มีค่าโรงงาน */ }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        try
        {
            var kvm = Host.Kvm;
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var up = DateTime.Now - proc.StartTime;
            var gateway = kvm is KvmHostService concrete
                ? OmniLink.Platform.Windows.WindowsHostInput.LastCreated
                : null;

            EngineInfoText.Text =
                $"mode          = {(Host is { } ? (Host.Kvm is Core.Ipc.IpcKvmClient ? "dashboard (IPC)" : "engine (in-proc)") : "?")}\n" +
                $"pid           = {Environment.ProcessId}\n" +
                $"uptime        = {up:dd\\.hh\\:mm\\:ss}\n" +
                $"ram (ws)      = {proc.WorkingSet64 / 1024 / 1024} MB\n" +
                $"threads       = {proc.Threads.Count}\n" +
                $"priority      = {proc.PriorityClass}\n" +
                $"hook rehooks  = {gateway?.RehookCount ?? -1}\n" +
                $"edge_poll_hz  = {Host?.Config.EdgePollHz ?? 0}\n" +
                $"switch_delay  = {Host?.Config.SwitchDelayMs ?? 0} ms";

            var lines = new List<string>();
            foreach (var kv in kvm.SessionViews.OrderBy(k => k.Key))
            {
                var s = kv.Value;
                lines.Add($"{kv.Key,-24} {(s.Connected ? "online " : "offline")} " +
                          $"ping={s.PingMs:0.#}ms agent={s.AgentVersion} life={s.LifecycleState} policy={s.Config.Policy}");
            }
            NodesInfoText.Text = kvm.SessionViews.Count == 0
                ? Loc.T("status.nodes.empty", "(ไม่มี node ใน config)")
                : string.Join("\n", lines);

            var hist = ClipboardHistoryStore.Default.GetEntries();
            var texts = hist.Count(e => e.MimeType != "image/png");
            var images = hist.Count - texts;
            ClipboardInfoText.Text =
                $"history       = {texts} text / {images} images (max 100/20)\n" +
                $"active_node   = {kvm.ActiveNodeId ?? "(host)"}\n" +
                $"master_lang   = {kvm.MasterLangGroup}\n" +
                $"virtual desk  = {kvm.VirtualLeft},{kvm.VirtualTop} → {kvm.VirtualRight}x{kvm.VirtualBottom}";
        }
        catch (Exception ex)
        {
            EngineInfoText.Text = "refresh failed: " + ex.Message;
        }
    }
}
