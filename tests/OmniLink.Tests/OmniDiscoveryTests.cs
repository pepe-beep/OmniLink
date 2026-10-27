using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

public class OmniDiscoveryTests
{
    [Fact]
    public void GeneratePairingPin_ReturnsValidFormat()
    {
        for (int i = 0; i < 50; i++)
        {
            var pin = OmniDiscoveryService.GeneratePairingPin();
            Assert.NotNull(pin);
            Assert.Equal(7, pin.Length);
            Assert.Equal('-', pin[3]);

            var parts = pin.Split('-');
            Assert.Equal(2, parts.Length);
            Assert.True(int.TryParse(parts[0], out var p1));
            Assert.True(int.TryParse(parts[1], out var p2));
            Assert.InRange(p1, 100, 999);
            Assert.InRange(p2, 0, 999);
        }
    }

    [Fact]
    public void DiscoveryPort_IsStandard24800()
    {
        Assert.Equal(24800, OmniProtocol.DiscoveryPort);
    }

    [Fact]
    public void DiscoveredNode_RecordProperties_WorkCorrectly()
    {
        var node = new DiscoveredNode(
            Uuid: "node-test1",
            Name: "ThinkPad Test",
            Os: "Linux",
            Ip: "192.168.1.50",
            Port: 24801,
            Width: 1920,
            Height: 1080,
            Paired: false,
            Version: "1.4.0");

        Assert.Equal("node-test1", node.Uuid);
        Assert.Equal("ThinkPad Test", node.Name);
        Assert.Equal("Linux", node.Os);
        Assert.Equal("192.168.1.50", node.Ip);
        Assert.Equal(24801, node.Port);
        Assert.Equal(1920, node.Width);
        Assert.Equal(1080, node.Height);
        Assert.False(node.Paired);
        Assert.Equal("1.4.0", node.Version);
    }

    // ── F1: dedupe multi-NIC (เครื่องเดียว uuid เดียวโผล่ทั้ง LAN และ VPN) ──

    private static DiscoveredNode N(string ip, string zone) => new(
        Uuid: "uuid-dup", Name: "L440", Os: "Linux", Ip: ip, Port: 24801,
        Width: 1920, Height: 1080, Paired: false, Version: "2.0.0", Zone: zone);

    [Fact]
    public void PreferIncomingNode_LanReplacesVpnAndOthers()
    {
        Assert.True(OmniDiscoveryService.PreferIncomingNode(N("10.85.0.1", "VPN (ZeroTier)"), N("192.168.99.2", "LAN")));
        Assert.True(OmniDiscoveryService.PreferIncomingNode(N("10.0.0.5", "Wi-Fi"), N("192.168.99.2", "LAN")));
        Assert.True(OmniDiscoveryService.PreferIncomingNode(N("10.85.0.1", "VPN"), N("192.168.99.2", "LAN")));
    }

    [Fact]
    public void PreferIncomingNode_DoesNotDowngradeOrReplaceLan()
    {
        Assert.False(OmniDiscoveryService.PreferIncomingNode(N("192.168.99.2", "LAN"), N("10.85.0.1", "VPN (ZeroTier)")));
        Assert.False(OmniDiscoveryService.PreferIncomingNode(N("192.168.99.2", "LAN"), N("192.168.99.2", "LAN")));
        Assert.False(OmniDiscoveryService.PreferIncomingNode(N("10.85.0.1", "VPN"), N("10.62.0.9", "Wi-Fi")));
    }

