using System.Windows;
using System.Windows.Media;

namespace OmniLink.Core;

/// <summary>
/// v1.9.1: คณิตศาสตร์ viewport ของผัง topology (pan/zoom) — pure function ทดสอบได้ตรงๆ
/// โมเดล: screen = scale * canvasPoint + (tx, ty) เก็บใน Matrix (M11=M22=scale, OffsetX=tx, OffsetY=ty)
/// </summary>
public static class MapViewportMath
{
    public const double MinScale = 0.4;
    public const double MaxScale = 3.0;

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>matrix จาก scale + offset</summary>
    public static Matrix Matrix(double scale, double tx, double ty)
        => new(scale, 0, 0, scale, tx, ty);

    public static double ClampScale(double scale)
        => double.IsNaN(scale) || double.IsInfinity(scale) || scale < MinScale ? MinScale
           : scale > MaxScale ? MaxScale
           : scale;

    /// <summary>
    /// Fit-to-view: คำนวณ matrix ให้เนื้อหา (contentW×contentH) พอดี viewport (viewW×viewH)
    /// กึ่งกลางทั้งสองแกน — scale clamp ตาม Min/Max
    /// </summary>
    public static Matrix Fit(double contentW, double contentH, double viewW, double viewH)
    {
        if (contentW <= 0 || contentH <= 0 || viewW <= 0 || viewH <= 0 ||
            !IsFinite(contentW) || !IsFinite(contentH) || !IsFinite(viewW) || !IsFinite(viewH))
        {
            return Matrix(1, 0, 0);
        }
        var scale = ClampScale(Math.Min(viewW / contentW, viewH / contentH));
        var tx = (viewW - scale * contentW) / 2;
        var ty = (viewH - scale * contentH) / 2;
        return Matrix(scale, tx, ty);
    }

    /// <summary>
    /// ซูมที่จุด cursor (cx, cy ในพิกัด viewport) — จุดใต้เมาส์คงที่ระหว่างซูม
    /// factor &lt; 1 = ซูมออก, &gt; 1 = ซูมเข้า (ประกอบกับ scale เดิมแล้ว clamp)
    /// </summary>
    public static Matrix ZoomAt(Matrix current, double factor, double cx, double cy)
    {
        var scale = ClampScale(current.M11 * factor);
        // จุด canvas ใต้ cursor: p = (cursor - t) / s ต้องคงอยู่ที่เดิมหลังซูม
        var px = (cx - current.OffsetX) / current.M11;
        var py = (cy - current.OffsetY) / current.M22;
        // v1.9.1 (เจอจริง): matrix เดิมมี NaN (fit ตอน size ยังไม่รู้) = อย่าซูมต่อ — คืนเดิมไว้ก่อน
        if (!IsFinite(px) || !IsFinite(py) || !IsFinite(scale)) return current;
        return Matrix(scale, cx - scale * px, cy - scale * py);
    }

    /// <summary>pan: เลื่อน offset ตาม delta (หน่วย viewport px)</summary>
    public static Matrix Pan(Matrix current, double dx, double dy)
        => Matrix(current.M11, current.OffsetX + dx, current.OffsetY + dy);

    /// <summary>bounding rect ของเนื้อหาหลัง transform (ใช้ตรวจ "อยู่ใน viewport" ทุกจุด)</summary>
    public static Rect TransformRect(Matrix m, Rect canvasRect)
        => new(m.M11 * canvasRect.X + m.OffsetX, m.M22 * canvasRect.Y + m.OffsetY,
               m.M11 * canvasRect.Width, m.M22 * canvasRect.Height);
}
