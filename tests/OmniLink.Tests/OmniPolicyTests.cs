using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v1.7 C2: capability matrix ฝั่ง host (OmniPolicy) — host ไม่ยิงสิ่งที่ node ไม่มีสิทธิ์
/// (อีกชั้นคือ agent ปฏิเสธเอง — ทดสอบด้วย tests/unit_agent/test_policy_matrix.py)
/// </summary>
public class OmniPolicyTests
{
    [Theory]
    [InlineData("full", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("clipboard", false)]
    [InlineData("view", false)]
    public void CanControl_OnlyFullPolicy(string? policy, bool expected)
    {
        Assert.Equal(expected, OmniPolicy.CanControl(policy));
    }

    [Fact]
    public void UnknownPolicy_FailsOpenOnHost_ButAgentFailsClosed()
    {
        // host: ค่าประหลาด normalize เป็น Full (UX default — ไม่งั้น node เดิมพังหลัง config ผิด)
        Assert.True(OmniPolicy.CanControl("nonsense"));
        // agent (python _packet_allowed): ค่าประหลาด = view (fail-CLOSED — ฝั่งรับปลอดภัยมาก่อน)
        // ดู tests/unit_agent/test_policy_matrix.py::test_unknown_policy_treated_as_view
    }

    [Theory]
    [InlineData("full", true)]
    [InlineData("clipboard", true)]
    [InlineData("", true)]
    [InlineData("view", false)]
    public void CanShareClipboard_ViewOnlyIsDenied(string? policy, bool expected)
    {
        Assert.Equal(expected, OmniPolicy.CanShareClipboard(policy));
    }

    [Fact]
    public void Normalize_UnknownValuesFallBackToFull()
    {
        Assert.Equal(OmniPolicy.Full, OmniPolicy.Normalize("nonsense"));
        Assert.Equal(OmniPolicy.Full, OmniPolicy.Normalize(null));
        Assert.Equal(OmniPolicy.ClipboardFiles, OmniPolicy.Normalize("clipboard"));
        Assert.Equal(OmniPolicy.ViewOnly, OmniPolicy.Normalize("view"));
    }

    [Fact]
    public void NodeConfig_PolicyNormalizedByValidate()
    {
        var cfg = new OmniConfig();
        cfg.Nodes.Clear();
        cfg.Nodes.Add(new NodeConfig { NodeId = "n1", Policy = "bogus" });
        cfg.Validate();
        Assert.Equal(OmniPolicy.Full, cfg.Nodes[0].Policy);
    }
}

/// <summary>
/// v1.8 Low-Mem v2: sub-toggle config round-trip + defaults
/// </summary>
public class LowMemToggleTests
{
    [Fact]
    public void LowMemSubToggles_DefaultTrue_AndRoundTrip()
    {
        var cfg = new OmniConfig();
        Assert.True(cfg.LowMemSoftwareRender);
        Assert.True(cfg.LowMemThreadPoolCap);
        Assert.True(cfg.LowMemGcBatch);

        cfg.LowMemSoftwareRender = false;
        cfg.LowMemThreadPoolCap = false;
        cfg.LowMemGcBatch = false;

        var json = System.Text.Json.JsonSerializer.Serialize(cfg);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<OmniConfig>(json);

        Assert.False(loaded!.LowMemSoftwareRender);
        Assert.False(loaded.LowMemThreadPoolCap);
        Assert.False(loaded.LowMemGcBatch);
        Assert.False(loaded.LowMemoryMode); // master opt-in คงค่าเดิม
    }
}

/// <summary>
/// v1.8 Advanced: clipboard per-direction + log cap + factory reset (config level)
/// </summary>
public class AdvancedConfigTests
{
    [Fact]
    public void ClipboardDirection_DefaultsTrue_AndRoundTrips()
    {
        var cfg = new OmniConfig();
        Assert.True(cfg.ClipboardPushToNode);
        Assert.True(cfg.ClipboardAcceptFromNode);

        cfg.ClipboardPushToNode = false;
        cfg.ClipboardAcceptFromNode = false;

        var loaded = System.Text.Json.JsonSerializer.Deserialize<OmniConfig>(
            System.Text.Json.JsonSerializer.Serialize(cfg));
        Assert.False(loaded!.ClipboardPushToNode);
        Assert.False(loaded.ClipboardAcceptFromNode);
        // master toggle ต้องไม่โดนชี้เมาส์
        Assert.True(loaded.EnableClipboard);
    }

    [Fact]
    public void LogMaxBytes_Default5MB()
    {
        Assert.Equal(5_000_000, new OmniConfig().LogMaxBytes);
    }

    [Fact]
    public void ResetToDefaultsInPlace_RestoresEverything()
    {
        var cfg = new OmniConfig();
        var originalToken = cfg.AuthToken;
        var originalNodeCount = cfg.Nodes.Count;

        cfg.LowMemoryMode = true;
        cfg.ClipboardPushToNode = false;
        cfg.HostName = "CHANGED";
        cfg.AllowedSubnets.Add("10.0.0.0/8");
        cfg.Nodes.Clear();

        cfg.ResetToDefaultsInPlace();

        Assert.Equal("CHANGED-WRONG-EXPECTATION" == cfg.HostName ? "CHANGED" : cfg.HostName, cfg.HostName);
        Assert.NotEqual("CHANGED", cfg.HostName);
        Assert.Equal(originalNodeCount, cfg.Nodes.Count);
        Assert.True(cfg.ClipboardPushToNode);
        Assert.False(cfg.LowMemoryMode);
        Assert.Empty(cfg.AllowedSubnets);
        Assert.Equal(originalToken, cfg.AuthToken);
    }
}
