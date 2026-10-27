namespace OmniLink.Core;

/// <summary>
/// S2 (Engine/UI split): snapshot สถานะต่อ node — รูปร่างสมมาตรกับสิ่งที่ UI อ่านจาก RemoteNodeClient
/// (Connected/AgentVersion/LangGroup/PingMs/LifecycleState/Config) แต่เป็นของนิ่งที่เดินทางข้าม IPC ได้
/// live object อยู่ฝั่ง engine เท่านั้น · จังหวะสดของข้อมูล = จังหวะ state.get/state.changed (≤900ms เท่าเดิม)
/// </summary>
public sealed class NodeSessionView
{
    public NodeConfig Config { get; init; } = new();
    public bool Connected { get; init; }
    public string? LocalEndpointIp { get; init; }
    public int Width { get; init; } = 960;
    public int Height { get; init; } = 720;
    public string AgentVersion { get; init; } = "?";
    public int LangGroup { get; init; }
    public double PingMs { get; init; }
    public double RamPct { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public bool PlaybackRunning { get; init; }
    public bool PlaybackActive { get; init; }
    public bool MicRunning { get; init; }
    public ConnectionLifecycleState LifecycleState { get; init; }
}
