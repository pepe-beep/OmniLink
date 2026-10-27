using OmniLink.Core.Strategies;

namespace OmniLink.Core;

/// <summary>id ของ host ในกราฟ topology (node จริงใช้ NodeId)</summary>
public static class TopologyHost
{
    public const string Id = "__host";
}

/// <summary>มุมของจอ — ใช้กับการสลับเชิงมุมทแยง (corner diagonal switch)</summary>
public enum TopologyCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// v1.9.1 กราฟ topology แบบ "ต่อได้ไม่จำกัด" — node แต่ละตัวมี anchor (เกาะข้างเครื่องไหน)
/// + position (ด้านไหนของเครื่องนั้น) แทนที่จะเป็นสล็อตตายรอบ host เท่านั้น
/// - สร้างจาก Config.Nodes (NodeConfig.Anchor ว่าง = เกาะ host — ย้อนหลังเข้ากับ config เดิม 100%)
/// - edge สองทางเสมอ (host→L440 = right ⇒ L440→host = left) เพื่อให้ "ลากกลับ" จากทุกเครื่องได้
/// - ตัดวงจร/orphan ด้วย BFS จาก host (node ที่ไปไม่ถึง = สลับด้วย edge ไม่ได้ แต่ยังใช้ hotkey/ปุ่มได้)
/// - duplicate (anchor+side ซ้ำ) = ตัวแรกชนะ + นับ ConflictCount ให้ UI เตือน
/// - implicit sibling edges: node ที่ cell ติดกัน (จากผัง anchor) และขอบหันเข้าหากันว่าง = เติมให้ (ปิดช่องตาราง 2×2 เช่น e5 ขวา -> A)
/// - cell มอบให้ "ทุก node รวม disabled" (ผังโชว์ badge DISABLED เหมือนเดิม) — แต่ edges/siblings เฉพาะ node ที่เปิดใช้งาน
/// pure class — ไม่แตะ Win32/network ทดสอบได้ตรงๆ
/// </summary>
public sealed class TopologyGraph
{
    private readonly Dictionary<string, Dictionary<ScreenEdge, string>> _edges = new();

    private readonly Dictionary<string, (int X, int Y)> _cells = new();

    /// <summary>cell ของแต่ละ node (จาก anchor chain — host=(0,0)) ใช้วาด minimap; node ตกวงจร/ชน cell = ไม่มี</summary>
    public IReadOnlyDictionary<string, (int X, int Y)> Cells => _cells;

    /// <summary>จำนวน (anchor+side) ที่ชนกัน — ตัวแรกชนะ ตัวถัดไปถูกข้าม (UI ใช้เตือน)</summary>
    public int ConflictCount { get; private set; }

    /// <summary>node ที่ BFS จาก host ไปไม่ถึง (anchor เป็นวงจรหรือชี้ node ที่ไม่มีจริง)</summary>
    public List<string> Unreachable { get; } = new();

    public TopologyGraph(IReadOnlyList<NodeConfig> nodes)
    {
        var byId = new Dictionary<string, NodeConfig>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            byId[n.NodeId] = n; // config normalize กัน id ซ้ำแล้ว — ตัวหลังทับ (ปลอดภัย)
        }

        // ── pass 0: cells ของ "ทุก node" (รวม disabled) — เดิน anchor chain จาก node ไปหาราก ──
        // (host=(0,0); ลูกขยับตามทิศ anchor; วงจร/ชน cell = ไม่มี cell — ผังไม่วาดแต่ไม่แครช)
        var cells = new Dictionary<string, (int X, int Y)> { [TopologyHost.Id] = (0, 0) };
        var cellOwner = new Dictionary<(int X, int Y), string> { [(0, 0)] = TopologyHost.Id };
        _cells[TopologyHost.Id] = (0, 0);
        foreach (var n in nodes)
        {
            AssignCell(n.NodeId, cells, cellOwner, byId);
        }

