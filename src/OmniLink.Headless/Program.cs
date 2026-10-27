using System.Text.Json;
using OmniLink.Core;
using OmniLink.Core.Abstractions;

namespace OmniLink.Headless;

/// <summary>
/// v2.0: OmniLink host แบบไม่มี UI (daemon) — รันบน Linux (Ubuntu/Mint/Lubuntu 22.04+) หรือ Windows
/// เป้าหมายแรก: L440 เพื่อพิสูจน์ "host บน Linux" + เป็นฐานให้ host↔host ในเฟสถัดไป
/// การตั้งค่า: ใช้ omnilink_config.json เดียวกับ host UI ทั้งหมด (root = OMNILINK_HOME
/// หรือโฟลเดอร์ที่มี config ข้าง exe — กติกา OmniPaths เดิม)
/// </summary>
internal static class Program
{
    private static volatile bool _stopping;
    private static KvmHostService? _kvm;
    private static OmniLink.Core.NodeAgentServer? _sub;
    private static System.Threading.Timer? _statusTimer;

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
        Console.WriteLine("omnilink-hostd 2.0.0");
        return 0;
    }

    private static int RunDaemon()
    {
        OmniLog.Configure();
        var cfg = OmniConfig.Load();
        OmniLimits.Normalize(cfg);
        cfg.Save();
        OmniLog.SetLevel(cfg.LogLevel);

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

        // อินพุต: Linux = X11 gateway (poll + grab) · Windows = NullHostInputGateway
        // (headless บน Windows ไม่มี edge-switch — คุมผ่านสูตรลัด/agent เท่านั้น)
        IHostInputGateway input = OperatingSystem.IsLinux()
            ? new OmniLink.Platform.Linux.LinuxHostInput(Environment.GetEnvironmentVariable("DISPLAY") ?? ":0")
            : new NullHostInputGateway();

        _kvm = new KvmHostService(cfg, static () => { }, null, input);
        // godclass-F3: pre-check ชนพอร์ตกับ node agent ก่อน bind — ข้อความชัดกว่ารอ SocketException ตอน Start
        if (cfg.SubModeEnabled && AgentPortInUse())
        {
            OmniLog.Warn("[hostd] sub_mode_enabled=true แต่พอร์ต 24801 ถูกถืออยู่ (น่าจะ node agent บนเครื่องเดียวกัน) → ข้าม sub-mode รอบนี้");
            cfg.SubModeEnabled = false;
        }
        else if (cfg.SubModeEnabled && input is OmniLink.Platform.Linux.LinuxHostInput linuxInput)
        {
            // v2.0 เฟส 3: โหมด sub บน Linux — แชร์ gateway เดียวกับ host-role (ภาษา/จอเป็น machine-global)
            _sub = new OmniLink.Core.NodeAgentServer(cfg,
                new OmniLink.Platform.Linux.LinuxNodeInputInjector(),
                linuxInput,
                new OmniLink.Platform.Linux.LinuxClipboardBridge(),
                new OmniLink.Platform.Linux.LinuxEscapeHotkey());
            _sub.OnStatus = static msg => OmniLog.Write("sub", msg);
            _kvm.SubActiveFocusProbe = () => _sub.ActiveFocus; // กันลูป host<->host
            _sub.Start();
        }
        else if (cfg.SubModeEnabled)
        {
            OmniLog.Warn("[hostd] sub_mode_enabled=true แต่แพลตฟอร์มนี้ยังไม่มี injector (Windows hostd ยังไม่รองรับ — ใช้แอป UI แทน)");
        }
        _kvm.OnStatusBanner = static msg => OmniLog.Write("banner", msg);
        _kvm.OnNodeOnline = static sess => OmniLog.Write("hostd", $"node online: {sess.Config.NodeId} ({sess.Config.Ip}) agent={sess.AgentVersion}");
        _kvm.OnClipboardFromClient = static text => OmniLog.Write("clipboard", $"รับจาก node len={text.Length} (headless ยังไม่เชื่อมคลิปบอร์ดเครื่องนี้)");
        _kvm.Start();

        // สถานะสำหรับ omnilink-hostd status / การวัดของจริง (เขียนทุก 5 วิ — I/O น้อยมาก)
        _statusTimer = new System.Threading.Timer(_ => WriteStatus(), null, 2000, 5000);

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _stopping = true; };
        // SIGTERM ของ Linux (systemctl stop): .NET ทันสมัยแปลงเป็น graceful ProcessExit ให้แล้ว
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _stopping = true;

        OmniLog.Write("boot", $"omnilink-hostd v{typeof(KvmHostService).Assembly.GetName().Version?.ToString(3) ?? "?"} ready — nodes ใน config จะถูกเชื่อมตาม lifecycle (hot/cold/freeze)");
        Console.WriteLine("omnilink-hostd running — Ctrl+C เพื่อหยุด");

        while (!_stopping)
        {
            Thread.Sleep(500);
        }

        OmniLog.Write("boot", "omnilink-hostd กำลังปิดตัว...");
        _statusTimer.Dispose();
        _sub?.Dispose();
        _kvm.Dispose();
        OmniLog.Write("boot", "omnilink-hostd ปิดแล้ว");
        return 0;
    }

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
                hostd = "2.0.0",
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
