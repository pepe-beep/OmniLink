using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;
using OmniLink.Core.Strategies;

namespace OmniLink.Core.Ipc;

/// <summary>
/// S2/S3 (Engine/UI split): ฝั่ง dashboard — implement IKvmHost โดยเดินสายไปหา engine (hostd)
/// ทุก state read มาจาก snapshot ล่าสุด (state.changed) · คำสั่งเดินทางผ่านท่อแบบ request/response
/// engine หลุด = เข้าโหมด offline (ค่าว่าง) แล้ว reconnect ตาม ladder 0.5→8s — **ห้าม** สตาร์ท engine ในตัวเอง
/// (กัน engine ซ้อนสองตัวแย่งพอร์ต 24801)
/// </summary>
public sealed class IpcKvmClient : IKvmHost
{
    private readonly object _gate = new();
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcMessage>> _pending = new();
    private Thread? _connector;
    private NamedPipeClientStream? _pipe;
    private int _nextId;
    private volatile IpcWireState? _state;
    private static readonly IpcWireState OfflineState = new();

    public volatile bool IsConnected;
    public event Action? StateChanged;        // ยิงบน reader thread — ผู้รับ marshal เอง
    public event Action? ConnectedChanged;

    public IpcKvmClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? IpcProtocol.DefaultPipeName;
    }

    // ── connection ──

    public void Start()
    {
        if (_connector is not null) return;
        _connector = new Thread(ConnectLoop) { IsBackground = true, Name = "ipc-connect" };
        _connector.Start();
    }

    private void ConnectLoop()
    {
        var ladder = new[] { 500, 1000, 2000, 4000, 8000 };
        var attempt = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(3000);
                IpcProtocol.WriteFrame(pipe, new IpcMessage
                {
                    V = IpcProtocol.Version,
                    Role = "ui",
                    App = IpcProtocol.AppUi,
                    Ver = typeof(IpcKvmClient).Assembly.GetName().Version?.ToString(3) ?? "?",
                });
                var hello = IpcProtocol.ReadFrame(pipe);
                if (hello is null || hello.Ok != true || hello.V != IpcProtocol.Version)
                {
                    throw new IOException(hello?.Err ?? "handshake ไม่ผ่าน");
                }
                lock (_gate) { _pipe = pipe; }
                IsConnected = true;
                attempt = 0;
                ConnectedChanged?.Invoke();
                OmniLog.Write("ipc", $"เชื่อม engine สำเร็จ (hostd v{hello.Ver} pid={hello.Data?.GetProperty("pid").GetInt32()})");
                ReaderLoop(pipe);
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) break; // Dispose ระหว่าง connect — จบเงียบ ๆ ห้ามแครช thread
                if (IsConnected)
                {
                    IsConnected = false;
                    ConnectedChanged?.Invoke();
                    OmniLog.Warn("[ipc] หลุดจาก engine: " + ex.Message);
                }
                else if (attempt == 0)
                {
                    OmniLog.Warn("[ipc] ยังไม่เจอ engine (hostd ไม่รัน?) — จะลองต่อเรื่อย ๆ (โหมด offline)");
                }
                var delay = ladder[Math.Min(attempt, ladder.Length - 1)];
                attempt++;
                try { _cts.Token.WaitHandle.WaitOne(delay); } catch { }
            }
            finally
            {
                lock (_gate)
                {
                    try { _pipe?.Dispose(); } catch { }
                    _pipe = null;
                }
                FailAllPending("engine offline");
            }
        }
    }

    private void ReaderLoop(NamedPipeClientStream pipe)
    {
        while (!_cts.IsCancellationRequested)
        {
            var msg = IpcProtocol.ReadFrame(pipe);
            if (msg is null) return;
            if (msg.Id is int id && _pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(msg);
            }
            else if (msg.Ev is string ev)
            {
                HandleEvent(ev, msg.Data);
            }
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
            {
                tcs.TrySetResult(IpcProtocol.Fail(reason));
            }
        }
    }

    private void HandleEvent(string ev, JsonElement? data)
    {
        try
        {
            switch (ev)
            {
                case "state.changed":
                    _state = data?.Deserialize<IpcWireState>(IpcProtocol.JsonOptions);
                    StateChanged?.Invoke();
                    break;

                case "banner":
                    var m = data?.TryGetProperty("msg", out var mg) == true ? mg.GetString() : null;
                    if (m is not null)
                    {
                        OmniEventHub.Default.Publish(new StatusBannerEvent(m));
                    }
                    break;

                case "node_online":
                    // ไม่ republish ลง hub — dashboard ใช้ state.changed เป็นตัวขับ refresh แล้ว
                    // (audio auto-retry เป็นของฝั่ง engine ทั้งหมดในโหมด split)
                    break;

                case "clipboard.text":
                    var text = data?.TryGetProperty("text", out var t) == true ? t.GetString() : null;
                    if (text is not null)
                    {
                        // engine เขียนคลิปบอร์ดเครื่องจริงแล้ว — dashboard รับผ่าน hub เพื่อ history + guard ซ้ำของเดิม
                        OmniEventHub.Default.Publish(new ClipboardReceivedEvent(text, "engine"));
                    }
                    break;

                case "clipboard.image":
                    if (data?.TryGetProperty("png_base64", out var b64) == true && b64.GetString() is { Length: > 0 } s)
                    {
                        OnClipboardImageFromClient?.Invoke(Convert.FromBase64String(s));
                    }
                    break;

                case "clipboard.files":
                    if (data is not null)
                    {
                        var files = data.Value.TryGetProperty("files", out var f)
                            ? f.Deserialize<List<string>>(IpcProtocol.JsonOptions)?.AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>()
                            : Array.Empty<string>();
                        var isCut = data.Value.TryGetProperty("is_cut", out var ic) && ic.GetBoolean();
                        OnClipboardFilesFromClient?.Invoke(files, isCut);
                    }
                    break;

                case "focus.mic":
                    if (data?.TryGetProperty("start", out var st) == true)
                    {
                        OnFocusMicAction?.Invoke(st.GetBoolean());
                    }
                    break;

                case "audio.activity":
                    if (data?.TryGetProperty("nodeId", out var an) == true &&
                        data.Value.TryGetProperty("active", out var aa))
                    {
                        OnNodeAudioActivityChanged?.Invoke(an.GetString() ?? "", aa.GetBoolean());
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[ipc] event '" + ev + "': " + ex.Message);
        }
    }

    // ── request/response ──

    private IpcMessage Request(string op, object? args = null, int timeoutMs = 8000)
    {
        var pipe = _pipe;
        if (pipe is null || !IsConnected)
        {
            return IpcProtocol.Fail("engine offline");
        }
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            lock (_gate)
            {
                IpcProtocol.WriteFrame(pipe, new IpcMessage { Id = id, Op = op, Args = IpcProtocol.ToElement(args) });
            }
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            return IpcProtocol.Fail("เขียนท่อไม่ได้: " + ex.Message);
        }
        try
        {
            return tcs.Task.Wait(timeoutMs) ? tcs.Task.Result : IpcProtocol.Fail("engine ตอบช้า (timeout)");
        }
        catch (AggregateException ex)
        {
            return IpcProtocol.Fail(ex.InnerException?.Message ?? "ipc error");
        }
    }

    private IpcWireState S => _state ?? OfflineState;

    // ── IKvmHost: state (จาก snapshot) ──

    public IReadOnlyDictionary<string, NodeSessionView> SessionViews
    {
        get
        {
            var s = S;
            var dict = new Dictionary<string, NodeSessionView>(s.Nodes.Count);
            foreach (var n in s.Nodes)
            {
                dict[n.Config.NodeId] = n;
            }
            return dict;
        }
    }

    public string? ActiveNodeId => S.ActiveNode;
    public int MasterLangGroup => S.MasterLang;
    public int VirtualLeft => S.Virtual.Left;
    public int VirtualTop => S.Virtual.Top;
    public int VirtualRight => S.Virtual.Right;
    public int VirtualBottom => S.Virtual.Bottom;

    public TopologyView TopologyView
    {
        get
        {
            var w = S.Topology;
            if (w is null) return new TopologyView();
            var cells = new Dictionary<string, (int X, int Y)>(w.Cells.Count);
            foreach (var kv in w.Cells)
            {
                cells[kv.Key] = (kv.Value.X, kv.Value.Y);
            }
            var edges = new Dictionary<string, IReadOnlyDictionary<ScreenEdge, string>>();
            foreach (var (from, byEdge) in w.Edges)
            {
                var parsed = new Dictionary<ScreenEdge, string>();
                foreach (var (edgeName, to) in byEdge)
                {
                    if (Enum.TryParse<ScreenEdge>(edgeName, out var e))
                    {
                        parsed[e] = to;
                    }
                }
                edges[from] = parsed;
            }
            return new TopologyView(cells, edges);
        }
    }

    public bool IsWindowHiddenInTray { get; set; }
    public Func<bool>? SubActiveFocusProbe { get; set; }

    // delegate wiring — dashboard โหมดเดิมที่ผูกเครื่องจริง (clipboard bridge) จะถูก engine ทำแทน
    public Func<string?>? HostClipboardGetter { get; set; }
    public Func<byte[]?>? HostPngGetter { get; set; }
    public HostFileDropGetterDelegate? HostFileDropGetter { get; set; }
    public Action<string>? OnClipboardFromClient { get; set; }
    public Action<byte[]?>? OnClipboardImageFromClient { get; set; }
    public Action<IReadOnlyList<string>, bool>? OnClipboardFilesFromClient { get; set; }
    public Action<string, bool>? OnNodeAudioActivityChanged { get; set; }
    public Action<bool>? OnFocusMicAction { get; set; }

    // ── IKvmHost: commands ──

    public void ReloadLayout() => Request("reload_layout");
    public void ReloadHotkeys() => Request("reload_hotkeys");
    public void UpdateStrategies() => Request("update_strategies");

    public void SwitchToNode(string nodeId, double normY)
    {
        Request("switch_to_node", new { nodeId, normY }, 3000);
    }

    public void CalibrateAllLanguages(int targetGroup) => Request("calibrate_langs", new { group = targetGroup });

    public int GetHostLangGroup()
    {
        var r = Request("get_host_lang");
        return r.Ok == true && r.Data?.TryGetProperty("group", out var g) == true ? g.GetInt32() : 0;
    }

    public (bool Ok, string Message) DeployRemoteAgent(NodeConfig ncfg)
    {
        var r = Request("deploy_agent", new { node = ncfg }, 180_000);
        if (r.Ok != true)
        {
            return (false, r.Err ?? "deploy ล้มเหลว");
        }
        var ok = r.Data?.TryGetProperty("ok", out var okEl) == true && okEl.GetBoolean();
        var msg = r.Data?.TryGetProperty("msg", out var msgEl) == true ? msgEl.GetString() ?? "" : "";
        return (ok, msg);
    }

    public void AddOrUpdateNode(NodeConfig cfg, string? token = null)
        => Request("add_or_update_node", new { node = cfg, token });

    public void SetNodePolicy(string nodeId, string policy)
    {
        Request("set_node_policy", new { nodeId, policy });
    }

    public bool RemoveNode(string nodeId)
    {
        var r = Request("remove_node", new { nodeId });
        return r.Ok == true && r.Data?.TryGetProperty("ok", out var okEl) == true && okEl.GetBoolean();
    }

    public bool SendNodeCommand(string ip, object payload)
        => Request("send_node_command", new { ip, payload }).Ok == true;

    public string? GetLocalEndpointIpForNode(string ip) => null; // audio seam — dashboard ไม่รัน audio เอง จึงไม่ถูกเรียก

    /// <summary>C2: dashboard mode — push ตอนสลับจอเองอยู่แล้ว (engine ทำให้)</summary>
    public void PushClipboardToActiveNode() { }

    /// <summary>S3: dashboard ยังต้อง "อ่าน/สั่ง" เสียง (engine เป็นคนรัน gst จริง)</summary>
    public bool AudioStreamRunning => S.Audio?.StreamRunning ?? false;
    public bool AudioMicRunning => S.Audio?.MicRunning ?? false;

    public void AudioOp(string op) => Request(op, null, 15000);

    /// <summary>S3: dashboard แก้ config ในเครื่องตัวเองแล้วส่งฉบับเต็มให้ engine apply+save (S0 §4.3)</summary>
    public void ReplaceConfig(OmniConfig config) => Request("config.replace", new { config }, 15_000);

    public List<AudioMicInfo> AudioScanMics()
    {
        var r = Request("audio.scan_mics", null, 30_000);
        if (r.Ok == true && r.Data?.TryGetProperty("mics", out var mics) == true)
        {
            return mics.Deserialize<List<AudioMicInfo>>(IpcProtocol.JsonOptions) ?? new List<AudioMicInfo>();
        }
        return new List<AudioMicInfo>();
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate)
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }
    }
}
