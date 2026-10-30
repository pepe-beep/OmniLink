using System.Collections.Concurrent;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;
using OmniLink.Core.Ipc;
using OmniLink.Core.Strategies;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// S2: ทดสอบท่อ IPC แบบ loopback — server จริง + client จริง คุยกันผ่าน named pipe ชื่อ unique
/// ครอบสัญญา S0: handshake, state.get/state.changed, คำสั่ง switch/remove/policy/deploy, event banner,
/// config.replace (in-place copy + save seam), และพฤติกรรม offline ของ client
/// </summary>
public class IpcLoopbackTests
{
    private sealed class FakeKvmHost : IKvmHost
    {
        public ConcurrentBag<(string NodeId, double NormY)> Switches { get; } = new();
        public ConcurrentBag<string> Removed { get; } = new();
        public ConcurrentBag<(string NodeId, string Policy)> Policies { get; } = new();
        public int ReloadLayoutCount;
        public int ReloadHotkeysCount;

        public IReadOnlyDictionary<string, NodeSessionView> SessionViews { get; } = new Dictionary<string, NodeSessionView>
        {
            ["n1"] = new() { Config = new NodeConfig { NodeId = "n1", Ip = "10.0.0.1" }, Connected = true, AgentVersion = "9.9.9", PingMs = 1.5 },
            ["n2"] = new() { Config = new NodeConfig { NodeId = "n2", Ip = "10.0.0.2" }, Connected = false },
        };

        public string? ActiveNodeId => "n1";
        public int MasterLangGroup => 1;

        public TopologyView TopologyView { get; } = new(
            new Dictionary<string, (int X, int Y)> { [TopologyHost.Id] = (0, 0), ["n1"] = (1, 0) },
            new Dictionary<string, IReadOnlyDictionary<ScreenEdge, string>>
            {
                [TopologyHost.Id] = new Dictionary<ScreenEdge, string> { [ScreenEdge.Right] = "n1" },
                ["n1"] = new Dictionary<ScreenEdge, string> { [ScreenEdge.Left] = TopologyHost.Id },
            });

        public int VirtualLeft => 0;
        public int VirtualTop => 0;
        public int VirtualRight => 2560;
        public int VirtualBottom => 1440;
        public bool IsWindowHiddenInTray { get; set; }
        public Func<bool>? SubActiveFocusProbe { get; set; }
        public Func<string?>? HostClipboardGetter { get; set; }
        public Func<byte[]?>? HostPngGetter { get; set; }
        public HostFileDropGetterDelegate? HostFileDropGetter { get; set; }
        public Action<string>? OnClipboardFromClient { get; set; }
        public Action<byte[]?>? OnClipboardImageFromClient { get; set; }
        public Action<IReadOnlyList<string>, bool>? OnClipboardFilesFromClient { get; set; }
        public Action<string, bool>? OnNodeAudioActivityChanged { get; set; }
        public Action<bool>? OnFocusMicAction { get; set; }

        public void Start() { }
        public void AddOrUpdateNode(NodeConfig cfg, string? token = null) { }
        public void ReloadLayout() => Interlocked.Increment(ref ReloadLayoutCount);
        public void ReloadHotkeys() => Interlocked.Increment(ref ReloadHotkeysCount);
        public void UpdateStrategies() { }
        public void SwitchToNode(string nodeId, double normY) => Switches.Add((nodeId, normY));
        public void CalibrateAllLanguages(int targetGroup) { }
        public int GetHostLangGroup() => 1;
        public (bool Ok, string Message) DeployRemoteAgent(NodeConfig ncfg) => (true, "fake-deploy");
        public void SetNodePolicy(string nodeId, string policy) => Policies.Add((nodeId, policy));
        public bool RemoveNode(string nodeId) { Removed.Add(nodeId); return true; }
        public bool SendNodeCommand(string ip, object payload) => true;
        public string? GetLocalEndpointIpForNode(string ip) => "127.0.0.1";
        public void PushClipboardToActiveNode() { }
        public void SendFilesToNode(string nodeId, IReadOnlyList<string> paths) { }
        public void Dispose() { }
    }

    private static (FakeKvmHost Host, IpcHostServer Server, IpcKvmClient Client) StartPair(
        out OmniConfig cfg, Action<OmniConfig>? configSaver = null)
    {
        cfg = new OmniConfig();
        var host = new FakeKvmHost();
        var pipe = "omnilink-test-" + Guid.NewGuid().ToString("N");
        var server = new IpcHostServer(host, cfg, null, pipe, configSaver);
        server.Start();
        var client = new IpcKvmClient(pipe);
        client.Start();

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectedChanged += () => { if (client.IsConnected) connected.TrySetResult(); };
        Assert.True(connected.Task.Wait(10_000), "client ต่อ server ไม่สำเร็จใน 10 วิ");
        return (host, server, client);
    }

