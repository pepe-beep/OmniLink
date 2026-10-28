using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>GroupA-D: gate การสลับหลัง node reconnect (กัน DESYNC — switch ก่อน agent พร้อม)</summary>
public class ReconnectGateTests
{
    [Fact]
    public void Blocks_Within_Gate_Window()
    {
        Assert.True(KvmHostService.SwitchGateBlocked(connectedAtTick: 1000, now: 1500, gateMs: 1500));
    }

    [Fact]
    public void Allows_After_Gate_Window()
    {
        Assert.False(KvmHostService.SwitchGateBlocked(connectedAtTick: 1000, now: 2501, gateMs: 1500));
    }

    [Fact]
    public void Allows_Exactly_At_Window_Edge()
    {
        Assert.False(KvmHostService.SwitchGateBlocked(connectedAtTick: 1000, now: 2500, gateMs: 1500));
    }

    [Fact]
    public void Disabled_When_Gate_Zero()
    {
        Assert.False(KvmHostService.SwitchGateBlocked(connectedAtTick: 1000, now: 1001, gateMs: 0));
    }

    [Fact]
    public void Handles_Stale_Connect_Tick()
    {
        // node ต่อค้างนาน — now น้อยกว่า connectedAtTick (tick wrap/นาฬิกา) ต้องไม่ block ตลอดไป
        Assert.False(KvmHostService.SwitchGateBlocked(connectedAtTick: 5000, now: 1000, gateMs: 1500));
    }

    [Fact]
    public void Config_Default_And_Clamp()
    {
        var cfg = new OmniConfig();
        Assert.Equal(1500, cfg.ReconnectSwitchGateMs);
        cfg.ReconnectSwitchGateMs = 99_999;
        OmniLimits.Normalize(cfg);
        Assert.Equal(10_000, cfg.ReconnectSwitchGateMs);
    }
}
