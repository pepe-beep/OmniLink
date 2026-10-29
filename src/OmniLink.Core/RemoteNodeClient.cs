using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Clipboard;
using OmniLink.Core.Events;
using OmniLink.Core.FileExchange;
using OmniLink.Core.Security;
using OmniLink.Core.Strategies;

namespace OmniLink.Core;

public sealed class RemoteNodeClient : IDisposable
{
    public NodeConfig Config { get; }
    public bool Connected { get; private set; }

    /// <summary>GroupA-D: TickCount64 ตอน connection กลับมาเป็น connected ล่าสุด — ใช้กันสวิตช์ทันทีที่ reconnect (DESYNC 17:57)</summary>
    public long ConnectedAtTick { get; private set; }
    public string? LocalEndpointIp { get; private set; }
    public int Width { get; private set; } = 960;
    public int Height { get; private set; } = 720;
    public string AgentVersion { get; private set; } = "?";

    /// <summary>
    /// Hardware-verified language group reported by the remote Linux agent (0 = EN, 1 = TH).
    /// Never faked by the Host UI.
    /// </summary>
    public int LangGroup { get; private set; }

    public double PingMs { get; private set; }
    public double RamPct { get; private set; }
    public double RamUsedGb { get; private set; }
    public double RamTotalGb { get; private set; }
    public bool PlaybackRunning { get; private set; }
    public bool PlaybackActive { get; private set; }
    public bool MicRunning { get; private set; }

    public ConnectionLifecycleState LifecycleState { get; private set; } = ConnectionLifecycleState.Hot;

    // Configurable thresholds for Adaptive Connection (ADR-005) - allows unit testing
    public int HotDurationMs { get; set; } = 30_000;
    public int ColdDurationMs { get; set; } = 300_000;
    public int HotRetryDelayMs { get; set; } = 2_000;
    public int ColdRetryDelayMs { get; set; } = 25_000;

    private readonly AutoResetEvent _wakeUpSignal = new(false);
    private readonly AutoResetEvent _wakeUpPing = new(false);
    private long _disconnectedSinceTick;

    public void WakeUp()
    {
        _disconnectedSinceTick = Environment.TickCount64;
        LifecycleState = ConnectionLifecycleState.Hot;
        _wakeUpSignal.Set();
    }

    private int CalculateRetryDelayAndSetState()
    {
        if (_disconnectedSinceTick == 0)
        {
            _disconnectedSinceTick = Environment.TickCount64;
        }

        var elapsed = Environment.TickCount64 - _disconnectedSinceTick;
        if (elapsed < HotDurationMs)
        {
            LifecycleState = ConnectionLifecycleState.Hot;
            return HotRetryDelayMs;
        }
        if (elapsed < ColdDurationMs)
        {
            LifecycleState = ConnectionLifecycleState.Cold;
            return ColdRetryDelayMs;
        }

        LifecycleState = ConnectionLifecycleState.Freeze;
        // v1.9.1 (บั๊กจากรายงานผู้ใช้: host เปิดก่อน sub เปิดทีหลัง = ไม่เห็น sub):
        // เดิม Freeze = หยุด retry ถาวร — sub ที่เปิดช้ากว่า ~5.5 นาทีไม่มีวันถูกเชื่อม
        // (ต้องกด Scan/รีสตาร์ทเอง) → คง retry ต่อที่จังหวะ Cold (25 วิ) ตลอดชีวิต session
        // ต้นทุนต่ำมาก (TCP connect เดียวต่อ 25 วิ) · SSH redeploy ยังโดน watchdog throttle 90s/2min เหมือนเดิม
        return ColdRetryDelayMs;
    }