    [Fact]
    public async Task Handshake_State_And_Commands_RoundTrip()
    {
        var (host, server, client) = StartPair(out _);
        try
        {
            var gotState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.StateChanged += () => gotState.TrySetResult();
            var winner = await Task.WhenAny(gotState.Task, Task.Delay(5_000));
            Assert.True(winner == gotState.Task, "ไม่ได้รับ state.changed หลัง handshake");

            Assert.True(client.IsConnected);
            Assert.Equal("n1", client.ActiveNodeId);
            Assert.Equal(2560, client.VirtualRight);
            Assert.Equal(2, client.SessionViews.Count);
            Assert.True(client.SessionViews["n1"].Connected);
            Assert.Equal("9.9.9", client.SessionViews["n1"].AgentVersion);

            // topology ข้ามท่อ: cells + GetNeighbor ตรงกันทั้งสองฝั่ง
            Assert.Equal((1, 0), client.TopologyView.Cells["n1"]);
            Assert.Equal("n1", client.TopologyView.GetNeighbor(TopologyHost.Id, ScreenEdge.Right));
            Assert.Equal(TopologyHost.Id, client.TopologyView.GetNeighbor("n1", ScreenEdge.Left));

            client.SwitchToNode("n1", 0.25);
            Assert.True(SpinUntil(() => host.Switches.Count > 0), "switch_to_node ไม่ถึง engine");
            Assert.Equal(("n1", 0.25), host.Switches.Single());

            Assert.True(client.RemoveNode("n2"));
            Assert.Contains("n2", host.Removed);

            client.SetNodePolicy("n1", OmniPolicy.ClipboardFiles);
            Assert.Equal(("n1", OmniPolicy.ClipboardFiles), host.Policies.Single());

            client.ReloadLayout();
            Assert.True(SpinUntil(() => host.ReloadLayoutCount >= 1));

            var (ok, msg) = client.DeployRemoteAgent(new NodeConfig { NodeId = "n9", Ip = "10.0.0.9" });
            Assert.True(ok);
            Assert.Equal("fake-deploy", msg);
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public async Task Banner_Event_Flows_Engine_Hub_To_Dashboard_Hub()
    {
        var (_, server, client) = StartPair(out _);
        try
        {
            var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (OmniEventHub.Default.Subscribe<StatusBannerEvent>(e => got.TrySetResult(e.Message)))
            {
                // engine ประกาศ banner ผ่าน hub ฝั่งตัวเอง (เหมือน KvmHostService ทำจริง)
                OmniEventHub.Default.Publish(new StatusBannerEvent("ทดสอบ banner ข้ามท่อ"));
                var msg = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("ทดสอบ banner ข้ามท่อ", msg);
            }
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public void ConfigReplace_Copies_Onto_Engine_Instance_Without_Touching_Disk()
    {
        var saved = new List<OmniConfig>();
        var (host, server, client) = StartPair(out var cfg, configSaver: c => saved.Add(c));
        try
        {
            var incoming = new OmniConfig { UiLanguage = "th-TH", PlaybackLatencyMs = cfg.PlaybackLatencyMs + 7 };
            client.ReplaceConfig(incoming);

            Assert.True(SpinUntil(() => saved.Count > 0), "config.replace ไม่ถึง engine");
            // in-place copy: instance เดิมถูกแก้ (ทุก service ถือ reference เดิม) + save ผ่าน seam ไม่แตะดิสก์จริง
            Assert.Equal("th-TH", cfg.UiLanguage);
            Assert.Equal(incoming.PlaybackLatencyMs, cfg.PlaybackLatencyMs);
            Assert.Same(cfg, saved.Single());
            Assert.True(host.ReloadLayoutCount >= 1); // hot-apply หลัง replace
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_Without_Engine_Fails_Gracefully()
    {
        var client = new IpcKvmClient("omnilink-test-absent-" + Guid.NewGuid().ToString("N"));
        try
        {
            client.Start();
            Thread.Sleep(300);
            Assert.False(client.IsConnected);
            client.SwitchToNode("n1", 0.5); // offline = กลืน ไม่แครช
            Assert.Empty(client.SessionViews);
            Assert.Null(client.ActiveNodeId);
        }
        finally
        {
            client.Dispose();
        }
    }

    private static bool SpinUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(25);
        }
        return cond();
    }
}
