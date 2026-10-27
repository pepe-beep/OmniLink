using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniLink.Core;

namespace OmniLink.Core;

public sealed record MicDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class AudioHubService : IDisposable
{
    private readonly OmniConfig _config;
    private readonly Func<string, object, bool> _sendNodeCmd;
    private readonly Func<string, string?>? _getLocalEndpointIp;
    private readonly Action<MicDeviceInfo>? _onMicResolved; // B5: แจ้ง UI ให้เป็นผู้เขียน config (single-writer)
    private readonly object _lock = new();
    private Process? _playbackProc;
    private Process? _micProc;
    // v1.8 Phase E: audio instrumentation — วัดจริงต้นทุน gst ต่อ stream (feasibility 4.3)
    private System.Threading.Timer? _audioMetricsTimer;
    private Process? _lastMetricsProc;

    private static string PidFilePath => Path.Combine(OmniPaths.RunStateDir, "audio_pids.json");

    public AudioHubService(
        OmniConfig config,
        Func<string, object, bool> sendNodeCmd,
        Func<string, string?>? getLocalEndpointIp = null,
        Action<MicDeviceInfo>? onMicResolved = null)
    {
        _config = config;
        _sendNodeCmd = sendNodeCmd;
        _getLocalEndpointIp = getLocalEndpointIp;
        _onMicResolved = onMicResolved;
    }

    public bool IsStreamRunning
    {
        get
        {
            lock (_lock)
            {
                return _playbackProc is not null && !_playbackProc.HasExited;
            }
        }
    }

    public bool IsMicRunning
    {
        get
        {
            lock (_lock)
            {
                return _micProc is not null && !_micProc.HasExited;
            }
        }
    }

    /// <summary>
    /// D5: เก็บกวาด gst-launch ค้างจากรอบก่อน (เช่น ถูก kill ทั้งโปรแกรม) โดยอ่าน PID file
    /// ตรวจชื่อโปรเซสก่อนฆ่าทุกครั้ง (กัน PID reuse) — ห้ามฆ่าด้วย /IM ทั้งระบบอีกแล้ว
    /// เพราะจะฆ่า GStreamer ของโปรแกรมอื่นในเครื่องด้วย
    /// </summary>
    public static void KillOrphanedAudioFromPreviousRun(string? gstExePath = null)
    {
        try
        {
            // ชั้น 1: PID file (แม่นต่อคู่ล่าสุด)
            if (File.Exists(PidFilePath))
            {
                var pids = JsonSerializer.Deserialize<Dictionary<string, int>>(AtomicFile.TryReadAllText(PidFilePath) ?? "{}");
                foreach (var pid in pids?.Values ?? Enumerable.Empty<int>())
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (p is not null && p.ProcessName.Contains("gst-launch", StringComparison.OrdinalIgnoreCase))
                        {
                            OmniLog.Write("audio", $"kill orphaned gst from previous run pid={pid}");
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(500);
                        }
                    }
                    catch
                    {
                        // โปรเซสตายไปแล้ว = ตามที่ต้องการ
                    }
                }
                try { File.Delete(PidFilePath); } catch { }
            }

