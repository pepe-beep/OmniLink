using OmniLink.Core.Strategies;

namespace OmniLink.Core.Abstractions;

/// <summary>
/// S2 (Engine/UI split): สัญญาหน้าด้านของ engine ต่อ UI — วัดจาก call sites จริงของ MainWindow (30 members, 71 จุด)
/// ผู้ implement ได้สองตัว: <see cref="KvmHostService"/> (in-proc โหมด engine เดิม) และ IpcKvmClient (โหมด dashboard)
/// กติกา: ทุก state read เป็น snapshot (NodeSessionView/TopologyView) · คำสั่งเป็น control-plane ล้วน —
/// hot path (hook/edge/input, K6-B) ไม่ผ่าน interface นี้เด็ดขาด
/// สัญญาฉบับเต็ม (ท่อ/ข้อความ): _lessons/war-room/IPC-PROTOCOL-CONTRACT-S0.md
/// </summary>
public interface IKvmHost : IDisposable
{
    // ── state (snapshot) ──
    IReadOnlyDictionary<string, NodeSessionView> SessionViews { get; }
    string? ActiveNodeId { get; }
    int MasterLangGroup { get; }
    TopologyView TopologyView { get; }
    int VirtualLeft { get; }
    int VirtualTop { get; }
    int VirtualRight { get; }
    int VirtualBottom { get; }

    /// <summary>UI บอก engine ว่าตัวเองซ่อนใน tray อยู่ (ใช้ throttle งานเบื้องหลัง — โหมด dashboard = ค่า local)</summary>
    bool IsWindowHiddenInTray { get; set; }

    /// <summary>กันลูป host↔host ของโหมด sub ฝัง (probe จาก sub server ฝั่งเดียวกันกับ host)</summary>
    Func<bool>? SubActiveFocusProbe { get; set; }

    // ── delegate ผูกกับฝั่ง UI (โหมด dashboard: engine ทำแทนแล้ว — client เก็บไว้เพื่อ compat ของ wiring) ──
    Func<string?>? HostClipboardGetter { get; set; }
    Func<byte[]?>? HostPngGetter { get; set; }
    HostFileDropGetterDelegate? HostFileDropGetter { get; set; }
    Action<string>? OnClipboardFromClient { get; set; }
    Action<byte[]?>? OnClipboardImageFromClient { get; set; }
    Action<IReadOnlyList<string>, bool>? OnClipboardFilesFromClient { get; set; }
    Action<string, bool>? OnNodeAudioActivityChanged { get; set; }
    Action<bool>? OnFocusMicAction { get; set; }

    // ── commands ──
    void Start();
    void ReloadLayout();
    void ReloadHotkeys();
    void UpdateStrategies();
    void AddOrUpdateNode(NodeConfig cfg, string? token = null);
    void SwitchToNode(string nodeId, double normY);
    void CalibrateAllLanguages(int targetGroup);
    int GetHostLangGroup();
    (bool Ok, string Message) DeployRemoteAgent(NodeConfig ncfg);
    void SetNodePolicy(string nodeId, string policy);
    bool RemoveNode(string nodeId);
    bool SendNodeCommand(string ip, object payload);
    string? GetLocalEndpointIpForNode(string ip);
}