        // ── pass 1: directed edges จาก anchor+position (เฉพาะ node ที่เปิดใช้งาน; first-wins ต่อ anchor+side) ──
        foreach (var n in nodes)
        {
            if (!n.Enabled) continue;
            var anchor = ResolveAnchor(n.Anchor, nodes);
            if (anchor is null)
            {
                Unreachable.Add(n.NodeId); // anchor ชี้ตัวเอง/วงจร — ตัดสินใจใน pass 3
                continue;
            }
            var side = ParseEdge(n.Position);
            AddEdge(anchor, side, n.NodeId);
        }

        // ── pass 2: reverse edges (สองทาง) — สลับกลับจากลูกไปพ่อได้เสมอ ──
        var direct = _edges.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(e => e.Key, e => e.Value));
        foreach (var (from, byEdge) in direct)
        {
            foreach (var (edge, to) in byEdge)
            {
                AddEdge(to, Opposite(edge), from, isReverse: true); // reverse ไม่นับเป็น conflict
            }
        }

        // ── pass 3: BFS จาก host — ตัด edge ที่ปลายทาง/ต้นทางไปไม่ถึง (วงจร/orphan) ──
        var reachable = new HashSet<string> { TopologyHost.Id };
        var queue = new Queue<string>();
        queue.Enqueue(TopologyHost.Id);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!_edges.TryGetValue(cur, out var byEdge)) continue;
            foreach (var to in byEdge.Values)
            {
                if (reachable.Add(to)) queue.Enqueue(to);
            }
        }
        foreach (var orphan in Unreachable.ToArray())
        {
            if (reachable.Contains(orphan)) Unreachable.Remove(orphan);
        }
        foreach (var (node, byEdge) in _edges.ToArray())
        {
            if (!reachable.Contains(node))
            {
                _edges.Remove(node);
                continue;
            }
            foreach (var (edge, to) in byEdge.ToArray())
            {
                if (!reachable.Contains(to)) byEdge.Remove(edge);
            }
        }

        // ── pass 4 (v1.9.1): implicit sibling-adjacency — ปิดช่อง "ตาราง 2×2" ──
        // คู่ที่ cell ติดกัน (ระยะ 1 แนวนอน/แนวตั้ง) และขอบ "หันเข้าหากัน" ยังว่างทั้งสองฝั่ง
        // = เติม edge ให้ทั้งสองทาง (เช่น e5.Right -> A.Left) — implicit ไม่นับ conflict
        // ไม่แตะขอบที่ถูก anchor จองไว้แล้ว · จองคู่เฉพาะ node ที่เปิดใช้งานทั้งสองฝั่ง
        bool Linkable(string id) => id == TopologyHost.Id ||
            (byId.TryGetValue(id, out var nn) && nn.Enabled);

        void TrySibling(string a, ScreenEdge aEdge, string b, ScreenEdge bEdge)
        {
            if (!Linkable(a) || !Linkable(b)) return; // disabled = แสดงบนผังแต่ไม่รับ edge
            if (GetNeighbor(a, aEdge) is null && GetNeighbor(b, bEdge) is null)
            {
                _edges[a][aEdge] = b;
                if (!_edges.TryGetValue(b, out var be)) _edges[b] = be = new Dictionary<ScreenEdge, string>();
                be[bEdge] = a;
            }
        }

        foreach (var (a, ca) in cells.OrderBy(kv => kv.Value.Y).ThenBy(kv => kv.Value.X).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList())
        {
            // เพื่อนบ้านขวา (x+1, y)
            if (cellOwner.TryGetValue((ca.X + 1, ca.Y), out var rightOwner) && rightOwner != a)
            {
                TrySibling(a, ScreenEdge.Right, rightOwner, ScreenEdge.Left);
            }
            // เพื่อนบ้านล่าง (x, y+1)
            if (cellOwner.TryGetValue((ca.X, ca.Y + 1), out var bottomOwner) && bottomOwner != a)
            {
                TrySibling(a, ScreenEdge.Bottom, bottomOwner, ScreenEdge.Top);
            }
        }
    }

    /// <summary>
    /// คำนวณ cell ของ node แบบ recursive ตาม anchor chain (memo ใน cells/cellOwner)
    /// — วงจร/ชี้ตัวเอง = ไม่มี cell · anchor ไม่มีจริง = ถือว่าเกาะ host · cell ชน = ตัวแรกชนะ
    /// </summary>
    private void AssignCell(string nodeId, Dictionary<string, (int X, int Y)> cells,
        Dictionary<(int X, int Y), string> cellOwner, Dictionary<string, NodeConfig> byId)
    {
        if (cells.ContainsKey(nodeId)) return;
        var path = new List<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var cur = nodeId;
        while (true)
        {
            if (cells.TryGetValue(cur, out var known))
            {
                break; // ถึงรากที่รู้ cell แล้ว (host หรือ node ที่คำนวณก่อนหน้า)
            }
            if (!visiting.Add(cur)) return; // วงจร — ทั้ง path ไม่มี cell
            path.Add(cur);
            var node = byId.TryGetValue(cur, out var nn) ? nn : null;
            if (node is null) break; // ปลายทางหลุด — ไม่ควรเกิด (caller เดินจาก node จริง)
            var anchorId = node.Anchor?.Trim() ?? "";
            if (anchorId.Length == 0 || anchorId == cur || !byId.ContainsKey(anchorId))
            {
                break; // เกาะ host (หรือ anchor หลุด → host ตาม ResolveAnchor) — host มี cell (0,0) อยู่แล้ว
            }
            cur = anchorId;
        }

        // กลับลงตาม path (ราก → ปลาย) คำนวณ cell ทีละขั้น
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var id = path[i];
            var node = byId[id];
            var parentCell = (0, 0);
            var anchorId = node.Anchor?.Trim() ?? "";
            if (anchorId.Length > 0 && cells.TryGetValue(anchorId, out var pc))
            {
                parentCell = pc;
            }
            var off = Offset(ParseEdge(node.Position));
            var cell = (parentCell.Item1 + off.X, parentCell.Item2 + off.Y);
            if (cellOwner.TryGetValue(cell, out var owner) && owner != id)
            {
                return; // ชน cell — node นี้และลูกโซ่ลงไปไม่มี cell (ผังไม่วาด)
            }
            cellOwner[cell] = id;
            cells[id] = cell;
            _cells[id] = cell;
        }
    }

    /// <summary>anchor ที่ใช้ได้ = node อื่นที่มีจริง (ว่าง = host) · ชี้ตัวเอง = null (วงจรทันที)</summary>
    private static string? ResolveAnchor(string? anchor, IReadOnlyList<NodeConfig> nodes)
    {
        if (string.IsNullOrWhiteSpace(anchor)) return TopologyHost.Id;
        return nodes.Any(n => n.NodeId == anchor) ? anchor : TopologyHost.Id;
    }

    private void AddEdge(string from, ScreenEdge edge, string to, bool isReverse = false)
    {
        if (from == to) return;
        if (!_edges.TryGetValue(from, out var byEdge))
        {
            _edges[from] = byEdge = new Dictionary<ScreenEdge, string>();
        }
        if (byEdge.ContainsKey(edge))
        {
            if (!isReverse) ConflictCount++;
            return; // first-wins
        }
        byEdge[edge] = to;
    }

    /// <summary>เพื่อนบ้านของ node ทาง edge ที่กำหนด (nodeId null/ว่าง = host) — ไม่มี = null</summary>
    public string? GetNeighbor(string? nodeId, ScreenEdge edge)
    {
        var id = string.IsNullOrWhiteSpace(nodeId) ? TopologyHost.Id : nodeId;
        return _edges.TryGetValue(id, out var byEdge) && byEdge.TryGetValue(edge, out var to) ? to : null;
    }

    /// <summary>
    /// ด้าน "ว่าง" ทั้งหมดของ node (ไม่มีเพื่อนบ้าน) — ghost "Place..." วางตรงนี้
    /// (host = TopologyHost.Id · node ที่ไม่มี cell ก็คำนวณจาก edges ได้ปกติ)
    /// </summary>
    public List<ScreenEdge> GetFreeSides(string? nodeId)
    {
        var result = new List<ScreenEdge>();
        foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
        {
            if (GetNeighbor(nodeId, edge) is null) result.Add(edge);
        }
        return result;
    }

    /// <summary>cell ของ node (host = (0,0)) — false ถ้า node ไม่มี cell (orphan/ชน cell)</summary>
    public bool TryGetCell(string nodeId, out (int X, int Y) cell)
    {
        var id = string.IsNullOrWhiteSpace(nodeId) ? TopologyHost.Id : nodeId;
        return _cells.TryGetValue(id, out cell);
    }

    /// <summary>
    /// เพื่อนบ้านทแยงของมุม — เดินกราฟสองชั้น: host→right→top หรือ host→top→right (กำหนดเส้นทางแนวนอนก่อน)
    /// ใช้กับ "ลากเมาส์จากมุม host ไปหาเครื่องที่วางทแยง" (เช่น A บนของ L440 เมื่อ L440 อยู่ขวาของ host)
    /// </summary>
    public string? GetDiagonal(string? nodeId, TopologyCorner corner)
    {
        var (firstEdge, secondEdge) = corner switch
        {
            TopologyCorner.TopRight => (ScreenEdge.Right, ScreenEdge.Top),
            TopologyCorner.BottomRight => (ScreenEdge.Right, ScreenEdge.Bottom),
            TopologyCorner.TopLeft => (ScreenEdge.Left, ScreenEdge.Top),
            _ => (ScreenEdge.Left, ScreenEdge.Bottom),
        };
        var viaFirst = GetNeighbor(nodeId, firstEdge);
        var diagA = viaFirst is null ? null : GetNeighbor(viaFirst, secondEdge);
        if (diagA is not null && diagA != TopologyHost.Id) return diagA;

        var viaSecond = GetNeighbor(nodeId, secondEdge);
        var diagB = viaSecond is null ? null : GetNeighbor(viaSecond, firstEdge);
        if (diagB is not null && diagB != TopologyHost.Id) return diagB;

        // ห้ามคืน host เป็น diagonal (สลับเข้าตัวเอง = no-op)
        if (diagA is not null && diagA != TopologyHost.Id) return diagA;
        if (diagB is not null && diagB != TopologyHost.Id) return diagB;
        return null;
    }

    /// <summary>แผนที่ของ node หนึ่งตัว (edge → ปลายทาง "__host" หรือ node id) — ใช้ยิง set_layout ให้ agent</summary>
    public Dictionary<string, string> GetNeighborMapForAgent(string? nodeId)
    {
        var id = string.IsNullOrWhiteSpace(nodeId) ? TopologyHost.Id : nodeId;
        var map = new Dictionary<string, string>();
        if (_edges.TryGetValue(id, out var byEdge))
        {
            foreach (var (edge, to) in byEdge)
            {
                map[EdgeToString(edge)] = to;
            }
        }
        return map;
    }

    /// <summary>cell offset ต่อทิศ (top = ขึ้น = y-1 ตามแนวภาพ)</summary>
    public static (int X, int Y) Offset(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Right => (1, 0),
        ScreenEdge.Left => (-1, 0),
        ScreenEdge.Top => (0, -1),
        _ => (0, 1),
    };

    public static ScreenEdge Opposite(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => ScreenEdge.Right,
        ScreenEdge.Right => ScreenEdge.Left,
        ScreenEdge.Top => ScreenEdge.Bottom,
        _ => ScreenEdge.Top,
    };

    public static ScreenEdge ParseEdge(string? position) => position?.ToLowerInvariant() switch
    {
        "left" => ScreenEdge.Left,
        "top" => ScreenEdge.Top,
        "bottom" => ScreenEdge.Bottom,
        _ => ScreenEdge.Right,
    };

    public static string EdgeToString(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => "left",
        ScreenEdge.Right => "right",
        ScreenEdge.Top => "top",
        _ => "bottom",
    };
}
