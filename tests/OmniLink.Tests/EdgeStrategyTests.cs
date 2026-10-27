using OmniLink.Core;
using OmniLink.Core.Strategies;
using OmniLink.Interop;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// Unit Tests สำหรับ Strategy Pattern ในการตัดสินใจข้ามขอบจอ (CP353002)
/// ครอบคลุม: InstantEdgeStrategy, DwellDelayEdgeStrategy, DoubleTapEdgeStrategy, Deadzone
/// </summary>
public class EdgeStrategyTests
{
    private readonly ScreenBounds _testMonitor = new(0, 0, 1920, 1080);

    [Fact]
    public void InstantEdgeStrategy_ShouldSwitchImmediately_WhenOnEdge()
    {
        var strategy = new InstantEdgeStrategy();
        var config = new OmniConfig { SwitchDelayMs = 0, DoubleTapMs = 0 };

        // บนขอบขวา (1919 >= 1920 - 2)
        Assert.True(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config));
        // บนขอบซ้าย (1 <= 0 + 2)
        Assert.True(strategy.ShouldSwitch(1, 500, ScreenEdge.Left, _testMonitor, config));
        // กลางจอ (500)
        Assert.False(strategy.ShouldSwitch(500, 500, ScreenEdge.Right, _testMonitor, config));
    }

    [Fact]
    public void EdgeStrategy_ShouldBlock_WhenInCornerDeadzone()
    {
        var strategy = new InstantEdgeStrategy();
        var config = new OmniConfig
        {
            CornerSize = 32,
            CornerTopRight = true,
            CornerBottomRight = true,
            CornerTopLeft = true,
            CornerBottomLeft = true
        };

        // มุมบนขวา (1919, 10 <= 32) -> ต้องโดนบล็อก
        Assert.False(strategy.ShouldSwitch(1919, 10, ScreenEdge.Right, _testMonitor, config));
        // ขอบขวากลางจอ (1919, 500) -> ไม่อยู่ในมุม ต้องผ่านได้
        Assert.True(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config));
    }

    [Fact]
    public void DwellDelayStrategy_ShouldRequireContinuousHold()
    {
        var strategy = new DwellDelayEdgeStrategy();
        var config = new OmniConfig { SwitchDelayMs = 250 };

        // Tick 1: แตะขอบขวาครั้งแรก -> ยังไม่สลับ (เริ่มนับเวลา)
        Assert.False(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1000));

        // Tick 2: แช่ต่อมา 150ms (nowMs = 1150) -> ยังไม่ถึง 250ms
        Assert.False(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1150));

        // Tick 3: แช่ต่อมา 260ms (nowMs = 1260) -> ครบกำหนด 250ms -> สลับจอทันที!
        Assert.True(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1260));
    }

    [Fact]
    public void DwellDelayStrategy_ShouldReset_IfCursorLeavesEdge()
    {
        var strategy = new DwellDelayEdgeStrategy();
        var config = new OmniConfig { SwitchDelayMs = 250 };

        // แตะขอบ
        strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1000);

        // เลื่อนเมาส์กลับเข้ากลางจอ (หลุดขอบ)
        strategy.ShouldSwitch(1000, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1100);

        // แตะขอบอีกครั้งที่ 1200ms -> ต้องเริ่มนับ 1 ใหม่ ไม่ใช่คิดต่อจาก 1000ms เดิม
        Assert.False(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1200));
        Assert.False(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1300));
        Assert.True(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1460)); // ผ่านที่ 1200 + 260ms
    }

    [Fact]
    public void DoubleTapStrategy_ShouldRequireTwoTaps()
    {
        var strategy = new DoubleTapEdgeStrategy();
        var config = new OmniConfig { DoubleTapMs = 400 };

        // Tap 1: ชนขอบ
        Assert.False(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1000));

        // ถอยเมาส์ออกจากขอบ
        Assert.False(strategy.ShouldSwitch(1850, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1100));

        // Tap 2: ชนขอบครั้งที่สอง ภายใน 400ms (ผ่านไป 200ms) -> สลับจอทันที!
        Assert.True(strategy.ShouldSwitch(1919, 500, ScreenEdge.Right, _testMonitor, config, nowMs: 1200));
    }

    [Fact]
    public void EdgeStrategyFactory_ShouldInstantiateCorrectStrategy()
    {
        var configInstant = new OmniConfig { SwitchDelayMs = 0, DoubleTapMs = 0 };
        Assert.IsType<InstantEdgeStrategy>(EdgeStrategyFactory.Create(configInstant));

        var configDwell = new OmniConfig { SwitchDelayMs = 250, DoubleTapMs = 0 };
        Assert.IsType<DwellDelayEdgeStrategy>(EdgeStrategyFactory.Create(configDwell));

        var configDoubleTap = new OmniConfig { SwitchDelayMs = 0, DoubleTapMs = 400 };
        Assert.IsType<DoubleTapEdgeStrategy>(EdgeStrategyFactory.Create(configDoubleTap));

        var configCombined = new OmniConfig { SwitchDelayMs = 250, DoubleTapMs = 400 };
        Assert.IsType<CombinedEdgeStrategy>(EdgeStrategyFactory.Create(configCombined));
    }
}
