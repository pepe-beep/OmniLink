using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;

using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;

namespace OmniLink.Core.Ipc;

/// <summary>
/// S2 (Engine/UI split): ฝั่ง engine — รับ dashboard client ผ่าน named pipe ตามสัญญา S0
/// เสียบ "หลัง facade": รับ IKvmHost + config (+ AudioHubService ถ้ามี) แล้วเปิด ops ให้ UI
/// กติกา: (1) ทุก op กลืน exception → ok:false เสมอ (2) event ทั้งหมด "โยงต่อ" handler เดิมที่ hostd ตั้งไว้
/// (bridge clipboard/log ไม่หาย) (3) hot path ไม่เกี่ยวกับคลาสนี้เด็ดขาด
/// </summary>
public sealed class IpcHostServer : IDisposable
{
    private readonly IKvmHost _kvm;
    private readonly OmniConfig _cfg;
    private readonly AudioHubService? _audio;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, ClientCtx> _clients = new();
    private Thread? _acceptThread;
    private int _clientSeq;
    private long _lastStatePush;
    private static readonly long StatePushMinTickMs = 500;

    public IpcHostServer(IKvmHost kvm, OmniConfig cfg, AudioHubService? audio, string? pipeName = null,
        Action<OmniConfig>? configSaver = null)
    {
        _kvm = kvm;
        _cfg = cfg;
        _audio = audio;
        _pipeName = pipeName ?? IpcProtocol.DefaultPipeName;
        _configSaver = configSaver ?? (c => c.Save());
        WireEventForwarding();
    }

    private readonly Action<OmniConfig> _configSaver;
    private string? _lastBannerText;
    private long _lastBannerTick;

    public void Start()
    {
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ipc-accept" };
        _acceptThread.Start();
        OmniLog.Write("ipc", $"engine IPC server รอ dashboard ที่ pipe '{_pipeName}' (v{IpcProtocol.Version})");
    }

