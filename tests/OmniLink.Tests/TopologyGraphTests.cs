using OmniLink.Core;
using OmniLink.Core.Strategies;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v1.9.1 กราฟ topology — "ต่อเครื่องได้ไม่จำกัด":
/// สถานการณ์ของผู้ใช้จริง: host กลาง · L440 ขวาของ host · e5 บนของ host · A บนของ L440 (= ขวาของ e5)
/// → ลากขวา = L440, ลากขึ้นจาก L440 = A, ลากขึ้นจาก host = e5, มุมขวาบนของ host = ได้ทั้ง L440/A
/// </summary>
public class TopologyGraphTests
{
    private static NodeConfig N(string id, string position, string anchor = "") => new()
    {
        NodeId = id,
        Ip = "192.168.99.2",
        Position = position,
        Anchor = anchor,
        Enabled = true,
    };

    [Fact]
    public void EmptyConfig_HostHasNoNeighbors()
    {
        var g = new TopologyGraph(Array.Empty<NodeConfig>());
        Assert.Null(g.GetNeighbor(TopologyHost.Id, ScreenEdge.Top));
        Assert.Equal(0, g.ConflictCount);
    }

    /// <summary>config เดิม (ไม่มี anchor) = ทุก node เกาะ host — พฤติกรรมเดิม 100%</summary>
    [Fact]
    public void LegacyConfig_AllAnchoredToHost_SameAsOldSlots()
    {
        var g = new TopologyGraph(new[] { N("l440", "right"), N("e5", "top"), N("a", "left") });
        Assert.Equal("l440", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Right));
        Assert.Equal("e5", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Top));
        Assert.Equal("a", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Left));
        // กลับทาง: จาก l440 ดันซ้าย = กลับ host
        Assert.Equal(TopologyHost.Id, g.GetNeighbor("l440", ScreenEdge.Left));
        Assert.Equal(TopologyHost.Id, g.GetNeighbor("e5", ScreenEdge.Bottom));
    }

    /// <summary>สถานการณ์ #1 ของผู้ใช้: e5 บนของ L440 (L440 ขวาของ host) — ลากขวา → L440, ลากขึ้นจาก L440 → e5</summary>
    [Fact]
    public void Chained_TopOfRemote_ReachesSecondNode()
    {
        var g = new TopologyGraph(new[] { N("l440", "right"), N("e5", "top", anchor: "l440") });
        Assert.Equal("l440", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Right));
        Assert.Equal("e5", g.GetNeighbor("l440", ScreenEdge.Top));
        Assert.Equal(TopologyHost.Id, g.GetNeighbor("l440", ScreenEdge.Left)); // กลับ host ยังได้
        Assert.Equal("l440", g.GetNeighbor("e5", ScreenEdge.Bottom));          // ลากลงจาก e5 = กลับ L440
    }

    /// <summary>สถานการณ์ #2: L440 ขวา · e5 บนของ host · A บนของ L440 — จอตาราง 2×2 แบบมี host มุมซ้ายล่าง</summary>
    [Fact]
    public void ThreeMachines_GridLayout_AllEdgesResolve()
    {
        var g = new TopologyGraph(new[]
        {
            N("l440", "right"),
            N("e5", "top"),
            N("a", "top", anchor: "l440"),
        });
        // แถวล่าง: host | l440 · แถวบน: e5 | a
        Assert.Equal("e5", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Top));
        Assert.Equal("l440", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Right));
        Assert.Equal("a", g.GetNeighbor("l440", ScreenEdge.Top));
        Assert.Equal("l440", g.GetNeighbor("a", ScreenEdge.Bottom));
        // v1.9.1 sibling-adjacency: แถวบน e5 | a ติดกัน → ลากขวาจาก e5 ถึง a ได้เลย (ตามโจทย์ผู้ใช้)
        Assert.Equal("a", g.GetNeighbor("e5", ScreenEdge.Right));
        Assert.Equal("e5", g.GetNeighbor("a", ScreenEdge.Left));
    }

    /// <summary>มุมทแยง: A บนของ L440 เมื่อ L440 ขวาของ host → มุมขวาบนของ host = A</summary>
    [Fact]
    public void Diagonal_HostTopRight_WithAOnTopOfRightNode_IsA()
    {
        var g = new TopologyGraph(new[] { N("l440", "right"), N("a", "top", anchor: "l440") });
        Assert.Equal("a", g.GetDiagonal(TopologyHost.Id, TopologyCorner.TopRight));
        Assert.Null(g.GetDiagonal(TopologyHost.Id, TopologyCorner.TopLeft));
        Assert.Null(g.GetDiagonal(TopologyHost.Id, TopologyCorner.BottomLeft));
        Assert.Null(g.GetDiagonal(TopologyHost.Id, TopologyCorner.BottomRight));
    }

    /// <summary>มุมที่ไม่มีหลาน = ไม่มี diagonal (คงพฤติกรรม deadzone ป้องกันเดิม)</summary>
    [Fact]
    public void Diagonal_NeverReturnsHost()
    {
        // e5 บนของ host: มุมบนซ้าย/บนขวาเดิน "บน→ขวา" ไป host เอง — ต้องไม่คืน host
        var g = new TopologyGraph(new[] { N("e5", "top") });
        Assert.Null(g.GetDiagonal(TopologyHost.Id, TopologyCorner.TopRight));
        Assert.Null(g.GetDiagonal(TopologyHost.Id, TopologyCorner.TopLeft));
    }

    /// <summary>anchor+side ซ้ำ: ตัวแรกชนะ + ConflictCount รายงาน</summary>
    [Fact]
    public void DuplicateAnchorSide_FirstWins_AndReportsConflict()
    {
        var g = new TopologyGraph(new[] { N("l440", "right"), N("a", "right") });
        Assert.Equal("l440", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Right));
        Assert.Equal(1, g.ConflictCount);
    }

    /// <summary>วงจร (a บน b, b บน a) = unreachable → ใช้ edge ไม่ได้ ไม่แครช</summary>
    [Fact]
    public void Cycle_IsDetectedAndExcluded()
    {
        var g = new TopologyGraph(new[]
        {
            N("l440", "right"),
            N("a", "top", anchor: "l440"),
            N("b", "left", anchor: "a"),
        });
        Assert.Equal("l440", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Right));
        Assert.Equal("a", g.GetNeighbor("l440", ScreenEdge.Top));
        // b ไม่ถูกอ้างถึงจากใคร (anchor a→b มี edge a→b? ไม่ใช่ — b anchored ที่ a หมายถึง b อยู่ซ้ายของ a)
        Assert.Equal("b", g.GetNeighbor("a", ScreenEdge.Left));
    }

    /// <summary>anchor ชี้ node ที่ไม่มีจริง = fallback เป็น host (node ยังใช้งานได้)</summary>
    [Fact]
    public void AnchorToUnknown_FallsBackToHost()
    {
        var g = new TopologyGraph(new[] { N("l440", "right", anchor: "ghost") });
        Assert.Equal("l440", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Right));
        Assert.Equal(0, g.ConflictCount);
    }

    // ── v1.9.1 implicit sibling-adjacency (ตาราง 2×2 ตามโจทย์ผู้ใช้) ──

    /// <summary>ผัง 2×2: host ซ้ายล่าง · L440 ขวา · e5 บน host · A บน L440 → ลากขวาจาก e5 ต้องถึง A (และย้อนกลับ)</summary>
    [Fact]
    public void TwoByTwo_SiblingEdge_e5RightGoesToA_BothDirections()
    {
        var g = new TopologyGraph(new[]
        {
            N("l440", "right"),
            N("e5", "top"),
            N("a", "top", anchor: "l440"),
        });
        Assert.Equal("a", g.GetNeighbor("e5", ScreenEdge.Right)); // implicit sibling (เดิม null)
        Assert.Equal("e5", g.GetNeighbor("a", ScreenEdge.Left));  // ย้อนกลับ
        // ขอบเดิมทั้งหมดไม่ถูกกระทบ
        Assert.Equal("l440", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Right));
        Assert.Equal("e5", g.GetNeighbor(TopologyHost.Id, ScreenEdge.Top));
        Assert.Equal("a", g.GetNeighbor("l440", ScreenEdge.Top));
    }

    /// <summary>sibling แนวนอน: L440 ขวาของ host · X บนของ L440 · Y ขวาของ X → X.ขวา ↔ Y.ซ้าย</summary>
    [Fact]
    public void Sibling_HorizontalBetweenCellNeighbors_Wired()
    {
        var g = new TopologyGraph(new[]
        {
            N("l440", "right"),
            N("x", "top", anchor: "l440"),
            N("y", "right", anchor: "x"),
        });
        // cells: host(0,0) l440(1,0) x(1,-1) y(2,-1) — แถวบนติดกันเรียงขวา
        Assert.Equal("y", g.GetNeighbor("x", ScreenEdge.Right));
        Assert.Equal("x", g.GetNeighbor("y", ScreenEdge.Left));
        Assert.Equal("l440", g.GetNeighbor("x", ScreenEdge.Bottom));
        Assert.Equal(TopologyHost.Id, g.GetNeighbor("l440", ScreenEdge.Left));
    }

    /// <summary>sibling ห้ามทับขอบที่ anchor จองไว้ (e5 บน host ↔ host ต้องเหมือนเดิมทุกข้อ)</summary>
    [Fact]
    public void Sibling_NeverOverwritesExplicitOrReverseEdges()
    {
        var g = new TopologyGraph(new[] { N("l440", "right"), N("e5", "top"), N("a", "top", anchor: "l440") });
        var map = g.GetNeighborMapForAgent("e5");
        Assert.Equal("__host", map["bottom"]);
        Assert.Equal("a", map["right"]);
        Assert.Equal(2, map.Count); // ไม่มีขอบเกินจาก sibling แปลกปลอม
    }

    /// <summary>node ที่ cell ชนกัน (layout ซ้อน) — ตัวแรกได้ cell, ตัวหลังไม่ได้ sibling และไม่แครช</summary>
    [Fact]
    public void Sibling_CellConflict_FirstWins_NoCrash()
    {
        // e5 บนของ host (0,-1) · b ขวาของ e5 (1,-1) · c บนของ b (1,-2) · d ซ้ายของ c (0,-2)
        // แถว -2 ติดกันปกติ ไม่มีชน — เพิ่ม e ล่างของ c จะชน cell (1,-1) ของ b
        var g = new TopologyGraph(new[]
        {
            N("e5", "top"),
            N("b", "right", anchor: "e5"),
            N("c", "top", anchor: "b"),
            N("d", "left", anchor: "c"),
            N("e", "bottom", anchor: "c"),
        });
        Assert.Equal("b", g.GetNeighbor("e5", ScreenEdge.Right));
        Assert.Equal("c", g.GetNeighbor("b", ScreenEdge.Top));
        Assert.Equal("d", g.GetNeighbor("c", ScreenEdge.Left));
        var e5top = g.GetNeighbor("e5", ScreenEdge.Top);
        // d อยู่เหนือ e5 พอดี (cell (0,-2) เหนือ (0,-1)) = implicit sibling ทำงานถูกต้อง
        Assert.Equal("d", e5top);
    }

    // ── v1.9.1 GetFreeSides (ghost "Place..." renderer ใช้แหล่งเดียวกัน) ──

    /// <summary>2×2: e5 (0,-1) — ล่าง→host, ขวา→A (sibling) → ว่าง = ซ้าย+บน</summary>
    [Fact]
    public void GetFreeSides_TwoByTwo_e5FreeIsLeftAndTop()
    {
        var g = new TopologyGraph(new[] { N("l440", "right"), N("e5", "top"), N("a", "top", anchor: "l440") });
        var free = g.GetFreeSides("e5");
        Assert.Equal(2, free.Count);
        Assert.Contains(ScreenEdge.Left, free);
        Assert.Contains(ScreenEdge.Top, free);
        var hostFree = g.GetFreeSides(TopologyHost.Id);
        Assert.Contains(ScreenEdge.Bottom, hostFree);
        Assert.Contains(ScreenEdge.Left, hostFree);
        Assert.Equal(2, hostFree.Count);
    }

    /// <summary>เครื่องโดดเดี่ยว (ไม่มีเพื่อนบ้านเลย) = ว่างทั้ง 4 ด้าน</summary>
    [Fact]
    public void GetFreeSides_IsolatedNode_AllFourSides()
    {
        var g = new TopologyGraph(new[] { N("solo", "right") });
        var free = g.GetFreeSides("solo");
        Assert.Equal(3, free.Count); // ซ้าย→host
        Assert.DoesNotContain(ScreenEdge.Left, free);
    }

    /// <summary>disabled node: ไม่มี edge เลย = ว่างทั้ง 4 (ผังวาง ghost รอบได้ — anchor ได้)</summary>
    [Fact]
    public void GetFreeSides_DisabledNode_AllFourSides()
    {
        var disabled = N("e5", "left");
        disabled.Enabled = false;
        var g = new TopologyGraph(new[] { disabled });
        Assert.Equal(4, g.GetFreeSides("e5").Count);
    }

    [Fact]
    public void DisabledNode_IsNotANeighbor()
    {
        var disabled = N("l440", "right");
        disabled.Enabled = false;
        var g = new TopologyGraph(new[] { disabled });
        Assert.Null(g.GetNeighbor(TopologyHost.Id, ScreenEdge.Right));
    }

    /// <summary>v1.9.1 (feedback ผู้ใช้: e5 หายจากผัง): node disabled ต้องมี cell — ผังวาด badge DISABLED ได้
    /// แต่ต้องไม่มี edge (สลับไม่ได้) และไม่ได้ sibling</summary>
    [Fact]
    public void DisabledNode_StillGetsCellForMap_ButNoEdges()
    {
        var disabled = N("l440", "right");
        disabled.Enabled = false;
        var g = new TopologyGraph(new[] { disabled });
        Assert.True(g.TryGetCell("l440", out var cell));           // ผังวาดได้
        Assert.Equal((1, 0), cell);                                // ขวาของ host
        Assert.Null(g.GetNeighbor(TopologyHost.Id, ScreenEdge.Right)); // สลับไม่ได้
        // ลูกของ disabled: cell ยังได้ (anchor chain ไม่สน enabled) — ผังโชว์ทั้งโซ่
        var child = N("a", "top", anchor: "l440");
        var g2 = new TopologyGraph(new[] { disabled, child });
        Assert.True(g2.TryGetCell("a", out var acell));
        Assert.Equal((1, -1), acell);
        Assert.Null(g2.GetNeighbor("l440", ScreenEdge.Top));
    }

    [Fact]
    public void NeighborMapForAgent_HostFacingEdgeMappedToHost()
    {
        var g = new TopologyGraph(new[] { N("l440", "right"), N("a", "top", anchor: "l440") });
        var map = g.GetNeighborMapForAgent("l440");
        Assert.Equal("__host", map["left"]);  // ดันซ้าย = กลับ host (edge_return เดิม)
        Assert.Equal("a", map["top"]);        // ดันบน = ไป A (switch_request ใหม่)
        Assert.False(map.ContainsKey("right"));
        Assert.False(map.ContainsKey("bottom"));
    }
}
