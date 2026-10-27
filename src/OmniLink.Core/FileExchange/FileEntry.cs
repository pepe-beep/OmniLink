using System;
using System.Text.Json.Serialization;

namespace OmniLink.Core.FileExchange;

/// <summary>
/// ข้อมูลรายละเอียดของไฟล์หรือโฟลเดอร์แต่ละรายการในกระบวนการโอนถ่ายไฟล์ (Information Expert)
/// </summary>
public sealed class FileEntry
{
    [JsonPropertyName("rel_path")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("is_dir")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("mtime_utc")]
    public long LastModifiedUtcTicks { get; set; }
}
