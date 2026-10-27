using System.Windows;
using System.Windows.Media;
using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v1.9.1: gesture state machine ของผัง — ครอบ sequence ทั้งหมดที่เคยเป็นบั๊ก:
/// pan ตายหลังขยับครั้งแรก / click-place ตาย / threshold / ปล่อยปุ่มกลางทาง
/// </summary>
public class MapGestureStateTests
{
    private static Matrix M(double scale = 1.0, double tx = 0, double ty = 0)
        => MapViewportMath.Matrix(scale, tx, ty);

    private static Point P(double x, double y) => new(x, y);

    [Fact]
    public void PressThenSmallMove_StaysPending_NoPan()
    {
        var g = new MapGestureState();
        g.Press(P(100, 100), M(), threshold: true);
        var m = g.Move(P(104, 103), leftDown: true); // ขยับ 4-3px < 8px
        Assert.Null(m);                              // ยังไม่ pan
        Assert.Equal(MapGestureState.Phase.Pending, g.Current);
    }

    /// <summary>บั๊กเดิม: pan ตายหลังขยับครั้งแรก — ตอนนี้ต้อง pan ต่อเนื่องทุก move</summary>
    [Fact]
    public void GhostDrag_CrossThreshold_PansContinuouslyOnEveryMove()
    {
        var g = new MapGestureState();
        var startMatrix = M(1.5, 10, 20);
        g.Press(P(100, 100), startMatrix, threshold: true);

        // ขยับครั้งแรกเกิน threshold → เข้า Panning + เลื่อนตาม delta จริง
        var m1 = g.Move(P(120, 110), leftDown: true);
        Assert.NotNull(m1);
        Assert.Equal(MapViewportMath.Pan(startMatrix, 20, 10), m1.Value);
        Assert.Equal(MapGestureState.Phase.Panning, g.Current);

        // บั๊กเดิมตรงนี้: move ถัดไปโดน guard ตัวเอง → แผนที่แช่ — ต้อง pan ต่อเนื่อง
        var m2 = g.Move(P(160, 130), leftDown: true);
        Assert.NotNull(m2);
        Assert.Equal(MapViewportMath.Pan(startMatrix, 60, 30), m2.Value);

        var m3 = g.Move(P(200, 160), leftDown: true);
        Assert.NotNull(m3);
        Assert.Equal(MapViewportMath.Pan(startMatrix, 100, 60), m3.Value);
    }

    /// <summary>กด ghost แล้วปล่อยโดยไม่ลาก = click (วางเครื่อง) — ไม่ใช่ pan</summary>
    [Fact]
    public void GhostClick_PressReleaseWithoutDrag_IsClick()
    {
        var g = new MapGestureState();
        g.Press(P(100, 100), M(), threshold: true);
        g.Move(P(103, 102), leftDown: true); // ขยับน้อย
        Assert.True(g.Release());            // = click
        Assert.Equal(MapGestureState.Phase.None, g.Current);
    }

    /// <summary>ลาก pan แล้วปล่อย = ไม่ใช่ click (ห้ามวางเครื่องตามหลัง pan)</summary>
    [Fact]
    public void GhostDrag_ThenRelease_IsNotClick()
    {
        var g = new MapGestureState();
        g.Press(P(100, 100), M(), threshold: true);
        g.Move(P(140, 140), leftDown: true); // เกิน threshold
        Assert.False(g.Release());
    }

    [Fact]
    public void GhostDrag_ReleaseButtonMidDrag_StopsPanning()
    {
        var g = new MapGestureState();
        g.Press(P(100, 100), M(), threshold: true);
        g.Move(P(130, 130), leftDown: true);
        Assert.Null(g.Move(P(150, 150), leftDown: false)); // ปล่อยปุ่ม → จบ
        Assert.Equal(MapGestureState.Phase.None, g.Current);
    }

    /// <summary>พื้นที่ว่าง (ไม่มี click action) = pan ทันทีไม่มี threshold</summary>
    [Fact]
    public void EmptyArea_PansImmediately_NoThreshold()
    {
        var g = new MapGestureState();
        g.Press(P(100, 100), M(), threshold: false);
        var m = g.Move(P(103, 102), leftDown: true); // ขยับ 3px ก็ pan
        Assert.NotNull(m);
        Assert.Equal(MapViewportMath.Pan(M(), 3, 2), m.Value);
    }

    [Fact]
    public void Pan_WorksAtZoomedScale()
    {
        var g = new MapGestureState();
        g.Press(P(100, 100), M(1.7, 40, -30), threshold: false);
        var m = g.Move(P(160, 150), leftDown: true);
        Assert.Equal(MapViewportMath.Pan(M(1.7, 40, -30), 60, 50), m.Value);
    }

    [Fact]
    public void MoveWithoutPress_NoOp()
    {
        var g = new MapGestureState();
        Assert.Null(g.Move(P(120, 120), leftDown: true));
        Assert.Equal(MapGestureState.Phase.None, g.Current);
    }

    [Fact]
    public void Cancel_ResetsPhase()
    {
        var g = new MapGestureState();
        g.Press(P(100, 100), M(), threshold: true);
        g.Cancel();
        Assert.Equal(MapGestureState.Phase.None, g.Current);
        Assert.Null(g.Move(P(150, 150), leftDown: true));
    }

    /// <summary>threshold ใช้ Chebyshev (แกนใดแกนหนึ่งเกิน = pan) — ลากแนวตั้งล้วนก็ทำงาน</summary>
    [Fact]
    public void GhostDrag_PureVertical_Pans()
    {
        var g = new MapGestureState();
        g.Press(P(100, 100), M(), threshold: true);
        var m = g.Move(P(100, 130), leftDown: true); // ลากล้วงแนวตั้ง 30px
        Assert.NotNull(m);
        Assert.Equal(MapViewportMath.Pan(M(), 0, 30), m.Value);
    }
}