    private int _requestedLangGroup = -1;
    private long _requestedLangTick;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _udpEndPoint;
    private readonly string _authToken;                         // F2: shared token ต่อโหนด
    private readonly byte[]? _udpKey;                           // v1.6: HMAC key ของ UDP packet (null = token ว่าง ส่งแบบไม่ลงลายเซ็น)
    private TcpClient? _tcp;
    private Stream? _stream;                                    // v1.6: เป็น SslStream ครอบ NetworkStream เสมอ (TLS)
    private readonly object _tcpLock = new();
    private volatile bool _running = true;
    private bool _authOk;                                       // F2: agent ยืนยัน token แล้วหรือยัง
    private long _authDeadlineTick;
    private bool _legacyReported;                               // F2c: แจ้งเตือน agent เก่าครั้งเดียวต่อ connection
    private bool _needsPairingLogged;                           // v1.6: แจ้ง "ต้องจับคู่ใหม่" ครั้งเดียวต่อ connection
    private readonly IHostLinkCallbacks _callbacks;

    public RemoteNodeClient(
        NodeConfig cfg,
        string authToken,
        IHostLinkCallbacks callbacks)
    {
        Config = cfg;
        Width = cfg.Width;
        Height = cfg.Height;
        _authToken = authToken ?? "";
        _udpKey = string.IsNullOrEmpty(_authToken) ? null : OmniPacketAuth.DeriveUdpKey(_authToken);
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));

        _udp = new UdpClient();
        _udp.Client.SendBufferSize = 262144;
        if (!IPAddress.TryParse(cfg.Ip, out var nodeIp))
        {
            nodeIp = IPAddress.None; // config ถูก validate แล้วที่ Load — นี่เป็นชั้นกันสุดท้าย ไม่ให้โยน
        }
        _udpEndPoint = new IPEndPoint(nodeIp, OmniProtocol.KvmUdpPort);

        var t = new Thread(ConnectionLoop) { IsBackground = true, Name = $"OmniNode_{cfg.NodeId}" };
        t.Start();
    }

    public int GetEffectiveLangForToggle()
    {
        if (_requestedLangGroup != -1 && (Environment.TickCount64 - _requestedLangTick) < 600)
        {
            return _requestedLangGroup;
        }
        return LangGroup;
    }

    /// <summary>คำสั่ง set_lang ที่ยังไม่ถูก agent ยืนยัน (ใช้ตัดสิน lost-command vs user-toggle)</summary>
    public int PendingLangRequest()
    {
        var req = _requestedLangGroup;
        if (req != -1 && (Environment.TickCount64 - _requestedLangTick) < 5000)
        {
            return req;
        }
        return -1;
    }

    public void RequestSetLanguage(int targetGroup, bool calibrate = false)
    {
        var norm = targetGroup == 1 ? 1 : 0;
        _requestedLangGroup = norm;
        _requestedLangTick = Environment.TickCount64;
        // Bug B fix: อย่าส่งซ้ำสองช่องทางพร้อมกัน (agent จะ ack ซ้ำ + ทำงานสองรอบ)
        // ใช้ TCP เมื่อออนไลน์ (เชื่อถือได้), UDP เฉพาะเมื่อ TCP ยังไม่ติด
        if (!SendTcp(new { type = "set_lang", group = norm }))
        {
            SendUdp(OmniProtocol.PackSetLang(norm, calibrate));
        }
    }

    public void SendUdp(byte[] packet)
    {
        SendUdp(packet.AsSpan());
    }

    /// <summary>v1.6: แนบ HMAC trailer 16 ไบต์ท้าย packet เสมอเมื่อมี token (agent ตรวจก่อนยอดรับ input)</summary>

    public void SendUdp(ReadOnlySpan<byte> packet)
    {
        var key = _udpKey;
        try
        {
            if (key is null)
            {
                _udp.Client.SendTo(packet, SocketFlags.None, _udpEndPoint);
                return;
            }

            // v1.8 SV-L1: wire v2 = [body][seq 8B][HMAC 16B] — agent ปฏิเสธ seq ที่ไม่ใหม่กว่าของ IP ตัวเอง
            var seq = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // SV-L1: UnixMs — agent ใช้ freshness window ±5s
            Span<byte> buf = packet.Length + OmniPacketAuth.SeqSize + OmniPacketAuth.TrailerSize <= 96
                ? stackalloc byte[packet.Length + OmniPacketAuth.SeqSize + OmniPacketAuth.TrailerSize]
                : new byte[packet.Length + OmniPacketAuth.SeqSize + OmniPacketAuth.TrailerSize];
            OmniPacketAuth.AppendTrailerWithSeq(packet, key, seq, buf);
            _udp.Client.SendTo(buf, SocketFlags.None, _udpEndPoint);
        }
        catch
        {
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SendMouseRel(int dx, int dy, byte modMask)
    {
        Span<byte> buf = stackalloc byte[6];
        OmniProtocol.WriteMouseRel(buf, dx, dy, modMask);
        SendUdp(buf);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SendMouseButton(byte button, bool down, byte modMask)
    {
        Span<byte> buf = stackalloc byte[4];
        OmniProtocol.WriteMouseButton(buf, button, down, modMask);
        SendUdp(buf);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SendMouseWheel(int wx, int wy, byte modMask)
    {
        Span<byte> buf = stackalloc byte[6];
        OmniProtocol.WriteMouseWheel(buf, wx, wy, modMask);
        SendUdp(buf);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SendKeyEvent(int x11Keycode, bool down, byte modMask)
    {
        Span<byte> buf = stackalloc byte[5];
        OmniProtocol.WriteKeyEvent(buf, x11Keycode, down, modMask);
        SendUdp(buf);
    }

    public bool SendTcp(object payload)
    {
        lock (_tcpLock)
        {
            if (!Connected || _stream is null) return false;
            try
            {
                var bytes = OmniProtocol.EncodeTcpJson(payload);
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
                return true;
            }
            catch
            {
                Connected = false;
                return false;
            }
        }
    }

    private void ConnectionLoop()
    {
        var lastPing = DateTime.UtcNow.AddSeconds(-10);
        while (_running)
        {
            if (!Config.Enabled)
            {
                if (Connected)
                {
                    Connected = false;
                    _callbacks.OnDisconnected(Config.NodeId);
                    _callbacks.OnStateChanged(Config.NodeId, "disabled");
                }
                _wakeUpSignal.WaitOne(1000);
                continue;
            }

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(Config.Ip, OmniProtocol.KvmTcpPort);
                if (!connectTask.Wait(TimeSpan.FromSeconds(2.0)) || !client.Connected)
                {
                    var retryDelay = CalculateRetryDelayAndSetState();
                    _wakeUpSignal.WaitOne(retryDelay);
                    continue;
                }

                client.NoDelay = true;
                client.ReceiveTimeout = 3000;
                var netStream = client.GetStream();
                netStream.WriteTimeout = 5000; // C5: กัน SendTcp ค้างไม่จำกัดเมื่อ peer hang บน connection

                // v1.6 Security: ครอบทุก connection ด้วย TLS — ตรวจ fingerprint ที่ pin ไว้ตอนจับคู่ (TOFU แบบ Deskflow)
                Stream stream;
                try
                {
                    var ssl = new SslStream(netStream, leaveInnerStreamOpen: false);
                    ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
                    {
                        TargetHost = Config.Ip,
                        RemoteCertificateValidationCallback = ValidateAgentCertificate,
                    });
                    stream = ssl;
                }
                catch (Exception ex)
                {
                    OmniLog.Warn($"[node] {Config.NodeId} TLS ล้มเหลว: {ex.Message} (agent เก่ากว่า v1.6 หรือ fingerprint ไม่ตรง → รอ redeploy / จับคู่ใหม่)");
                    throw;
                }

                lock (_tcpLock)
                {
                    _tcp = client;
                    _stream = stream;
                    if (client.Client.LocalEndPoint is IPEndPoint lep)
                    {
                        LocalEndpointIp = lep.Address.ToString();
                    }
                    Connected = true;
                    ConnectedAtTick = Environment.TickCount64;
                    _disconnectedSinceTick = 0;
                    LifecycleState = ConnectionLifecycleState.Hot;
                }

                // F2: auth ต้องเป็นข้อความแรกสุด — ก่อน _onConnected
                // (ไม่งั้น onConnected จะ trigger RequestSetLanguage ยิง set_lang แทรกก่อน auth แล้วโดน agent ตัด)
                _authOk = false;
                _legacyReported = false;
                _authDeadlineTick = Environment.TickCount64 + 3000;
                SendTcp(new { type = "auth", token = _authToken });

                OmniLog.Write("node", $"connected id={Config.NodeId} ip={Config.Ip}");
                _callbacks.OnConnected(Config.NodeId);
                _callbacks.OnStateChanged(Config.NodeId, "connected");

                _wakeUpPing.Reset();
                var pingThread = new Thread(() =>
                {
                    while (_running && Connected && Config.Enabled)
                    {
                        var pingIntervalMs = _callbacks.GetAdaptivePingIntervalMs(Config.NodeId);
                        var ts = (double)Stopwatch.GetTimestamp();
                        SendTcp(new { type = "ping", ts });
                        if (_wakeUpPing.WaitOne(pingIntervalMs)) break;
                    }
                }) { IsBackground = true, Name = $"Ping_{Config.NodeId}" };
                pingThread.Start();

                var headerBuf = new byte[4];
                while (_running && Connected && Config.Enabled)
                {
                    // F2: fail-closed — agent ไม่ยืนยัน token ภายใน 3 วิ = ตัดการเชื่อมต่อแล้ว retry
                    // (agent เก่าจะถูกตรวจจับผ่าน hello แล้ว auto-redeploy ตาม F2c)
                    if (!_authOk && Environment.TickCount64 > _authDeadlineTick)
                    {
                        OmniLog.Warn($"[node] {Config.NodeId} ไม่ยืนยัน auth ภายใน 3 วินาที — ปิด connection");
                        break;
                    }

                    if (!ReadExact(stream, headerBuf, 4)) break;
                    var len = BinaryPrimitives.ReadUInt32BigEndian(headerBuf);
                    if (len == 0 || len > 1024 * 128) break;
                    var body = new byte[len];
                    if (!ReadExact(stream, body, (int)len)) break;
                    HandleMessage(Encoding.UTF8.GetString(body));
                }
            }
            catch
            {
            }
            finally
            {
                _wakeUpPing.Set();
                lock (_tcpLock)
                {
                    Connected = false;
                    LocalEndpointIp = null;
                    _stream = null;
                    _tcp = null;
                }
                _callbacks.OnDisconnected(Config.NodeId);
                _callbacks.OnStateChanged(Config.NodeId, "disconnected");
                var retryDelay = CalculateRetryDelayAndSetState();
                _wakeUpSignal.WaitOne(retryDelay);
            }
        }
    }

    /// <summary>v1.6: ตรวจ certificate ของ agent ด้วย fingerprint ที่ pin ไว้ใน config — ไม่แคร์ chain/CN (self-signed ตั้งใจ)</summary>
    private bool ValidateAgentCertificate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
        {
            OmniLog.Warn($"[node] {Config.NodeId} ไม่มี TLS certificate — ปฏิเสธ (agent เก่ากว่า v1.6?)");
            return false;
        }

        var fp = OmniPacketAuth.CertificateFingerprint(certificate.Export(X509ContentType.Cert));
        var pinned = (Config.CertFingerprintSha256 ?? "").Replace(":", "").Replace("-", "").ToLowerInvariant();

        if (string.IsNullOrEmpty(pinned))
        {
            if (!_needsPairingLogged)
            {
                _needsPairingLogged = true;
                OmniLog.Warn($"[node] {Config.NodeId} ยังไม่มี TLS fingerprint pin → ต้องจับคู่ใหม่ 1 ครั้ง (ระบบความปลอดภัย v1.6)");
            }
            return false;
        }

        if (!string.Equals(fp, pinned, StringComparison.Ordinal))
        {
            OmniLog.Error($"[node] {Config.NodeId} TLS FINGERPRINT ไม่ตรง! pin={pinned[..Math.Min(12, pinned.Length)]}.. got={fp[..12]}.. → ปฏิเสธ (cert ถูกสร้างใหม่ หรือมีผู้แทรกกลาง — จับคู่ใหม่เฉพาะเมื่อยืนยันเครื่องลูกได้)");
            return false;
        }

        return true;
    }

    private static bool ReadExact(Stream stream, byte[] buf, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var r = stream.Read(buf, offset, count - offset);
            if (r <= 0) return false;
            offset += r;
        }
        return true;
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var mtype = typeProp.GetString();

            if (mtype == "auth_ok")
            {
                _authOk = true; // F2: token ถูกต้อง
                try { if (_tcp?.Client is not null) _tcp.Client.ReceiveTimeout = 30000; } catch { }
                return;
            }

            if (mtype == "clipboard")
            {
                // Clipboard ข้ามเครื่อง: agent push มาตอนผู้ใช้ออกจอ Linux
                var mime = root.TryGetProperty("mime", out var mp) ? mp.GetString() : "text/plain";
                if (root.TryGetProperty("data", out var dp) || mime == "image/png")
                {
                    string? dataB64 = root.TryGetProperty("data", out var dataProp) ? dataProp.GetString() :
                                      root.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                    if (!string.IsNullOrEmpty(dataB64))
                    {
                        var raw = OmniImage.FromBase64(dataB64);
                        var sanitized = OmniImage.Codec.SanitizePng(raw);
                        if (sanitized.Length > 0)
                        {
                            try { _callbacks.OnClipboardReceived("image/png", sanitized); } catch { }
                        }
                    }
                    return;
                }

                var text = root.TryGetProperty("text", out var tp2) ? tp2.GetString() : null;
                if (!string.IsNullOrEmpty(text))
                {
                    if (Encoding.UTF8.GetByteCount(text) <= 5_000_000)
                    {
                        try { _callbacks.OnClipboardReceived("text/plain", Encoding.UTF8.GetBytes(text!)); } catch { }
                    }
                }
                return;
            }

            if (mtype == "clip_meta")
            {
                // ACK-Gate: remote agent announced available clipboard item
                var token = root.TryGetProperty("token", out var tk) ? tk.GetString() : null;
                if (!string.IsNullOrEmpty(token))
                {
                    SendTcp(new { type = "clip_fetch", token });
                }
                return;
            }

            if (mtype == "clip_fetch")
            {
                // Remote agent requests cached data for token
                var token = root.TryGetProperty("token", out var tk) ? tk.GetString() : null;
                if (!string.IsNullOrEmpty(token) && _callbacks.TryGetPendingClip(token, out var clip))
                {
                    SendTcp(new { type = "clip_data", token, mime = clip.mime, data = Convert.ToBase64String(clip.data) });
                }
                return;
            }

            if (mtype == "clip_data")
            {
                // Remote agent returned the requested data
                var mime = root.TryGetProperty("mime", out var mp) ? mp.GetString() : "text/plain";
                var dataB64 = root.TryGetProperty("data", out var dp) ? dp.GetString() : null;
                if (!string.IsNullOrEmpty(dataB64))
                {
                    var raw = OmniImage.FromBase64(dataB64);
                    if (mime == "image/png")
                    {
                        var sanitized = OmniImage.Codec.SanitizePng(raw);
                        if (sanitized.Length > 0)
                        {
                            try { _callbacks.OnClipboardReceived("image/png", sanitized); } catch { }
                        }
                    }
                    else
                    {
                        try { _callbacks.OnClipboardReceived("text/plain", raw); } catch { }
                    }
                }
                return;
            }

            if (mtype == "history_request")
            {
                _callbacks.OnHistoryRequestReceived(Config.NodeId);
                return;
            }

            if (mtype == "history_get")
            {
                var hid = root.TryGetProperty("id", out var hp) ? hp.GetString() : null;
                if (!string.IsNullOrEmpty(hid))
                {
                    _callbacks.OnHistoryGetReceived(Config.NodeId, hid);
                }
                return;
            }

            if (mtype == "file_offer")
            {
                var manifestJson = root.TryGetProperty("manifest", out var mp) ? mp.GetString() : null;
                if (!string.IsNullOrEmpty(manifestJson))
                {
                    _callbacks.OnFileOfferReceived(Config.NodeId, manifestJson);
                }
                return;
            }

            if (mtype == "file_accept")
            {
                var transferId = root.TryGetProperty("id", out var ip) ? ip.GetString() : null;
                if (!string.IsNullOrEmpty(transferId))
                {
                    _callbacks.OnFileAcceptReceived(Config.NodeId, transferId);
                }
                return;
            }

            if (mtype == "file_chunk")
            {
                var transferId = root.TryGetProperty("id", out var ip) ? ip.GetString() : null;
                var fileIdx = root.TryGetProperty("idx", out var xp) ? xp.GetInt32() : 0;
                var offset = root.TryGetProperty("offset", out var op) ? op.GetInt64() : 0;
                var dataB64 = root.TryGetProperty("data", out var dp) ? dp.GetString() : null;
                if (!string.IsNullOrEmpty(transferId) && !string.IsNullOrEmpty(dataB64))
                {
                    _callbacks.OnFileChunkReceived(Config.NodeId, transferId, fileIdx, offset, dataB64);
                }
                return;
            }

            if (mtype == "file_done")
            {
                var transferId = root.TryGetProperty("id", out var ip) ? ip.GetString() : null;
                var fileIdx = root.TryGetProperty("idx", out var xp) ? xp.GetInt32() : 0;
                if (!string.IsNullOrEmpty(transferId))
                {
                    _callbacks.OnFileDoneReceived(Config.NodeId, transferId, fileIdx);
                }
                return;
            }

            if (mtype == "file_result")
            {
                var transferId = root.TryGetProperty("id", out var ip) ? ip.GetString() : null;
                var fileIdx = root.TryGetProperty("idx", out var xp) ? xp.GetInt32() : 0;
                var status = root.TryGetProperty("status", out var sp) ? sp.GetString() : "ok";
                if (!string.IsNullOrEmpty(transferId))
                {
                    _callbacks.OnFileResultReceived(Config.NodeId, transferId, fileIdx, status ?? "ok");
                }
                return;
            }

            if (mtype == "file_complete")
            {
                var transferId = root.TryGetProperty("id", out var ip) ? ip.GetString() : null;
                var success = root.TryGetProperty("success", out var sp) && sp.GetBoolean();
                if (!string.IsNullOrEmpty(transferId))
                {
                    _callbacks.OnFileCompleteReceived(Config.NodeId, transferId, success);
                }
                return;
            }

            if (mtype == "edge_return")
            {
                var edge = root.TryGetProperty("edge", out var ep) ? ep.GetString() ?? "left" : "left";
                var normY = root.TryGetProperty("norm_y", out var yp) ? yp.GetDouble() : 0.5;
                var normX = root.TryGetProperty("norm_x", out var xp) ? xp.GetDouble() : 0.5;
                _callbacks.OnEdgeReturn(Config.NodeId, edge, normY, normX);
            }
            else if (mtype == "switch_request")
            {
                // v1.9.1: agent ขอสลับไป node ข้างเคียง (chain topology) — norm มาจากตำแหน่งที่ดันบนจอ agent
                var target = root.TryGetProperty("target", out var tp2) ? tp2.GetString() : null;
                var edgeS = root.TryGetProperty("edge", out var ep2) ? ep2.GetString() ?? "left" : "left";
                var normXr = root.TryGetProperty("norm_x", out var xp2) ? xp2.GetDouble() : 0.5;
                var normYr = root.TryGetProperty("norm_y", out var yp2) ? yp2.GetDouble() : 0.5;
                if (!string.IsNullOrEmpty(target))
                {
                    _callbacks.OnSwitchRequestReceived(Config.NodeId, target, edgeS, normXr, normYr);
                }
            }
            else if (mtype is "hello" or "pong" or "telemetry" or "lang_ack")
            {
                // Version handshake: agent ตัวไหนเวอร์ชันไม่ตรง canonical = deploy ทับทันที
                // (กันกรณี deploy ถูกข้ามเพราะ connect เร็ว หรือไฟล์ฝั่งลูกถูกแก้โดยภายนอก)
                if (mtype == "hello")
                {
                    var ver = root.TryGetProperty("version", out var vp2) ? vp2.GetString() : null;
                    AgentVersion = ver ?? "(ไม่ระบุ = รุ่นเก่ามาก)";
                    var hasAuthFlag = root.TryGetProperty("auth", out var ap) && ap.GetBoolean();
                    if (!hasAuthFlag || ver != KvmHostService.ExpectedAgentVersion)
                    {
                        if (!_legacyReported)
                        {
                            _legacyReported = true;
                            try { _callbacks.OnLegacyAgent(Config.NodeId, AgentVersion); } catch { }
                        }
                    }
                    if (root.TryGetProperty("hostname", out var hnp) && hnp.GetString() is { Length: > 0 } detectedHost)
                    {
                        try { _callbacks.OnHostnameDiscovered(Config.NodeId, detectedHost); } catch { }
                    }
                }
                if (root.TryGetProperty("width", out var wp)) Width = wp.GetInt32();
                if (root.TryGetProperty("height", out var hp)) Height = hp.GetInt32();
                if (root.TryGetProperty("lang_group", out var lp))
                {
                    var reported = lp.GetInt32() == 1 ? 1 : 0;
                    LangGroup = reported;
                    if (_requestedLangGroup == reported)
                    {
                        _requestedLangGroup = -1;
                    }
                    if (mtype == "lang_ack")
                    {
                        OmniLog.Write("lang", $"remote_lang_ack node={Config.NodeId} group={reported}");
                    }
                }
                if (root.TryGetProperty("ram_pct", out var rp)) RamPct = rp.GetDouble();
                if (root.TryGetProperty("ram_used_gb", out var rug)) RamUsedGb = rug.GetDouble();
                if (root.TryGetProperty("ram_total_gb", out var rtg)) RamTotalGb = rtg.GetDouble();
                if (root.TryGetProperty("playback_running", out var pbp)) PlaybackRunning = pbp.GetBoolean();
                if (root.TryGetProperty("playback_active", out var pap))
                {
                    var pActive = pap.GetBoolean();
                    PlaybackActive = pActive;
                    try { _callbacks.OnAudioActivityReported(Config.NodeId, pActive); } catch { }
                }
                if (root.TryGetProperty("mic_running", out var mp)) MicRunning = mp.GetBoolean();
                // INCIDENT#2: agent รายงานสถานะ focus กลับมาทุก pong → Host ตรวจ desync ได้ทันที
                if (root.TryGetProperty("focused", out var fp))
                {
                    var focused = fp.GetBoolean();
                    if (!focused)
                    {
                        try { _callbacks.OnFocusedReported(Config.NodeId, false); } catch { }
                    }
                }
                if (mtype == "pong" && root.TryGetProperty("ts", out var tsp))
                {
                    double rtt;
                    if (tsp.TryGetInt64(out var sentTs64) && sentTs64 > 1_000_000)
                    {
                        rtt = Stopwatch.GetElapsedTime(sentTs64).TotalMilliseconds;
                    }
                    else
                    {
                        var rawVal = tsp.GetDouble();
                        if (rawVal > 1_000_000)
                        {
                            rtt = Stopwatch.GetElapsedTime((long)rawVal).TotalMilliseconds;
                        }
                        else
                        {
                            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                            rtt = (now - rawVal) * 1000.0;
                        }
                    }

                    if (rtt >= 0 && rtt < 5000)
                    {
                        // Low-pass Exponential Moving Average (EMA) smoothing (alpha = 0.25)
                        PingMs = PingMs <= 0 ? Math.Round(rtt, 1) : Math.Round(PingMs * 0.75 + rtt * 0.25, 1);
                    }
                }
                _callbacks.OnStateChanged(Config.NodeId, "telemetry");
            }
        }
        catch (Exception ex)
        {
            // INCIDENT#2: เดิมกลืนเงียบ — ข้อความ edge_return พังกลางทางแล้วไม่มีใครรู้
            OmniLog.Error("[node] HandleMessage error", ex);
        }
    }

    public void Dispose()
    {
        _running = false;
        _wakeUpSignal.Set();
        _wakeUpPing.Set();
        SendUdp(OmniProtocol.PackReleaseAll());
        try { _tcp?.Close(); } catch { }
        try { _udp.Close(); } catch { }
    }
}
