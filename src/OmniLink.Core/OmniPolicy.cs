namespace OmniLink.Core;

/// <summary>
/// OmniPolicy — capability matrix ต่อ node (v1.7 Zone Model)
/// ค่า policy: "full" (ควบคุมได้ทุกอย่าง) / "clipboard" (clipboard+ไฟล์เท่านั้น ไม่มี input)
/// / "view" (telemetry/audio เท่านั้น) · default "full" ตอบโจทย์ "จับคู่แล้ว = เชื่อใจ"
/// การบังคับเป็น 2 ชั้น: host ฝั่งส่งไม่ยิง (C2) และ agent ฝั่งรับปฏิเสธ (C3 — security จริง)
/// </summary>
public static class OmniPolicy
{
    public const string Full = "full";
    public const string ClipboardFiles = "clipboard";
    public const string ViewOnly = "view";

    public static readonly string[] All = { Full, ClipboardFiles, ViewOnly };

    public static string Normalize(string? policy) => policy switch
    {
        ClipboardFiles => ClipboardFiles,
        ViewOnly => ViewOnly,
        _ => Full,
    };

    /// <summary>ควบคุมเมาส์/คีย์บอร์ดของเครื่องนั้นได้ (edge switching ใช้เงื่อนไขนี้)</summary>
    public static bool CanControl(string? policy) => Normalize(policy) == Full;

    /// <summary>ส่ง/รับ clipboard (text/image) และไฟล์ได้</summary>
    public static bool CanShareClipboard(string? policy) => Normalize(policy) != ViewOnly;

    /// <summary>โอนไฟล์ข้ามเครื่องได้</summary>
    public static bool CanTransferFiles(string? policy) => Normalize(policy) != ViewOnly;

    /// <summary>สตรีมเสียง (playback/mic) ได้</summary>
    public static bool CanStreamAudio(string? policy) => Normalize(policy) != ViewOnly;
}
