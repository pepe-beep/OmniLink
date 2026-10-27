using System.Collections.Generic;
using System.Text;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;
using Xunit;

namespace OmniLink.Tests;

public class HostLinkCallbacksTests
{
    private class FakeHostLinkCallbacks : IHostLinkCallbacks
    {
        public readonly List<string> Events = new();

        public void OnEdgeReturn(string nodeId, string edge, double normY, double normX = 0.5)
        {
            Events.Add($"EdgeReturn:{nodeId}:{edge}:{normY}:{normX}");
        }

        public void OnConnected(string nodeId)
        {
            Events.Add($"Connected:{nodeId}");
        }

        public void OnDisconnected(string nodeId)
        {
            Events.Add($"Disconnected:{nodeId}");
        }

        public void OnClipboardReceived(string mimeType, byte[] data)
        {
            Events.Add($"Clipboard:{mimeType}:{Encoding.UTF8.GetString(data)}");
        }

        public void OnStateChanged(string nodeId, string state)
        {
            Events.Add($"StateChanged:{nodeId}:{state}");
        }

        public void OnLegacyAgent(string nodeId, string agentVersion)
        {
            Events.Add($"LegacyAgent:{nodeId}:{agentVersion}");
        }

        public void OnFocusedReported(string nodeId, bool focused)
        {
            Events.Add($"FocusedReported:{nodeId}:{focused}");
        }
    }

    [Fact]
    public void FakeHostLinkCallbacks_ShouldRecordAllInvocations()
    {
        var callbacks = new FakeHostLinkCallbacks();

        callbacks.OnConnected("node-1");
        callbacks.OnStateChanged("node-1", "connected");
        callbacks.OnEdgeReturn("node-1", "right", 0.75);
        callbacks.OnClipboardReceived("text/plain", Encoding.UTF8.GetBytes("hello"));
        callbacks.OnLegacyAgent("node-1", "0.9.0");
        callbacks.OnFocusedReported("node-1", false);
        callbacks.OnDisconnected("node-1");

        Assert.Equal(7, callbacks.Events.Count);
        Assert.Equal("Connected:node-1", callbacks.Events[0]);
        Assert.Equal("StateChanged:node-1:connected", callbacks.Events[1]);
        Assert.Equal("EdgeReturn:node-1:right:0.75:0.5", callbacks.Events[2]);
        Assert.Equal("Clipboard:text/plain:hello", callbacks.Events[3]);
        Assert.Equal("LegacyAgent:node-1:0.9.0", callbacks.Events[4]);
        Assert.Equal("FocusedReported:node-1:False", callbacks.Events[5]);
        Assert.Equal("Disconnected:node-1", callbacks.Events[6]);
    }

    [Fact]
    public void KvmHostService_ImplementsIHostLinkCallbacks()
    {
        var cfg = new OmniConfig();
        var hub = new OmniEventHub();
        var uiUpdateCalled = false;
        var host = new KvmHostService(cfg, () => uiUpdateCalled = true, hub);

        Assert.IsAssignableFrom<IHostLinkCallbacks>(host);

        var callbacks = (IHostLinkCallbacks)host;

        // Disconnected callback triggers UI update & event hub publish
        NodeConnectionEvent? connEvt = null;
        using var sub = hub.Subscribe<NodeConnectionEvent>(e => connEvt = e);

        callbacks.OnDisconnected("node-test");

        Assert.True(uiUpdateCalled);
        Assert.NotNull(connEvt);
        Assert.Equal("node-test", connEvt.NodeId);
        Assert.False(connEvt.Connected);
    }
}
