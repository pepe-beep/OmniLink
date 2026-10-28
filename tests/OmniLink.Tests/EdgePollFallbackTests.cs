using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Tests.TestDoubles;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// GroupA-P: ทดสอบ polling fallback — GetCursorPos timer ต้องผลิต synthetic Move
/// เข้าคิวเดียวกับ hook โดยมีเงื่อนไข: hz&gt;0, throttle ตาม hz, และหยุดขณะคุม remote
/// (switching logic จริงเดินบน CheckEdgeSwitch เดิม — ครอบด้วย E2E suite)
/// </summary>
public class EdgePollFallbackTests
{
    private static (KvmHostService Kvm, FakePollGateway Gw, OmniConfig Cfg) MakeService(int hz, int x = 1500, int y = 400)
    {
        var cfg = new OmniConfig { EdgePollHz = hz };
        var gw = new FakePollGateway(x, y);
        var kvm = new KvmHostService(cfg, static () => { }, null, gw);
        kvm._lastEdgePollTick = 0;
        return (kvm, gw, cfg);
    }

    private static void SetActiveNode(KvmHostService kvm, string? nodeId)
    {
        var prop = typeof(KvmHostService).GetProperty(nameof(KvmHostService.ActiveNodeId))!;
        prop.GetSetMethod(nonPublic: true)!.Invoke(kvm, new object?[] { nodeId });
    }

    [Fact]
    public void Poll_Produces_Synthetic_Move_At_Gateway_Position()
    {
        var (kvm, gw, _) = MakeService(hz: 30, x: 1500, y: 400);
        try
        {
            Assert.True(kvm.TryPollEdgePosition(out var ev), "hz=30 ครั้งแรกต้อง poll ได้");
            Assert.Equal(MouseInputMessage.Move, ev.Message);
            Assert.Equal(1500, ev.X);
            Assert.Equal(400, ev.Y);
        }
        finally
        {
            kvm.Dispose();
        }
    }

    [Fact]
    public void Poll_Follows_Cursor_Changes_Between_Ticks()
    {
        var (kvm, gw, _) = MakeService(hz: 120, x: 100, y: 100);
        try
        {
            kvm._lastEdgePollTick = -10_000; // เปิดหน้าต่าง throttle กว้าง (hz=120 → ~8ms)
            Assert.True(kvm.TryPollEdgePosition(out var a));
            Assert.Equal(100, a.X);

            gw.SetPosition(1917, 432);
            Thread.Sleep(12); // ผ่าน throttle window 8ms ของ hz=120
            Assert.True(kvm.TryPollEdgePosition(out var b));
            Assert.Equal(1917, b.X);
            Assert.Equal(432, b.Y);
        }
        finally
        {
            kvm.Dispose();
        }
    }

    [Fact]
    public void Poll_Throttles_By_Hz()
    {
        var (kvm, _, _) = MakeService(hz: 1); // หน้าต่าง 1000ms — deterministic
        try
        {
            Assert.True(kvm.TryPollEdgePosition(out _));
            Assert.False(kvm.TryPollEdgePosition(out _), "ครั้งที่สองใน 1 วิต้องโดน throttle");
        }
        finally
        {
            kvm.Dispose();
        }
    }

    [Fact]
    public void Poll_Disabled_When_Hz_Zero()
    {
        var (kvm, _, cfg) = MakeService(hz: 0);
        try
        {
            cfg.EdgePollHz = 0;
            Assert.False(kvm.TryPollEdgePosition(out _), "edge_poll_hz=0 = ปิด polling");
        }
        finally
        {
            kvm.Dispose();
        }
    }

    [Fact]
    public void Poll_Stops_While_Controlling_Remote()
    {
        var (kvm, _, _) = MakeService(hz: 30);
        try
        {
            SetActiveNode(kvm, "psl440-ThinkPad-L440");
            Assert.False(kvm.TryPollEdgePosition(out _),
                "ขณะคุม remote ต้องไม่ poll — synthetic move จะถูก forward ไปหา node จริง");
        }
        finally
        {
            kvm.Dispose();
        }
    }

    [Fact]
    public void Poll_Resumes_After_Return_To_Host()
    {
        var (kvm, _, _) = MakeService(hz: 30);
        try
        {
            SetActiveNode(kvm, "psl440-ThinkPad-L440");
            Assert.False(kvm.TryPollEdgePosition(out _));
            SetActiveNode(kvm, null); // edge_return กลับ host แล้ว
            Thread.Sleep(40); // ผ่าน throttle window 33ms ของ hz=30
            Assert.True(kvm.TryPollEdgePosition(out _));
        }
        finally
        {
            kvm.Dispose();
        }
    }

    [Fact]
    public void Config_Defaults_And_Clamp()
    {
        var cfg = new OmniConfig();
        Assert.Equal(30, cfg.EdgePollHz); // default เปิดใช้ที่ 30Hz
        OmniLimits.Normalize(cfg);
        Assert.Equal(30, cfg.EdgePollHz);

        cfg.EdgePollHz = 9999;
        OmniLimits.Normalize(cfg);
        Assert.Equal(120, cfg.EdgePollHz); // clamp บน

        cfg.EdgePollHz = -5;
        OmniLimits.Normalize(cfg);
        Assert.Equal(0, cfg.EdgePollHz); // clamp ล่าง (0 = ปิด)
    }
}
