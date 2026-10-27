using System.Text.Json;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;
using OmniLink.Core.Ipc;

namespace OmniLink.Headless;

/// <summary>
/// v2.0: OmniLink host แบบไม่มี UI (daemon) — รันบน Linux (Ubuntu/Mint/Lubuntu 22.04+) หรือ Windows
/// v2.1 S1 (Engine/UI split): hostd บน Windows = engine เต็มรูปแบบ (hook/edge-switch จริง, clipboard,
/// audio hub, discovery, sub-mode) — parity กับ WPF host ตามแผน _lessons/war-room/BACKLOG-ROADMAP §1-S1
/// การตั้งค่า: ใช้ omnilink_config.json เดียวกับ host UI ทั้งหมด (root = OMNILINK_HOME
/// หรือโฟลเดอร์ที่มี config ข้าง exe — กติกา OmniPaths เดิม)
/// </summary>
internal static class Program
{
    private static volatile bool _stopping;
    private static KvmHostService? _kvm;
    private static OmniLink.Core.NodeAgentServer? _sub;
    private static System.Threading.Timer? _statusTimer;
    private static AudioHubService? _audio;          // Windows parity wiring เท่านั้น (Linux = null)
    private static IpcHostServer? _ipc;              // S2: ท่อควบคุมของ dashboard
#if OMNILINK_WINDOWS
    private static OmniDiscoveryService? _discovery;
#endif

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cmd = args.FirstOrDefault() ?? "run";

