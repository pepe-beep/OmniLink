using System.Collections.Generic;
using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>godclass-K6b: pipeline เป็น pure component — ทดสอบลำดับ/พฤติกรรมคิวโดยไม่ต้องมีเครื่องจริง</summary>
public class InputPipelineTests
{
    [Fact]
    public void Post_ThenDrain_PreservesOrderAndPayload()
    {
        var pipeline = new InputPipeline();
        pipeline.Post(InputPipeline.CmdType.EdgeReturn, "node-a", "left", 0.5, 0.25);
        pipeline.Post(InputPipeline.CmdType.SwitchRequest, "node-b", "top", 0.1, 0.9);
        pipeline.Post(InputPipeline.CmdType.FocusedReport, "node-a", focused: false);

        var seen = new List<InputPipeline.Command>();
        var drained = pipeline.Drain(cmd => seen.Add(cmd));

        Assert.Equal(3, drained);
        Assert.Equal(InputPipeline.CmdType.EdgeReturn, seen[0].Type);
        Assert.Equal("node-a", seen[0].NodeId);
        Assert.Equal("left", seen[0].Edge);
        Assert.Equal(0.25, seen[0].NormY);
        Assert.Equal(InputPipeline.CmdType.SwitchRequest, seen[1].Type);
        Assert.Equal("node-b", seen[1].NodeId);
        Assert.Equal(InputPipeline.CmdType.FocusedReport, seen[2].Type);
        Assert.False(seen[2].Focused);
        Assert.Equal(0, pipeline.PendingCount);
    }

    [Fact]
    public void Drain_EmptyQueue_ProcessesNothing()
    {
        var pipeline = new InputPipeline();
        var calls = 0;
        Assert.Equal(0, pipeline.Drain(_ => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Post_Overflow_DropsOldest_NeverThrows()
    {
        var pipeline = new InputPipeline();
        for (var i = 0; i < 100; i++)
        {
            pipeline.Post(InputPipeline.CmdType.SwitchRequest, "n" + i);
        }

        Assert.True(pipeline.PendingCount <= 64);
        var last = (InputPipeline.Command?)null;
        pipeline.Drain(cmd => last = cmd);
        // DropOldest → คำสั่งล่าสุด (n99) ต้องรอดเสมอ
        Assert.NotNull(last);
        Assert.Equal("n99", last!.Value.NodeId);
    }
}
