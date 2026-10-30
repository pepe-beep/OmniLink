using OmniLink.Core;
using OmniLink.Core.Strategies;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// ทดสอบ semantics การสลับจอฝั่ง Host ตามมาตรฐาน Deskflow:
/// "hold ขอบค้าง ตาม switchDelayMs หรือ double-tap ภายใน DoubleTapMs — อย่างใดอย่างหนึ่งพอ"
/// บทเรียน 2026-10-20: factory ที่เลือก DoubleTap โดยค่าเดียว (DoubleTapMs>0) ทำให้
/// "ดันขอบเดี่ยวค้างไว้" ไม่มีวันสลับ = ผู้ใช้เข้าใจว่าโปรแกรมพัง
/// </summary>
public class SwitchingSemanticsTests
{
    private static OmniConfig Cfg(int delay, int doubleTap, int corner = 0)
        => new OmniConfig { SwitchDelayMs = delay, DoubleTapMs = doubleTap, CornerSize = corner };

    private const int W = 1920, H = 1080;
    private static readonly ScreenBounds Mon = new(0, 0, W, H);

    [Fact]
    public void FactoryDefault_PicksCombinedStrategy()
    {
        // แก้ไข BUG-2: config default (delay=250, tap=400) → factory เลือก CombinedEdgeStrategy
        // เพื่อรองรับ Deskflow semantics: ดันแช่ก็ได้ หรือ เคาะสองครั้งก็ได้ (อย่างใดอย่างหนึ่งพอ)
        var s = EdgeStrategyFactory.Create(Cfg(250, 400));
        Assert.IsType<CombinedEdgeStrategy>(s);
    }