        try
        {
            return cmd switch
            {
                "run" => RunDaemon(),
                "status" => PrintStatus(),
                "version" => VersionStamp(),
                "-h" or "--help" or "help" => Help(),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hostd] fatal: {ex}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            omnilink-hostd — OmniLink host daemon (headless, v2.0)

            คำสั่ง:
              run        รัน host daemon (ค่าเริ่มต้น) — อ่าน omnilink_config.json ข้าง exe หรือ $OMNILINK_HOME
              status     พิมพ์สถานะจาก logs/status.json
              version    พิมพ์เวอร์ชัน
            """);
        return 0;
    }

    private static int VersionStamp()
    {
        Console.WriteLine($"omnilink-hostd {typeof(KvmHostService).Assembly.GetName().Version?.ToString(3) ?? "?"}");
        return 0;
    }

    private static int RunDaemon()
    {
        OmniLog.Configure();
        var cfg = OmniConfig.Load();
        OmniLimits.Normalize(cfg);
        cfg.Save();
        OmniLog.SetLevel(cfg.LogLevel);
        OmniLog.SetMaxLogBytes(cfg.LogMaxBytes);       // parity กับ UI (F2 รอบ v1.9)
        OmniLog.SetDiagnostics(cfg.DiagnosticsEnabled);

        OmniLog.Write("boot", $"omnilink-hostd starting pid={Environment.ProcessId} os={Environment.OSVersion.Platform} root={OmniPaths.Root}");

        // Low-Memory Profile (พกพา): ตรวจจาก /proc/meminfo (Linux) — Windows host UI ทำแบบเดียวกันใน Program.cs ฝั่ง UI
        var totalRam = TotalPhysicalMemoryBytes();
        var isLowRam = totalRam > 0 && totalRam <= 4_294_967_296UL;
        if (isLowRam || cfg.LowMemoryMode)
        {
            var applied = new List<string>();
            if (cfg.LowMemThreadPoolCap)
            {
                ThreadPool.SetMinThreads(2, 2);
                ThreadPool.SetMaxThreads(8, 8);
                applied.Add("ThreadPool 2-8");
            }
            if (cfg.LowMemGcBatch)
            {
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Batch;
                applied.Add("GCBatch");
            }
            OmniLog.Write("boot", $"[LowMem] profile ACTIVE mode={(isLowRam ? "auto(<=4GB)" : "manual")} (RAM: {totalRam / 1024 / 1024}MB) applied=[{string.Join(", ", applied)}]");
        }

        // อินพุต: Linux = X11 gateway (poll + grab) · Windows = WindowsHostInput จริง (S1: เดิม Null = ไม่มี edge-switch)
        IHostInputGateway input = CreateInputGateway();

        _kvm = new KvmHostService(cfg, static () => { }, null, input);
        // godclass-F3: pre-check ชนพอร์ตกับ node agent ก่อน bind — ข้อความชัดกว่ารอ SocketException ตอน Start
        if (cfg.SubModeEnabled && AgentPortInUse())
        {
            OmniLog.Warn("[hostd] sub_mode_enabled=true แต่พอร์ต 24801 ถูกถืออยู่ (น่าจะ node agent บนเครื่องเดียวกัน) → ข้าม sub-mode รอบนี้");
            cfg.SubModeEnabled = false;
        }
        else if (cfg.SubModeEnabled)
        {
            // S1: ทั้ง Linux (XTest) และ Windows (SendInput) มี injector ครบแล้ว — แชร์ gateway เดียวกับ host-role (ภาษา/จอเป็น machine-global)
            _sub = CreateSubServer(cfg, input);
            if (_sub is not null)
            {
                _sub.OnStatus = static msg => OmniLog.Write("sub", msg);
                _kvm.SubActiveFocusProbe = () => _sub.ActiveFocus; // กันลูป host<->host
                _sub.Start();
            }
            else
            {
                OmniLog.Warn("[hostd] sub_mode_enabled=true แต่แพลตฟอร์มนี้ไม่มี injector → ข้าม sub-mode รอบนี้");
            }
        }

        // S1 parity: clipboard เขียนเครื่องนี้จริง + audio hub + smart audio + hotkey audio + discovery (Windows)
        // ฝั่ง Linux คงพฤติกรรมเดิมทุกบรรทัด (stub log เหมือนเดิม — ยังไม่เคยทดสอบ live)
        WireEngineParity(cfg);

        if (cfg.PreventSleep)
        {
            EnablePreventSleep(); // v1.9 C4: เธรด keeper ค้าง ES_CONTINUOUS ตลอดที่เปิดใช้ (Windows เท่านั้น)
        }

        if (cfg.AutoStartKvm)
        {
            _kvm.Start();
        }
        else
        {
            OmniLog.Write("boot", "auto_start_kvm=false → engine ไม่ทำงาน (โหมด KVM ปิด — parity กับ UI)");
        }

        // สถานะสำหรับ omnilink-hostd status / การวัดของจริง (เขียนทุก 5 วิ — I/O น้อยมาก)
        _statusTimer = new System.Threading.Timer(_ => WriteStatus(), null, 2000, 5000);

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _stopping = true; };
        // SIGTERM ของ Linux (systemctl stop): .NET ทันสมัยแปลงเป็น graceful ProcessExit ให้แล้ว
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _stopping = true;

        OmniLog.Write("boot", $"omnilink-hostd v{typeof(KvmHostService).Assembly.GetName().Version?.ToString(3) ?? "?"} ready — nodes ใน config จะถูกเชื่อมตาม lifecycle (hot/cold/freeze)");
        Console.WriteLine("omnilink-hostd running — Ctrl+C เพื่อหยุด");

        // S2: เปิดท่อ IPC รอ dashboard ตามสัญญา S0 (Core ล้วน — ทำได้ทั้ง Linux/Windows)
        if (cfg.IpcEnabled && cfg.AutoStartKvm)
        {
            _ipc = new IpcHostServer(_kvm, cfg, _audio);
            _ipc.Start();
        }

        // S1: boot sequence หลัง Start (deploy + auto audio + calibrate) — เดินหลังบูตเสมอ ไม่บล็อก main loop
        RunBootSequence(cfg);

        while (!_stopping)
        {
            Thread.Sleep(500);
        }

        OmniLog.Write("boot", "omnilink-hostd กำลังปิดตัว...");
        _statusTimer.Dispose();
        try { _ipc?.Dispose(); } catch { }
        ShutdownParity(); // S1: หยุด audio ก่อนปิด session (D1: exit ต้องหยุด remote เสียงด้วย)
        _sub?.Dispose();
        _kvm.Dispose();
        OmniLog.Write("boot", "omnilink-hostd ปิดแล้ว");
        return 0;
    }

#if OMNILINK_WINDOWS
    private static IHostInputGateway CreateInputGateway() => new OmniLink.Platform.Windows.WindowsHostInput();

    private static OmniLink.Core.NodeAgentServer? CreateSubServer(OmniConfig cfg, IHostInputGateway input)
        => new(cfg,
            new OmniLink.Platform.Windows.WindowsNodeInputInjector(),
            input,
            new OmniLink.Core.Clipboard.WindowsClipboardBridge(),
            new OmniLink.Platform.Windows.WindowsEscapeHotkey());

    private static void EnablePreventSleep() => PreventSleepService.SetEnabled(true);

    /// <summary>S1: parity กับ wiring ของ MainWindow — clipboard 3 ทิศ + audio hub + smart audio + hotkey/online events + discovery</summary>
    private static void WireEngineParity(OmniConfig cfg)
    {
        var kvm = _kvm!;
        var clip = new OmniLink.Core.Clipboard.WindowsClipboardBridge();

        // engine ดึงคลิปบอร์ดของเครื่องนี้เพื่อ push หา node (เดิม UI ผูก HostClipboardGetter — hostd เดิมไม่ผูก = ไม่มีทางส่ง)
        kvm.HostClipboardGetter = () => clip.GetText();
        kvm.HostPngGetter = () =>
        {
            if (clip.TryGetPng(out var png) && png.Length > 0)
            {
                return png;
            }
            return null;
        };
        kvm.HostFileDropGetter = (out bool isCut) => clip.GetFileDropList(out isCut);

        // node ส่งคลิปมาเขียนลงเครื่องนี้จริง (ปิด stub "headless ยังไม่เชื่อมคลิปบอร์ดเครื่องนี้") — กัน echo ด้วยเปรียบเทียบค่าปัจจุบันก่อนเขียน เหมือน UI
        kvm.OnClipboardFromClient = text =>
        {
            try
            {
                if (clip.GetText() != text)
                {
                    clip.SetText(text);
                }
            }
            catch { }
        };
        kvm.OnClipboardImageFromClient = png =>
        {
            try
            {
                if (png is { Length: > 0 })
                {
                    clip.SetPng(png);
                }
            }
            catch { }
        };
        kvm.OnClipboardFilesFromClient = (files, isCut) =>
        {
            try
            {
                clip.SetFileDropList(files, isCut);
            }
            catch { }
        };

        // Smart Audio & Smart Focus-Aware Mic (เหมือน MainWindow แต่ไม่มี Dispatcher — เขียน config ตรง)
        _audio = new AudioHubService(
            cfg,
            kvm.SendNodeCommand,
            kvm.GetLocalEndpointIpForNode,
            d =>
            {
                try
                {
                    cfg.MicDeviceId = d.Id;
                    cfg.MicDeviceName = d.Name;
                    cfg.Save();
                }
                catch { }
            });

        kvm.OnNodeAudioActivityChanged = (nodeId, active) =>
        {
            try { _audio.HandleNodeAudioActivity(nodeId, active); } catch { }
        };
        kvm.OnFocusMicAction = start =>
        {
            try
            {
                if (start)
                {
                    if (!_audio.IsMicRunning)
                    {
                        OmniLog.Write("audio", "[smart] Focus บนเครื่องลูก -> เปิดไมค์อัตโนมัติ");
                        try { _audio.StartMic(); } catch { }
                    }
                }
                else if (_audio.IsMicRunning)
                {
                    OmniLog.Write("audio", "[smart] กลับมาทำงานบน Host นานเกินกำหนด -> พักไมค์อัตโนมัติ");
                    try { _audio.StopMic(); } catch { }
                }
            }
            catch { }
        };

        // hotkey ขอ toggle เสียง (engine จับปุ่มแล้ง publish มาที่ hub — hostd เป็นผู้ฟังแทน UI)
        OmniEventHub.Default.Subscribe<HotkeyAudioRequestedEvent>(e =>
        {
            try
            {
                if (e.ActionType == HotkeyActionConfig.ToggleStream)
                {
                    if (_audio.IsStreamRunning) { _audio.StopStream(); OmniLog.Write("audio", "hotkey → ปิด stream"); }
                    else { _audio.StartStream(); OmniLog.Write("audio", "hotkey → เปิด stream"); }
                }
                else if (e.ActionType == HotkeyActionConfig.ToggleMic)
                {
                    if (_audio.IsMicRunning) { _audio.StopMic(); OmniLog.Write("audio", "hotkey → ปิดไมค์"); }
                    else { _audio.StartMic(); OmniLog.Write("audio", "hotkey → เปิดไมค์"); }
                }
            }
            catch (Exception ex) { OmniLog.Warn("[audio] hotkey toggle: " + ex.Message); }
        });

        // node ออนไลน์ → retry เปิดเสียงตาม config (เหมือน MainWindow)
        OmniEventHub.Default.Subscribe<NodeConnectionEvent>(e =>
        {
            if (!e.Connected) return;
            try
            {
                if (cfg.AutoStartStream && !_audio.IsStreamRunning)
                {
                    OmniLog.Write("audio", "node online → retry start_stream (auto)");
                    try { _audio.StartStream(); } catch (Exception ex) { OmniLog.Warn("[audio] retry stream: " + ex.Message); }
                }
                if (cfg.AutoStartMic && !_audio.IsMicRunning)
                {
                    OmniLog.Write("audio", "node online → retry start_mic (auto)");
                    try { _audio.StartMic(); } catch (Exception ex) { OmniLog.Warn("[audio] retry mic: " + ex.Message); }
                }
            }
            catch { }
        });

        // discovery: สแกนเจออะไรให้ log ไว้ (หน้าจอจับคู่ยังเป็นของ UI — hostd ใช้เพื่อ observability)
        _discovery = new OmniDiscoveryService(cfg, node =>
            OmniLog.Write("discovery", $"เจอ node สด: {node.Name} ({node.Ip}) paired={node.Paired} zone={node.Zone}"));
    }

    /// <summary>S1: boot sequence แบบเดียวกับ OnWindowLoaded ของ UI — deploy + auto audio + calibrate + scan ครั้งแรก (background ทั้งหมด)</summary>
    private static void RunBootSequence(OmniConfig cfg)
    {
        var kvm = _kvm!;
        if (!cfg.AutoStartKvm) return;

        Task.Run(async () =>
        {
            try
            {
                OmniLog.Write("boot", "กำลังเชื่อมต่อเครื่องลูกข่ายและเตรียมระบบเสียง LAN...");
                await Task.Delay(500);

                foreach (var ncfg in cfg.Nodes)
                {
                    if (!ncfg.Enabled) continue;
                    if (!kvm.Sessions.TryGetValue(ncfg.NodeId, out var sess) || !sess.Connected)
                    {
                        var (ok, msg) = kvm.DeployRemoteAgent(ncfg);
                        OmniLog.Write("deploy", $"boot deploy ok={ok}: {msg}");
                        await Task.Delay(600);
                    }
                }

                if (cfg.AutoStartStream || cfg.AutoStartMic)
                {
                    AudioHubService.KillOrphanedAudioFromPreviousRun(cfg.GstExe); // D5: PID file + orphan-match เฉพาะ gst ของเรา
                    for (var i = 0; i < 30; i++)
                    {
                        if (kvm.Sessions.Values.Any(s => s.Connected)) break;
                        await Task.Delay(500);
                    }
                    if (cfg.AutoStartStream) { try { _audio?.StartStream(); } catch { } }
                    if (cfg.AutoStartMic) { try { _audio?.StartMic(); } catch { } }
                }

                try { kvm.CalibrateAllLanguages(kvm.GetHostLangGroup()); } catch { }

                try
                {
                    var found = await _discovery!.ScanNetworkAsync();
                    OmniLog.Write("discovery", $"scan เริ่มต้นพบ {found.Count} เครื่อง");
                }
                catch { }

                OmniLog.Write("boot", "hostd พร้อมทำงาน (S1 parity wiring ครบ)");
            }
            catch (Exception ex)
            {
                OmniLog.Error("boot", ex);
            }
        });
    }

    private static void ShutdownParity()
    {
        try { _audio?.ShutdownFull(); } catch { } // D1: สั่งหยุด remote ก่อนปิด session
        try { _discovery?.Dispose(); } catch { }
    }
#else
    private static IHostInputGateway CreateInputGateway()
        => OperatingSystem.IsLinux()
            ? new OmniLink.Platform.Linux.LinuxHostInput(Environment.GetEnvironmentVariable("DISPLAY") ?? ":0")
            : new NullHostInputGateway();

    private static OmniLink.Core.NodeAgentServer? CreateSubServer(OmniConfig cfg, IHostInputGateway input)
        => input is OmniLink.Platform.Linux.LinuxHostInput linuxInput
            ? new OmniLink.Core.NodeAgentServer(cfg,
                new OmniLink.Platform.Linux.LinuxNodeInputInjector(),
                linuxInput,
                new OmniLink.Platform.Linux.LinuxClipboardBridge(),
                new OmniLink.Platform.Linux.LinuxEscapeHotkey())
            : null;

    private static void EnablePreventSleep() { /* Linux: ยังไม่รองรับ (คงเดิม) */ }

    private static void WireEngineParity(OmniConfig cfg)
    {
        // Linux hostd คงพฤติกรรมเดิมทุกบรรทัด (S1 ขยายเฉพาะ Windows — ประเด็น Linux parity เป็นงานถัดไปตาม roadmap §3.5)
        _kvm!.OnClipboardFromClient = static text => OmniLog.Write("clipboard", $"รับจาก node len={text.Length} (headless ยังไม่เชื่อมคลิปบอร์ดเครื่องนี้)");
    }

    private static void RunBootSequence(OmniConfig cfg) { /* Linux: คงเดิม — ไม่มี boot deploy (ทดสอบ live ไม่ได้เพราะ L440 ปิด) */ }

    private static void ShutdownParity() { }
#endif

    private static bool AgentPortInUse()
    {
        try
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, OmniProtocol.KvmTcpPort);
            probe.Start();
            probe.Stop();
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
    }

    private static void WriteStatus()
    {
        try
        {
            var kvm = _kvm;
            if (kvm is null) return;
            var nodes = kvm.Sessions.Values.Select(s => new
            {
                id = s.Config.NodeId,
                ip = s.Config.Ip,
                connected = s.Connected,
                agent = s.AgentVersion,
                ping_ms = Math.Round(s.PingMs, 1),
                lifecycle = s.LifecycleState.ToString(),
                policy = s.Config.Policy,
            }).ToList();
            var doc = new
            {
                hostd = typeof(KvmHostService).Assembly.GetName().Version?.ToString(3) ?? "?",
                app_version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?", // F2: ยืนยันความสดของ binary
                pid = Environment.ProcessId,
                updated = DateTimeOffset.Now.ToString("O"),
                active_node = kvm.ActiveNodeId,
                master_lang = kvm.MasterLangGroup,
                ram_mb = Environment.WorkingSet / 1024 / 1024,
                nodes,
            };
            AtomicFile.WriteAllText(OmniPaths.StatusJsonPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[hostd] write status failed: " + ex.Message);
        }
    }

    private static int PrintStatus()
    {
        var path = OmniPaths.StatusJsonPath;
        if (!File.Exists(path))
        {
            Console.WriteLine($"ยังไม่มี {path} — hostd ยังไม่เคยรันจาก root นี้");
            return 1;
        }
        Console.WriteLine(File.ReadAllText(path));
        return 0;
    }

    /// <summary>อ่าน RAM รวมแบบพกพา: Linux = /proc/meminfo · อื่นๆ = GC info (0 = ไม่รู้)</summary>
    private static ulong TotalPhysicalMemoryBytes()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    {
                        var kb = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[1];
                        return ulong.Parse(kb) * 1024;
                    }
                }
            }
            return (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch
        {
            return 0;
        }
    }
}