    // ── accept + client loop ──

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = StreamFactoryOverride?.Invoke(_pipeName) ?? new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var connected = server.WaitForConnectionAsync(_cts.Token);
                connected.Wait(_cts.Token);
                var ctx = new ClientCtx(server, Interlocked.Increment(ref _clientSeq));
                _clients[ctx.Seq] = ctx;
                OmniLog.Write("ipc", "dashboard เชื่อมต่อเข้ามา");
                var t = new Thread(() => ClientLoop(ctx)) { IsBackground = true, Name = "ipc-client" };
                t.Start();
            }
            catch (OperationCanceledException) { try { server?.Dispose(); } catch { } break; }
            catch (Exception ex)
            {
                try { server?.Dispose(); } catch { }
                if (_cts.IsCancellationRequested) break;
                OmniLog.Warn("[ipc] accept: " + ex.Message);
                Thread.Sleep(1000);
            }
        }
    }

    private void ClientLoop(ClientCtx ctx)
    {
        try
        {
            var hello = IpcProtocol.ReadFrame(ctx.Stream);
            if (hello?.V != IpcProtocol.Version || hello.Role != "ui")
            {
                IpcProtocol.WriteFrame(ctx.Stream, new IpcMessage { Ok = false, Err = $"version-mismatch (engine=v{IpcProtocol.Version})" });
                return;
            }
            IpcProtocol.WriteFrame(ctx.Stream, new IpcMessage
            {
                V = IpcProtocol.Version,
                Role = "engine",
                App = IpcProtocol.AppEngine,
                Ver = typeof(KvmHostService).Assembly.GetName().Version?.ToString(3) ?? "?",
                Ok = true,
            });
            PushState(ctx);

            while (!ctx.Closed)
            {
                var msg = IpcProtocol.ReadFrame(ctx.Stream);
                if (msg is null) break;
                if (msg.Id is int id && msg.Op is string op)
                {
                    var resp = SafeHandle(op, msg.Args);
                    resp.Id = id;
                    IpcProtocol.WriteFrame(ctx.Stream, resp);
                }
            }
        }
        catch (Exception ex)
        {
            OmniLog.Write("ipc", "dashboard หลุด: " + ex.Message);
        }
        finally
        {
            _clients.TryRemove(ctx.Seq, out _);
            ctx.Dispose();
            OmniLog.Write("ipc", "dashboard ออกจากท่อ");
        }
    }

    private IpcMessage SafeHandle(string op, JsonElement? args)
    {
        try
        {
            return HandleOp(op, args);
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[ipc] op '{op}' ล้มเหลว: {ex.Message}");
            return IpcProtocol.Fail(ex.Message);
        }
    }

    // ── ops ──

    private IpcMessage HandleOp(string op, JsonElement? args)
    {
        switch (op)
        {
            case "ping":
                return IpcProtocol.Ok(new { pong = true });

            case "state.get":
                return IpcProtocol.Ok(BuildState());

            case "reload_layout":
                _kvm.ReloadLayout();
                RequestStatePush();
                return IpcProtocol.Ok();

            case "reload_hotkeys":
                _kvm.ReloadHotkeys();
                return IpcProtocol.Ok();

            case "update_strategies":
                _kvm.UpdateStrategies();
                return IpcProtocol.Ok();

            case "switch_to_node":
            {
                var nodeId = args?.GetProperty("nodeId").GetString();
                var normY = args?.TryGetProperty("normY", out var ny) == true ? ny.GetDouble() : 0.5;
                if (string.IsNullOrEmpty(nodeId)) return IpcProtocol.Fail("ต้องมี nodeId");
                _kvm.SwitchToNode(nodeId!, normY);
                RequestStatePush();
                return IpcProtocol.Ok();
            }

            case "calibrate_langs":
            {
                var group = args?.GetProperty("group").GetInt32() ?? 0;
                _kvm.CalibrateAllLanguages(group);
                return IpcProtocol.Ok();
            }

            case "get_host_lang":
                return IpcProtocol.Ok(new { group = _kvm.GetHostLangGroup() });

            case "deploy_agent":
            {
                var node = args?.GetProperty("node").Deserialize<NodeConfig>(IpcProtocol.JsonOptions);
                if (node is null) return IpcProtocol.Fail("ต้องมี node");
                var (ok, msg) = _kvm.DeployRemoteAgent(node);
                return IpcProtocol.Ok(new { ok, msg });
            }

            case "add_or_update_node":
            {
                var node = args?.GetProperty("node").Deserialize<NodeConfig>(IpcProtocol.JsonOptions);
                var token = args?.TryGetProperty("token", out var tk) == true ? tk.GetString() : null;
                if (node is null) return IpcProtocol.Fail("ต้องมี node");
                _kvm.AddOrUpdateNode(node, token);
                RequestStatePush();
                return IpcProtocol.Ok();
            }

            case "set_node_policy":
            {
                var nodeId = args?.GetProperty("nodeId").GetString();
                var policy = args?.GetProperty("policy").GetString();
                if (nodeId is null || policy is null) return IpcProtocol.Fail("ต้องมี nodeId/policy");
                _kvm.SetNodePolicy(nodeId, policy);
                RequestStatePush();
                return IpcProtocol.Ok();
            }

            case "remove_node":
            {
                var nodeId = args?.GetProperty("nodeId").GetString();
                if (nodeId is null) return IpcProtocol.Fail("ต้องมี nodeId");
                var ok = _kvm.RemoveNode(nodeId);
                RequestStatePush();
                return IpcProtocol.Ok(new { ok });
            }

            case "send_node_command":
            {
                var ip = args?.GetProperty("ip").GetString();
                if (ip is null || args?.TryGetProperty("payload", out var payload) != true)
                {
                    return IpcProtocol.Fail("ต้องมี ip/payload");
                }
                var ok = _kvm.SendNodeCommand(ip, payload);
                return IpcProtocol.Ok(new { ok });
            }

            case "config.replace":
            {
                var incoming = args?.GetProperty("config").Deserialize<OmniConfig>(IpcProtocol.JsonOptions);
                if (incoming is null) return IpcProtocol.Fail("ต้องมี config");
                ApplyConfigReplace(incoming);
                return IpcProtocol.Ok();
            }

            case "audio.state":
                return IpcProtocol.Ok(BuildAudio());

            case "audio.start_stream": _audio?.StartStream(); return IpcProtocol.Ok(BuildAudio());
            case "audio.stop_stream": _audio?.StopStream(); return IpcProtocol.Ok(BuildAudio());
            case "audio.start_mic": _audio?.StartMic(); return IpcProtocol.Ok(BuildAudio());
            case "audio.stop_mic": _audio?.StopMic(); return IpcProtocol.Ok(BuildAudio());
            case "audio.reset_both": _audio?.ResetBoth(); return IpcProtocol.Ok(BuildAudio());
            case "audio.scan_mics":
                if (_audio is null) return IpcProtocol.Fail("engine ไม่มี audio hub");
                return IpcProtocol.Ok(new { mics = _audio.ScanMicrophones().Select(m => new AudioMicInfo { Id = m.Id, Name = m.Name }).ToList() });

            default:
                return IpcProtocol.Fail($"unsupported-op '{op}'");
        }
    }

    /// <summary>S0 §4.3: engine เป็นเจ้าของไฟล์ config — dashboard ส่งฉบับเต็มมา "ทับ" แบบ in-place copy
    /// (เพราะทุก service ถือ reference ของ _cfg เดิม) แล้ว normalize + save + hot-apply ส่วนที่มี reload</summary>
    private void ApplyConfigReplace(OmniConfig incoming)
    {
        foreach (var prop in typeof(OmniConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            prop.SetValue(_cfg, prop.GetValue(incoming));
        }
        OmniLimits.Normalize(_cfg);
        _configSaver(_cfg);
        _kvm.ReloadLayout();
        _kvm.ReloadHotkeys();
        _kvm.UpdateStrategies();
        OmniLog.Write("ipc", "dashboard push config.replace → apply + save แล้ว (hot: layout/hotkeys/strategies)");
        RequestStatePush();
    }

    // ── state snapshot + events ──

    private IpcWireState BuildState()
    {
        var st = new IpcWireState
        {
            ActiveNode = _kvm.ActiveNodeId,
            MasterLang = _kvm.MasterLangGroup,
            Virtual = new IpcWireRect { Left = _kvm.VirtualLeft, Top = _kvm.VirtualTop, Right = _kvm.VirtualRight, Bottom = _kvm.VirtualBottom },
            AppVersion = typeof(KvmHostService).Assembly.GetName().Version?.ToString(3) ?? "?",
            Pid = Environment.ProcessId,
            Audio = BuildAudio(),
        };
        var topo = _kvm.TopologyView;
        var cells = new Dictionary<string, IpcWireCell>();
        foreach (var kv in topo.Cells)
        {
            cells[kv.Key] = new IpcWireCell { X = kv.Value.X, Y = kv.Value.Y };
        }
        var edges = new Dictionary<string, Dictionary<string, string>>();
        foreach (var kv in topo.EdgesView)
        {
            edges[kv.Key] = kv.Value.ToDictionary(e => e.Key.ToString(), e => e.Value);
        }
        st.Topology = new IpcWireTopology { Cells = cells, Edges = edges };
        foreach (var kv in _kvm.SessionViews)
        {
            st.Nodes.Add(kv.Value);
        }
        return st;
    }

    private IpcWireAudio? BuildAudio()
        => _audio is null ? null : new IpcWireAudio { StreamRunning = _audio.IsStreamRunning, MicRunning = _audio.IsMicRunning };

    /// <summary>เรียกเมื่อ state น่าจะเปลี่ยน — throttle ตามสัญญา (ไม่ถี่กว่า 500ms)</summary>
    public void RequestStatePush()
    {
        var now = Environment.TickCount64;
        if (now - _lastStatePush < StatePushMinTickMs) return;
        _lastStatePush = now;
        Broadcast("state.changed", BuildState());
    }

    private void Broadcast(string ev, object? data)
    {
        var msg = new IpcMessage { Ev = ev, Data = IpcProtocol.ToElement(data) };
        foreach (var ctx in _clients.Values)
        {
            try
            {
                if (!ctx.Closed)
                {
                    lock (ctx.WriteLock)
                    {
                        IpcProtocol.WriteFrame(ctx.Stream, msg);
                    }
                }
            }
            catch { ctx.Closed = true; }
        }
    }

    private void PushState(ClientCtx ctx)
    {
        try
        {
            lock (ctx.WriteLock)
            {
                IpcProtocol.WriteFrame(ctx.Stream, new IpcMessage { Ev = "state.changed", Data = IpcProtocol.ToElement(BuildState()) });
            }
        }
        catch { ctx.Closed = true; }
    }

    /// <summary>โยงต่อ delegate เดิมของ engine (hostd ตั้งไว้แล้ว — log/bridge ต้องไม่หาย) แล้ว broadcast ให้ dashboard</summary>
    private void WireEventForwarding()
    {
        // banner ของ engine เดินทางผ่าน event hub เสมอ (engine-mode UI ก็รับทางนี้) — server แค่ส่งต่อลงท่อ
        // dedup 2 วิ: กันกรณี server/client อยู่ process เดียวกัน (unit test) ที่ hub วนกลับมาที่ server
        OmniEventHub.Default.Subscribe<StatusBannerEvent>(e =>
        {
            var now = Environment.TickCount64;
            if (e.Message == _lastBannerText && now - _lastBannerTick < 2000) return;
            _lastBannerText = e.Message;
            _lastBannerTick = now;
            Broadcast("banner", new { msg = e.Message });
        });

        var prevClipText = _kvm.OnClipboardFromClient;
        _kvm.OnClipboardFromClient = text =>
        {
            prevClipText?.Invoke(text);
            Broadcast("clipboard.text", new { text });
            RequestStatePush();
        };
        var prevClipPng = _kvm.OnClipboardImageFromClient;
        _kvm.OnClipboardImageFromClient = png =>
        {
            prevClipPng?.Invoke(png);
            Broadcast("clipboard.image", new { png_base64 = png is { Length: > 0 } ? Convert.ToBase64String(png) : null });
        };
        var prevClipFiles = _kvm.OnClipboardFilesFromClient;
        _kvm.OnClipboardFilesFromClient = (files, isCut) =>
        {
            prevClipFiles?.Invoke(files, isCut);
            Broadcast("clipboard.files", new { files, is_cut = isCut });
        };

        var prevFocusMic = _kvm.OnFocusMicAction;
        _kvm.OnFocusMicAction = start =>
        {
            prevFocusMic?.Invoke(start);
            Broadcast("focus.mic", new { start });
        };
        var prevAudioAct = _kvm.OnNodeAudioActivityChanged;
        _kvm.OnNodeAudioActivityChanged = (nodeId, active) =>
        {
            prevAudioAct?.Invoke(nodeId, active);
            Broadcast("audio.activity", new { nodeId, active });
        };

        OmniEventHub.Default.Subscribe<NodeConnectionEvent>(e =>
        {
            Broadcast("node_online", new { nodeId = e.NodeId, ip = e.Ip, connected = e.Connected, agent = "", lang = e.LangGroup });
            RequestStatePush();
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var ctx in _clients.Values)
        {
            ctx.Dispose();
        }
        _clients.Clear();
    }

    /// <summary>godclass-G3: Windows hostd inject factory (ACL current user+SYSTEM) จาก Platform.Windows — Core คง zero-dep</summary>
    public static Func<string, NamedPipeServerStream>? StreamFactoryOverride;

    private sealed class ClientCtx(NamedPipeServerStream stream, int seq)
    {
        public NamedPipeServerStream Stream { get; } = stream;
        public int Seq { get; } = seq;
        public object WriteLock { get; } = new();
        public volatile bool Closed;

        public void Dispose()
        {
            Closed = true;
            try { Stream.Dispose(); } catch { }
        }
    }
}
