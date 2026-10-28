using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Clipboard;
using OmniLink.Core.Events;
using OmniLink.Core.FileExchange;
using OmniLink.Core.Security;
using OmniLink.Core.Strategies;

namespace OmniLink.Core;


public sealed class KvmHostService : IDisposable, IHostLinkCallbacks, Abstractions.IKvmHost
{
    private readonly IOmniEventHub _eventHub;
    public OmniConfig Config { get; }
    // KV-1 fix: ถูกอ่านจาก mouse hot path / boot task / watchdog ขณะ UI thread เขียน —
    // Dictionary ปกติไม่ทน read-during-write; ConcurrentDictionary จึงจำเป็น
    public ConcurrentDictionary<string, RemoteNodeClient> Sessions { get; } = new();
    public string? ActiveNodeId { get => _switch.ActiveNodeId; private set => _switch.ActiveNodeId = value; } // godclass-K6a
    public int MasterLangGroup => _lang.MasterLangGroup;

    /// <summary>A2: ล็อกเมาส์ไว้จอปัจจุบัน (Scroll Lock toggle) — panic/unlock ยัง override เสมอ</summary>
    public bool LockedToScreen { get => _switch.LockedToScreen; set => _switch.LockedToScreen = value; } // godclass-K6a (volatile backing)

    /// <summary>BOOT-ORDER FIX: ถูกยิงทุกครั้งที่โหนดเชื่อมต่อสำเร็จ (MainWindow ใช้ retry เสียง)</summary>
    public Action<RemoteNodeClient>? OnNodeOnline;

    private readonly ClipboardRouter _clip;
    public FileTransferManager FileTransfer { get; }
    /// <summary>A6: banner สถานะสั้นๆ ให้ UI แสดง (เช่น clipboard วิ่งข้ามจอ)</summary>
    public Action<string>? OnStatusBanner;
    internal const string ExpectedAgentVersion = "2.0.0"; // v2.0 — bump พร้อม node_agent เสมอ (สองไฟล์นี้ sync กัน)

    // godclass-K3: delegate กลุ่ม clipboard ย้ายไป ClipboardRouter.Ports — facade property คงชื่อเดิม
    /// <summary>Clipboard ข้ามเครื่อง: MainWindow ฉีด getter ที่อ่าน Windows clipboard บน STA/UI thread</summary>
    public Func<string?>? HostClipboardGetter { get => _clip.Ports.HostClipboardGetter; set => _clip.Ports.HostClipboardGetter = value; }
    /// <summary>Clipboard ข้ามเครื่อง: MainWindow ฉีด getter สำหรับอ่านภาพ PNG บน STA thread</summary>
    public Func<byte[]?>? HostPngGetter { get => _clip.Ports.HostPngGetter; set => _clip.Ports.HostPngGetter = value; }
    /// <summary>Clipboard ข้ามเครื่อง: MainWindow ฉีด getter สำหรับอ่านรายชื่อไฟล์ที่ถูก Cut/Copy</summary>
    public HostFileDropGetterDelegate? HostFileDropGetter { get => _clip.Ports.HostFileDropGetter; set => _clip.Ports.HostFileDropGetter = value; }
    /// <summary>Clipboard ข้ามเครื่อง: รับข้อความจาก agent → MainWindow เขียน Windows clipboard บน UI thread</summary>
    public Action<string>? OnClipboardFromClient { get => _clip.Ports.OnClipboardFromClient; set => _clip.Ports.OnClipboardFromClient = value; }
    /// <summary>Clipboard ข้ามเครื่อง: รับภาพ PNG จาก agent → MainWindow เขียน Windows clipboard</summary>
    public Action<byte[]?>? OnClipboardImageFromClient { get => _clip.Ports.OnClipboardImageFromClient; set => _clip.Ports.OnClipboardImageFromClient = value; }
    /// <summary>Clipboard ข้ามเครื่อง: รับไฟล์จาก agent → MainWindow เขียน Windows clipboard (CF_HDROP)</summary>
    public Action<IReadOnlyList<string>, bool>? OnClipboardFilesFromClient { get => _clip.Ports.OnClipboardFilesFromClient; set => _clip.Ports.OnClipboardFilesFromClient = value; }
    private readonly ReverseKnockListener _knock;

    public int VirtualLeft { get; private set; }
    public int VirtualTop { get; private set; }
    public int VirtualRight { get; private set; } = 1920;
    public int VirtualBottom { get; private set; } = 1080;

    // C2: สลับด้วย reference assignment (atomic) แทน Clear+AddRange ที่แข่งขันกับ hook thread
    private volatile ScreenBounds[] _monitors = Array.Empty<ScreenBounds>();

    /// <summary>v2.0: ประตูอินพุตของ OS นี้ (hook/เคอร์เซอร์/จอ/ภาษา) — ฉีดตอนสร้าง (Windows = WindowsHostInput)</summary>
    private readonly IHostInputGateway _input;


    /// <summary>
    /// บล็อกสลับจอเมื่อมีแอปหน้าจอเต็มทำงานอยู่ (ตาม config) — ทางหนีฉุกเฉินไม่ถูกบล็อกเสมอ
    /// เรียกจาก mouse hook เฉพาะตอนเมาส์ชนขอบจริง
    /// </summary>
    /// <summary>A2: toggle ล็อกเมาส์ — ล็อกแล้วเมาส์ข้ามจอไม่ได้ (ทั้งสองทิศ) จนกว่าจะกดปลด/ฉุกเฉิน</summary>
    private void ToggleScreenLock()
    {
        LockedToScreen = !LockedToScreen;
        OmniLog.Write("kvm", LockedToScreen
            ? "ล็อกเมาส์ไว้จอปัจจุบัน (กด Scroll Lock อีกครั้งเพื่อปลด / ฉุกเฉิน = Ctrl+Alt+Backspace)"
            : "ปลดล็อกเมาส์แล้ว — สลับจอได้ตามปกติ");
        _onUiUpdate();
    }

    /// <summary>A2: ใช้ใน mouse hook — เมาส์ชนขอบตอนล็อกอยู่ = ผ่าน event ไป Windows ตามปกติ (ไม่สลับ)</summary>
    private bool ScreenLockBlocksEdgeSwitch()
    {
        if (!LockedToScreen) return false;
        var now = Environment.TickCount64;
        if (now - _switch.LastLockLogTick > 2000)
        {
            _switch.LastLockLogTick = now;
            OmniLog.Write("kvm", "บล็อกสลับจอ: ล็อกเมาส์อยู่ (Scroll Lock)");
        }
        return true;
    }


    private bool ShouldBlockEdgeByFullscreen()
    {
        if (!Config.BlockSwitchOnFullscreen) return false;
        if (!_input.IsForegroundFullscreen()) return false;
        var now = Environment.TickCount64;
        if (now - _switch.LastFsBlockLogTick > 2000)
        {
            _switch.LastFsBlockLogTick = now;
            OmniLog.Write("kvm", "บล็อกสลับจอ: มีหน้าต่างหน้าจอเต็มทำงานอยู่ (กดปิดได้ใน Settings)");
        }
        return true;
    }
    private readonly SwitchingState _switch;
    private readonly LanguageSyncService _lang;

    private readonly HotkeyEngine _hotkeys;
    private readonly HostTopologyService _topo;

    /// <summary>
    /// v2.0 แผน 3.4 กันลูป host↔host: เมื่อเครื่องนี้ "กำลังถูกคุม" โดย host ตัวอื่น (โหมด sub active)
    /// ให้พักการสลับจอของ host-role ของตัวเอง — หนึ่งคู่ active ต่อหนึ่งเวลา
    /// (owner ของ NodeAgentServer เป็นคนเสียบ probe นี้)
    /// </summary>
    public Func<bool>? SubActiveFocusProbe { get; set; }

    /// <summary>กราฟปัจจุบันสำหรับ UI (minimap) — ถ้ายังไม่ build = สร้างชั่วคราวจาก config</summary>
    public TopologyGraph Topology => _topo.CurrentOrTemp(); // godclass-K5

    /// <summary>S2 (IKvmHost): snapshot ต่อ node สำหรับ dashboard/IPC — สร้างต่อการเรียก (จังหวะ refresh 900ms = ถูก)</summary>
    public IReadOnlyDictionary<string, NodeSessionView> SessionViews
    {
        get
        {
            var dict = new Dictionary<string, NodeSessionView>(Sessions.Count);
            foreach (var kv in Sessions)
            {
                var s = kv.Value;
                dict[kv.Key] = new NodeSessionView
                {
                    Config = s.Config,
                    Connected = s.Connected,
                    LocalEndpointIp = s.LocalEndpointIp,
                    Width = s.Width,
                    Height = s.Height,
                    AgentVersion = s.AgentVersion,
                    LangGroup = s.LangGroup,
                    PingMs = s.PingMs,
                    RamPct = s.RamPct,
                    RamUsedGb = s.RamUsedGb,
                    RamTotalGb = s.RamTotalGb,
                    PlaybackRunning = s.PlaybackRunning,
                    PlaybackActive = s.PlaybackActive,
                    MicRunning = s.MicRunning,
                    LifecycleState = s.LifecycleState,
                };
            }
            return dict;
        }
    }

    /// <summary>S2 (IKvmHost): ผังฉบับเดินทางข้าม IPC ได้ (Cells + GetNeighbor)</summary>
    public TopologyView TopologyView
    {
        get
        {
            var g = _topo.CurrentOrTemp();
            var cells = new Dictionary<string, (int X, int Y)>(g.Cells.Count);
            foreach (var kv in g.Cells)
            {
                cells[kv.Key] = kv.Value;
            }
            return new TopologyView(cells, g.EdgesView);
        }
    }

