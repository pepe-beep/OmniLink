using System.Collections.Generic;
using OmniLink.Core.Events;
using Xunit;

namespace OmniLink.Tests;

// godclass-K7a: ใช้ record ทดสอบของตัวเองแทน AudioStateChangedEvent ที่ถูกลบ (production ไม่เคย publish)
public record TestEvent(bool Value);

/// <summary>
/// Unit Tests สำหรับ Observer Pattern / Event Hub (CP353002)
/// </summary>
public class EventHubTests
{
    [Fact]
    public void EventHub_ShouldNotifySubscribers()
    {
        var hub = new OmniEventHub();
        var receivedBanners = new List<string>();

        using var sub = hub.Subscribe<StatusBannerEvent>(e => receivedBanners.Add(e.Message));

        hub.Publish(new StatusBannerEvent("Ready"));
        hub.Publish(new StatusBannerEvent("Connected to L440"));

        Assert.Equal(2, receivedBanners.Count);
        Assert.Equal("Ready", receivedBanners[0]);
        Assert.Equal("Connected to L440", receivedBanners[1]);
    }

    [Fact]
    public void EventHub_ShouldStopNotifying_AfterDisposeSubscription()
    {
        var hub = new OmniEventHub();
        var count = 0;

        var sub = hub.Subscribe<TestEvent>(e => count++);

        hub.Publish(new TestEvent(true));
        Assert.Equal(1, count);

        // Unsubscribe (Dispose)
        sub.Dispose();

        hub.Publish(new TestEvent(false));
        Assert.Equal(1, count); // ต้องไม่เพิ่มขึ้น
    }

    [Fact]
    public void EventHub_ShouldDeliverNodeConnectionEvents()
    {
        var hub = new OmniEventHub();
        NodeConnectionEvent? received = null;

        using var sub = hub.Subscribe<NodeConnectionEvent>(e => received = e);

        hub.Publish(new NodeConnectionEvent("node-alpha", "192.168.1.50", true, 1));

        Assert.NotNull(received);
        Assert.Equal("node-alpha", received.NodeId);
        Assert.Equal("192.168.1.50", received.Ip);
        Assert.True(received.Connected);
        Assert.Equal(1, received.LangGroup);
    }

    [Fact]
    public void EventHub_ShouldDeliverClipboardReceivedEvents()
    {
        var hub = new OmniEventHub();
        ClipboardReceivedEvent? received = null;

        using var sub = hub.Subscribe<ClipboardReceivedEvent>(e => received = e);

        hub.Publish(new ClipboardReceivedEvent("Sample copied text", "node-linux"));

        Assert.NotNull(received);
        Assert.Equal("Sample copied text", received.Text);
        Assert.Equal("node-linux", received.SourceNodeId);
    }

    [Fact]
    public void EventHub_ShouldDeliverLanguageChangedEvents()
    {
        var hub = new OmniEventHub();
        LanguageChangedEvent? received = null;

        using var sub = hub.Subscribe<LanguageChangedEvent>(e => received = e);

        hub.Publish(new LanguageChangedEvent(1, true, null));

        Assert.NotNull(received);
        Assert.Equal(1, received.LangGroup);
        Assert.True(received.IsHost);
        Assert.Null(received.NodeId);
    }
}
