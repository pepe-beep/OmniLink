using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace OmniLink.Core;

/// <summary>
/// ย้ายออกจาก KvmHostService (godclass-K1): กลุ่ม WATCH/SSH — watchdog deploy
/// + SSH deploy pipeline + agent version mismatch auto-redeploy
/// โค้ดย้ายแบบ move-verbatim จาก KvmHostService.cs เดิม (commit 45409f9)
/// </summary>
internal sealed class NodeDeployService
{
    private readonly OmniConfig _config;
    private readonly ConcurrentDictionary<string, RemoteNodeClient> _sessions;
    private readonly Dictionary<string, long> _lastConnectedTick = new();
    private long _lastAutoDeployTick;

    public NodeDeployService(OmniConfig config, ConcurrentDictionary<string, RemoteNodeClient> sessions)
    {
        _config = config;
        _sessions = sessions;
    }

    /// <summary>บูต-order resilience: โหนด enabled แต่ offline นานเกิน 90s → ลอง deploy ซ้ำทุก ≥120s</summary>
    public void CheckDeployHealth(long startTick)
    {
        try
        {
            foreach (var ncfg in _config.Nodes)
            {
                if (!ncfg.Enabled || !_sessions.TryGetValue(ncfg.NodeId, out var sess))
                {
                    continue;
                }
                if (sess.Connected)
                {
                    _lastConnectedTick[ncfg.NodeId] = Environment.TickCount64;
                    continue;
                }
                var lastOk = _lastConnectedTick.TryGetValue(ncfg.NodeId, out var t) ? t : 0;
                var offlineFor = Environment.TickCount64 - Math.Max(lastOk, startTick);
                if (offlineFor > 90000 &&
                    Environment.TickCount64 - Interlocked.Read(ref _lastAutoDeployTick) > 120000)
                {
                    Interlocked.Exchange(ref _lastAutoDeployTick, Environment.TickCount64);
                    OmniLog.Write("deploy",
                        $"watchdog: {ncfg.NodeId} offline {offlineFor / 1000}s (เครื่องลูกอาจเพิ่งเปิด) → retry deploy");
                    var cfgCopy = ncfg;
                    Task.Run(() =>
                    {
                        var (ok, msg) = DeployRemoteAgent(cfgCopy);
                        OmniLog.Write("deploy", $"watchdog deploy ok={ok}: {msg}");
                    });
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>F2c: พบ agent รุ่นเก่า/เวอร์ชันไม่ตรง → deploy ใหม่ให้อัตโนมัติ (throttle 30 วินาที)
    /// บทเรียน 2026-10-17: boot-deploy มี race กับการ connect ที่เร็ว — version handshake คือกลไก
    /// รับประกันว่า agent บนเครื่องลูกจะ converge ไปที่ canonical เสมอ ไม่ว่าใครไปแก้ไฟล์</summary>
    public void OnAgentMismatch(NodeConfig cfg, string foundVersion)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastAutoDeployTick) < 30000)
        {
            return;
        }
        Interlocked.Exchange(ref _lastAutoDeployTick, now);
        OmniLog.Warn($"[node] {cfg.NodeId} agent version='{foundVersion}' != expected '{KvmHostService.ExpectedAgentVersion}' → auto-redeploy");
        Task.Run(() =>
        {
            var (ok, msg) = DeployRemoteAgent(cfg);
            OmniLog.Write("deploy", $"auto-redeploy ok={ok}: {msg}");
        });
    }

    public (bool Ok, string Message) DeployRemoteAgent(NodeConfig ncfg)
    {
        var agentPath = OmniPaths.NodeAgentPath;
        if (!File.Exists(agentPath))
        {
            return (false, Loc.T("deploy.nofile", "ไม่พบไฟล์ node_agent/omnilink_agent.py"));
        }

        try
        {
            var agentBytes = File.ReadAllBytes(agentPath);

            var upPsi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh",
                Arguments = $"-o ConnectTimeout=3 {ncfg.SshUser}@{ncfg.Ip} \"mkdir -p ~/omnilink && cat > ~/omnilink/omnilink_agent.py && chmod +x ~/omnilink/omnilink_agent.py\"",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using (var upProc = Process.Start(upPsi))
            {
                if (upProc is null) return (false, Loc.T("deploy.nossh", "ไม่สามารถเรียก ssh.exe"));
                upProc.StandardInput.BaseStream.Write(agentBytes, 0, agentBytes.Length);
                upProc.StandardInput.Close();
                upProc.WaitForExit(6000);
            }

                var effectiveToken = F2SanitizeToken(!string.IsNullOrEmpty(ncfg.AuthToken) ? ncfg.AuthToken : _config.AuthToken);
                // B2: sanitize allowlist — เหลือเฉพาะอักขระ CIDR (กัน injection ผ่าน config)
                var allowlistCsv = string.Join(",", (_config.AllowedSubnets ?? new List<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => new string(s.Trim().Where(c => char.IsAsciiDigit(c) || c is '.' or '/' or ',').ToArray()))
                    .Where(s => s.Length > 0));
                var launcherScript =
                    "import os, signal, subprocess, time\n" +
                    "my_pid = os.getpid()\n" +
                    "subprocess.run(['pkill', '-f', 'deskflow'], stderr=subprocess.DEVNULL)\n" +
                    "res = subprocess.run(['pgrep', '-f', 'omnilink_agent.py'], stdout=subprocess.PIPE, text=True)\n" +
                    "for line in res.stdout.splitlines():\n" +
                    "    pid = int(line.strip())\n" +
                    "    if pid != my_pid:\n" +
                    "        try: os.kill(pid, signal.SIGTERM)\n" +
                    "        except Exception: pass\n" +
                    "time.sleep(0.2)\n" +
                    "env = os.environ.copy()\n" +
                    "env['DISPLAY'] = ':0'\n" +
                    "env['DBUS_SESSION_BUS_ADDRESS'] = f'unix:path=/run/user/{os.getuid()}/bus'\n" +
                    $"env['OMNILINK_TOKEN'] = '{effectiveToken}'\n" +
                    // B2: allowlist subnet ส่งตามไปให้ agent กรอง connection ตั้งแต่ handshake
                    (allowlistCsv.Length > 0 ? $"env['OMNILINK_ALLOW_SUBNETS'] = '{allowlistCsv}'\n" : "") +
                    // B2: + เขียนไฟล์ไว้ให้ autostart หลัง reboot อ่าน (env ไม่อยู่รอดข้ามบูต)
                    (allowlistCsv.Length > 0
                        ? "with open(os.path.expanduser('~/omnilink/allowed_subnets'), 'w') as af:\n" +
                          $"    af.write('{allowlistCsv}')\n"
                        : "") +
                    "log = open(os.path.expanduser('~/omnilink/agent.log'), 'wb')\n" +
                    "subprocess.Popen(['python3', '-u', os.path.expanduser('~/omnilink/omnilink_agent.py')], env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)\n" +
                    // BOOT-ORDER FIX: เขียน token ลงไฟล์ — agent ที่ autostart ตอนเครื่องลูกบูตเองอ่านจากไฟล์ได้
                    "with open(os.path.expanduser('~/omnilink/auth_token'), 'w') as tf:\n" +
                    $"    tf.write('{effectiveToken}')\n" +
                    "os.chmod(os.path.expanduser('~/omnilink/auth_token'), 0o600)\n" +
                // BOOT-ORDER FIX: ติดตั้ง autostart ให้ agent เปิดเองเมื่อเครื่องลูกบูต (สถาปัตยกรรมแบบ Deskflow)
                "ast_dir = os.path.expanduser('~/.config/autostart')\n" +
                "os.makedirs(ast_dir, exist_ok=True)\n" +
                "desk = os.path.join(ast_dir, 'omnilink-agent.desktop')\n" +
                "with open(desk, 'w') as df:\n" +
                "    df.write('[Desktop Entry]\\nType=Application\\nName=OmniLink Agent\\nExec=python3 -u %s\\nX-GNOME-Autostart-enabled=true\\n' % os.path.expanduser('~/omnilink/omnilink_agent.py'))\n" +
                "stale = os.path.join(ast_dir, 'omnilink-agent.desktop.disabled')\n" +
                "if os.path.exists(stale):\n" +
                "    os.remove(stale)\n";

            var startPsi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh",
                Arguments = $"-o ConnectTimeout=3 {ncfg.SshUser}@{ncfg.Ip} \"python3 -\"",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using (var startProc = Process.Start(startPsi))
            {
                if (startProc is null) return (false, Loc.T("deploy.nostart", "ไม่สามารถรัน Agent ผ่าน SSH"));
                var scriptBytes = Encoding.UTF8.GetBytes(launcherScript);
                startProc.StandardInput.BaseStream.Write(scriptBytes, 0, scriptBytes.Length);
                startProc.StandardInput.Close();
                startProc.WaitForExit(6000);
            }

            // F3: ห้ามรายงานสำเร็จปลอม — รอยืนยันว่า agent ขึ้นจริงโดย probe TCP 24801 (~3.6s)
            for (var i = 0; i < 12; i++)
            {
                Thread.Sleep(300);
                try
                {
                    using var probe = new TcpClient();
                    if (probe.ConnectAsync(ncfg.Ip, OmniProtocol.KvmTcpPort).Wait(500) && probe.Connected)
                    {
                        OmniLog.Write("deploy", $"deployed agent to {ncfg.Ip} (verified)");
                        return (true, Loc.T("deploy.ok", "ติดตั้งและเริ่มทำงาน Agent บน {0} สำเร็จ (ยืนยันการเชื่อมต่อแล้ว)", ncfg.Ip));
                    }
                }
                catch
                {
                }
            }

            OmniLog.Error($"[deploy] agent ไม่ตอบ TCP หลัง deploy {ncfg.Ip}");
            return (false, Loc.T("deploy.noresponse", "Deploy แล้วแต่ Agent ไม่ตอบสนอง (อาจมีโปรเซสเก่าถือพอร์ตค้างอยู่)"));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>F2: ตัด token ให้เหลือแต่ตัวอักษรปลอดภัยต่อการฝังใน shell script (กัน injection)</summary>
    private static string F2SanitizeToken(string? token)
    {
        var clean = new string((token ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        return clean.Length >= 16 ? clean : new string('0', 64); // token เสีย = ใช้ค่าที่ agent จะปฏิเสธ (fail-closed)
    }
}
