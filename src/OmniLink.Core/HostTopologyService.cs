using System.Collections.Concurrent;

namespace OmniLink.Core;

/// <summary>
/// ย้ายออกจาก KvmHostService (godclass-K5): กราฟ topology (anchor ต่อเครื่องได้ไม่จำกัด)
/// + reload เมื่อ layout เปลี่ยน + push set_layout ให้ agent
/// โค้ดย้ายแบบ move-verbatim (Config/Sessions ปรับเป็น deps) จาก KvmHostService.cs เดิม (commit 392c046)
/// </summary>
internal sealed class HostTopologyService
{
    private readonly OmniConfig _config;
    private readonly ConcurrentDictionary<string, RemoteNodeClient> _sessions;
    private TopologyGraph? _topology;

    public HostTopologyService(OmniConfig config, ConcurrentDictionary<string, RemoteNodeClient> sessions)
    {
        _config = config;
        _sessions = sessions;
    }

    /// <summary>กราฟปัจจุบัน (null ถ้ายังไม่ build — ผู้เรียกสร้างชั่วคราวจาก config เอง)</summary>
    public TopologyGraph? CurrentGraph => Volatile.Read(ref _topology);

    public TopologyGraph CurrentOrTemp() => CurrentGraph ?? new TopologyGraph(_config.Nodes);

    /// <summary>เรียกเมื่อ layout/anchor/position เปลี่ยน — rebuild กราฟ + ยิง set_layout ให้ agent ทุกตัวที่เชื่อมอยู่</summary>
    public void Reload()
    {
        var graph = new TopologyGraph(_config.Nodes);
        Volatile.Write(ref _topology, graph);
        if (graph.ConflictCount > 0)
        {
            OmniLog.Warn($"[layout] anchor+side ชนกัน {graph.ConflictCount} จุด — ตัวแรกชนะ (แก้ได้ที่แท็บ Devices)");
        }
        foreach (var orphan in graph.Unreachable)
        {
            OmniLog.Warn($"[layout] node '{orphan}' anchor สร้างวงจร/ชี้ของไม่มีจริง — สลับด้วยขอบไม่ได้ (ยังใช้ปุ่ม/สูตรลัดได้)");
        }
        foreach (var kv in _sessions)
        {
            if (!kv.Value.Connected) continue;
            try
            {
                SendLayoutTo(kv.Value, graph);
            }
            catch (Exception ex)
            {
                OmniLog.Warn("[layout] set_layout failed: " + ex.Message);
            }
        }
        OmniLog.Write("layout", "topology rebuilt (" + graph.ConflictCount + " conflict)");
    }

    /// <summary>ส่งแผนที่เพื่อนบ้านของ node นี้ให้ agent — agent ใช้ตัดสินขอบไหนไปเครื่องไหน</summary>
    public void SendLayoutTo(RemoteNodeClient sess, TopologyGraph? graph)
    {
        var g = graph ?? new TopologyGraph(_config.Nodes);
        sess.SendTcp(new { type = "set_layout", neighbors = g.GetNeighborMapForAgent(sess.Config.NodeId) });
    }
}
