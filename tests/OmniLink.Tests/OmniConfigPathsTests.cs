using System.IO;
using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

// OmniPaths._root เป็น static ที่ test หลาย class อ่าน — ทุก class ที่แตะ OmniPaths ต้องอยู่
// collection นี้เพื่อกัน race (flake: SetRootForTest ชั่วคราวถูกอ่านข้าม thread)
[CollectionDefinition("OmniPathsSerial")]
public class OmniPathsSerialCollection;

[Collection("OmniPathsSerial")]
public class OmniConfigTests
{
    private static NodeConfig Node(string id, string ip, bool enabled = true)
        => new() { NodeId = id, Ip = ip, Enabled = enabled };

    [Fact]
    public void Validate_BadIpDisablesNode()
    {
        var cfg = new OmniConfig();
        cfg.Nodes.Clear();
        cfg.Nodes.Add(Node("a", "192.168.99.2", enabled: true));
        cfg.Nodes.Add(Node("b", "999.999.1.1", enabled: true));
        cfg.Nodes.Add(Node("c", "not-an-ip", enabled: true));

        OmniConfig.ValidateAndNormalize(cfg);

        Assert.True(cfg.Nodes[0].Enabled);
        Assert.False(cfg.Nodes[1].Enabled); // ip ผิดรูปแบบ → disable ไม่ crash
        Assert.False(cfg.Nodes[2].Enabled);
    }

    [Fact]
    public void Validate_DuplicateOrEmptyIdDisables()
    {
        var cfg = new OmniConfig();
        cfg.Nodes.Clear();
        cfg.Nodes.Add(Node("dup", "10.0.0.1"));
        cfg.Nodes.Add(Node("dup", "10.0.0.2"));
        cfg.Nodes.Add(Node("", "10.0.0.3"));

        OmniConfig.ValidateAndNormalize(cfg);

        Assert.True(cfg.Nodes[0].Enabled);   // ตัวแรกถือ id ได้
        Assert.False(cfg.Nodes[1].Enabled);  // id ซ้ำ → disable
        Assert.False(cfg.Nodes[2].Enabled);  // id ว่าง → disable
    }

    [Fact]
    public void Validate_NormalizesPositionAndSize()
    {
        var cfg = new OmniConfig();
        cfg.Nodes.Clear();
        cfg.Nodes.Add(new NodeConfig { NodeId = "n", Ip = "10.0.0.9", Position = "upstairs", Width = -5, Height = 99999, SshUser = null! });

        OmniConfig.ValidateAndNormalize(cfg);

        var n = cfg.Nodes[0];
        Assert.Equal("right", n.Position);
        Assert.Equal(960, n.Width);
        Assert.Equal(720, n.Height);
        Assert.Equal("", n.SshUser);
    }

    [Fact]
    public void Validate_NullNodeListBecomesEmpty()
    {
        var cfg = new OmniConfig { Nodes = null! };
        OmniConfig.ValidateAndNormalize(cfg);
        Assert.NotNull(cfg.Nodes);
        Assert.Empty(cfg.Nodes);
    }

    [Fact]
    public void Validate_AutoDetectsGstExe_WhenMissingOrEmpty()
    {
        var cfg = new OmniConfig { GstExe = @"C:\non_existent_folder_xyz\gst-launch-1.0.exe" };
        OmniConfig.ValidateAndNormalize(cfg);

        // ต้องทำการ auto-detect แทนการเก็บพาธที่ไม่ถูกต้องไว้
        Assert.NotNull(cfg.GstExe);
        Assert.NotEmpty(cfg.GstExe);
    }
}

public class OmniPathsTests
{
    [Fact]
    public void DiscoverRoot_FindsMarkerByWalkUp()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "omnilink-test-" + Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(baseDir, "a", "b", "c");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(baseDir, "AGENTS.md"), "marker");

        var found = OmniPaths.DiscoverRoot(inner);

        Assert.Equal(baseDir, found, ignoreCase: true);
        Directory.Delete(baseDir, recursive: true);
    }

    [Fact]
    public void DiscoverRoot_NoMarkerFallsBackToStartDir()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "omnilink-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);

        var found = OmniPaths.DiscoverRoot(baseDir);

        Assert.Equal(baseDir, found, ignoreCase: true);
        Directory.Delete(baseDir, recursive: true);
    }

    [Fact]
    public void SetRootForTest_ChangesRootAndDerivedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnilink-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            OmniPaths.SetRootForTest(root);
            Assert.Equal(root, OmniPaths.Root);
            Assert.Equal(Path.Combine(root, "logs"), OmniPaths.LogsDir);
            Assert.Equal(Path.Combine(root, "logs", "crash.log"), OmniPaths.CrashLogPath);
            Assert.Equal(Path.Combine(root, "node_agent", "omnilink_agent.py"), OmniPaths.NodeAgentPath);
        }
        finally
        {
            OmniPaths.SetRootForTest(null); // คืนค่าให้ discovery ทำงานปกติ
        }
    }

    [Fact]
    public void DiscoverGstreamerExe_ReturnsValidOrFallback()
    {
        var gst = OmniLink.Platform.Windows.GstLocator.DiscoverGstreamerExe();
        Assert.NotNull(gst);
        Assert.NotEmpty(gst);
        // ต้องคืนค่าเป็นชื่อไฟล์ gst-launch-1.0.exe หรือ absolute path ไปยัง exe
        Assert.Contains("gst-launch-1.0", gst);
    }
}
