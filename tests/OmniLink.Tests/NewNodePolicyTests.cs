using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v2.0 §4.5 (ACER report 4.5): policy เริ่มต้นเชิงทรัพยากรของ node ที่จับคู่ใหม่ —
/// config new_node_policy (full/clipboard/view) ต้องถูก normalize และใช้จริงตอนจับคู่
/// (host เครื่องจิ๋ว/2GB ตั้ง view → node ใหม่ถูกตัดงาน input/clipboard ทันทีตามกลไก v1.7 เดิม)
/// </summary>
public class NewNodePolicyTests
{
    [Theory]
    [InlineData("view", OmniPolicy.ViewOnly)]
    [InlineData("clipboard", OmniPolicy.ClipboardFiles)]
    [InlineData("full", OmniPolicy.Full)]
    [InlineData("", OmniPolicy.Full)]       // ว่าง = full (UX เดิม)
    [InlineData("nonsense", OmniPolicy.Full)] // ค่านอกชุด = full (fail-open ตาม v1.7)
    public void NewNodePolicy_NormalizedToKnownSet(string raw, string expected)
    {
        var cfg = new OmniConfig { NewNodePolicy = raw };
        OmniLimits.Normalize(cfg);
        Assert.Equal(expected, cfg.NewNodePolicy);
    }

    [Fact]
    public void NewNodePolicy_DefaultIsFull_BackCompat()
    {
        // ค่าเริ่มต้นต้องเป็น full — ผู้ใช้เดิมจับคู่เครื่องใหม่แล้วคุมได้ทันทีเหมือนเดิม
        Assert.Equal(OmniPolicy.Full, new OmniConfig().NewNodePolicy);
    }

    [Fact]
    public void ViewPolicy_BlocksControlAndClipboard_PathForTinyNodes()
    {
        // กลไก v1.7 เดิมที่ §4.5 อ้าง: view = ไม่รับ input + ไม่แชร์ clipboard/files
        Assert.False(OmniPolicy.CanControl(OmniPolicy.ViewOnly));
        Assert.False(OmniPolicy.CanShareClipboard(OmniPolicy.ViewOnly));
        Assert.False(OmniPolicy.CanTransferFiles(OmniPolicy.ViewOnly));
        // ฝั่งรับ (agent/sub) ก็ต้องตัด packet ตาม matrix เดียวกัน
        Assert.False(NodeAgentServer.PacketAllowed("view", OmniProtocol.PktMouseMoveRel));
        Assert.False(NodeAgentServer.PacketAllowed("view", OmniProtocol.PktKeyEvent));
        Assert.True(NodeAgentServer.PacketAllowed("view", OmniProtocol.PktReleaseAll)); // safety override
    }
}