    [Fact]
    public async Task ScanNetworkAsync_CanDiscoverRunningAgentOnLan()
    {
        var config = OmniConfig.Load();
        using var discovery = new OmniDiscoveryService(config);
        var nodes = await discovery.ScanNetworkAsync(timeoutMs: 2000);
        Assert.NotNull(nodes);
        // If we found nodes on this network (such as ThinkPad L440):
        if (nodes.Count > 0)
        {
            var l440 = nodes.FirstOrDefault(n => n.Ip == "192.168.99.2");
            Assert.NotNull(l440);
            Assert.Equal("psl440-ThinkPad-L440", l440.Name);
            Assert.Equal("Linux", l440.Os);
            Assert.Equal(24801, l440.Port);
            // อย่า hardcode เวอร์ชัน — agent อัปเกรดได้ตลอด (test เคย fail เพราะ lock ไว้ที่ 1.4.0
            // ตอน fix A2 ทำให้ scan เจอ agent จริงเป็นครั้งแรก)
            Assert.False(string.IsNullOrWhiteSpace(l440.Version));
        }
    }

    /// <summary>seam จำลอง: plain stream + fingerprint ปลอม (หน่วยทดสอบ pairing logic — TLS จริงทดสอบที่ live gate)</summary>
    private const string MockFingerprint = "a1b2c3d4e5f6a7b8a1b2c3d4e5f6a7b8a1b2c3d4e5f6a7b8a1b2c3d4e5f6a7b8";

    private static Task<(System.IO.Stream, string?)> PlainStreamFactory(
        System.Net.Sockets.TcpClient client, string host, CancellationToken ct)
        => Task.FromResult<(System.IO.Stream, string?)>((client.GetStream(), MockFingerprint));

    [Fact]
    public async Task RequestPairingAsync_InvalidPin_ReturnsFailure()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var header = new byte[4];
            await stream.ReadExactlyAsync(header);
            var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            var body = new byte[len];
            await stream.ReadExactlyAsync(body);

            var rejectPayload = System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":\"pair_response\",\"success\":false,\"reason\":\"Invalid PIN\"}");
            var respHeader = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(respHeader, (uint)rejectPayload.Length);
            await stream.WriteAsync(respHeader);
            await stream.WriteAsync(rejectPayload);
            await stream.FlushAsync();
        });

        try
        {
            var config = OmniConfig.Load();
            using var discovery = new OmniDiscoveryService(config);
            var testNode = new DiscoveredNode(
                Uuid: "node-dummy",
                Name: "ThinkPad L440",
                Os: "Linux",
                Ip: System.Net.IPAddress.Loopback.ToString(),
                Port: port,
                Width: 960,
                Height: 720,
                Paired: false,
                Version: "1.4.0");

            var result = await discovery.RequestPairingAsync(testNode, "000-000", timeoutSec: 5,
                confirmFingerprint: _ => true, streamFactory: PlainStreamFactory);
            await serverTask;

            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains("Invalid PIN", result.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RequestPairingAsync_WhenPeerAccepts_ParsesResultCleanly()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var header = new byte[4];
            await stream.ReadExactlyAsync(header);
            var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            var body = new byte[len];
            await stream.ReadExactlyAsync(body);

            var acceptPayload = System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":\"pair_accept\",\"token\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"uuid\":\"node-mock\",\"name\":\"Mock Node\",\"width\":1920,\"height\":1080" +
                ",\"cert_fingerprint\":\"" + MockFingerprint + "\"}");
            var respHeader = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(respHeader, (uint)acceptPayload.Length);
            await stream.WriteAsync(respHeader);
            await stream.WriteAsync(acceptPayload);
            await stream.FlushAsync();
        });

        try
        {
            var config = OmniConfig.Load();
            using var discovery = new OmniDiscoveryService(config);
            var testNode = new DiscoveredNode(
                Uuid: "node-mock",
                Name: "Mock Node",
                Os: "Linux",
                Ip: "127.0.0.1",
                Port: port,
                Width: 1920,
                Height: 1080,
                Paired: false,
                Version: "1.4.0");

            var result = await discovery.RequestPairingAsync(testNode, "123-456", timeoutSec: 5,
                confirmFingerprint: _ => true, streamFactory: PlainStreamFactory);
            await serverTask;

            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.Equal("Mock Node", result.Name);
            Assert.Equal(64, result.Token?.Length);
            Assert.Equal(MockFingerprint, result.Fingerprint);
        }
        finally
        {
            listener.Stop();
        }
    }
}
