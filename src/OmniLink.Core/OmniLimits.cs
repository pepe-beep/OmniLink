namespace OmniLink.Core;

/// <summary>
/// แหล่งเดียวของช่วงค่าที่ยอมรับได้ (clamp) ทุกหน้า Settings ต้องอ้างที่นี่เสมอ —
/// เดิม clamp กระจัดกระจาย 2 ที่และค่าไม่ตรงกัน (SettingsViewModel vs MainWindow)
/// </summary>
public static class OmniLimits
{
    public const int SwitchDelayMin = 0, SwitchDelayMax = 2000, SwitchDelayDefault = 250;
    public const int DoubleTapMin = 0, DoubleTapMax = 1000, DoubleTapDefault = 400;
    public const int CornerMin = 0, CornerMax = 200, CornerDefault = 32;
    public const int AudioIdleMin = 1, AudioIdleMax = 300, AudioIdleDefault = 5;
    public const int MicIdleMin = 1, MicIdleMax = 600, MicIdleDefault = 30;
    public const int PingActiveMin = 200, PingActiveMax = 10000, PingActiveDefault = 1500;
    public const int PingIdleMin = 500, PingIdleMax = 30000, PingIdleDefault = 5000;
    public const int PingTrayMin = 1000, PingTrayMax = 60000, PingTrayDefault = 10000;
    public const int PlaybackLatencyMin = 0, PlaybackLatencyMax = 2000, PlaybackLatencyDefault = 20;
    public const long UdpBufferMin = 65536, UdpBufferMax = 4_194_304; public const int UdpBufferDefault = 262144;
    public const int ClipMaxMin = 1000, ClipMaxMax = 5_000_000, ClipMaxDefault = 1_000_000;
    public const int LogKbMin = 500, LogKbMax = 50_000; public const int LogKbDefault = 5_000_000 / 1024;
    public const int PortMin = 1024, PortMax = 65535;

    public static readonly string[] UiLanguages = { "auto", "th-TH", "en-US" };
    public static readonly string[] CloseActions = { "minimize_to_tray", "exit" };
    public static readonly string[] LogLevels = { "OFF", "ERROR", "WARN", "INFO" };

    public static int ClampInt(int v, int min, int max) => v < min ? min : (v > max ? max : v);

    /// <summary>เรียกตอน Load/Save — ค่าที่ผู้ใช้แก้มือ/ไฟล์เก่าให้อยู่ในชุดที่รู้จัก</summary>
    public static void Normalize(OmniConfig cfg)
    {
        if (!UiLanguages.Contains(cfg.UiLanguage)) cfg.UiLanguage = "auto";
        cfg.NewNodePolicy = OmniPolicy.Normalize(cfg.NewNodePolicy); // §4.5: ค่านอกชุด = full
        if (!CloseActions.Contains(cfg.CloseButtonAction)) cfg.CloseButtonAction = "minimize_to_tray";
        var level = (cfg.LogLevel ?? "").Trim().ToUpperInvariant();
        cfg.LogLevel = LogLevels.Contains(level) ? level : "INFO";

        // v1.9.1 (Bar test เจอจริง): ตัวเลขทุกตัวต้องถูก clamp ที่ normalize — เดิม clamp อยู่แค่ใน UI path
        // ทำให้ config ที่แก้มือเป็นค่าติดลบ/ยักษ์ ผ่านเข้า engine ได้
        cfg.EdgePollHz = ClampInt(cfg.EdgePollHz, 0, 120); // GroupA-P: 0=ปิด, สูงสุด 120Hz
        cfg.SwitchDelayMs = ClampInt(cfg.SwitchDelayMs, SwitchDelayMin, SwitchDelayMax);
        cfg.DoubleTapMs = ClampInt(cfg.DoubleTapMs, DoubleTapMin, DoubleTapMax);
        cfg.CornerSize = ClampInt(cfg.CornerSize, CornerMin, CornerMax);
        cfg.AudioIdleTimeoutSec = ClampInt(cfg.AudioIdleTimeoutSec, AudioIdleMin, AudioIdleMax);
        cfg.MicIdleTimeoutSec = ClampInt(cfg.MicIdleTimeoutSec, MicIdleMin, MicIdleMax);
        cfg.PingActiveMs = ClampInt(cfg.PingActiveMs, PingActiveMin, PingActiveMax);
        cfg.PingIdleMs = ClampInt(cfg.PingIdleMs, PingIdleMin, PingIdleMax);
        cfg.PingTrayMs = ClampInt(cfg.PingTrayMs, PingTrayMin, PingTrayMax);
        cfg.PlaybackLatencyMs = ClampInt(cfg.PlaybackLatencyMs, PlaybackLatencyMin, PlaybackLatencyMax);
        cfg.UdpSocketBufferBytes = ClampInt(cfg.UdpSocketBufferBytes, (int)UdpBufferMin, (int)UdpBufferMax);
        cfg.ClipboardMaxBytes = ClampInt(cfg.ClipboardMaxBytes, ClipMaxMin, ClipMaxMax);
        if (cfg.LogMaxBytes < 500_000 || cfg.LogMaxBytes > 50_000_000) cfg.LogMaxBytes = 5_000_000;
        cfg.CornerSwitchPx = ClampInt(cfg.CornerSwitchPx, 16, 200);
    }
}
