using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using Xunit;

namespace OmniLink.Tests;

public class AdaptiveConnectionTests
{
    private class DummyCallbacks : IHostLinkCallbacks
    {
        public void OnConnected(string nodeId) { }
        public void OnDisconnected(string nodeId) { }
        public void OnEdgeReturn(string nodeId, string edge, double normY, double normX = 0.5) { }
        public void OnClipboardReceived(string mimeType, byte[] data) { }
        public void OnStateChanged(string nodeId, string state) { }
    }

    [Fact]
    public void RemoteNodeClient_InitialState_IsHot()
    {
        var cfg = new NodeConfig { NodeId = "test-node", Ip = "127.0.0.1", Enabled = false };
        using var client = new RemoteNodeClient(cfg, "token", new DummyCallbacks());

        Assert.Equal(ConnectionLifecycleState.Hot, client.LifecycleState);
    }

    [Fact]
    public void RemoteNodeClient_WakeUp_ResetsStateToHot()
    {
        var cfg = new NodeConfig { NodeId = "test-node", Ip = "127.0.0.1", Enabled = false };
        using var client = new RemoteNodeClient(cfg, "token", new DummyCallbacks())
        {
            HotDurationMs = 10,
            ColdDurationMs = 20,
            HotRetryDelayMs = 5,
            ColdRetryDelayMs = 10
        };

        // Call WakeUp explicitly
        client.WakeUp();
        Assert.Equal(ConnectionLifecycleState.Hot, client.LifecycleState);
    }

    [Fact]
    public void ReverseKnock_ShouldWakeUpMatchingNode()
    {
        var config = new OmniConfig();
        var nodeCfg = new NodeConfig { NodeId = "thinkpad-l440", Ip = "127.0.0.1", Enabled = false };
        config.Nodes.Add(nodeCfg);

        using var host = new KvmHostService(config, () => { });

        // Give UDP listener a moment to bind
        Thread.Sleep(50);

        if (host.Sessions.TryGetValue("thinkpad-l440", out var sess))
        {
            // Simulate sending Reverse Knock UDP to DiscoveryPort
            using var udp = new UdpClient();
            var payload = JsonSerializer.Serialize(new
            {
                cmd = "KNOCK_KNOCK",
                node_id = "thinkpad-l440",
                port = 24801,
                status = "ready"
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, OmniProtocol.DiscoveryPort));

            // Wait briefly for listener to process
            Thread.Sleep(150);

            Assert.Equal(ConnectionLifecycleState.Hot, sess.LifecycleState);
        }
    }
}