    /// <summary>เรียกเมื่อ layout/anchor/position เปลี่ยน — rebuild กราฟ + ยิง set_layout (logic ย้าย HostTopologyService)</summary>
    public void ReloadLayout() => _topo.Reload();

    /// <summary>ส่งแผนที่เพื่อนบ้านของ node นี้ให้ agent — agent ใช้ตัดสินขอบไหนไปเครื่องไหน</summary>
    private void SendLayoutTo(RemoteNodeClient sess, TopologyGraph? graph) => _topo.SendLayoutTo(sess, graph);

    /// <summary>เรียกเมื่อ hotkeys ใน config เปลี่ยน (UI save) — rebuild นอก hook thread (logic ย้าย HotkeyEngine)</summary>
    public void ReloadHotkeys() => _hotkeys.Reload(Config);

    /// <summary>สลับล็อกเมาส์จากสูตรลัด (pattern เดียวกับ Scroll Lock — แจ้ง lock state ไปเครื่องที่คุมอยู่)</summary>
    public void ToggleLockFromHotkey()
    {
        ToggleScreenLock();
        var activeId = ActiveNodeId;
        if (activeId is not null && Sessions.TryGetValue(activeId, out var sess) && sess.Connected)
        {
            sess.SendTcp(new { type = "lock", on = LockedToScreen });
        }
    }

    private void RunHotkeyActions(List<HotkeyActionConfig> actions)
    {
        foreach (var action in actions)
        {
            try
            {
                switch (action.Type)
                {
                    case HotkeyActionConfig.SwitchNode:
                        if (!string.IsNullOrEmpty(action.NodeId) && Sessions.ContainsKey(action.NodeId))
                        {
                            SwitchToNode(action.NodeId);
                            _eventHub.Publish(new StatusBannerEvent(Loc.T("banner.hotkey.switched", "สูตรลัด: สลับไปเครื่อง {0}", action.NodeId)));
                        }
                        else
                        {
                            OmniLog.Warn($"[hotkey] switch_node target '{action.NodeId}' not in sessions — skipped");
                        }
                        break;
                    case HotkeyActionConfig.ToggleLock:
                        ToggleLockFromHotkey();
                        _eventHub.Publish(new StatusBannerEvent(Loc.T("banner.hotkey.lock", "สูตรลัด: {0}", LockedToScreen
                            ? Loc.T("settings.switch.lock.on", "ล็อกเมาส์ไว้จอปัจจุบัน")
                            : Loc.T("settings.switch.lock.off", "ปลดล็อกเมาส์แล้ว"))));
                        break;
                    case HotkeyActionConfig.ToggleStream:
                    case HotkeyActionConfig.ToggleMic:
                        _eventHub.Publish(new HotkeyAudioRequestedEvent(action.Type)); // audio เป็นของ MainWindow
                        break;
                    case HotkeyActionConfig.CalibrateEn:
                        CalibrateAllLanguages(0);
                        break;
                    case HotkeyActionConfig.CalibrateTh:
                        CalibrateAllLanguages(1);
                        break;
                }
            }
            catch (Exception ex)
            {
                OmniLog.Warn("[hotkey] action failed: " + ex.Message);
            }
        }
    }
    private readonly Action _onUiUpdate;
    private System.Threading.Timer? _watchdogTimer;
    private int _watchdogTickCount;

    private readonly InputPipeline _pipeline = new(); // godclass-K6b: คิวคำสั่ง switching — pump เดียวกับ mouse worker
    private readonly Channel<MouseHookData> _mouseEventQueue = Channel.CreateBounded<MouseHookData>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private Thread? _mouseWorkerThread;
    private bool _hookGatewayStarted;
    private volatile bool _running = true;

    private bool _switchHintShown;

    private IEdgeSwitchStrategy _edgeStrategy;

    private System.Threading.Timer? _edgeDwellTimer;
    private readonly object _dwellLock = new();
    private string? _dwellTargetId;
    private ScreenEdge _dwellEdge;
    private ScreenBounds _dwellMonitor;
    private int _dwellLastY;

    public KvmHostService(OmniConfig config, Action onUiUpdate, IOmniEventHub? eventHub = null, IHostInputGateway? inputGateway = null)
    {
        Config = config;
        _deploy = new NodeDeployService(config, Sessions);
        _knock = new ReverseKnockListener(Sessions, () => _running);
        _onUiUpdate = onUiUpdate;
        _eventHub = eventHub ?? OmniEventHub.Default;
        _input = inputGateway ?? new NullHostInputGateway();
        _switch = new SwitchingState();
        _lang = new LanguageSyncService(config, Sessions, _input, _eventHub, _onUiUpdate, () => ActiveNodeId, () => _running, _switch);
        _hotkeys = new HotkeyEngine();
        _topo = new HostTopologyService(config, Sessions);
        FileTransfer = new FileTransferManager(_eventHub);
        _clip = new ClipboardRouter(config, Sessions, FileTransfer, _eventHub, () => OnStatusBanner, () => ActiveNodeId);
        _input.Warmup();
        WarmupHookCallbacks();

        _edgeStrategy = EdgeStrategyFactory.Create(config);

        RefreshMonitors();
        _lang.MasterLangGroup = _input.GetForegroundLangGroup();
        _knock.Start();
    }

    private void WarmupHookCallbacks()
    {
        try
        {
            var kbMethod = typeof(KvmHostService).GetMethod(nameof(OnKeyHook), BindingFlags.NonPublic | BindingFlags.Instance);
            if (kbMethod != null) RuntimeHelpers.PrepareMethod(kbMethod.MethodHandle);

            var msMethod = typeof(KvmHostService).GetMethod(nameof(OnMouseHook), BindingFlags.NonPublic | BindingFlags.Instance);
            if (msMethod != null) RuntimeHelpers.PrepareMethod(msMethod.MethodHandle);

            // F1: JIT ด้วย strategy path ที่ full callback จะเรียก (กัน first-edge-event timeout)
            // godclass-K7c: จงใจใช้ reflection+PrepareMethod — ShouldSwitch ของ strategy มี state (_lastEdge/Reset)
            // ห้ามเรียกจริงตอน warmup ไม่งั้น state จะปนเปื้อน; อย่า "แก้" เป็น direct call
            try
            {
                var swMethod = _edgeStrategy?.GetType().GetMethod("ShouldSwitch");
                if (swMethod != null) RuntimeHelpers.PrepareMethod(swMethod.MethodHandle);
            }
            catch { }
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[hook] Pre-JIT warmup warning: {ex.Message}");
        }
    }

    public void UpdateStrategies()
    {
        _edgeStrategy = EdgeStrategyFactory.Create(Config);
        _lang.UpdateStrategy(Config.LanguageMode);
    }

    public void RestoreSavedHostLanguage() => _lang.RestoreSavedHostLanguage(); // godclass-K4

    public void Start()
    {
        _startTick = Environment.TickCount64;
        // godclass-G5 (root cause 27 ต.ค.): engine = latency-critical — High priority กัน CPU starvation
        // ที่ทำ hook callback เกิน LowLevelHooksTimeout จน Windows ถอด hook เป็นรอบ (เงียบกระจุก 15-33 วิ)
        if (Config.ProcessPriorityHigh)
        {
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                OmniLog.Write("boot", "[priority] engine process PriorityClass=High (anti-starvation, config process_priority_high)");
            }
            catch (Exception ex)
            {
                OmniLog.Warn($"[priority] set High failed: {ex.Message}");
            }
        }
        ReloadLayout(); // v1.9.1: build กราฟ topology ก่อนติด hook
        ReloadHotkeys(); // v1.9 C1: build ดัชนีสูตรลัดก่อนติด hook
        if (Config.StopDeskflowOnStart)
        {
            DeskflowConflictGuard.StopConflictingDeskflow();
        }

        List<NodeConfig> nodesSnapshot;
        lock (OmniConfig.NodesGate) // SV-M16: enumerate บน snapshot — Nodes ถูก mutate จาก UI ได้
        {
            nodesSnapshot = Config.Nodes.ToList();
        }
        foreach (var ncfg in nodesSnapshot)
        {
            var effectiveToken = !string.IsNullOrEmpty(ncfg.AuthToken) ? ncfg.AuthToken : (Config.AuthToken ?? "");
            Sessions.TryAdd(ncfg.NodeId, new RemoteNodeClient(
                ncfg,
                effectiveToken,
                this));
        }

        if (_hookGatewayStarted) return; // idempotent — Stop() แล้ว Start() ใหม่จะเริ่มเธรด pump ของ gateway ให้
        _hookGatewayStarted = true;

        _input.Start(OnKeyHook, OnMouseHook);

        if (_mouseWorkerThread is null)
        {
            _mouseWorkerThread = new Thread(MouseWorkerLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "OmniLink_MouseWorker",
            };
            _mouseWorkerThread.Start();
        }

        if (_watchdogTimer is null)
        {
            _watchdogTimer = new System.Threading.Timer(WatchdogTimerCallback, null, 3000, 3000);
        }

