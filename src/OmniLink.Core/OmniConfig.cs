using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniLink.Core;

// godclass-K7d: แยกจาก OmniConfig.cs เดิม (649 บรรทัดรวม 3 ประเภท) แบบ move-verbatim
public sealed class OmniConfig
{
    public static string ConfigFilePath => OmniPaths.ConfigPath;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("host_name")]
    public string HostName { get; set; } = "PSPV";

    [JsonPropertyName("host_ip")]
    public string HostIp { get; set; } = "192.168.99.55";

    [JsonPropertyName("gst_exe")]
    public string GstExe { get; set; } = OmniPaths.DiscoverGstExe();

    [JsonPropertyName("mic_device_id")]
    public string MicDeviceId { get; set; } = "";

    [JsonPropertyName("mic_device_name")]
    public string MicDeviceName { get; set; } = "";

    [JsonPropertyName("language_mode")]
    public string LanguageMode { get; set; } = "global_calibrated"; // "global_calibrated" or "target_only"

    [JsonPropertyName("grave_switch_enabled")]
    public bool GraveSwitchEnabled { get; set; } = true;

    [JsonPropertyName("auto_eng_host_on_client")]
    public bool AutoEngHostOnClient { get; set; } = true;

    [JsonPropertyName("auto_start_kvm")]
    public bool AutoStartKvm { get; set; } = true;

    [JsonPropertyName("auto_start_stream")]
    public bool AutoStartStream { get; set; } = true;

    [JsonPropertyName("auto_start_mic")]
    public bool AutoStartMic { get; set; } = false;

    [JsonPropertyName("stop_deskflow_on_start")]
    public bool StopDeskflowOnStart { get; set; } = true;

    [JsonPropertyName("playback_latency_ms")]
    public int PlaybackLatencyMs { get; set; } = 20;

    [JsonPropertyName("udp_socket_buffer_bytes")]
    public int UdpSocketBufferBytes { get; set; } = 262144;

    [JsonPropertyName("new_node_policy")]
    public string NewNodePolicy { get; set; } = OmniPolicy.Full; // v2.0 §4.5: policy เริ่มต้นของ node ที่จับคู่ใหม่ (full/clipboard/view — เครื่อง host ต่ำสเปกตั้ง view เพื่อประหยัด)

    [JsonPropertyName("edge_poll_hz")]
    public int EdgePollHz { get; set; } = 30;

    [JsonPropertyName("reconnect_switch_gate_ms")]
    public int ReconnectSwitchGateMs { get; set; } = 1500; // GroupA-D: ห้ามสลับไป node ที่ reconnect มาไม่ถึงเวลานี้ (กัน DESYNC — agent ยังไม่พร้อม focus) · 0 = ปิด // GroupA-P: polling fallback ตรวจขอบด้วย GetCursorPos (~30Hz) — สลับจอได้แม้ LL hook ตาย · 0 = ปิด

    [JsonPropertyName("sub_mode_enabled")]
    public bool SubModeEnabled { get; set; } = false; // v2.0 เฟส 3: ให้เครื่องนี้ถูกคุมโดย host ตัวอื่นได้ (โหมด sub ฝัง)

    // ─── v2.1 S3 (Engine/UI split): บทบาทของแอป ───
    [JsonPropertyName("ui_role")]
    public string UiRole { get; set; } = "engine"; // "engine" = UI รัน engine ในตัว (เดิม) · "dashboard" = UI เป็น client ของ hostd

    [JsonPropertyName("ipc_enabled")]
    public bool IpcEnabled { get; set; } = true; // hostd เปิด pipe รอ dashboard ตามสัญญา S0

    [JsonPropertyName("theme_mode")]
    public int ThemeMode { get; set; } = 0; // v2.0: 0 = ตามปัจจุบัน (น้ำเงินเข้ม), 1 = สีดำ, 2 = สว่าง — แทน backdrop ที่ถอนออกแล้ว

    [JsonPropertyName("diagnostics_enabled")]
    public bool DiagnosticsEnabled { get; set; } = true;

    // ─── v1.9 i18n + UX: ภาษา UI / พฤติกรรมปิดหน้าต่าง / ระดับ log ───
    [JsonPropertyName("ui_language")]
    public string UiLanguage { get; set; } = "auto"; // auto = ตามภาษา Windows (th* → ไทย)

    [JsonPropertyName("close_button_action")]
    public string CloseButtonAction { get; set; } = "minimize_to_tray"; // minimize_to_tray | exit

    [JsonPropertyName("log_level")]
    public string LogLevel { get; set; } = "INFO"; // OFF | ERROR | WARN | INFO

    // ─── v1.9 C1/C4: สูตรลัดผู้ใช้ + กัน sleep + ทิปครั้งแรก ───
    [JsonPropertyName("hotkeys")]
    public List<HotkeyRuleConfig> Hotkeys { get; set; } = new();

    [JsonPropertyName("prevent_sleep")]
    public bool PreventSleep { get; set; } = false; // ค้างเครื่องไว้ไม่ให้เข้าโหมด sleep ขณะใช้เป็น KVM host

    [JsonPropertyName("first_run_done")]
    public bool FirstRunDone { get; set; } = false;

    // ─── v1.9.1: สลับเชิงมุมทแยง (corner diagonal) — ลากจากมุม host ข้ามไปเครื่องที่วางทแยง ───
    [JsonPropertyName("corner_switch_enabled")]
    public bool CornerSwitchEnabled { get; set; } = false;

    [JsonPropertyName("corner_switch_px")]
    public int CornerSwitchPx { get; set; } = 64;

    [JsonPropertyName("low_memory_mode")]
    public bool LowMemoryMode { get; set; } = false;

    /// <summary>godclass-G5 (root cause 27 ต.ค.): process PriorityClass=High — KVM input เป็น latency-critical
    /// ใต้โหลดหนัก (AI tools/compile) process Normal ถูก starve จน hook callback เกิน LowLevelHooksTimeout
    /// → Windows ถอด hook (ตายเป็นรอบ 15-33 วิ) · default true · ปิดได้ถ้าไม่ต้องการ</summary>
    [JsonPropertyName("process_priority_high")]
    public bool ProcessPriorityHigh { get; set; } = true;

    // ─── v1.8 Low-Mem Profile v2: sub-toggles (ใช้เมื่อ profile active — ปรับแต่งลึกใน Advanced) ───
    [JsonPropertyName("lowmem_software_render")]
    public bool LowMemSoftwareRender { get; set; } = true;

    [JsonPropertyName("lowmem_threadpool_cap")]
    public bool LowMemThreadPoolCap { get; set; } = true;

    [JsonPropertyName("lowmem_gc_batch")]
    public bool LowMemGcBatch { get; set; } = true;

    // ─── v1.8 Advanced: clipboard per-direction + log rotation ───
    [JsonPropertyName("clipboard_push_to_node")]
    public bool ClipboardPushToNode { get; set; } = true;

    [JsonPropertyName("clipboard_accept_from_node")]
    public bool ClipboardAcceptFromNode { get; set; } = true;

    [JsonPropertyName("log_max_bytes")]
    public int LogMaxBytes { get; set; } = 5_000_000;

    /// <summary>v1.8: คืนค่าทุก field เป็นค่าโรงงาน in-place (ใช้โดยปุ่ม Factory Reset — backup ก่อนเรียกเสมอ)</summary>
    public void ResetToDefaultsInPlace()
    {
        var fresh = new OmniConfig();
        foreach (var prop in typeof(OmniConfig).GetProperties())
        {
            if (prop.CanWrite)
            {
                prop.SetValue(this, prop.GetValue(fresh));
            }
        }
    }

    // ─── Smart Adaptive Resource Engine (Advanced Tuning) ───
    [JsonPropertyName("smart_audio_standby")]
    public bool SmartAudioStandby { get; set; } = true;

    [JsonPropertyName("audio_idle_timeout_sec")]
    public int AudioIdleTimeoutSec { get; set; } = 5;

    [JsonPropertyName("smart_mic_follow_focus")]
    public bool SmartMicFollowFocus { get; set; } = true;

    [JsonPropertyName("mic_idle_timeout_sec")]
    public int MicIdleTimeoutSec { get; set; } = 30;

    [JsonPropertyName("adaptive_ping_enabled")]
    public bool AdaptivePingEnabled { get; set; } = true;

    [JsonPropertyName("ping_active_ms")]
    public int PingActiveMs { get; set; } = 1500;

    [JsonPropertyName("ping_idle_ms")]
    public int PingIdleMs { get; set; } = 5000;

    [JsonPropertyName("ping_tray_ms")]
    public int PingTrayMs { get; set; } = 10000;

    [JsonPropertyName("enable_clipboard")]
    public bool EnableClipboard { get; set; } = true;

    [JsonPropertyName("block_switch_on_fullscreen")]
    public bool BlockSwitchOnFullscreen { get; set; } = true;

    // ─── A1 Switching UX ───
    [JsonPropertyName("switch_delay_ms")]
    public int SwitchDelayMs { get; set; } = 250;

    [JsonPropertyName("double_tap_ms")]
    public int DoubleTapMs { get; set; } = 400;

    [JsonPropertyName("corner_size")]
    public int CornerSize { get; set; } = 32;

    [JsonPropertyName("corner_top_left")]
    public bool CornerTopLeft { get; set; } = true;

    [JsonPropertyName("corner_top_right")]
    public bool CornerTopRight { get; set; } = true;

    [JsonPropertyName("corner_bottom_left")]
    public bool CornerBottomLeft { get; set; } = true;

    [JsonPropertyName("corner_bottom_right")]
    public bool CornerBottomRight { get; set; } = true;

    // ─── A2 Lock-to-screen ───
    [JsonPropertyName("lock_to_screen_enabled")]
    public bool LockToScreenEnabled { get; set; } = true;

    // ─── Advanced Networking & Security (Deskflow Superiority) ───
    [JsonPropertyName("kvm_tcp_port")]
    public int KvmTcpPort { get; set; } = 24801;

    [JsonPropertyName("kvm_udp_port")]
    public int KvmUdpPort { get; set; } = 24802;

    [JsonPropertyName("discovery_port")]
    public int DiscoveryPort { get; set; } = 24800;

    [JsonPropertyName("playback_rtp_port")]
    public int PlaybackRtpPort { get; set; } = 50044;

    [JsonPropertyName("clipboard_max_bytes")]
    public int ClipboardMaxBytes { get; set; } = 1_000_000;

    /// <summary>B2: ล็อคใช้งานเฉพาะเครือข่ายที่เชื่อใจ (CIDR เช่น "192.168.99.0/24") —
    /// ว่าง = อนุญาตทุกที่ (พฤติกรรมเดิม) · agent ใช้กรอง TCP/UDP/discovery, host ใช้เตือนตอนเพิ่ม node</summary>
    [JsonPropertyName("allowed_subnets")]
    public List<string> AllowedSubnets { get; set; } = new();

    [JsonPropertyName("auth_token")]
    public string AuthToken { get; set; } = "";

    /// <summary>SV-M16: gate กลางสำหรับ mutate/enumerate Nodes ข้าม thread (UI / node reader / audio)</summary>
    public static readonly object NodesGate = new();

    [JsonPropertyName("nodes")]
    public List<NodeConfig> Nodes { get; set; } = new()
    {
        new NodeConfig
        {
            NodeId = "psl440-ThinkPad-L440",
            Name = "ThinkPad L440 (Ubuntu)",
            Ip = "192.168.99.2",
            Position = "right",
            Enabled = true,
            AudioPlaybackEnabled = true,
            AudioMicEnabled = true,
            SshUser = "psl440",
            Width = 960,
            Height = 720,
        },
        new NodeConfig
        {
            NodeId = "pse5-aspiree5411g",
            Name = "Aspire E5-411G (Linux)",
            Ip = "192.168.99.1",
            Position = "left",
            Enabled = false,
            AudioPlaybackEnabled = false,
            AudioMicEnabled = false,
            SshUser = "pse5",
            Width = 1366,
            Height = 768,
        },
    };

    public static OmniConfig Load()
    {
        var path = OmniPaths.ConfigPath;
        try
        {
            var json = AtomicFile.TryReadAllText(path);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var loaded = JsonSerializer.Deserialize<OmniConfig>(json);
                if (loaded is not null)
                {
                    // SV-M15 fix (1/2): nodes = 0 เป็นสถานะที่ถูกต้องของผู้ใช้ (ลบจนหมดได้)
                    // เดิมเงื่อนไข `Nodes.Count > 0` ทำให้ user ที่ลบหมดถูก regenerate demo nodes + Save ทับ (token หาย)
                    ValidateAndNormalize(loaded);
                    EnsureSecurityDefaults(loaded, persist: true);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // SV-M15 fix (2/2): ไฟล์ JSON พัง — ห้าม Save default ทับทันที (เดิม token/pairing หายถาวร)
            // คืนค่า default "ในหน่วยความจำ" อย่างเดียว ให้ไฟล์เดิมรอผู้ใช้แก้/Save ครั้งถัดไปจากการตั้งค่าจริง
            OmniLog.Error($"[config] ไฟล์ config อ่านไม่ได้ ({ex.Message}) — ใช้ค่า default ชั่วคราว โดยไม่เขียนทับไฟล์เดิม");
        }

        var cfg = new OmniConfig();
        ValidateAndNormalize(cfg);
        EnsureSecurityDefaults(cfg, persist: false);
        return cfg;
    }

    private static readonly object _saveLock = new();
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public void Save()
    {
        try
        {
            string json;
            lock (_saveLock)
            {
                SchemaVersion = 2;
            if (string.IsNullOrWhiteSpace(AuthToken))
            {
                AuthToken = GenerateToken();
            }

                json = JsonSerializer.Serialize(this, _jsonOpts);
            }
            AtomicFile.WriteAllText(OmniPaths.ConfigPath, json, writeUtf8Bom: false);
        }
        catch (Exception ex)
        {
            OmniLog.Error("config save failed", ex);
        }
    }

    /// <summary>ตรวจสอบและแก้ค่าที่ผิดปกติให้อยู่ในขอบเขตที่ปลอดภัย (ไม่โยน exception)</summary>
    public void Validate() => ValidateAndNormalize(this);

    public static void ValidateAndNormalize(OmniConfig cfg)
    {
        if (cfg.KvmTcpPort <= 1024 || cfg.KvmTcpPort > 65535) cfg.KvmTcpPort = 24801;
        if (cfg.KvmUdpPort <= 1024 || cfg.KvmUdpPort > 65535) cfg.KvmUdpPort = 24802;
        if (cfg.DiscoveryPort <= 1024 || cfg.DiscoveryPort > 65535) cfg.DiscoveryPort = 24800;
        if (cfg.PlaybackRtpPort <= 1024 || cfg.PlaybackRtpPort > 65535) cfg.PlaybackRtpPort = 50044;

        if (cfg.Nodes is null)
        {
            cfg.Nodes = new List<NodeConfig>();
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in cfg.Nodes.ToArray())
        {
            if (n is null)
            {
                cfg.Nodes.Remove(n!);
                continue;
            }
            // v1.9.1 (Bar test เจอจริง): TryParse ผู้กว้างรับ "999.999.999.999" ได้ —
            // ต้องเช็ค strict แบบ round-trip (parse แล้วต้องพิมพ์กลับได้ตรงเป๊ะ)
            var ipOk = System.Net.IPAddress.TryParse(n.Ip ?? "", out var parsedIp) &&
                       parsedIp.ToString() == n.Ip;
            if (!ipOk)
            {
                OmniLog.Warn($"[config] node '{n.NodeId}' ip='{n.Ip}' ไม่ถูกต้อง → ปิดใช้งานโหนดนี้");
                n.Enabled = false;
            }
            if (string.IsNullOrWhiteSpace(n.NodeId) || !seenIds.Add(n.NodeId))
            {
                OmniLog.Warn($"[config] node id ซ้ำ/ว่าง '{n.NodeId}' → ปิดใช้งานโหนดนี้");
                n.Enabled = false;
            }
            if (n.Position is not ("left" or "right" or "top" or "bottom"))
            {
                n.Position = "right";
            }
            if (n.DeviceType is not ("laptop" or "desktop" or "server"))
            {
                n.DeviceType = "laptop";
            }
            n.Policy = OmniPolicy.Normalize(n.Policy); // v1.7: policy ผิดค่า = full (fail-open สู่ default ที่ปลอดภัยต่อ UX)
            if (n.Width <= 0 || n.Width > 8192)
            {
                n.Width = 960;
            }
            if (n.Height <= 0 || n.Height > 8192)
            {
                n.Height = 720;
            }
            n.SshUser ??= "";
            n.Ip ??= "";
        }

        // ค้นหาพาธ GStreamer ใหม่อัตโนมัติหากค่าใน Config ว่าง หรือชี้ไปยังไฟล์ที่ไม่มีจริงบนเครื่องนี้
        if (string.IsNullOrWhiteSpace(cfg.GstExe) || !File.Exists(cfg.GstExe))
        {
            var discovered = OmniPaths.DiscoverGstExe();
            if (File.Exists(discovered))
            {
                OmniLog.Write("config", $"GStreamer path auto-detected: '{discovered}'");
                cfg.GstExe = discovered;
            }
        }

        OmniLimits.Normalize(cfg); // v1.9: ภาษา UI / พฤติกรรมปิด / log level ต้องเป็นค่าที่รู้จักเสมอ
    }

    private static void EnsureSecurityDefaults(OmniConfig cfg, bool persist)
    {
        if (!string.IsNullOrWhiteSpace(cfg.AuthToken))
        {
            return;
        }
        cfg.AuthToken = GenerateToken();
        if (persist)
        {
            cfg.Save();
        }
    }

    private static string GenerateToken()
    {
        try
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }
        catch
        {
            // Fallback ที่ยังคงเดายากพอสำหรับเครื่องทั่วไป (ไม่ควรถูกเรียก)
            return Convert.ToHexString(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")))[..64];
        }
    }
}