    [Fact]
    public void Combined_SinglePushHold_SwitchesAfterDwellDelay()
    {
        var s = new CombinedEdgeStrategy();
        long t = 1000;
        var cfg = Cfg(250, 400);
        Assert.False(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t));        // จุดเริ่ม
        Assert.False(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t + 200));  // ยังไม่ครบ 250ms
        Assert.True(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t + 260));   // ครบ 250ms สลับทันที
    }

    [Fact]
    public void Combined_TwoTapsWithinWindow_Switches()
    {
        var s = new CombinedEdgeStrategy();
        long t = 1000;
        var cfg = Cfg(250, 400);
        Assert.False(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t));        // tap 1
        Assert.False(s.ShouldSwitch(1900, 540, ScreenEdge.Right, Mon, cfg, t + 60));   // ถอยออกจากขอบ
        Assert.True(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t + 120));   // tap 2 สลับทันที
    }

    [Fact]
    public void Dwell_SinglePushHold_SwitchesAfterDelay()
    {
        var s = new DwellDelayEdgeStrategy();
        long t = 1000;
        var cfg = Cfg(250, 0);
        Assert.False(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t));      // จับจองจุดเริ่ม
        Assert.False(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t + 200)); // ยังไม่ครบ
        Assert.True(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t + 260));  // ครบ 250ms
    }

    [Fact]
    public void Dwell_LeavingEdge_ResetsTimer()
    {
        var s = new DwellDelayEdgeStrategy();
        long t = 1000;
        var cfg = Cfg(250, 0);
        s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t);
        s.ShouldSwitch(1900, 540, ScreenEdge.Right, Mon, cfg, t + 100); // ออกจากขอบ → reset
        Assert.False(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t + 300)); // เริ่มนับใหม่
        Assert.True(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t + 560));
    }

    [Fact]
    public void DoubleTap_TwoTapsWithinWindow_Switches()
    {
        var s = new DoubleTapEdgeStrategy();
        long t = 1000;
        var cfg = Cfg(0, 400);
        Assert.False(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t));        // tap 1
        Assert.False(s.ShouldSwitch(1900, 540, ScreenEdge.Right, Mon, cfg, t + 60));   // ออกขอบ
        Assert.True(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t + 120));   // tap 2
    }

    [Fact]
    public void DoubleTap_SinglePushHold_NeverSwitches()
    {
        // GAP ที่ผู้ใช้เจอ: ดันค้าง 10 วินาที = ไม่มีวันสลับภายใต้ DoubleTap-only
        var s = new DoubleTapEdgeStrategy();
        var cfg = Cfg(0, 400);
        long t = 1000;
        s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t);
        Assert.False(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg, t + 10_000));
    }

    [Fact]
    public void Corner_DeadzoneBlocksSwitch()
    {
        var s = new InstantEdgeStrategy();
        var cfg = Cfg(0, 0, corner: 32);
        // ขอบขวา ใกล้มุมบน (y=10 < 32) → มุมขวาบนเปิด = บล็อก
        Assert.False(s.ShouldSwitch(1919, 10, ScreenEdge.Right, Mon, cfg));
        // กลางจอ → สลับได้
        Assert.True(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, cfg));
    }

    [Fact]
    public void Corner_DisabledCornerAllowsSwitch()
    {
        var s = new InstantEdgeStrategy();
        var cfg = Cfg(0, 0, corner: 32);
        cfg.CornerTopRight = false;
        Assert.True(s.ShouldSwitch(1919, 10, ScreenEdge.Right, Mon, cfg));
    }

    [Fact]
    public void LeftEdge_OnLeftSideMonitor_NegativeCoords()
    {
        // จอซ้ายวางที่ x=-1920 (เครื่องนี้มี 2 จอ: -1920..0 และ 0..1920)
        var leftMon = new ScreenBounds(-1920, 0, 0, H);
        var s = new InstantEdgeStrategy();
        Assert.True(s.ShouldSwitch(-1919, 540, ScreenEdge.Left, leftMon, Cfg(0, 0)));
        Assert.False(s.ShouldSwitch(-1800, 540, ScreenEdge.Left, leftMon, Cfg(0, 0)));
    }

    [Fact]
    public void Instant_TouchesEdge_SwitchesImmediately()
    {
        var s = new InstantEdgeStrategy();
        Assert.True(s.ShouldSwitch(1919, 540, ScreenEdge.Right, Mon, Cfg(0, 0)));
    }

    [Fact]
    public void TopEdge_SwitchesCorrectly()
    {
        var s = new InstantEdgeStrategy();
        // บนขอบบนสุด (y <= 4)
        Assert.True(s.ShouldSwitch(960, 2, ScreenEdge.Top, Mon, Cfg(0, 0)));
        // กลางจอ (y = 540)
        Assert.False(s.ShouldSwitch(960, 540, ScreenEdge.Top, Mon, Cfg(0, 0)));
    }

    [Fact]
    public void BottomEdge_SwitchesCorrectly()
    {
        var s = new InstantEdgeStrategy();
        // บนขอบล่างสุด (y >= 1080 - 4 = 1076)
        Assert.True(s.ShouldSwitch(960, 1079, ScreenEdge.Bottom, Mon, Cfg(0, 0)));
        // กลางจอ (y = 540)
        Assert.False(s.ShouldSwitch(960, 540, ScreenEdge.Bottom, Mon, Cfg(0, 0)));
    }

    [Fact]
    public void MultiMonitor_SecondaryMonitor_BottomAndTopEdge()
    {
        // จำลอง DISPLAY5: Left = -1536, Top = 1, Right = -256, Bottom = 721
        var mon5 = new ScreenBounds(-1536, 1, -256, 721);
        var s = new InstantEdgeStrategy();

        // ขอบล่างของจอรอง (-800, 720) -> ต้องสลับได้
        Assert.True(s.ShouldSwitch(-800, 720, ScreenEdge.Bottom, mon5, Cfg(0, 0)));

        // ขอบบนของจอรอง (-800, 2) -> ต้องสลับได้
        Assert.True(s.ShouldSwitch(-800, 2, ScreenEdge.Top, mon5, Cfg(0, 0)));
    }

    [Fact]
    public void FullscreenAndLock_AreHookLevelNotStrategy()
    {
        // เอกสารสถาปัตยกรรม: fullscreen/lock blocks อยู่ใน KvmHostService (hook layer)
        // strategy ต้องไม่รู้จักทั้งสอง — ป้องกันการย้าย logic ไปกองรวมกัน
        Assert.DoesNotContain(typeof(IEdgeSwitchStrategy).GetMethods(), m =>
            m.Name.Contains("Fullscreen", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Lock", StringComparison.OrdinalIgnoreCase));
    }
}
