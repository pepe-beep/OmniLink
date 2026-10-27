using System.Windows;
using System.Windows.Media;

namespace OmniLink.Core;

/// <summary>
/// v1.9.1: state machine ของ gesture บนผัง topology (pan/zoom map) — pure, unit-test ได้ทุก sequence
/// Phase: None → Press (Pending: คลิกหรือลากยังไม่ตัดสิน) → ลากเกิน threshold → Panning (ต่อเนื่องทุก move)
/// ปล่อยขณะ Pending = "click" (ghost วางเครื่อง) · ปล่อยขณะ Panning = จบ pan
/// บทเรียนจากบั๊กเดิม: ใช้ flag เดียวกันทั้ง "รอคลิก" และ "กำลัง pan" ทำให้ pan ตายหลังขยับครั้งแรก
/// และคลิกวางเครื่องตายตาม — แยก phase ชัดจึงไม่ชนกัน
/// </summary>
public sealed class MapGestureState
{
    public enum Phase { None, Pending, Panning }

    public const double Threshold = 8.0;

    public Phase Current { get; private set; } = Phase.None;
    private Point _start;
    private Matrix _startMatrix;

    /// <summary>กดลง — เริ่ม pending · threshold=true = ต้องลากเกิน 8px ก่อนเข้า pan (ghost มี click action)</summary>
    public void Press(Point pos, Matrix matrix, bool threshold)
    {
        Current = Phase.Pending;
        _start = pos;
        _startMatrix = matrix;
        ThresholdActive = threshold;
    }

    /// <summary>true = กดอยู่และรอตัดสิน (ยังไม่ลากเกิน threshold)</summary>
    public bool ThresholdActive { get; private set; }

    /// <summary>
    /// ขยับ — คืน matrix ใหม่เมื่อ pan เลื่อนจริง · null = ยังไม่เลื่อน (pending/threshold หรือปล่อยปุ่มแล้ว)
    /// </summary>
    public Matrix? Move(Point pos, bool leftDown)
    {
        switch (Current)
        {
            case Phase.Pending:
                if (!leftDown)
                {
                    Current = Phase.None;
                    return null;
                }
                var dx0 = pos.X - _start.X;
                var dy0 = pos.Y - _start.Y;
                if (ThresholdActive && Math.Abs(dx0) <= Threshold && Math.Abs(dy0) <= Threshold) return null;
                Current = Phase.Panning;
                // เข้าโหมด pan แล้วเลื่อนตาม delta จริงของ move นี้ทันที (ไม่กระโดด)
                return MapViewportMath.Pan(_startMatrix, dx0, dy0);
            case Phase.Panning:
                if (!leftDown)
                {
                    Current = Phase.None;
                    return null;
                }
                return MapViewportMath.Pan(_startMatrix, pos.X - _start.X, pos.Y - _start.Y);
            default:
                return null;
        }
    }

    /// <summary>ปล่อยปุ่ม — true = เป็น click (pending โดยไม่เคยลากเกิน threshold)</summary>
    public bool Release()
    {
        var wasClick = Current == Phase.Pending;
        Current = Phase.None;
        return wasClick;
    }

    /// <summary>ยกเลิก gesture (เช่น drag-drop ของการ์ดเข้ามาแทรก)</summary>
    public void Cancel() => Current = Phase.None;
}
