using OmniLink.Core.Strategies;
using OmniLink.Domain;
using Xunit;

namespace OmniLink.Tests;

public class LayoutModelTests
{
    [Fact]
    public void ThreeScreen_LinearTopology_NavigatesCorrectly()
    {
        var layout = new LayoutModel();

        // Node Left: ThinkPad L440 (X: -1366, Y: 0, 1366x768)
        var leftNode = new MachineNode(
            "thinkpad-l440",
            "ThinkPad L440",
            IsHost: false,
            new ScreenPosition(-1366, 0, 1366, 768),
            RightNodeId: "host-pc");

        // Node Center: Host PC (X: 0, Y: 0, 1920x1080)
        var hostNode = new MachineNode(
            "host-pc",
            "Host Desktop",
            IsHost: true,
            new ScreenPosition(0, 0, 1920, 1080),
            LeftNodeId: "thinkpad-l440",
            RightNodeId: "ext-monitor");

        // Node Right: External Monitor (X: 1920, Y: 0, 1920x1080)
        var rightNode = new MachineNode(
            "ext-monitor",
            "External Node",
            IsHost: false,
            new ScreenPosition(1920, 0, 1920, 1080),
            LeftNodeId: "host-pc");

        layout.AddOrUpdateNode(leftNode);
        layout.AddOrUpdateNode(hostNode);
        layout.AddOrUpdateNode(rightNode);

        // Verify Host navigation
        Assert.Equal("thinkpad-l440", layout.GetTargetNode("host-pc", ScreenEdge.Left));
        Assert.Equal("ext-monitor", layout.GetTargetNode("host-pc", ScreenEdge.Right));
        Assert.Null(layout.GetTargetNode("host-pc", ScreenEdge.Top));

        // Verify Left Node navigation
        Assert.Equal("host-pc", layout.GetTargetNode("thinkpad-l440", ScreenEdge.Right));
        Assert.Null(layout.GetTargetNode("thinkpad-l440", ScreenEdge.Left));

        // Verify Right Node navigation
        Assert.Equal("host-pc", layout.GetTargetNode("ext-monitor", ScreenEdge.Left));
        Assert.Null(layout.GetTargetNode("ext-monitor", ScreenEdge.Right));
    }

    [Fact]
    public void ScreenPosition_NegativeCoordinates_ComputesBoundsCorrectly()
    {
        var pos = new ScreenPosition(-1920, -1080, 1920, 1080);

        Assert.Equal(-1920, pos.X);
        Assert.Equal(-1080, pos.Y);
        Assert.Equal(0, pos.Right);
        Assert.Equal(0, pos.Bottom);
    }

    [Fact]
    public void LinkNodes_BidirectionalLinks_CreatedSuccessfully()
    {
        var layout = new LayoutModel();
        var host = new MachineNode("host", "Host", IsHost: true, new ScreenPosition(0, 0, 1920, 1080));
        var topNode = new MachineNode("top-pc", "Top PC", IsHost: false, new ScreenPosition(0, -1080, 1920, 1080));

        layout.AddOrUpdateNode(host);
        layout.AddOrUpdateNode(topNode);

        layout.LinkNodes("host", ScreenEdge.Top, "top-pc");

        Assert.Equal("top-pc", layout.GetTargetNode("host", ScreenEdge.Top));
        Assert.Equal("host", layout.GetTargetNode("top-pc", ScreenEdge.Bottom));
    }

    [Fact]
    public void LayoutValidator_ValidTopology_ReturnsTrue()
    {
        var layout = new LayoutModel();
        layout.AddOrUpdateNode(new MachineNode("host", "Host", IsHost: true, new ScreenPosition(0, 0, 1920, 1080)));

        var res = LayoutValidator.Validate(layout);

        Assert.True(res.IsValid);
        Assert.Empty(res.Errors);
    }

    [Fact]
    public void LayoutValidator_NoHost_ReturnsInvalid()
    {
        var layout = new LayoutModel();
        layout.AddOrUpdateNode(new MachineNode("client-1", "Client", IsHost: false, new ScreenPosition(0, 0, 1920, 1080)));

        var res = LayoutValidator.Validate(layout);

        Assert.False(res.IsValid);
        Assert.Contains(res.Errors, e => e.Contains("Host"));
    }

    [Fact]
    public void LayoutValidator_BrokenReference_ReturnsInvalid()
    {
        var layout = new LayoutModel();
        layout.AddOrUpdateNode(new MachineNode("host", "Host", IsHost: true, new ScreenPosition(0, 0, 1920, 1080), LeftNodeId: "missing-node"));

        var res = LayoutValidator.Validate(layout);

        Assert.False(res.IsValid);
        Assert.Contains(res.Errors, e => e.Contains("missing-node"));
    }
}
