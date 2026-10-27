using System.Windows;
using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>v1.9.1: คณิต pan/zoom ของผัง topology — fit กึ่งกลาง, zoom จุด cursor นิ่ง, clamp 0.4-3.0</summary>
public class MapViewportMathTests
{
    [Fact]
    public void Fit_CentersContent_AndClamps()
    {
        var m = MapViewportMath.Fit(600, 300, 800, 400);
        Assert.Equal(800.0 / 600, m.M11, 5);              // scale = min(1.33, 1.33)
        Assert.Equal((800 - 800.0 / 600 * 600) / 2, m.OffsetX, 5); // tx = 0
        Assert.Equal((400 - 800.0 / 600 * 300) / 2, m.OffsetY, 5); // ty = 0 (พอดี)

        // เนื้อหาเล็กกว่า viewport → ซูมเข้าจนพอดี (scale = min(2.67, 2.67) ไม่ชน cap)
        var m2 = MapViewportMath.Fit(300, 150, 800, 400);
        Assert.Equal(800.0 / 300, m2.M11, 5);
        Assert.Equal((800 - 800.0 / 300 * 300) / 2, m2.OffsetX, 5); // กึ่งกลางแกน X
    }

    [Fact]
    public void Fit_TinyViewport_ClampsScale()
    {
        // viewport เล็กมาก → scale ต่ำกว่า 0.4 → clamp
        var m = MapViewportMath.Fit(600, 300, 100, 80);
        Assert.Equal(MapViewportMath.MinScale, m.M11, 5);
    }

    [Fact]
    public void Fit_HugeViewport_ClampsMaxScale()
    {
        var m = MapViewportMath.Fit(600, 300, 5000, 4000);
        Assert.Equal(MapViewportMath.MaxScale, m.M11, 5);
    }

    [Fact]
    public void Fit_InvalidSizes_ReturnsIdentity()
    {
        var m = MapViewportMath.Fit(0, 300, 800, 400);
        Assert.Equal(1, m.M11, 5);
        Assert.Equal(0, m.OffsetX, 5);
    }

    /// <summary>จุดใต้ cursor ต้องอยู่ที่เดิมหลังซูม (หัวใจของ zoom-at-cursor)</summary>
    [Fact]
    public void ZoomAt_KeepsCursorPointStationary()
    {
        var current = MapViewportMath.Matrix(1.0, 50, 30);
        const double cx = 400, cy = 250;
        // จุด canvas ใต้ cursor ก่อนซูม
        var px = (cx - current.OffsetX) / current.M11;
        var py = (cy - current.OffsetY) / current.M22;

        var after = MapViewportMath.ZoomAt(current, 1.25, cx, cy);
        var screenX = after.M11 * px + after.OffsetX;
        var screenY = after.M22 * py + after.OffsetY;
        Assert.Equal(cx, screenX, 5);
        Assert.Equal(cy, screenY, 5);
    }

    [Fact]
    public void ZoomAt_ClampsAtMinAndMax()
    {
        var m = MapViewportMath.Matrix(0.5, 0, 0);
        var min = MapViewportMath.ZoomAt(m, 0.1, 100, 100);   // 0.05 → clamp 0.4
        Assert.Equal(MapViewportMath.MinScale, min.M11, 5);

        var max = MapViewportMath.ZoomAt(m, 100, 100, 100);   // 50 → clamp 3.0
        Assert.Equal(MapViewportMath.MaxScale, max.M11, 5);
    }

    [Fact]
    public void ZoomAt_NanOrInfinityFactor_SafeClamp()
    {
        var m = MapViewportMath.Matrix(1.0, 0, 0);
        var bad = MapViewportMath.ZoomAt(m, double.NaN, 100, 100);
        Assert.Equal(MapViewportMath.MinScale, bad.M11, 5);
    }

    [Fact]
    public void Pan_ShiftsOffset_KeepsScale()
    {
        var m = MapViewportMath.Matrix(1.5, 10, 20);
        var p = MapViewportMath.Pan(m, 30, -40);
        Assert.Equal(1.5, p.M11, 5);
        Assert.Equal(40, p.OffsetX, 5);
        Assert.Equal(-20, p.OffsetY, 5);
    }

    /// <summary>TransformRect: การ์ดใน canvas → rect บนจอ (ใช้โดย assertion "ไม่เกินขอบ")</summary>
    [Fact]
    public void TransformRect_ScalesAndOffsets()
    {
        var m = MapViewportMath.Matrix(2.0, 100, 50);
        var r = MapViewportMath.TransformRect(m, new Rect(10, 20, 30, 40));
        Assert.Equal(120, r.X, 5);           // 2*10+100
        Assert.Equal(90, r.Y, 5);            // 2*20+50
        Assert.Equal(60, r.Width, 5);        // 2*30
        Assert.Equal(80, r.Height, 5);       // 2*40
    }

    /// <summary>End-to-end: fit แล้ว transform rect ของเนื้อหาทั้งก้อนต้องอยู่ใน viewport</summary>
    [Fact]
    public void FitThenTransform_WholeContentInsideViewport()
    {
        var m = MapViewportMath.Fit(600, 300, 800, 400);
        var content = MapViewportMath.TransformRect(m, new Rect(0, 0, 600, 300));
        Assert.True(content.X >= 0 && content.Y >= 0);
        Assert.True(content.Right <= 800 + 0.5 && content.Bottom <= 400 + 0.5);
    }
}
