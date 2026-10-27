using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniLink.Core;

// godclass-K7d: แยกจาก OmniConfig.cs เดิม (649 บรรทัดรวม 3 ประเภท) แบบ move-verbatim
public sealed class NodeConfig
{
    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = "psl440-ThinkPad-L440";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "ThinkPad L440 (Ubuntu)";

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; } = null;

    [JsonPropertyName("custom_name")]
    public string? CustomName { get; set; } = null;

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(CustomName)
        ? CustomName
        : (!string.IsNullOrWhiteSpace(Hostname) ? Hostname : Name);

    public void SetCustomName(string? newName)
    {
        CustomName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
    }

    public void ResetName()
    {
        CustomName = null;
    }

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "192.168.99.2";

    [JsonPropertyName("position")]
    public string Position { get; set; } = "right"; // "right", "left", "top", or "bottom"

    [JsonPropertyName("device_type")]
    public string DeviceType { get; set; } = "laptop"; // "laptop", "desktop", or "server"

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("audio_playback_enabled")]
    public bool AudioPlaybackEnabled { get; set; } = true;

    [JsonPropertyName("audio_mic_enabled")]
    public bool AudioMicEnabled { get; set; } = true;

    [JsonPropertyName("ssh_user")]
    public string SshUser { get; set; } = "psl440";

    [JsonPropertyName("width")]
    public int Width { get; set; } = 960;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 720;

    [JsonPropertyName("auth_token")]
    public string? AuthToken { get; set; } = null;

    /// <summary>v1.6 TLS: SHA-256 fingerprint ของ certificate ฝั่ง agent (pin ตอนจับคู่ — TOFU แบบ Deskflow)
    /// ว่าง = ยังไม่เคยจับคู่บนระบบ TLS → host จะปฏิเสธการเชื่อมจนกว่าจะจับคู่ใหม่</summary>
    [JsonPropertyName("cert_fingerprint_sha256")]
    public string CertFingerprintSha256 { get; set; } = "";

    /// <summary>v1.7 Zone Model: capability ของ node — full (ควบคุมได้ครบ) / clipboard (clipboard+ไฟล์
    /// เท่านั้น) / view (telemetry เท่านั้น) — default full = "จับคู่แล้วเชื่อใจเต็มที่"</summary>
    [JsonPropertyName("policy")]
    public string Policy { get; set; } = OmniPolicy.Full;

    /// <summary>v1.9.1 กราฟ topology: เกาะข้างเครื่องไหน (NodeId อื่น หรือ ว่าง = host) —
    /// ต่อจากเครื่องลูกได้อีกที (เช่น e5 บน L440 เมื่อ L440 ขวาของ host)</summary>
    [JsonPropertyName("anchor")]
    public string Anchor { get; set; } = "";

    /// <summary>v1.9 C2: แม็ป modifier ต่อเครื่อง (shift/ctrl/alt/super/altgr → target|none) — ว่าง = ไม่แม็ป
    /// แปลงฝั่ง host ก่อนส่ง ไม่กระทบ keyspace ไทย (INV-2/11) ดู OmniModifiers.RemapKeycode</summary>
    [JsonPropertyName("modifier_map")]
    public Dictionary<string, string> ModifierMap { get; set; } = new();
}