        _lang.StartWatcher();
    }

    private readonly NodeDeployService _deploy;
    private long _startTick;

    private void WatchdogTimerCallback(object? state)
    {
        if (!_running) return;

        // 1. Hook Watchdog (runs every 3 seconds)
        _input.RunHealthCheck();

        // 2. Deploy Watchdog (runs every 30 seconds = every 10 ticks)
        if (Interlocked.Increment(ref _watchdogTickCount) % 10 == 0)
        {
            _deploy.CheckDeployHealth(_startTick);
        }

        // 3. Smart Focus-Aware Mic Standby Check
        if (ActiveNodeId is null && Config.SmartMicFollowFocus && _switch.ReturnToHostTick > 0)
        {
            var idleMs = Environment.TickCount64 - _switch.ReturnToHostTick;
            var timeoutMs = Math.Clamp(Config.MicIdleTimeoutSec, 5, 300) * 1000;
            if (idleMs >= timeoutMs)
            {
                _switch.ReturnToHostTick = 0;
                try { OnFocusMicAction?.Invoke(false); } catch { }
            }
        }
    }

    private void OnNodeConnected(RemoteNodeClient sess)
    {
        FileTransfer.RegisterNodeSender(sess.Config.NodeId, msg => sess.SendTcp(msg));
        // v1.7 C3: ส่ง policy ให้ agent เก็บไว้ enforce ฝั่งรับทุกครั้งที่เชื่อมต่อ
        sess.SendTcp(new { type = "set_policy", policy = OmniPolicy.Normalize(sess.Config.Policy) });
        // v1.9.1: ส่งแผนที่เพื่อนบ้าน — agent ใช้ตัดสินขอบไหน → node ไหน (chain topology)
        try { SendLayoutTo(sess, _topo.CurrentGraph); } catch (Exception ex) { OmniLog.Warn("[layout] push failed: " + ex.Message); }
        if (Config.LanguageMode == "global_calibrated")
        {
            sess.RequestSetLanguage(MasterLangGroup, calibrate: true);
        }

        // BOOT-ORDER FIX: ลูกออนไลน์ทีหลัง (เช่นเปิดทีหลัง host) → ให้โอกาสระบบเสียงเริ่มตาม
        try { OnNodeOnline?.Invoke(sess); } catch { }
        try { _eventHub.Publish(new NodeConnectionEvent(sess.Config.NodeId, sess.Config.Ip, true, sess.LangGroup)); } catch { }

        // INCIDENT#2: agent จะล้าง active_focus ทุกครั้งที่ TCP reconnect (finally block)
        // ถ้า Host ยังคิดว่ากำลังควบคุมเครื่องนี้อยู่ → ต้อง re-assert enter_screen ทันที
        // ไม่งั้นเมาส์เคลื่อนบน Linux ได้แต่กลับไม่ได้ (edge detection ไม่ armed)
        if (ActiveNodeId == sess.Config.NodeId)
        {
            OmniLog.Warn($"[kvm] reconnect ขณะควบคุม {sess.Config.NodeId} อยู่ → re-assert enter_screen");
            sess.SendTcp(new
            {
                type = "enter_screen",
                position = sess.Config.Position,
                norm_y = 0.5,
                lang_group = sess.LangGroup,
            });
        }
    }

    private long _lastReassertTick;

    /// <summary>INCIDENT#2: ตรวจ desync จาก pong.focused — Host คิดว่าควบคุมอยู่แต่ agent ไม่ focused</summary>
    /// <summary>godclass-K6b: ผู้จัดการคำสั่งจาก pipeline — รันบน pump thread เดียว (MouseWorkerLoop)</summary>
    private void HandlePipelineCommand(InputPipeline.Command cmd)
    {
        switch (cmd.Type)
        {
            case InputPipeline.CmdType.EdgeReturn:
                OnEdgeReturnFromClient(cmd.NodeId, cmd.Edge, cmd.NormY, cmd.NormX);
                break;
            case InputPipeline.CmdType.SwitchRequest:
                if (ActiveNodeId != cmd.NodeId) break; // สถานะหลุดกลางทาง — ทิ้งตามพฤติกรรม edge_return เดิม
                HandleSwitchRequest(cmd.Target, cmd.Edge, cmd.NormX, cmd.NormY);
                break;
            case InputPipeline.CmdType.SwitchDirect:
                // FIX(K6b-hotfix): busy=1 ถูก acquire ไว้ที่ผู้โพสต์ (HandleEdgeHit/dwell) — เดิม release
                // อยู่ใน finally ของ Task.Run ที่ถูกแทน = ติดค้าง 1 ตลอดหลังสลับครั้งแรก (edge switch ตายเงียบ)
                try
                {
                    SwitchToNode(cmd.NodeId, cmd.NormX, cmd.NormY);
                }
                finally
                {
                    Interlocked.Exchange(ref _switch.EdgeSwitchBusy, 0);
                }
                break;
            case InputPipeline.CmdType.HotkeyActions:
                if (cmd.Payload is List<HotkeyActionConfig> actions)
                {
                    RunHotkeyActions(actions);
                }
                break;
            case InputPipeline.CmdType.LanguageToggle:
                TriggerLanguageToggle();
                break;
            case InputPipeline.CmdType.FocusedReport:
                if (Sessions.TryGetValue(cmd.NodeId, out var fsess))
                {
                    OnAgentFocusedReported(fsess, cmd.Focused);
                }
                break;
        }
    }

    /// <summary>v1.9.1: agent ดันขอบที่มีเพื่อนบ้าน → สลับไป node นั้น (รันบน pipeline thread)</summary>
    private void HandleSwitchRequest(string target, string edge, double normX, double normY)
    {
        var (nx, ny) = edge switch
        {
            "top" => (normX, 1.0),
            "bottom" => (normX, 0.0),
            "left" => (1.0, normY),
            _ => (0.0, normY),
        };
        OmniLog.Write("kvm", $"switch_request target={target} edge={edge}");
        SwitchToNode(target, nx, ny);
    }

    private void OnAgentFocusedReported(RemoteNodeClient sess, bool focused)
    {
        if (focused || ActiveNodeId != sess.Config.NodeId)
        {
            return;
        }
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastReassertTick) < 5000)
        {
            return; // throttle 5 วินาที
        }
        Interlocked.Exchange(ref _lastReassertTick, now);
        OmniLog.Warn($"[kvm] DESYNC: host คิดว่าควบคุม {sess.Config.NodeId} อยู่ แต่ agent รายงาน focused=false → re-assert enter_screen");
        sess.SendTcp(new
        {
            type = "enter_screen",
            position = sess.Config.Position,
            norm_y = 0.5,
            lang_group = sess.LangGroup,
        });
    }

    #region IHostLinkCallbacks Implementation
    void IHostLinkCallbacks.OnEdgeReturn(string nodeId, string edge, double normY, double normX)
    {
        _pipeline.Post(InputPipeline.CmdType.EdgeReturn, nodeId, edge, normX, normY); // godclass-K6b
    }

    /// <summary>
    /// v1.9.1: agent ดันขอบที่มีเพื่อนบ้าน (ไม่ใช่ขอบหันหา host) → สลับไป node นั้นทันที
    /// จุดวางเคอร์เซอร์บนปลายทาง = "ขอบตรงข้าม" ของ edge ที่ดัน (ดัน top → โผล่ขอบล่างของปลายทาง)
    /// </summary>
    void IHostLinkCallbacks.OnSwitchRequestReceived(string nodeId, string target, string edge, double normX, double normY)
    {
        _pipeline.Post(InputPipeline.CmdType.SwitchRequest, nodeId, edge, normX, normY, target: target); // godclass-K6b (ตรวจ ActiveNodeId บน pump thread)
    }

    void IHostLinkCallbacks.OnHostnameDiscovered(string nodeId, string hostname)
    {
        if (Sessions.TryGetValue(nodeId, out var sess))
        {
            if (sess.Config.Hostname != hostname)
            {
                sess.Config.Hostname = hostname;
                Config.Save();
                _onUiUpdate();
            }
        }
    }

    void IHostLinkCallbacks.OnConnected(string nodeId)
    {
        if (Sessions.TryGetValue(nodeId, out var sess))
        {
            OnNodeConnected(sess);
        }
    }

    void IHostLinkCallbacks.OnDisconnected(string nodeId)
    {
        FileTransfer.UnregisterNodeSender(nodeId);
        try { _eventHub.Publish(new NodeConnectionEvent(nodeId, "", false, 0)); } catch { }
        _onUiUpdate();
    }

    void IHostLinkCallbacks.OnClipboardReceived(string mimeType, byte[] data)
    {
        _clip.OnClipboardReceived(mimeType, data); // godclass-K3
    }

    void IHostLinkCallbacks.OnStateChanged(string nodeId, string state)
    {
        _onUiUpdate();
    }

    void IHostLinkCallbacks.OnLegacyAgent(string nodeId, string agentVersion)
    {
        if (Sessions.TryGetValue(nodeId, out var sess))
        {
            _deploy.OnAgentMismatch(sess.Config, agentVersion);
        }
    }

    void IHostLinkCallbacks.OnFocusedReported(string nodeId, bool focused)
    {
        _pipeline.Post(InputPipeline.CmdType.FocusedReport, nodeId, focused: focused); // godclass-K6b
    }

    public bool TryGetPendingClip(string token, out (byte[] data, string mime) clip)
    {
        return _clip.TryGetPendingClip(token, out clip); // godclass-K3
    }

    bool IHostLinkCallbacks.TryGetPendingClip(string token, out (byte[] data, string mime) clip)
    {
        return TryGetPendingClip(token, out clip);
    }

    void IHostLinkCallbacks.OnFileOfferReceived(string nodeId, string manifestJson) => _clip.OnFileOfferReceived(nodeId, manifestJson);
    void IHostLinkCallbacks.OnFileAcceptReceived(string nodeId, string transferId) => _clip.OnFileAcceptReceived(nodeId, transferId);
    void IHostLinkCallbacks.OnFileChunkReceived(string nodeId, string transferId, int fileIndex, long offset, string base64Data) => _clip.OnFileChunkReceived(nodeId, transferId, fileIndex, offset, base64Data);
    void IHostLinkCallbacks.OnFileDoneReceived(string nodeId, string transferId, int fileIndex) => _clip.OnFileDoneReceived(nodeId, transferId, fileIndex);
    void IHostLinkCallbacks.OnFileResultReceived(string nodeId, string transferId, int fileIndex, string status) => _clip.OnFileResultReceived(nodeId, transferId, fileIndex, status);
    void IHostLinkCallbacks.OnFileCompleteReceived(string nodeId, string transferId, bool success) => _clip.OnFileCompleteReceived(nodeId, transferId, success);

    public bool IsWindowHiddenInTray { get; set; } = false;
    public Action<string, bool>? OnNodeAudioActivityChanged { get; set; }
    public Action<bool>? OnFocusMicAction { get; set; }

    void IHostLinkCallbacks.OnAudioActivityReported(string nodeId, bool active)
    {
        try { OnNodeAudioActivityChanged?.Invoke(nodeId, active); } catch { }
    }

    int IHostLinkCallbacks.GetAdaptivePingIntervalMs(string nodeId)
    {
        if (!Config.AdaptivePingEnabled)
        {
            return Math.Clamp(Config.PingActiveMs, 500, 10000);
        }

        // v1.8 C-lean (feasibility 4.6): node policy=view/clipboard ไม่เคยรับ input —
        // ping ที่จังหวะประหยัดสุดเท่าที่ config ให้ (telemetry ยังเดิน แค่ช้าลง)
        if (Sessions.TryGetValue(nodeId, out var policySess) &&
            !OmniPolicy.CanControl(policySess.Config.Policy))
        {
            return Math.Clamp(Config.PingTrayMs, 3000, 30000);
        }

        // 1. ถ้าหน้าต่างถูกซ่อนใน System Tray และไม่ได้สลับจอไปที่เครื่องลูก -> โหมดประหยัดพลังงานขั้นสุด
        if (IsWindowHiddenInTray && ActiveNodeId != nodeId)
        {
            return Math.Clamp(Config.PingTrayMs, 3000, 30000);
        }

        // 2. ถ้าโฟกัสอยู่บนเครื่องลูกข่ายตัวนี้ -> Real-time ping
        if (ActiveNodeId == nodeId)
        {
            return Math.Clamp(Config.PingActiveMs, 500, 5000);
        }

        // 3. ถ้าเมาส์อยู่ใกล้ขอบจอ (เตรียมสลับ) -> เร่ง Ping รอ
        if (_switch.IsNearEdge)
        {
            return Math.Min(1000, Math.Clamp(Config.PingActiveMs, 500, 5000));
        }

        // 4. กรณีทั่วไปขณะทำงานบน Host -> Idle ping
        return Math.Clamp(Config.PingIdleMs, 1000, 15000);
    }

    #endregion

    public void AddOrUpdateNode(NodeConfig cfg, string? token = null)
    {
        if (!string.IsNullOrEmpty(token)) cfg.AuthToken = token;
        lock (OmniConfig.NodesGate) // SV-M16: mutate ภายใต้ gate เดียวกับผู้อ่าน
        {
            var existing = Config.Nodes.FirstOrDefault(n => n.NodeId == cfg.NodeId || n.Ip == cfg.Ip);
            if (existing != null)
            {
                existing.Name = cfg.Name;
                existing.Ip = cfg.Ip;
                existing.Width = cfg.Width;
                existing.Height = cfg.Height;
                existing.Position = cfg.Position;
                existing.Enabled = true;
                if (!string.IsNullOrEmpty(cfg.AuthToken)) existing.AuthToken = cfg.AuthToken;
                cfg = existing;
            }
            else
            {
                Config.Nodes.Add(cfg);
            }
        }

        if (Sessions.TryRemove(cfg.NodeId, out var oldSession))
        {
            oldSession.Dispose();
        }

        var effectiveToken = !string.IsNullOrEmpty(cfg.AuthToken) ? cfg.AuthToken : (Config.AuthToken ?? "");
        Sessions[cfg.NodeId] = new RemoteNodeClient(
            cfg,
            effectiveToken,
            this);
        _onUiUpdate();
    }

    /// <summary>
    /// v1.7 C2/C3: เปลี่ยน policy ของ node — ปรับ config + แจ้ง agent ทันที (set_policy)
    /// และถ้าลดระดับขณะกำลังควบคุม node นั้น → คืนการควบคุมให้ host อัตโนมัติ
    /// </summary>
    public void SetNodePolicy(string nodeId, string policy)
    {
        var normalized = OmniPolicy.Normalize(policy);
        Sessions.TryGetValue(nodeId, out var sess);
        var cfg = sess?.Config ?? Config.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (cfg is null)
        {
            OmniLog.Warn($"[kvm] SetNodePolicy: ไม่พบ node '{nodeId}'");
            return;
        }

        lock (OmniConfig.NodesGate)
        {
            cfg.Policy = normalized;
        }

        if (ActiveNodeId == nodeId && !OmniPolicy.CanControl(normalized))
        {
            OmniLog.Warn($"[kvm] policy ของ {nodeId} ลดเป็น '{normalized}' ขณะควบคุมอยู่ → คืนการควบคุมอัตโนมัติ");
            sess?.SendTcp(new { type = "leave_screen" });
            sess?.SendUdp(OmniProtocol.PackReleaseAll());
            ActiveNodeId = null;
            _switch.ModMask = 0;
        }

        sess?.SendTcp(new { type = "set_policy", policy = normalized });
        OmniLog.Write("kvm", $"set node policy {nodeId} → '{normalized}'");
        _onUiUpdate();
    }

    /// <summary>
    /// ลบโหนดออกจากระบบ KvmHostService และตัดการเชื่อมต่อ (สำหรับลบอุปกรณ์ที่ไม่ใช้งาน)
    /// </summary>
    public bool RemoveNode(string nodeId)
    {
        lock (OmniConfig.NodesGate) // SV-M16
        {
            var node = Config.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
            if (node == null) return false;
            Config.Nodes.Remove(node);
        }

        if (Sessions.TryRemove(nodeId, out var sess))
        {
            try { sess.Dispose(); } catch { }
        }
        if (ActiveNodeId == nodeId)
        {
            ActiveNodeId = null;
            _switch.ModMask = 0;
        }
        _onUiUpdate();
        return true;
    }

    // v2.0: การติด/ถอด hook, message pump และ watchdog rehook ย้ายไปอยู่ที่ IHostInputGateway
    // ของแพลตฟอร์มนั้นๆ (Windows = WindowsHostInput) — พฤติกรรม/คำ log เดิม 100%

    /// <summary>facade คงเดิม (MainWindow/AddFirstNodeWizard เรียกผ่านนี้) — logic ย้ายไป NodeDeployService (godclass-K1)</summary>
    public (bool Ok, string Message) DeployRemoteAgent(NodeConfig ncfg) => _deploy.DeployRemoteAgent(ncfg);

    public void RefreshMonitors()
    {
        var list = _input.GetMonitors();

        if (list.Length > 0)
        {
            _monitors = list; // immutable swap — ผู้อ่าน capture reference เดิมไว้ได้ปลอดภัย
            VirtualLeft = list.Min(m => m.Left);
            VirtualTop = list.Min(m => m.Top);
            VirtualRight = list.Max(m => m.Right);
            VirtualBottom = list.Max(m => m.Bottom);

            var rightMon = list.OrderByDescending(m => m.Right).First();
            _switch.AnchorX = (rightMon.Left + rightMon.Right) / 2;
            _switch.AnchorY = (rightMon.Top + rightMon.Bottom) / 2;
        }
    }

    public int GetHostLangGroup() => _lang.GetHostLangGroup(); // godclass-K4
    public void SetWindowsLangGroupTracked(int group) => _lang.SetWindowsLangGroupTracked(group);
    public void CalibrateAllLanguages(int targetGroup) => _lang.CalibrateAllLanguages(targetGroup);
    public void TriggerLanguageToggle() => _lang.TriggerLanguageToggle();

    public bool SendNodeCommand(string ip, object payload)
    {
        foreach (var sess in Sessions.Values)
        {
            if (sess.Config.Ip == ip && sess.Connected)
            {
                return sess.SendTcp(payload);
            }
        }
        return false;
    }

    /// <summary>
    /// ดึง Local IP ของ Interface เครือข่ายที่เชื่อมต่อกับโหนดปลายทางนี้จริง (Dynamic Host IP Detection)
    /// เพื่อใช้ในสตรีมเสียง แทนการ Hardcode ค่า host_ip คงที่ใน Config
    /// </summary>
    public string? GetLocalEndpointIpForNode(string ip)
    {
        foreach (var sess in Sessions.Values)
        {
            if (sess.Config.Ip == ip && sess.Connected && !string.IsNullOrWhiteSpace(sess.LocalEndpointIp))
            {
                return sess.LocalEndpointIp;
            }
        }
        return null;
    }

    private void StartEdgeDwell(string targetId, ScreenEdge edge, ScreenBounds monitor, int coord, int delayMs)
    {
        lock (_dwellLock)
        {
            if (_dwellTargetId == targetId && _dwellEdge == edge)
            {
                _dwellLastY = coord;
                return;
            }

            _dwellTargetId = targetId;
            _dwellEdge = edge;
            _dwellMonitor = monitor;
            _dwellLastY = coord;

            _edgeDwellTimer?.Dispose();
            _edgeDwellTimer = new System.Threading.Timer(OnEdgeDwellTimeout, null, delayMs, Timeout.Infinite);
        }
    }

    private void CancelEdgeDwell()
    {
        lock (_dwellLock)
        {
            if (_dwellTargetId is not null)
            {
                _dwellTargetId = null;
                _edgeDwellTimer?.Dispose();
                _edgeDwellTimer = null;
            }
        }
    }

    private void OnEdgeDwellTimeout(object? state)
    {
        string? targetId;
        ScreenEdge edge;
        ScreenBounds monitor;

        lock (_dwellLock)
        {
            targetId = _dwellTargetId;
            edge = _dwellEdge;
            monitor = _dwellMonitor;
            _dwellTargetId = null;
            _edgeDwellTimer?.Dispose();
            _edgeDwellTimer = null;
        }

        if (targetId is null || ActiveNodeId is not null) return;

        // ดึงพิกัดเคอร์เซอร์ล่าสุดตรงๆ จาก OS
        if (!_input.GetCursorPos(out var ptX, out var ptY)) return;

        bool stillOnEdge = edge switch
        {
            ScreenEdge.Right => ptX >= monitor.Right - 4,
            ScreenEdge.Left => ptX <= monitor.Left + 4,
            ScreenEdge.Top => ptY <= monitor.Top - 4,
            ScreenEdge.Bottom => ptY >= monitor.Bottom - 4,
            _ => false
        };

        if (!stillOnEdge || EdgeStrategyHelper.IsInCornerDeadzone(ptX, ptY, monitor, Config))
        {
            _edgeStrategy.Reset();
            return;
        }

        if (ScreenLockBlocksEdgeSwitch() || ShouldBlockEdgeByFullscreen())
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _switch.EdgeSwitchBusy, 1, 0) != 0)
        {
            return;
        }

        // godclass-K6b: คำนวณพิกัด (pure, timer thread) แล้วโพสต์ — สลับจริงบน pump thread
        double dwellNormX, dwellNormY;
        if (edge == ScreenEdge.Top)
        {
            dwellNormX = Math.Clamp((double)(ptX - monitor.Left) / Math.Max(1, monitor.Right - monitor.Left - 1), 0.0, 1.0);
            dwellNormY = 1.0;
        }
        else if (edge == ScreenEdge.Bottom)
        {
            dwellNormX = Math.Clamp((double)(ptX - monitor.Left) / Math.Max(1, monitor.Right - monitor.Left - 1), 0.0, 1.0);
            dwellNormY = 0.0;
        }
        else if (edge == ScreenEdge.Left)
        {
            dwellNormX = 0.5;
            dwellNormY = Math.Clamp((double)(ptY - monitor.Top) / Math.Max(1, monitor.Bottom - monitor.Top - 1), 0.0, 1.0);
        }
        else
        {
            dwellNormX = 0.5;
            dwellNormY = Math.Clamp((double)(ptY - monitor.Top) / Math.Max(1, monitor.Bottom - monitor.Top - 1), 0.0, 1.0);
        }
        _pipeline.Post(InputPipeline.CmdType.SwitchDirect, targetId, normX: dwellNormX, normY: dwellNormY);
    }

    public void SwitchToNode(string nodeId, double normY)
    {
        SwitchToNode(nodeId, 0.5, normY);
    }

    public void SwitchToNode(string nodeId, double normX = 0.5, double normY = 0.5)
    {
        CancelEdgeDwell();
        if (ActiveNodeId == nodeId) return; // C3: อยู่จอเดิม — ไม่สลับซ้ำ
        if (!Sessions.TryGetValue(nodeId, out var sess) || !sess.Connected) return;
        // v1.7 C2: node ที่ policy ไม่ใช่ full = ไม่รับ input — การสลับจอ (ส่งมอบเมาส์/คีย์) จึงปฏิเสธ
        if (!OmniPolicy.CanControl(sess.Config.Policy))
        {
            OmniLog.Warn($"[kvm] ปฏิเสธสลับไป {nodeId} — policy='{sess.Config.Policy}' ไม่อนุญาตให้ควบคุม input");
            return;
        }
        RefreshMonitors();

        int targetClientLang;
        if (Config.LanguageMode == "global_calibrated")
        {
            targetClientLang = MasterLangGroup;
        }
        else
        {
            _switch.SavedHostLangBeforeClient = _input.GetForegroundLangGroup();
            targetClientLang = sess.LangGroup;
        }

        if (Config.AutoEngHostOnClient)
        {
            SetWindowsLangGroupTracked(0);
        }

        ActiveNodeId = nodeId;
        _switch.ModMask = 0;
        _switch.WinSwallowedOnRemote = false;
        _switch.WinForwardedToRemote = false;
        _switch.WinConsumedByCombo = false;
        _input.SetCursorPos(_switch.AnchorX, _switch.AnchorY);

        sess.RequestSetLanguage(targetClientLang, calibrate: true);
        sess.SendTcp(new
        {
            type = "enter_screen",
            position = sess.Config.Position,
            norm_x = normX,
            norm_y = normY,
            lang_group = targetClientLang,
            policy = OmniPolicy.Normalize(sess.Config.Policy), // v1.7 C3
            // A1: พารามิเตอร์การสลับจอ
            switch_delay_ms = Config.SwitchDelayMs,
            double_tap_ms = Config.DoubleTapMs,
            corner_size = Config.CornerSize,
            corners = new Dictionary<string, bool>
            {
                ["tl"] = Config.CornerTopLeft,
                ["tr"] = Config.CornerTopRight,
                ["bl"] = Config.CornerBottomLeft,
                ["br"] = Config.CornerBottomRight,
            },
            // A2: สถานะล็อกปัจจุบัน
            locked = LockedToScreen,
        });
        // Clipboard ข้ามเครื่อง (แบบ Deskflow): เข้าเครื่องลูก = ของ Windows วางบน Linux
        _clip.TryPushClipboardTo(sess);

        _switch.ReturnToHostTick = 0;
        if (Config.SmartMicFollowFocus && sess.Config.AudioMicEnabled)
        {
            try { OnFocusMicAction?.Invoke(true); } catch { }
        }

        OmniLog.Write("kvm", $"switch_to_node node={nodeId} lang={targetClientLang}");
        // P0 hint: บอกทางหนีทุกครั้งที่สลับครั้งแรกของโปรแกรม (บทเรียนจากเหตุเมาส์ติด 2026-10-17)
        if (!_switchHintShown)
        {
            _switchHintShown = true;
            OmniLog.Write("kvm", "hint: ลากเมาส์ดันขอบจอฝั่งตรงข้ามเพื่อกลับ • ฉุกเฉิน = Ctrl+Alt+Backspace (ฝั่ง Host) หรือ Ctrl+Alt+Shift+Q (ฝั่ง Linux)");
        }
        _onUiUpdate();
    }

    private void OnEdgeReturnFromClient(string nodeId, string edge, double normY, double normX = 0.5)
    {
        // INCIDENT#2: log ตั้งแต่บรรทัดแรก — ให้เห็นว่าข้อความ "มาถึง" host หรือไม่
        // และถ้าไม่ match ก็รู้ทันทีว่าทิ้งเพราะอะไร (เดิม return เงียบ)
        if (ActiveNodeId != nodeId)
        {
            OmniLog.Warn($"[kvm] edge_return มาถึงแต่ทิ้ง (node={nodeId}, ActiveNodeId={ActiveNodeId ?? "null"})");
            return;
        }
        Sessions.TryGetValue(nodeId, out var sess);
        var pos = sess?.Config.Position?.ToLowerInvariant() ?? "right";

        ActiveNodeId = null;
        if (Config.SmartMicFollowFocus)
        {
            _switch.ReturnToHostTick = Environment.TickCount64;
        }
        _switch.ModMask = 0;
        _switch.WinSwallowedOnRemote = false;
        _switch.WinForwardedToRemote = false;
        _switch.WinConsumedByCombo = false;

        RefreshMonitors();
        var mons = _monitors;
        if (mons.Length == 0)
        {
            OmniLog.Warn("[kvm] edge return: ไม่พบข้อมูลจอภาพ — ยกเลิกการย้ายเคอร์เซอร์");
            return;
        }
        int targetX, targetY;
        if (pos == "right")
        {
            var rightMon = mons.OrderByDescending(m => m.Right).First();
            targetX = rightMon.Right - 6;
            targetY = (int)(rightMon.Top + normY * Math.Max(1, rightMon.Bottom - rightMon.Top - 1));
        }
        else if (pos == "top")
        {
            var topMon = mons.OrderBy(m => m.Top).First();
            targetX = (int)(topMon.Left + normX * Math.Max(1, topMon.Right - topMon.Left - 1));
            targetY = topMon.Top + 6;
        }
        else if (pos == "bottom")
        {
            var bottomMon = mons.OrderByDescending(m => m.Bottom).First();
            targetX = (int)(bottomMon.Left + normX * Math.Max(1, bottomMon.Right - bottomMon.Left - 1));
            targetY = bottomMon.Bottom - 6;
        }
        else
        {
            var leftMon = mons.OrderBy(m => m.Left).First();
            targetX = leftMon.Left + 6;
            targetY = (int)(leftMon.Top + normY * Math.Max(1, leftMon.Bottom - leftMon.Top - 1));
        }

        _input.SetCursorPos(targetX, targetY);

        _lang.Strategy.HandleEdgeReturnToHost(nodeId, this);

        OmniLog.Write("kvm", $"edge_return_to_host from={nodeId}");
        _onUiUpdate();
    }

    private void UpdateModMask(uint vk, bool isDown)
    {
        byte bit = vk switch
        {
            0xA0 or 0x10 => OmniProtocol.ModLShift,
            0xA1 => OmniProtocol.ModRShift,
            0xA2 or 0x11 => OmniProtocol.ModLCtrl,
            0xA3 => OmniProtocol.ModRCtrl,
            0xA4 or 0x12 => OmniProtocol.ModLAlt,
            0xA5 => OmniProtocol.ModRAlt,
            0x5B => OmniProtocol.ModLSuper,
            0x5C => OmniProtocol.ModRSuper,
            _ => 0,
        };

        if (vk is 0x5B or 0x5C)
        {
            _switch.WinHeld = isDown;
        }

        if (bit != 0)
        {
            if (isDown) _switch.ModMask |= bit;
            else _switch.ModMask &= (byte)~bit;
        }
    }

    /// <summary>Alt+Shift trigger: กด Shift ต่อจาก Alt หรือกด Alt ต่อจาก Shift = ตั้งใจสลับภาษา</summary>
    internal static bool IsAltShiftLanguageTrigger(uint vk, bool altHeld, bool shiftHeld)
    {
        var isShift = vk is 0x10 or 0xA0 or 0xA1;
        var isAlt = vk is 0x12 or 0xA4 or 0xA5;
        return (isShift && altHeld) || (isAlt && shiftHeld);
    }

    /// <summary>
    /// เหตุการณ์คีย์บอร์ดจาก input gateway (เดิมคือ LL hook callback ตรงๆ) —
    /// คืน true = กลืนปุ่มไม่ส่งต่อให้ OS · คืน false = ให้ OS ไปต่อตามปกติ
    /// </summary>
    private bool OnKeyHook(KeyHookData kb)
    {
        try
        {
            {
                var isDown = kb.IsDown;
                var isUp = kb.IsUp;

                if (!kb.IsInjected)
                {
                    if (isDown || isUp)
                    {
                        UpdateModMask(kb.VkCode, isDown);
                    }

                    // v1.9 C1: สูตรลัดผู้ใช้ → action — lookup เล็กมาก ปลอดภัยบน LL hook
                    // (งานจริงยิงออก Task.Run ตาม INV-5; กลืนปุ่มไม่ให้ OS เห็น เหมือน Scroll Lock)
                    if (isDown)
                    {
                        var hotkeyIdx = _hotkeys.Index;
                        if (hotkeyIdx is not null && hotkeyIdx.Count > 0 &&
                            hotkeyIdx.TryGetValue(kb.VkCode, out var rules) &&
                            rules.Any(r => HotkeyKey.Matches(r.Mask, _switch.ModMask)))
                        {
                            var captured = rules.Where(r => HotkeyKey.Matches(r.Mask, _switch.ModMask))
                                                .SelectMany(r => r.Actions).ToList();
                            _pipeline.Post(InputPipeline.CmdType.HotkeyActions, payload: captured); // godclass-K6c (INV-5: hook ไม่ทำงานหนักเอง)
                            return true;
                        }
                    }

                    var activeId = ActiveNodeId;

                    // =========================================================================
                    // CASE 1: Controlling the Windows Host (activeId is null)
                    // =========================================================================
                    if (activeId is null)
                    {
                        // 1A. Win + Space on Host: NEVER swallow! Let Windows handle Win+Space
                        //     with 0ms native latency and zero Start Menu popups.
                        if (isDown && kb.VkCode == 0x20 && _switch.WinHeld)
                        {
                            _switch.IgnoreHostLangPollUntilTick = 0;
                            _lang.Wake();
                            return false;
                        }

                        // 1C. Scroll Lock = ล็อกเมาส์ไว้จอปัจจุบัน (A2) — ใช้ได้ทั้งตอนอยู่ host และ remote
                    if (isDown && kb.VkCode == 0x91 && Config.LockToScreenEnabled)
                    {
                        ToggleScreenLock();
                        return true;
                    }

                    // 1B. Grave Accent `~` (scanCode 0x29 without modifiers) on Host:
                        if (Config.GraveSwitchEnabled && kb.ScanCode == 0x29)
                        {
                            if (isDown && _switch.ModMask == 0)
                            {
                                _switch.GraveSwallowedDown = true;
                                _pipeline.Post(InputPipeline.CmdType.LanguageToggle); // godclass-K6c
                                return true;
                            }
                            if (isUp && _switch.GraveSwallowedDown)
                            {
                                _switch.GraveSwallowedDown = false;
                                return true;
                            }
                        }

                        return false;
                    }

                    // =========================================================================
                    // CASE 2: Controlling a Remote Linux Client (activeId is not null)
                    // =========================================================================
                    if (!Sessions.TryGetValue(activeId, out var sess) || !sess.Connected)
                    {
                        // INCIDENT#2: จุดเคลียร์ ActiveNodeId "เงียบ" — เคยทำให้วินิจฉัยไม่เจอ
                        OmniLog.Warn("[kvm] ActiveNodeId เคลียร์ (keyboard hook: session disconnected)");
                        ActiveNodeId = null;
                        return false;
                    }
                    // v1.7 C2: policy ถูกลดระดับขณะควบคุมอยู่ = คืนการควบคุมทันที (กัน input หลุดเกินสิทธิ์)
                    if (!OmniPolicy.CanControl(sess.Config.Policy))
                    {
                        OmniLog.Warn($"[kvm] policy ของ {activeId} ไม่อนุญาต input แล้ว → คืนการควบคุมอัตโนมัติ (keyboard path)");
                        sess.SendUdp(OmniProtocol.PackReleaseAll());
                        ActiveNodeId = null;
                        _switch.ModMask = 0;
                        return false;
                    }

                    // 2A. Panic Return: Ctrl + Alt + Backspace
                    var ctrlDown = (_switch.ModMask & (OmniProtocol.ModLCtrl | OmniProtocol.ModRCtrl)) != 0;
                    var altDown = (_switch.ModMask & (OmniProtocol.ModLAlt | OmniProtocol.ModRAlt)) != 0;
                    if (isDown && kb.VkCode == 0x08 && ctrlDown && altDown)
                    {
                        if (LockedToScreen)
                        {
                            LockedToScreen = false; // ฉุกเฉินปลดล็อกเสมอ
                            OmniLog.Write("kvm", "panic → ปลดล็อกจออัตโนมัติ");
                        }
                        // KV-3/PY-F8 fix: แจ้ง agent ให้เคลียร์ active_focus (เดิมส่งแค่ release ผ่าน UDP —
                        // agent ติดสถานะ Active ค้าง, edge detection ยัง armed จนกว่าจะกด hotkey ฝั่งลูก)
                        // godclass-K6c-fix: SendTcp = SslStream.Write ใต้ _tcpLock **ไม่มี timeout** —
                        // ถ้า connection ค้าง callback บล็อกเป็นวินาที → LL hook timeout → Windows ถอด hook
                        // (ต้นตอ "hook เงียบ 16 วิ" 27 ต.ค.) — hook thread ห้าม block: โพสต์งาน network ออกไปทำ
                        var leaveSess = sess;
                        Task.Run(() =>
                        {
                            try { leaveSess.SendTcp(new { type = "leave_screen" }); } catch { }
                            try { leaveSess.SendUdp(OmniProtocol.PackReleaseAll()); } catch { }
                        });
                        _pipeline.Post(InputPipeline.CmdType.EdgeReturn, activeId, "left", 0, 0.5); // godclass-K6b
                        return true;
                    }

                    // 2A-bis: Scroll Lock toggle ระหว่างควบคุม remote
                    if (isDown && kb.VkCode == 0x91 && Config.LockToScreenEnabled)
                    {
                        ToggleScreenLock();
                        var lockSess = sess;
                        var lockOn = LockedToScreen;
                        Task.Run(() => { try { lockSess.SendTcp(new { type = "lock", on = lockOn }); } catch { } }); // godclass-K6c-fix
                        return true;
                    }

                    // 2B. LWin / RWin handling on Remote Linux Client:
                    //     Hold Super_L back until we know whether user is pressing Win+Space
                    //     (language toggle) or another Win+Key shortcut, preventing both Windows
                    //     Start Menu and Ubuntu GNOME Activities Overview from popping up!
                    if (kb.VkCode is 0x5B or 0x5C)
                    {
                        var winX11Kc = kb.VkCode == 0x5C ? 134 : 133;
                        if (isDown)
                        {
                            if (!_switch.WinSwallowedOnRemote)
                            {
                                _switch.WinSwallowedOnRemote = true;
                                _switch.WinForwardedToRemote = false;
                                _switch.WinConsumedByCombo = false;
                            }
                            return true;
                        }
                        if (isUp)
                        {
                            if (_switch.WinForwardedToRemote)
                            {
                                sess.SendKeyEvent(winX11Kc, false, _switch.ModMask);
                            }
                            else if (!_switch.WinConsumedByCombo)
                            {
                                // User tapped and released Super key alone on Linux
                                sess.SendKeyEvent(winX11Kc, true, OmniProtocol.ModLSuper);
                                sess.SendKeyEvent(winX11Kc, false, 0);
                            }
                            _switch.WinSwallowedOnRemote = false;
                            _switch.WinForwardedToRemote = false;
                            _switch.WinConsumedByCombo = false;
                            return true;
                        }
                    }

                    // 2C. Win + Space OR Grave Accent `~` while controlling Remote Linux Client:
                    var isWinSpaceRemote = kb.VkCode == 0x20 && _switch.WinHeld;
                    var isGraveRemote = Config.GraveSwitchEnabled && kb.ScanCode == 0x29 && (_switch.ModMask == 0 || _switch.GraveSwallowedDown);

                    if (isWinSpaceRemote)
                    {
                        _switch.WinConsumedByCombo = true;
                        if (_switch.WinForwardedToRemote)
                        {
                            sess.SendKeyEvent(133, false, 0);
                            _switch.WinForwardedToRemote = false;
                        }
                        if (isDown)
                        {
                            _pipeline.Post(InputPipeline.CmdType.LanguageToggle); // godclass-K6c
                        }
                        return true;
                    }

                    if (isGraveRemote)
                    {
                        if (isDown && _switch.ModMask == 0)
                        {
                            _switch.GraveSwallowedDown = true;
                            _pipeline.Post(InputPipeline.CmdType.LanguageToggle); // godclass-K6c
                            return true;
                        }
                        if (isUp && _switch.GraveSwallowedDown)
                        {
                            _switch.GraveSwallowedDown = false;
                            return true;
                        }
                    }

                    // 2C-bis: Alt+Shift — นิสัยสลับภาษาคลาสสิกของคนไทย (เดสก์ท็อป Linux ไม่ตอบสนอง
                    // synthetic Alt+Shift ที่เรา forward ไป = ผู้ใช้กดแล้วเงียบ) → กลืนแล้ว toggle เอง
                    if (isDown && IsAltShiftLanguageTrigger(kb.VkCode,
                            altDown, (_switch.ModMask & (OmniProtocol.ModLShift | OmniProtocol.ModRShift)) != 0))
                    {
                        OmniLog.Write("lang", "Alt+Shift toggle (remote)");
                        _pipeline.Post(InputPipeline.CmdType.LanguageToggle); // godclass-K6c
                        return true;
                    }

                    // 2D. Any other key while LWin is held on Remote Linux Client:
                    //     Flush the deferred Super_L key-down before sending the combo key.
                    if (isDown && _switch.WinHeld && _switch.WinSwallowedOnRemote && !_switch.WinForwardedToRemote)
                    {
                        _switch.WinForwardedToRemote = true;
                        _switch.WinConsumedByCombo = true;
                        sess.SendKeyEvent(133, true, _switch.ModMask);
                    }

                    // gateway ของ X11 แนบ keycode จริงมา → ใช้ตรงๆ; Windows = 0 → แปลงจาก VK/scan เหมือนเดิม
                    var x11Kc = kb.X11Keycode != 0
                        ? (int)kb.X11Keycode
                        : OmniProtocol.Win32ToX11Keycode(kb.VkCode, kb.ScanCode, kb.IsExtended);
                    // v1.9 C2: แม็ป modifier ต่อเครื่อง — แปลงก่อนส่ง (คีย์ปกติ/keyspace ไทยผ่านทันที ไม่แตะ INV-2/11)
                    x11Kc = OmniModifiers.RemapKeycode(x11Kc, sess.Config.ModifierMap);
                    if (x11Kc != 0)
                    {
                        sess.SendKeyEvent(x11Kc, isDown, _switch.ModMask);
                    }
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>
    /// เหตุการณ์เมาส์จาก input gateway (เดิมคือ LL hook callback ตรงๆ) —
    /// probe สังเคราะห์ถูก gateway กลั้นไว้ก่อนถึงตรงนี้ · คืน true = กลืน (กำลังคุม remote)
    /// </summary>
    private bool OnMouseHook(MouseHookData ev)
    {
        _mouseEventQueue.Writer.TryWrite(ev);
        return ActiveNodeId is not null;
    }

    private void MouseWorkerLoop()
    {
        var reader = _mouseEventQueue.Reader;
        while (_running)
        {
            try
            {
                // godclass-G7 (root-cause fix 28 ต.ค.): WaitToReadAsync แบบ block ไม่จำกัดเวลา
                // ทำให้คำสั่ง SwitchDirect ที่โพสต์ตอนเมาส์นิ่ง "ค้างจนกว่าเมาส์จะขยับครั้งต่อไป"
                // = delay หลักวินาที-หลักสิบวินาทีที่ผู้ใช้เจอ — จำกัดรอ 50ms แล้ว drain คำสั่งเสมอ
                string waitErr;
                bool hasItems;
                using (var waitCts = new CancellationTokenSource(50))
                {
                    try
                    {
                        var vt = reader.WaitToReadAsync(waitCts.Token);
                        hasItems = vt.IsCompleted ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        hasItems = false; // ครบ 50ms — drain คำสั่งแล้ววนใหม่ (CPU แทบไม่ใช้)
                    }
                    catch (Exception ex)
                    {
                        waitErr = ex.Message;
                        OmniLog.Warn($"[kvm] mouse queue wait error: {waitErr}");
                        Thread.Sleep(200);
                        continue;
                    }
                }
                if (!hasItems)
                {
                    _pipeline.Drain(HandlePipelineCommand);
                    continue;
                }

                // godclass-K6b: drain คำสั่ง switching ก่อนเมาส์เสมอ — state ถูกแตะบน thread เดียว
                _pipeline.Drain(HandlePipelineCommand);

                while (reader.TryRead(out var ev))
                {
                    ProcessSingleMouseEvent(ev);
                }

                // godclass-K6b fix: drain รอบปิด — ถ้าผู้ใช้ดันขอบแล้วหยุดเมาส์ คำสั่ง SwitchDirect
                // ที่โพสต์ระหว่าง batch นี้ต้องถูกทำทันที (ไม่รอ mouse event ถัดไป)
                _pipeline.Drain(HandlePipelineCommand);
            }
            catch (Exception ex)
            {
                if (!_running) break;
                OmniLog.Warn($"[kvm] MouseWorkerLoop warning: {ex.Message}");
            }
        }
    }

    private void ProcessSingleMouseEvent(in MouseHookData ev)
    {
        var activeId = ActiveNodeId;
        var msg = ev.Message;
        var x = ev.X;
        var y = ev.Y;

        if (activeId is null)
        {
            if (msg == MouseInputMessage.Move)
            {
                var mons = _monitors;
                if (mons.Length > 0)
                {
                    _switch.IsNearEdge = (x <= mons[0].Left + 50 || x >= mons[^1].Right - 50);
                }
                CheckEdgeSwitch(x, y);
            }
        }
        else
        {
            if (!Sessions.TryGetValue(activeId, out var sess) || !sess.Connected)
            {
                // INCIDENT#2: จุดเคลียร์ ActiveNodeId "เงียบ" — เคยทำให้วินิจฉัยไม่เจอ
                OmniLog.Warn("[kvm] ActiveNodeId เคลียร์ (mouse hook: session disconnected)");
                ActiveNodeId = null;
                return;
            }
            // v1.7 C2: policy ลดระดับขณะคุม (mouse path) — หยุดส่ง input ทันที
            if (!OmniPolicy.CanControl(sess.Config.Policy))
            {
                OmniLog.Warn($"[kvm] policy ของ {activeId} ไม่อนุญาต input แล้ว → คืนการควบคุมอัตโนมัติ (mouse path)");
                sess.SendUdp(OmniProtocol.PackReleaseAll());
                ActiveNodeId = null;
                _switch.ModMask = 0;
                return;
            }

            switch (msg)
            {
                case MouseInputMessage.Move:
                    var dx = x - _switch.AnchorX;
                    var dy = y - _switch.AnchorY;
                    if (dx != 0 || dy != 0)
                    {
                        sess.SendMouseRel(dx, dy, _switch.ModMask);
                        _input.SetCursorPos(_switch.AnchorX, _switch.AnchorY);
                    }
                    break;

                case MouseInputMessage.LeftDown:
                    sess.SendMouseButton(1, true, _switch.ModMask);
                    break;
                case MouseInputMessage.LeftUp:
                    sess.SendMouseButton(1, false, _switch.ModMask);
                    break;
                case MouseInputMessage.MiddleDown:
                    sess.SendMouseButton(2, true, _switch.ModMask);
                    break;
                case MouseInputMessage.MiddleUp:
                    sess.SendMouseButton(2, false, _switch.ModMask);
                    break;
                case MouseInputMessage.RightDown:
                    sess.SendMouseButton(3, true, _switch.ModMask);
                    break;
                case MouseInputMessage.RightUp:
                    sess.SendMouseButton(3, false, _switch.ModMask);
                    break;
                case MouseInputMessage.XDown:
                    var xbtnDown = ((ev.MouseData >> 16) & 0xFFFF) == 1 ? (byte)8 : (byte)9;
                    sess.SendMouseButton(xbtnDown, true, _switch.ModMask);
                    break;
                case MouseInputMessage.XUp:
                    var xbtnUp = ((ev.MouseData >> 16) & 0xFFFF) == 1 ? (byte)8 : (byte)9;
                    sess.SendMouseButton(xbtnUp, false, _switch.ModMask);
                    break;
                case MouseInputMessage.Wheel:
                    var wheelDelta = (short)((ev.MouseData >> 16) & 0xFFFF);
                    sess.SendMouseWheel(0, wheelDelta, _switch.ModMask);
                    break;
                case MouseInputMessage.HWheel:
                    var hwheelDelta = (short)((ev.MouseData >> 16) & 0xFFFF);
                    sess.SendMouseWheel(hwheelDelta, 0, _switch.ModMask);
                    break;
            }
        }
    }

    /// <summary>เช็คว่า id ควบคุมได้จริง (connected + enabled + full policy) — ใช้ต่อจากกราฟสำหรับ corner diagonal</summary>
    private bool NeighborOrNullForId(TopologyGraph graph, string id)
    {
        if (id == TopologyHost.Id) return true;
        return Sessions.TryGetValue(id, out var ns) && ns.Connected &&
               ns.Config.Enabled && OmniPolicy.CanControl(ns.Config.Policy);
    }

    /// <summary>
    /// v1.9.1: สลับเชิงมุมทแยง — เข้าโซนมุมที่ตั้งไว้ = สลับทันทีไปหลาน (bypass strategy/dwell
    /// เพราะ corner deadzone เดิมจะบล็อก; โซนนี้เปิดเองใน Settings → Switching)
    /// วางเคอร์เซอร์บนเครื่องปลายทางที่มุม "ตรงข้าม" เพื่อให้ลากต่อได้เนียน
    /// </summary>
    private void HandleCornerDiagonalHit(string targetId, TopologyCorner corner)
    {
        if (ScreenLockBlocksEdgeSwitch() || ShouldBlockEdgeByFullscreen())
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _switch.EdgeSwitchBusy, 1, 0) != 0)
        {
            return;
        }
        _edgeStrategy.Reset();
        CancelEdgeDwell();
        var (normX, normY) = corner switch
        {
            TopologyCorner.TopRight => (1.0, 0.0),
            TopologyCorner.TopLeft => (0.0, 0.0),
            TopologyCorner.BottomRight => (1.0, 1.0),
            _ => (0.0, 1.0),
        };
        // godclass-K6b: ผู้เรียก (CheckEdgeSwitch) อยู่บน pump แล้ว — สลับ inline
        try
        {
            SwitchToNode(targetId, normX, normY);
            _eventHub.Publish(new StatusBannerEvent(Loc.T("banner.corner.switched", "สลับมุมทแยงไปยัง {0}", targetId)));
        }
        finally
        {
            Interlocked.Exchange(ref _switch.EdgeSwitchBusy, 0);
        }
    }

    private long _edgeDbgTick;

    private void CheckEdgeSwitch(int x, int y)
    {
        // KV-2 fix: ฟังก์ชันนี้รัน "ต่อทุก mouse move" (1000Hz mouse = ~1000 ครั้ง/วิ) —
        // ห้ามมี LINQ alloc (closure/delegate ต่อครั้ง) และต้อง early-return ให้เร็วที่สุด
        if (Sessions.IsEmpty) return; // ไม่มี node เลย = ไม่มีทางสลับ จบตรงนี้
        if (SubActiveFocusProbe?.Invoke() == true)
        {
            CancelEdgeDwell();
            _edgeStrategy.Reset();
            return; // กันลูป host↔host: เครื่องนี้กำลังถูกคุมอยู่ — พัก edge ของ host-role
        }

        var mons = _monitors; // C2: capture ครั้งเดียว ใช้ตลอดการตัดสินใจ
        if (mons.Length == 0) return;
        if (Environment.TickCount64 - _edgeDbgTick > 500)
        {
            _edgeDbgTick = Environment.TickCount64;
            OmniLog.Write("edgedbg", $"CheckEdge x={x} y={y} mons={string.Join("|", mons.Select(m => $"{m.Left}..{m.Right}"))} active={ActiveNodeId ?? "host"}");
        }

        // 1. หาว่าเคอร์เซอร์อยู่บนจอใด (หรือใกล้จอใดที่สุด — for-loop แทน OrderBy)
        ScreenBounds curMon = default;
        bool found = false;
        long bestDist = long.MaxValue;
        foreach (var m in mons)
        {
            if (x >= m.Left && x < m.Right && y >= m.Top && y < m.Bottom)
            {
                curMon = m;
                found = true;
                break;
            }
        }
        if (!found)
        {
            foreach (var m in mons)
            {
                var d = Math.Abs((long)x - (m.Left + m.Right) / 2) + Math.Abs((long)y - (m.Top + m.Bottom) / 2);
                if (d < bestDist)
                {
                    bestDist = d;
                    curMon = m;
                }
            }
        }
        var monBounds = curMon;

        // 2. ตรวจสอบขอบนอกสุดของจอภาพ (tolerance 4px ป้องกัน DPI หรือ taskbar flick พลาดขอบ)
        //    + เช็คจอเพื่อนบ้านในลูปเดียว (เดิม: mons.Any(...) ×4 = closure alloc 4 ชุดต่อ move)
        const int tol = 4;
        var atRight = x >= curMon.Right - tol;
        var atLeft = x <= curMon.Left + tol;
        var atTop = y <= curMon.Top + tol;
        var atBottom = y >= curMon.Bottom - tol;
        if (mons.Length > 1 && (atRight || atLeft || atTop || atBottom))
        {
            foreach (var m in mons)
            {
                var inX = x >= m.Left && x < m.Right;
                var inY = y >= m.Top && y < m.Bottom;
                if (atRight && m.Left >= curMon.Right - 10 && inY) atRight = false;
                if (atLeft && m.Right <= curMon.Left + 10 && inY) atLeft = false;
                if (atTop && m.Bottom <= curMon.Top + 10 && inX) atTop = false;
                if (atBottom && m.Top >= curMon.Bottom - 10 && inX) atBottom = false;
            }
        }

        // 3. v1.9.1: หาปลายทางจาก "กราฟ topology" แทน slot ตาย — node ต่อเครื่องลูกได้ไม่จำกัด
        //    (GetNeighbor อ่านจากกราฟที่ build จาก anchor+position; node ปิด/ไม่เต็มสิทธิ์ = ไม่ถือเป็นเพื่อนบ้าน)
        var graph = _topo.CurrentOrTemp();
        string? NeighborOrNull(ScreenEdge e)
        {
            var id = graph.GetNeighbor(TopologyHost.Id, e);
            if (id is null) return null;
            return Sessions.TryGetValue(id, out var ns) && ns.Connected &&
                   ns.Config.Enabled && OmniPolicy.CanControl(ns.Config.Policy)
                ? id : null;
        }
        string? topTarget = NeighborOrNull(ScreenEdge.Top);
        string? bottomTarget = NeighborOrNull(ScreenEdge.Bottom);
        string? rightTarget = NeighborOrNull(ScreenEdge.Right);
        string? leftTarget = NeighborOrNull(ScreenEdge.Left);

        // 3b. v1.9.1: สลับเชิงมุมทแยง — มุมที่มี "หลาน" (เช่น A บนของ L440 เมื่อ L440 ขวาของ host)
        //     เข้าโซนมุม (CornerSwitchPx) = สลับทันทีไปหลาน (bypass corner deadzone — ผู้ใช้เปิดเองใน Settings)
        if (Config.CornerSwitchEnabled && (atTop || atBottom) && (atLeft || atRight))
        {
            var corner =
                (atTop, atRight) == (true, true) ? TopologyCorner.TopRight :
                (atTop, atLeft) == (true, true) ? TopologyCorner.TopLeft :
                (atBottom, atRight) == (true, true) ? TopologyCorner.BottomRight :
                TopologyCorner.BottomLeft;
            var radius = Math.Clamp(Config.CornerSwitchPx, 16, 200);
            var cornerX = corner is TopologyCorner.TopRight or TopologyCorner.BottomRight ? curMon.Right : curMon.Left;
            var cornerY = corner is TopologyCorner.TopLeft or TopologyCorner.TopRight ? curMon.Top : curMon.Bottom;
            if (Math.Abs(x - cornerX) <= radius && Math.Abs(y - cornerY) <= radius)
            {
                var diag = graph.GetDiagonal(TopologyHost.Id, corner);
                if (diag is not null && NeighborOrNullForId(graph, diag))
                {
                    HandleCornerDiagonalHit(diag, corner);
                    return;
                }
            }
        }

        // 4. สลับหน้าจอตามทิศทางที่มีโหนดรอรับ
        if (atTop && topTarget != null)
        {
            HandleEdgeHit(topTarget, ScreenEdge.Top, monBounds, x, y, isHorizontal: true);
        }
        else if (atBottom && bottomTarget != null)
        {
            HandleEdgeHit(bottomTarget, ScreenEdge.Bottom, monBounds, x, y, isHorizontal: true);
        }
        else if (atRight && rightTarget != null)
        {
            HandleEdgeHit(rightTarget, ScreenEdge.Right, monBounds, x, y, isHorizontal: false);
        }
        else if (atLeft && leftTarget != null)
        {
            HandleEdgeHit(leftTarget, ScreenEdge.Left, monBounds, x, y, isHorizontal: false);
        }
        else
        {
            if (Environment.TickCount64 - _edgeDbgTick > 500)
            {
                _edgeDbgTick = Environment.TickCount64;
                OmniLog.Write("edgedbg", $"no-target: atR={atRight} atL={atLeft} rT={rightTarget != null} lT={leftTarget != null} tT={topTarget != null} bT={bottomTarget != null}");
            }
            CancelEdgeDwell();
            _edgeStrategy.Reset();
        }
    }

    private void HandleEdgeHit(string targetId, ScreenEdge edge, ScreenBounds monBounds, int x, int y, bool isHorizontal)
    {
        OmniLog.Write("edgedbg", $"HandleEdgeHit target={targetId} edge={edge}");
        if (ScreenLockBlocksEdgeSwitch() || ShouldBlockEdgeByFullscreen())
        {
            CancelEdgeDwell();
            return;
        }

        if (_edgeStrategy.ShouldSwitch(x, y, edge, monBounds, Config))
        {
            CancelEdgeDwell();
            if (Interlocked.CompareExchange(ref _switch.EdgeSwitchBusy, 1, 0) == 0)
            {
                // godclass-K6b: คำนวณพิกัด (pure) แล้วโพสต์ — สลับจริงบน pump thread
                double dwellNormX, dwellNormY;
                if (isHorizontal)
                {
                    dwellNormX = Math.Clamp((double)(x - monBounds.Left) / Math.Max(1, monBounds.Right - monBounds.Left - 1), 0.0, 1.0);
                    dwellNormY = edge == ScreenEdge.Top ? 1.0 : 0.0;
                }
                else
                {
                    dwellNormX = 0.5;
                    dwellNormY = Math.Clamp((double)(y - monBounds.Top) / Math.Max(1, monBounds.Bottom - monBounds.Top - 1), 0.0, 1.0);
                }
                _pipeline.Post(InputPipeline.CmdType.SwitchDirect, targetId, normX: dwellNormX, normY: dwellNormY);
            }
        }
        else if (Config.SwitchDelayMs > 0 && !EdgeStrategyHelper.IsInCornerDeadzone(x, y, monBounds, Config))
        {
            StartEdgeDwell(targetId, edge, monBounds, isHorizontal ? x : y, Config.SwitchDelayMs);
        }
    }

    public void Dispose()
    {
        _running = false;
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
        _knock.Dispose();
        _mouseEventQueue.Writer.TryComplete();
        CancelEdgeDwell();
        _lang.Wake();
        ActiveNodeId = null;
        _input.Stop();
        foreach (var sess in Sessions.Values)
        {
            sess.Dispose();
        }
    }
}
