using OmniLink.Core.Strategies;

namespace OmniLink.Core;

/// <summary>
/// S2 (Engine/UI split): ผัง topology ฉบับเดินทางข้าม IPC ได้ — เก็บเฉพาะข้อมูลที่ UI อ่านจริง
/// (Cells + GetNeighbor) · ฝั่ง engine สร้างจาก TopologyGraph, ฝั่ง dashboard สร้างจากข้อมูลบนท่อ
/// semantics ของ GetNeighbor ลอกมาจาก TopologyGraph.GetNeighbor เป๊ะ (null/ว่าง = host)
/// </summary>
public sealed class TopologyView
{
    private readonly Dictionary<string, Dictionary<ScreenEdge, string>> _edges = new();

    /// <summary>cell ของแต่ละ node (host=(0,0)) — ใช้วาดผัง; node ตกวงจร/ชน cell = ไม่มี</summary>
    public IReadOnlyDictionary<string, (int X, int Y)> Cells { get; init; } =
        new Dictionary<string, (int X, int Y)>();

    public TopologyView() { }

    public TopologyView(IReadOnlyDictionary<string, (int X, int Y)> cells,
        IReadOnlyDictionary<string, IReadOnlyDictionary<ScreenEdge, string>> edges)
    {
        Cells = cells;
        foreach (var (from, byEdge) in edges)
        {
            _edges[from] = new Dictionary<ScreenEdge, string>(byEdge);
        }
    }

    /// <summary>S2: snapshot edges ให้ฝั่ง server แพ็กลงท่อ</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<ScreenEdge, string>> EdgesView
    {
        get
        {
            var copy = new Dictionary<string, IReadOnlyDictionary<ScreenEdge, string>>(_edges.Count);
            foreach (var kv in _edges)
            {
                copy[kv.Key] = new Dictionary<ScreenEdge, string>(kv.Value);
            }
            return copy;
        }
    }

    public string? GetNeighbor(string? nodeId, ScreenEdge edge)
    {
        var id = string.IsNullOrWhiteSpace(nodeId) ? TopologyHost.Id : nodeId;
        return _edges.TryGetValue(id, out var byEdge) && byEdge.TryGetValue(edge, out var to) ? to : null;
    }
}