            // ชั้น 2: กวาด orphan ที่ PID file เก็บไม่ครบ ด้วย Native Process API (ไม่ spawn powershell.exe)
            // ต้องผ่านทั้ง 2 เงื่อนไข เพื่อไม่ฆ่า GStreamer ของโปรแกรมอื่น (D5):
            //   (ก) executable path ตรงกับ gst ที่ OmniLink ตั้งค่าไว้
            //   (ข) เป็น orphan จริง — parent ตายแล้ว หรือ PID ของ parent ถูก reuse (parent เกิดทีหลังลูก)
            if (!string.IsNullOrWhiteSpace(gstExePath))
            {
                var expectedPath = Path.GetFullPath(gstExePath);
                foreach (var p in Process.GetProcessesByName("gst-launch-1.0"))
                {
                    using (p)
                    {
                        try
                        {
                            var path = p.MainModule?.FileName;
                            if (path is null || !string.Equals(Path.GetFullPath(path), expectedPath, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            if (!IsOrphan(p))
                            {
                                continue;
                            }
                            OmniLog.Write("audio", $"kill orphaned gst (orphan-match) pid={p.Id}");
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(500);
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[audio] orphan cleanup failed: {ex.Message}");
        }
    }

    private static bool IsOrphan(Process p)
    {
        var ppid = ProcessParentPid.GetParentProcessId(p);
        if (ppid <= 0 || ppid == Environment.ProcessId)
        {
            return false;
        }
        try
        {
            using var parent = Process.GetProcessById(ppid);
            return parent.StartTime > p.StartTime; // PID reuse → parent ตัวจริงตายไปแล้ว
        }
        catch (ArgumentException)
        {
            return true; // parent ไม่อยู่แล้ว
        }
        catch
        {
            return false;
        }
    }

    private (string Id, string Ip, string SshUser)? GetPrimaryAudioNode()
    {
        // SV-M16 fix: enumerate บน snapshot ภายใต้ gate เดียวกับผู้ mutate (UI pairing/remove)
        lock (OmniConfig.NodesGate)
        {
            foreach (var n in _config.Nodes)
            {
                if (n.Enabled && (n.AudioPlaybackEnabled || n.AudioMicEnabled) && OmniPolicy.CanStreamAudio(n.Policy)) // v1.7: view-only node = ไม่มีเสียง
                {
                    return (n.NodeId, n.Ip, n.SshUser);
                }
            }
        }
        return null;
    }

    /// <summary>SV-M18 fix: gst ตายเองแล้ว handle ค้าง non-null → smart-standby ไม่มีวัน revive — prune เสมอก่อนเช็ค</summary>
    private void PruneDeadPlaybackProcLocked()
    {
        if (_playbackProc is not null && _playbackProc.HasExited)
        {
            try { _playbackProc.Dispose(); } catch { }
            _playbackProc = null;
        }
    }

    private void TriggerRemoteAudio(string ip, string action)
    {
        // 1. ดึง IP จาก Socket ที่เชื่อมต่อกับโหนดนี้จริง (Dynamic Host IP)
        // 2. ถ้ายังไม่ได้ ให้ fallback ไปใช้ค่าใน Config
        var resolvedHostIp = _getLocalEndpointIp?.Invoke(ip);
        if (string.IsNullOrWhiteSpace(resolvedHostIp))
        {
            resolvedHostIp = _config.HostIp;
        }
        else if (resolvedHostIp.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
        {
            // แปลง IPv4-mapped IPv6 (::ffff:192.168.x.x) เป็น standard IPv4 (192.168.x.x) สำหรับ GStreamer udpsink
            resolvedHostIp = resolvedHostIp[7..];
        }

        if (_sendNodeCmd(ip, new { type = "audio_control", action, host_ip = resolvedHostIp }))
        {
            OmniLog.Write("audio", $"ส่งคำสั่ง '{action}' ไปยัง {ip} (บอกให้ส่งเสียงกลับมาที่ host_ip={resolvedHostIp})");
            return;
        }
        OmniLog.Warn($"[audio] ส่งคำสั่ง '{action}' ไปยัง {ip} ไม่สำเร็จ (โหนดออฟไลน์หรือ TCP ขาด)");
    }

    private long _lastAudioActiveTick = Environment.TickCount64;
    private volatile bool _streamExplicitlyEnabled = false;

    private bool StartStreamLocalUnlocked((string Id, string Ip, string SshUser) node)
    {
        PruneDeadPlaybackProcLocked();
        if (!File.Exists(_config.GstExe) || _playbackProc is not null) return false;
        var bufSz = _config.UdpSocketBufferBytes;
        var caps = "application/x-rtp,media=audio,clock-rate=48000,encoding-name=L16,channels=2,payload=96";
        var args =
            $"-q rtpbin name=rtpbin latency={_config.PlaybackLatencyMs} drop-on-latency=true " +
            $"udpsrc port={OmniProtocol.PlaybackRtpPort} buffer-size={bufSz} caps=\"{caps}\" ! rtpbin.recv_rtp_sink_0 " +
            "rtpbin. ! rtpL16depay ! audioconvert ! audioresample ! audio/x-raw,format=S16LE ! wasapi2sink sync=true qos=true " +
            $"udpsrc port={OmniProtocol.PlaybackRtcpSrPort} ! rtpbin.recv_rtcp_sink_0 " +
            $"rtpbin.send_rtcp_src_0 ! udpsink host={node.Ip} port={OmniProtocol.PlaybackRtcpRrPort} sync=false async=false";

        var psi = new ProcessStartInfo
        {
            FileName = _config.GstExe,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["GST_DEBUG"] = "1";
        psi.EnvironmentVariables["GST_DEBUG_FILE"] = OmniPaths.StreamDebugLog;

        _playbackProc = Process.Start(psi);
        UpdatePidFileUnlocked();
        return _playbackProc is not null;
    }

    public void HandleNodeAudioActivity(string nodeId, bool active)
    {
        if (!_config.SmartAudioStandby || !_streamExplicitlyEnabled) return;
        lock (_lock)
        {
            var node = GetPrimaryAudioNode();
            if (node is null || node.Value.Id != nodeId) return;

            if (active)
            {
                _lastAudioActiveTick = Environment.TickCount64;
                PruneDeadPlaybackProcLocked(); // SV-M18: ตัวเก่าตายแล้วต้องเปิดใหม่ได้
                if (_playbackProc is null)
                {
                    OmniLog.Write("audio", $"[smart] โหนด '{nodeId}' มีเสียงเล่น -> ปลุกท่อรับเสียง (wasapi2sink) ให้ทำงานทันที");
                    StartStreamLocalUnlocked(node.Value);
                }
            }
            else
            {
                var idleSeconds = (Environment.TickCount64 - _lastAudioActiveTick) / 1000.0;
                var timeoutSec = Math.Clamp(_config.AudioIdleTimeoutSec, 2, 60);
                if (idleSeconds >= timeoutSec && _playbackProc is not null)
                {
                    OmniLog.Write("audio", $"[smart] โหนด '{nodeId}' ไม่มีเสียงเล่นนาน {idleSeconds:0.0}s -> พักท่อรับเสียงเพื่อลดภาระ CPU และแรม");
                    StopStreamLocalUnlocked();
                }
            }
        }
    }

    public bool StartStream()
    {
        (string Id, string Ip, string SshUser)? node;
        lock (_lock)
        {
            _streamExplicitlyEnabled = true;
            _lastAudioActiveTick = Environment.TickCount64;
            StopStreamLocalUnlocked();

            // D4: ตรวจเครื่องตัวเองให้ครบก่อนสั่ง remote — ไม่งั้น remote เริ่มส่งเสียงแต่เราไม่มีตัวรับ
            if (!File.Exists(_config.GstExe))
            {
                OmniLog.Warn($"[audio] ไม่พบ gst-launch ที่ '{_config.GstExe}' — ยกเลิก start_stream ก่อนสั่ง remote");
                return false;
            }
            node = GetPrimaryAudioNode();
            if (node is null)
            {
                OmniLog.Warn("[audio] ไม่มีโหนดที่เปิดใช้ระบบเสียงอยู่ — ยกเลิก start_stream");
                return false;
            }
        }

        // SV-M17 fix: network send + sleep อยู่ "นอก" lock — เดิมถือ lock ครอบทั้งชุด (~5 วิ)
        // ทำให้ status poll / stop คำสั่งโดนบล็อกจน UI สะดุด
        TriggerRemoteAudio(node.Value.Ip, "start_stream");
        Thread.Sleep(350);

        lock (_lock)
        {
            var started = StartStreamLocalUnlocked(node.Value);
            if (started)
            {
                // v1.8 Phase E: log ต้นทุน gst จริง 60 วิหลังเริ่ม (feasibility 4.3 — ตัวเลขเพื่อวางแผน Acer)
                StartAudioMetricsLoop();
            }
            return started;
        }
    }

    private void StartAudioMetricsLoop()
    {
        _audioMetricsTimer?.Dispose();
        _lastMetricsProc = _playbackProc;
        _audioMetricsTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var proc = _playbackProc;
                if (proc is null || proc.HasExited) return;
                var wsMb = proc.WorkingSet64 / 1024.0 / 1024.0;
                var privMb = proc.PrivateMemorySize64 / 1024.0 / 1024.0;
                var cpu1 = proc.TotalProcessorTime.TotalMilliseconds;
                Thread.Sleep(2000);
                proc.Refresh();
                var cpu2 = proc.TotalProcessorTime.TotalMilliseconds;
                if (proc.HasExited) return;
                var cpuPct = (cpu2 - cpu1) / 2000.0 * 100.0;
                OmniLog.Write("audio", $"[metrics] gst playback pid={proc.Id} WS={wsMb:0.0}MB Private={privMb:0.0}MB CPU={cpuPct:0.0}% (2s window) — ตัวเลขเพื่อวางแผนเครื่อง host ต่ำสเปก");
            }
            catch
            {
                // instrumentation ห้ามแครชเสียง
            }
        }, null, 60_000, Timeout.Infinite);
    }

    /// <summary>D7: ฆ่า process tree แบบมี deadline สั้น แล้วคืน handle เสมอ</summary>
    private static void KillProcessTree(Process? proc)
    {
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                if (!proc.WaitForExit(500))
                {
                    var pid = proc.Id;
                    using var killer = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/F /T /PID {pid}",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    });
                    killer?.WaitForExit(500);
                }
            }
        }
        catch
        {
        }
        try { proc.Dispose(); } catch { }
    }

    private void StopStreamLocalUnlocked()
    {
        try
        {
            KillProcessTree(_playbackProc);
        }
        catch
        {
        }
        _playbackProc = null;
        UpdatePidFileUnlocked();
    }

    public void StopStream()
    {
        lock (_lock)
        {
            _streamExplicitlyEnabled = false;
            StopStreamLocalUnlocked();
            var node = GetPrimaryAudioNode();
            if (node is not null)
            {
                TriggerRemoteAudio(node.Value.Ip, "stop_stream");
            }
        }
    }

    public bool StartMic()
    {
        (string Id, string Ip, string SshUser)? node;
        lock (_lock)
        {
            StopMicLocalUnlocked();

            // D4: เหมือน StartStream — ตรวจ local ก่อนสั่ง remote
            if (!File.Exists(_config.GstExe))
            {
                OmniLog.Warn($"[audio] ไม่พบ gst-launch ที่ '{_config.GstExe}' — ยกเลิก start_mic ก่อนสั่ง remote");
                return false;
            }
            node = GetPrimaryAudioNode();
            if (node is null)
            {
                OmniLog.Warn("[audio] ไม่มีโหนดที่เปิดใช้ระบบเสียงอยู่ — ยกเลิก start_mic");
                return false;
            }
        }

        // SV-M17 fix: network send + Sleep + ScanMicrophones (รอ gst-device-monitor ถึง 5 วิ)
        // อยู่นอก lock — เดิมถือ lock ครอบทั้งชุด (~5.4 วิ) บล็อก status poll และ stop
        TriggerRemoteAudio(node.Value.Ip, "start_mic");
        Thread.Sleep(350);

        var micId = _config.MicDeviceId;
        var devs = ScanMicrophones();
        if (!devs.Any(d => d.Id == micId))
        {
            MicDeviceInfo? matched = null;

            // 1. ลองหาจากชื่อที่เคยบันทึกไว้ใน config ก่อน (ถ้ามี)
            if (!string.IsNullOrWhiteSpace(_config.MicDeviceName))
            {
                matched = devs.FirstOrDefault(d =>
                    d.Name.Contains(_config.MicDeviceName, StringComparison.OrdinalIgnoreCase) ||
                    _config.MicDeviceName.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
            }

            // 2. ถ้าไม่พบ ให้เลือกไมค์ตัวแรกที่ระบบตรวจพบ (Universal Fallback ไม่เจาะจงยี่ห้อ)
            matched ??= devs.FirstOrDefault();

            if (matched is not null)
            {
                micId = matched.Id;
                OmniLog.Write("audio", $"เลือกใช้อุปกรณ์ไมค์: '{matched.Name}' (id={matched.Id})");
                // B5: อย่าเขียน config จาก background thread — ให้ UI เป็นผู้เก็บค่าเอง
                try { _onMicResolved?.Invoke(matched); } catch { }
            }
            else
            {
                micId = "DEFAULT";
            }
        }

        // SV-L11 fix: micId มาจาก output ของ gst-device-monitor — escape ก่อนฝังใน gst-launch args
        var micIdSafe = (micId ?? "").Replace('\\', '/').Replace("\"", "'");
        var devArg = (!string.IsNullOrEmpty(micIdSafe) && micIdSafe != "DEFAULT") ? $"device=\"{micIdSafe}\" " : "";
            var bufSz = _config.UdpSocketBufferBytes;

            var args =
                $"-q rtpbin name=rtpbin wasapi2src {devArg}low-latency=true ! audioconvert ! audioresample ! " +
                "audio/x-raw,format=S16BE,channels=2,rate=48000 ! rtpL16pay pt=97 ! rtpbin.send_rtp_sink_0 " +
                $"rtpbin.send_rtp_src_0 ! udpsink host={node.Value.Ip} port={OmniProtocol.MicRtpPort} buffer-size={bufSz} sync=false async=false " +
                $"rtpbin.send_rtcp_src_0 ! udpsink host={node.Value.Ip} port={OmniProtocol.MicRtcpSrPort} sync=false async=false " +
                $"udpsrc port={OmniProtocol.MicRtcpRrPort} ! rtpbin.recv_rtcp_sink_0";

        var psi = new ProcessStartInfo
        {
            FileName = _config.GstExe,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["GST_DEBUG"] = "1";
        psi.EnvironmentVariables["GST_DEBUG_FILE"] = OmniPaths.MicDebugLog;

        // SV-M17: สร้าง process ภายใต้ lock สั้นๆ (แค่จัดการ _micProc field)
        lock (_lock)
        {
            _micProc = Process.Start(psi);
            UpdatePidFileUnlocked();
            return _micProc is not null;
        }
    }

    private void StopMicLocalUnlocked()
    {
        try
        {
            KillProcessTree(_micProc);
        }
        catch
        {
        }
        _micProc = null;
        UpdatePidFileUnlocked();
    }

    public void StopMic()
    {
        lock (_lock)
        {
            StopMicLocalUnlocked();
            var node = GetPrimaryAudioNode();
            if (node is not null)
            {
                TriggerRemoteAudio(node.Value.Ip, "stop_mic");
            }
        }
    }

    /// <summary>
    /// D1: ปิดทั้งสองทิศทาง "รวมถึงฝั่ง remote" — ต้องเรียกก่อนปิด KVM sessions
    /// เพราะการสั่ง stop ฝั่ง Linux ต้องใช้ TCP connection ที่ยังเปิดอยู่
    /// </summary>
    public void ShutdownFull()
    {
        try { StopStream(); } catch (Exception ex) { OmniLog.Warn($"[audio] stop_stream ตอนปิดโปรแกรม: {ex.Message}"); }
        try { StopMic(); } catch (Exception ex) { OmniLog.Warn($"[audio] stop_mic ตอนปิดโปรแกรม: {ex.Message}"); }
    }

    public void ResetBoth()
    {
        StopStream();
        StopMic();
        Thread.Sleep(250);
        StartStream();
        StartMic();
    }

    /// <summary>
    /// D3: อ่าน stdout/stderr พร้อมกันแบบ async (กัน pipe buffer deadlock) + มี deadline
    /// เดิมใช้ ReadToEnd() ระหว่างที่ redirect stderr ไว้แต่ไม่เคยอ่าน = deadlock ได้ตามมาตรฐาน §2 ที่ห้ามไว้
    /// </summary>
    public List<MicDeviceInfo> ScanMicrophones()
    {
        var list = new List<MicDeviceInfo> { new("DEFAULT", "ไมโครโฟนเริ่มต้นของระบบ (Default System Mic)") };
        var gstDir = Path.GetDirectoryName(_config.GstExe);
        if (string.IsNullOrEmpty(gstDir)) return list;
        var monExe = Path.Combine(gstDir, "gst-device-monitor-1.0.exe");
        if (!File.Exists(monExe)) return list;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = monExe,
                Arguments = "Audio/Source",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return list;

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            var output = outTask.IsCompletedSuccessfully ? outTask.Result : string.Empty;
            _ = errTask;

            var curId = "";
            var curDesc = "";
            var isLoopback = false;

            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Contains("Device found:"))
                {
                    if (!string.IsNullOrEmpty(curId) && !string.IsNullOrEmpty(curDesc) && !isLoopback)
                    {
                        list.Add(new MicDeviceInfo(curId, curDesc));
                    }
                    curId = "";
                    curDesc = "";
                    isLoopback = false;
                    continue;
                }

                var mId = Regex.Match(line, @"^device\.id\s*=\s*(\S+)");
                if (mId.Success) curId = mId.Groups[1].Value.Trim();

                var mDesc = Regex.Match(line, @"wasapi2\.device\.description\s*=\s*(.+)$");
                if (mDesc.Success) curDesc = mDesc.Groups[1].Value.Trim();

                if (line.Contains("wasapi2.device.loopback = true")) isLoopback = true;
            }

            if (!string.IsNullOrEmpty(curId) && !string.IsNullOrEmpty(curDesc) && !isLoopback)
            {
                list.Add(new MicDeviceInfo(curId, curDesc));
            }
        }
        catch
        {
        }

        return list;
    }

    /// <summary>D5: บันทึก PID ของ gst ที่เราเป็นเจ้าของ (ต้องถือ _lock อยู่แล้ว)</summary>
    private void UpdatePidFileUnlocked()
    {
        try
        {
            var pids = new Dictionary<string, int>();
            if (_playbackProc is not null && !_playbackProc.HasExited)
            {
                pids["playback"] = _playbackProc.Id;
            }
            if (_micProc is not null && !_micProc.HasExited)
            {
                pids["mic"] = _micProc.Id;
            }

            if (pids.Count > 0)
            {
                Directory.CreateDirectory(OmniPaths.RunStateDir);
                AtomicFile.WriteAllText(PidFilePath, JsonSerializer.Serialize(pids));
            }
            else
            {
                try { File.Delete(PidFilePath); } catch { }
            }
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[audio] อัปเดต PID file ล้มเหลว: {ex.Message}");
        }
    }

    /// <summary>ฆ่าเฉพาะ local — การหยุดฝั่ง remote ต้องเรียก ShutdownFull ก่อน Dispose</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            StopStreamLocalUnlocked();
            StopMicLocalUnlocked();
        }
    }
}
