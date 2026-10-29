using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;
using OmniLink.Core.Clipboard;
using OmniLink.Core.FileExchange;
using OmniLink.Core.Security;

namespace OmniLink.Core;

/// <summary>
/// v2.0 เฟส 3: "โหมด sub ฝังใน host" — เครื่องนี้ (host ตัวใดก็ได้ ทุก OS) สามารถถูกคุมโดย host ตัวอื่น
/// โดยเล่นเป็น node agent บนสาย (wire-compatible กับ node_agent/omnilink_agent.py ทุกข้อความ):
/// TCP 24801 (TLS + auth/pair PIN + TOFU fingerprint) · UDP 24802 (อินพุต + HMAC + seq anti-replay)
/// · policy matrix ฝั่งรับ · edge machine แบบ push+hold/double-tap ของ A1 · ฉุกเฉิน Ctrl+Alt+Shift+Q
/// กันลูป host↔host (แผน 3.4): ตอน ActiveFocus ฝั่ง host-role ต้องพัก edge ของตัวเอง — ดู KvmHostService.SubActiveFocusProbe
/// </summary>
public sealed class NodeAgentServer : IDisposable
{
    private const int EdgePushThresholdPx = 60; // P0: ดันขอบสะสมเกินกี่ px จึงสลับ (เดียวกับ agent)
    private const long EdgeReturnDedupMs = 50;  // dedup burst 7 ครั้ง/4ms ที่เคยเจอจริง

    private readonly OmniConfig _config;
    private readonly INodeInputInjector _input;
    private readonly IHostInputGateway _gateway;   // ใช้ร่วมกับ host-role: ภาษาเป็น machine-global
    private readonly IClipboardBridge? _clipboard; // null = headless ยังไม่เชื่อมคลิปบอร์ด
    private readonly IEscapeHotkeyHook _escape;

    private readonly object _sendLock = new();
    private SslStream? _activeStream;
    private readonly FileTransferManager _files = new(OmniEventHub.Default); // B9: รับไฟล์จาก host จริง (เดิม v1 ข้าม)

    private TcpListener? _tcp;
    private UdpClient? _udp;
    private volatile bool _running;

    // ── identity & secrets (เก็บใน runstate/ — สร้างครั้งแรกแล้วใช้ต่อ)
    public string MachineUuid { get; }
    public string PairingPin { get; private set; } = "";
    private string _authToken = "";
    private byte[]? _udpKey;
    private X509Certificate2? _cert;
    public string CertFingerprint { get; private set; } = "";

    // ── session / security state
    public volatile bool ActiveFocus;
    private string _nodePosition = "right";
    private volatile bool _screenLocked;
    private volatile string _remotePolicy = "full";
    private volatile Dictionary<string, string> _neighbors = new();
    private readonly HashSet<string> _authedHosts = new();
    private readonly Dictionary<string, int> _authedConns = new();
    private readonly object _authedLock = new();
    private readonly Dictionary<string, List<long>> _pairAttempts = new();
    private readonly Dictionary<string, long> _pairBlockedUntil = new();

    // ── edge machine (A1 — mirror agent)
    private double _edgePush;
    private long _atEdgeSince;
    private bool _hadRealPush;
    private long _lastLeaveAt;
    private long _lastEdgeReturnSent;
    private int _switchDelayMs = 250;
    private int _doubleTapMs = 400;
    private int _cornerSize = 32;
    private readonly Dictionary<string, bool> _corners = new() { ["tl"] = true, ["tr"] = true, ["bl"] = true, ["br"] = true };
    private long _lastUdpMove;
    private volatile string _lastClipHash = "";

    /// <summary>บันทึนเหตุการณ์สำหรับ UI banner / log (เช่น "host 192.168.x.x เข้าคุมเครื่องนี้")</summary>
    public Action<string>? OnStatus;
    public Action? OnPairingChanged;

    public NodeAgentServer(OmniConfig config, INodeInputInjector input, IHostInputGateway gateway,
        IClipboardBridge? clipboard, IEscapeHotkeyHook escape)
    {
        _config = config;
        _input = input;
        _gateway = gateway;
        _clipboard = clipboard;
        _escape = escape;
        MachineUuid = LoadOrCreateUuid();
        LoadOrCreateIdentity();
    }

    // ═══════════════════════ lifecycle ═══════════════════════

    public void Start()
    {
        if (_running) return;

        // ทนพอร์ตไม่ว่าง (เช่น node agent เก่ายังครองอยู่) — โหมด sub ปิดตัวเองแต่ host-role ต้องอยู่ต่อ
        try
        {
            _tcp = new TcpListener(IPAddress.Any, OmniProtocol.KvmTcpPort);
            _tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tcp.Start(5);

            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.ReceiveBufferSize = 262144;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, OmniProtocol.KvmUdpPort));
        }
        catch (Exception ex)
        {
            OmniLog.Error($"[sub] bind {OmniProtocol.KvmTcpPort}/{OmniProtocol.KvmUdpPort} ไม่สำเร็จ (โหมด sub ปิดใช้งานรอบนี้): " + ex.Message);
            try { _tcp?.Stop(); } catch { }
            try { _udp?.Close(); } catch { }
            _tcp = null;
            _udp = null;
            OnStatus?.Invoke(Loc.T("sub.portbusy", "เปิดโหมด sub ไม่สำเร็จ — พอร์ต 24801/24802 ถูกโปรแกรมอื่นถืออยู่"));
            return;
        }

        _running = true;
        new Thread(AcceptLoop) { IsBackground = true, Name = "OmniSub_Accept" }.Start();
        new Thread(UdpLoop) { IsBackground = true, Name = "OmniSub_Udp" }.Start();

        // P0: ทางหนีของเครื่องที่ถูกกัก — Ctrl+Alt+Shift+Q จากคีย์บอร์ดจริงของเครื่องนี้เสมอ
        _escape.Start(() => ForceReturn("escape-hotkey"));

        OmniLog.Write("sub", $"โหมด sub พร้อม — TCP {OmniProtocol.KvmTcpPort}/TLS + UDP {OmniProtocol.KvmUdpPort}, uuid={MachineUuid}, auth={(string.IsNullOrEmpty(_authToken) ? "PAIRING-READY" : "on")}, fp={CertFingerprint[..Math.Min(16, CertFingerprint.Length)]}..");
        OnStatus?.Invoke(Loc.T("sub.started", "โหมด sub เปิดแล้ว — เครื่องหลักเครื่องอื่นเพิ่มเครื่องนี้เป็น node ได้ (PIN จับคู่: {0})", PairingPin));
    }

    public void Dispose()
    {
        _running = false;
        try { _escape.Stop(); } catch { }
        ForceReturnInternal("shutdown", sendToHost: false);
        try { _tcp?.Stop(); } catch { }
        try { _udp?.Close(); } catch { }
        lock (_sendLock)
        {
            try { _activeStream?.Close(); } catch { }
            _activeStream = null;
        }
    }

    // ═══════════════════════ identity & secrets ═══════════════════════

    private static string StatePath(string name) => Path.Combine(OmniPaths.RunStateDir, name);

    private static string LoadOrCreateUuid()
    {
        try
        {
            var p = StatePath("sub_machine_id");
            if (File.Exists(p)) return File.ReadAllText(p).Trim();
            var id = "host-" + Guid.NewGuid().ToString("N")[..8];
            Directory.CreateDirectory(OmniPaths.RunStateDir);
            File.WriteAllText(p, id);
            return id;
        }
        catch
        {
            return "host-unknown";
        }
    }

    private void LoadOrCreateIdentity()
    {
        Directory.CreateDirectory(OmniPaths.RunStateDir);

        // token
        try
        {
            var p = StatePath("sub_auth_token");
            if (File.Exists(p)) _authToken = File.ReadAllText(p).Trim();
            if (string.IsNullOrEmpty(_authToken))
            {
                _authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                File.WriteAllText(p, _authToken);
            }
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[sub] สร้าง/อ่าน token ไม่สำเร็จ: " + ex.Message);
        }
        _udpKey = OmniPacketAuth.DeriveUdpKey(_authToken);

        // PIN (หมุนใหม่ทุกครั้งที่จับคู่สำเร็จ)
        try
        {
            var p = StatePath("sub_pairing_pin");
            if (File.Exists(p)) PairingPin = File.ReadAllText(p).Trim();
            if (string.IsNullOrEmpty(PairingPin))
            {
                PairingPin = NewPin();
                File.WriteAllText(p, PairingPin);
            }
        }
        catch
        {
            PairingPin = NewPin();
        }

        // TLS cert (TOFU — fingerprint คือสิ่งที่เครื่องหลัก pin)
        try
        {
            var pfx = StatePath("sub_cert.pfx");
            if (File.Exists(pfx))
            {
                _cert = new X509Certificate2(pfx, (string?)null, KeyStorageFlags);
            }
            else
            {
                using var rsa = RSA.Create(2048);
                var req = new CertificateRequest("CN=OmniLink-Sub-" + MachineUuid, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var fresh = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(825));
                File.WriteAllBytes(pfx, fresh.Export(X509ContentType.Pfx));
                // กับดัก Windows/SChannel: cert จาก CreateSelfSigned ถือ key แบบ ephemeral ซึ่งฝั่ง TLS-server
                // ใช้ไม่ได้ ("platform does not support ephemeral keys") — import ใหม่พร้อม PersistKeySet เสมอ
                _cert = new X509Certificate2(pfx, (string?)null, KeyStorageFlags);
            }
            CertFingerprint = OmniPacketAuth.CertificateFingerprint(_cert.Export(X509ContentType.Cert));
        }
        catch (Exception ex)
        {
            OmniLog.Error("[sub] สร้าง TLS certificate ไม่สำเร็จ — โหมด subใช้ไม่ได้ (fail-closed): " + ex.Message);
        }
    }

    private const X509KeyStorageFlags KeyStorageFlags =
        X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable;

    private static string NewPin() =>
        $"{RandomNumberGenerator.GetInt32(100, 1000)}-{RandomNumberGenerator.GetInt32(100, 1000)}";

    private bool IpAllowed(string ip)
    {
        var subnets = _config.AllowedSubnets;
        if (subnets is null || subnets.Count == 0) return true;
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        foreach (var s in subnets)
        {
            if (IPNetwork.TryParse(s.Trim(), out var net) && net.Contains(addr))
            {
                return true;
            }
        }
        return false;
    }

    // ═══════════════════════ TCP ═══════════════════════

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = _tcp!.AcceptTcpClient();
                var thread = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "OmniSub_Client" };
                thread.Start();
            }
            catch
            {
                if (!_running) break;
                Thread.Sleep(300);
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        var remoteIp = "unknown";
        try
        {
            if (client.Client.RemoteEndPoint is IPEndPoint rep) remoteIp = rep.Address.ToString();
        }
        catch { }

        // B2: allowlist ก่อน TLS handshake (ไม่เปิดเผยแม้แต่การมีอยู่)
        if (!IpAllowed(remoteIp))
        {
            OmniLog.Warn($"[sub] ปฏิเสธ connection จาก {remoteIp} (นอก allowlist)");
            try { client.Close(); } catch { }
            return;
        }

        // v1.6 hardening: fail-closed — ไม่มี cert = ไม่รับ
        if (_cert is null)
        {
            try { client.Close(); } catch { }
            return;
        }

        SslStream ssl;
        try
        {
            ssl = new SslStream(client.GetStream(), false);
            ssl.AuthenticateAsServer(_cert); // OS เลือก TLS version (1.2/1.3) — ไม่บังคับ client cert
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[sub] TLS handshake failed จาก {remoteIp}: {ex.Message}");
            try { client.Close(); } catch { }
            return;
        }

        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 5000; // กัน connection เงียบแฮงค์ระหว่างรอ auth

            // F2: ข้อความแรกต้องเป็น auth ที่ถูกต้อง หรือ pair_request — เหมือน agent ทุกประการ
            var buf = new byte[4096];
            var acc = new List<byte>();
            string? firstType = null;
            JsonDocument? firstDoc = null;
            var deadline = Environment.TickCount64 + 5000;
            while (_running && firstType is null)
            {
                if (Environment.TickCount64 > deadline) break;
                var n = ssl.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                acc.AddRange(buf.AsSpan(0, n).ToArray());
                while (acc.Count >= 4 && firstType is null)
                {
                    var len = (int)ReadLenBE(acc);
                    if (len > 65536)
                    {
                        try { ssl.Close(); } catch { }
                        return;
                    }
                    if (acc.Count < 4 + len) break;
                    var body = acc.GetRange(4, len).ToArray();
                    acc.RemoveRange(0, 4 + len);
                    try
                    {
                        firstDoc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
                        firstType = firstDoc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                    }
                    catch
                    {
                        try { ssl.Close(); } catch { }
                        return;
                    }
                }
            }

            if (firstType == "pair_request")
            {
                HandlePairRequest(ssl, remoteIp, firstDoc!);
                return;
            }

            var token = "";
            try
            {
                if (firstType == "auth" && firstDoc!.RootElement.TryGetProperty("token", out var tp))
                {
                    token = tp.GetString() ?? "";
                }
            }
            catch { }

            if (firstType != "auth" || !FixedTimeEquals(token, _authToken))
            {
                OmniLog.Warn("[sub] ปฏิเสธ auth จาก " + remoteIp);
                try { ssl.Close(); } catch { }
                return;
            }
        }
        catch
        {
            try { ssl.Close(); } catch { }
            return;
        }

        // ★ ยืนยันสำเร็จ — อนุญาต UDP ของ IP นี้ + จับคู่ connection (จากนี้ block รอข้อความได้ไม่จำกัด)
        client.ReceiveTimeout = 0;
        lock (_authedLock)
        {
            _authedHosts.Add(remoteIp);
            _authedConns[remoteIp] = _authedConns.TryGetValue(remoteIp, out var c) ? c + 1 : 1;
        }
        var isNewActive = false;
        lock (_sendLock)
        {
            if (_activeStream is not null && !ReferenceEquals(_activeStream, ssl))
            {
                try { _activeStream.Close(); } catch { } // INCIDENT#2: conn ใหม่ชนะเสมอ — ฆ่าเบี้ยหนู
            }
            if (!ReferenceEquals(_activeStream, ssl))
            {
                _activeStream = ssl;
                isNewActive = true;
            }
        }
        if (isNewActive)
        {
            OmniLog.Write("sub", $"host {remoteIp} ยืนยัน token สำเร็จ (conn ใหม่ชนะ)");
        }

        SendDirect(ssl, new { type = "auth_ok" });
        var helloRam = RamMetrics();
        SendDirect(ssl, new
        {
            type = "hello",
            auth = true,
            version = KvmHostService.ExpectedAgentVersion, // sync กับ host เสมอ — ไม่มี version-mismatch ล็อกหล่น
            hostname = Environment.MachineName,
            width = _input.ScreenWidth,
            height = _input.ScreenHeight,
            lang_group = _gateway.GetForegroundLangGroup(),
            playback_running = false,
            playback_active = false,
            mic_running = false,
            os = OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsLinux() ? "linux" : "other"),
            ram_pct = helloRam.Pct,
            ram_used_gb = helloRam.Used,
            ram_total_gb = helloRam.Total,
        });

        try
        {
            var header = new byte[4];
            var pending = new List<byte>();
            var readBuf = new byte[8192];
            while (_running)
            {
                var n = ssl.Read(readBuf, 0, readBuf.Length);
                if (n <= 0) break;
                pending.AddRange(readBuf.AsSpan(0, n).ToArray());
                while (pending.Count >= 4)
                {
                    var len = (int)ReadLenBE(pending);
                    if (len <= 0 || len > 128 * 1024)
                    {
                        OmniLog.Warn($"[sub] oversized msg_len={len} — ตัด connection");
                        return;
                    }
                    if (pending.Count < 4 + len) break;
                    var body = pending.GetRange(4, len).ToArray();
                    pending.RemoveRange(0, 4 + len);
                    try
                    {
                        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
                        ProcessControlMessage(doc.RootElement, remoteIp);
                    }
                    catch (Exception ex)
                    {
                        // PY-F9: ข้อความเดียวพัง = ทิ้งข้อความนั้น ไม่ตัด connection
                        OmniLog.Warn($"[sub] process msg failed: {ex.Message}");
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            ForceReturnInternal("conn-closed", sendToHost: false);
            lock (_authedLock)
            {
                if (_authedConns.TryGetValue(remoteIp, out var left) && left <= 1)
                {
                    _authedConns.Remove(remoteIp);
                    _authedHosts.Remove(remoteIp);
                }
                else if (_authedConns.ContainsKey(remoteIp))
                {
                    _authedConns[remoteIp] = left - 1;
                }
            }
            lock (_sendLock)
            {
                if (ReferenceEquals(_activeStream, ssl)) _activeStream = null;
            }
            try { ssl.Close(); } catch { }
        }
    }

    private void HandlePairRequest(SslStream ssl, string ip, JsonDocument doc)
    {
        // PY-F1: rate limit 10 ครั้ง/10 นาที แล้วบล็อก 5 นาที — เหมือน agent
        var now = Environment.TickCount64;
        lock (_pairAttempts)
        {
            if (_pairBlockedUntil.TryGetValue(ip, out var until) && now < until)
            {
                SendDirect(ssl, new { type = "pair_reject", reason = "Too many attempts, try later" });
                Close(ssl);
                return;
            }
            var window = _pairAttempts.TryGetValue(ip, out var list) ? list : new List<long>();
            window.RemoveAll(t => now - t > 600_000);
            if (window.Count >= 10)
            {
                _pairBlockedUntil[ip] = now + 300_000;
                OmniLog.Warn($"[sub] {ip} เดา PIN เกิน 10 ครั้ง/10 นาที — บล็อก 5 นาที");
                SendDirect(ssl, new { type = "pair_reject", reason = "Too many attempts, try later" });
                Close(ssl);
                return;
            }
            _pairAttempts[ip] = window;
        }

        var reqPin = "";
        var hostName = "Host";
        try
        {
            if (doc.RootElement.TryGetProperty("pin", out var pp)) reqPin = (pp.GetString() ?? "").Trim().Replace(" ", "").Replace("-", "");
            if (doc.RootElement.TryGetProperty("host_name", out var hp) && hp.GetString() is { Length: > 0 } hn) hostName = hn;
        }
        catch { }
        var myPin = PairingPin.Replace(" ", "").Replace("-", "");

        if (!FixedTimeEquals(reqPin, myPin))
        {
            lock (_pairAttempts)
            {
                _pairAttempts.TryGetValue(ip, out var list);
                list ??= new List<long>();
                list.Add(now);
                _pairAttempts[ip] = list;
                OmniLog.Warn($"[sub] pair ปฏิเสธจาก {ip}: PIN ไม่ถูก (attempt {list.Count}/10)");
            }
            SendDirect(ssl, new { type = "pair_reject", reason = "Invalid PIN" });
            Close(ssl);
            return;
        }

        // จับคู่สำเร็จ: ออก token ใหม่ + หมุน PIN + แจ้ง fingerprint ของ cert จริง (TOFU)
        _authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _udpKey = OmniPacketAuth.DeriveUdpKey(_authToken);
        try { File.WriteAllText(StatePath("sub_auth_token"), _authToken); } catch { }
        PairingPin = NewPin();
        try { File.WriteAllText(StatePath("sub_pairing_pin"), PairingPin); } catch { }
        lock (_authedLock) { _authedHosts.Add(ip); }
        OmniLog.Write("sub", $"[sub] จับคู่สำเร็จจาก {ip} ({hostName}) — token ใหม่ + PIN หมุนแล้ว");
        OnPairingChanged?.Invoke();

        SendDirect(ssl, new
        {
            type = "pair_accept",
            token = _authToken,
            uuid = MachineUuid,
            name = Environment.MachineName,
            width = _input.ScreenWidth,
            height = _input.ScreenHeight,
            cert_fingerprint = CertFingerprint,
        });
        Close(ssl);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a ?? "");
        var bb = Encoding.UTF8.GetBytes(b ?? "");
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }


    private static uint ReadLenBE(List<byte> b) =>
        (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);

    private static void Close(SslStream s)
    {
        try { s.Close(); } catch { }
    }

    private void SendDirect(SslStream ssl, object payload)
    {
        try
        {
            var bytes = OmniProtocol.EncodeTcpJson(payload);
            ssl.Write(bytes, 0, bytes.Length);
            ssl.Flush();
        }
        catch
        {
        }
    }

    private void SendToHost(object payload)
    {
        lock (_sendLock)
        {
            if (_activeStream is null) return;
            SendDirect(_activeStream, payload);
        }
    }

    // ═══════════════════════ control messages ═══════════════════════

    private void ProcessControlMessage(JsonElement root, string hostIp)
    {
        var mtype = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
        switch (mtype)
        {
            case "set_layout":
            {
                // v1.9.1 chain: edge → target ("__host" = กลับเครื่องหลัก, อื่นๆ = switch_request)
                var nb = new Dictionary<string, string>();
                if (root.TryGetProperty("neighbors", out var np) && np.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in np.EnumerateObject())
                    {
                        var v = kv.Value.GetString();
                        if (!string.IsNullOrEmpty(v)) nb[kv.Name] = v;
                    }
                }
                _neighbors = nb; // immutable swap — UDP thread อ่าน lock-free
                OmniLog.Write("sub", "neighbors=" + string.Join(",", _neighbors.Select(kv => kv.Key + "→" + kv.Value)));
                break;
            }

            case "set_policy":
            {
                var p = root.TryGetProperty("policy", out var pp) ? pp.GetString() : "full";
                _remotePolicy = p is "full" or "clipboard" or "view" ? p : "full";
                OmniLog.Write("sub", $"[sub] host ตั้ง policy='{_remotePolicy}'");
                if (_remotePolicy != "full" && ActiveFocus)
                {
                    ForceReturnInternal("policy-downgrade", sendToHost: false);
                    PushClipboardAsync();
                }
                break;
            }

            case "enter_screen":
            {
                if (root.TryGetProperty("policy", out var ep) && ep.GetString() is "full" or "clipboard" or "view")
                {
                    _remotePolicy = ep.GetString()!;
                }
                if (_remotePolicy != "full")
                {
                    OmniLog.Warn($"[sub] ปฏิเสธ enter_screen — policy='{_remotePolicy}' ไม่อนุญาต input");
                    break;
                }

                _nodePosition = root.TryGetProperty("position", out var posp) ? posp.GetString() ?? "right" : "right";
                var normY = root.TryGetProperty("norm_y", out var nyp) ? nyp.GetDouble() : 0.5;
                var normX = root.TryGetProperty("norm_x", out var nxp) ? nxp.GetDouble() : 0.5;
                if (root.TryGetProperty("locked", out var lp) && lp is { ValueKind: JsonValueKind.True or JsonValueKind.False })
                {
                    _screenLocked = lp.GetBoolean(); // PY-F15: รับสถานะล็อกจาก host
                }
                if (root.TryGetProperty("lang_group", out var lgp))
                {
                    var g = lgp.GetInt32() == 1 ? 1 : 0;
                    Task.Run(() => _gateway.RequestLangChange(g));
                }

                _input.RefreshDimensions();
                int entryX, entryY;
                switch (_nodePosition)
                {
                    case "left": entryX = Math.Max(0, _input.ScreenWidth - 25); entryY = (int)(normY * (_input.ScreenHeight - 1)); break;
                    case "top": entryX = (int)(normX * (_input.ScreenWidth - 1)); entryY = Math.Max(0, _input.ScreenHeight - 25); break;
                    case "bottom": entryX = (int)(normX * (_input.ScreenWidth - 1)); entryY = 24; break;
                    default: entryX = 24; entryY = (int)(normY * (_input.ScreenHeight - 1)); break;
                }
                _input.MoveAbsolute(entryX, entryY);
                ResetEdgeState();

                // A1: พารามิเตอร์การสลับจอมาพร้อม enter_screen
                try
                {
                    if (root.TryGetProperty("switch_delay_ms", out var sd)) _switchDelayMs = Math.Max(0, sd.GetInt32());
                    if (root.TryGetProperty("double_tap_ms", out var dt)) _doubleTapMs = Math.Max(0, dt.GetInt32());
                    if (root.TryGetProperty("corner_size", out var cs)) _cornerSize = Math.Max(0, cs.GetInt32());
                    if (root.TryGetProperty("corners", out var cn) && cn.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var kv in cn.EnumerateObject())
                        {
                            _corners[kv.Name] = kv.Value.GetBoolean();
                        }
                    }
                }
                catch { }

                ActiveFocus = true;
                OmniLog.Write("sub", $"[sub] enter_screen pos={_nodePosition} ({entryX},{entryY}) delay={_switchDelayMs}ms tap={_doubleTapMs}ms corner={_cornerSize}px");
                OnStatus?.Invoke(Loc.T("sub.controlled", "เครื่องนี้กำลังถูกคุมโดย host ({0}) — หนีฉุกเฉิน Ctrl+Alt+Shift+Q", hostIp));
                break;
            }

            case "leave_screen":
                ForceReturnInternal("leave_screen", sendToHost: false);
                PushClipboardAsync();
                break;

            case "lock":
                _screenLocked = root.TryGetProperty("on", out var onp) && onp.GetBoolean();
                OmniLog.Write("sub", _screenLocked ? "ล็อกจอ (host สั่ง)" : "ปลดล็อกจอ (host สั่ง)");
                break;

            case "set_lang":
            {
                var g = root.TryGetProperty("group", out var gp) && gp.GetInt32() == 1 ? 1 : 0;
                Task.Run(() =>
                {
                    _gateway.RequestLangChange(g);
                    SendToHost(new { type = "lang_ack", lang_group = _gateway.GetForegroundLangGroup() });
                });
                break;
            }

            case "toggle_lang":
            {
                var next = _gateway.GetForegroundLangGroup() == 1 ? 0 : 1;
                Task.Run(() =>
                {
                    _gateway.RequestLangChange(next);
                    SendToHost(new { type = "lang_ack", lang_group = _gateway.GetForegroundLangGroup() });
                });
                break;
            }

            case "key_event":
            {
                // ช่องทาง TCP สำรอง (เหมือน agent) — เมธอดหลักคือ UDP
                if (_remotePolicy != "full" || !ActiveFocus) break;
                var kc = root.TryGetProperty("keycode", out var kcp) ? kcp.GetInt32() : 0;
                var down = root.TryGetProperty("down", out var dp) && dp.GetBoolean();
                if (kc is >= 8 and <= 255) _input.KeyEvent(kc, down);
                break;
            }

            case "clipboard":
            {
                if (_remotePolicy == "view") break;
                if (root.TryGetProperty("text", out var ctp) && ctp.GetString() is { Length: > 0 } text)
                {
                    var ok = _clipboard?.SetText(text) ?? false;
                    if (ok)
                    {
                        _lastClipHash = ClipboardRouter.ClipHash("text", text); // A1: กัน echo
                        ClipboardHistoryStore.Default.AddTextEntry(text, "host"); // C2: เข้าประวัติ
                        OmniLog.Write("sub", $"[sub] รับ clipboard จาก host len={text.Length}");
                    }
                }
                // B9a: ภาพ fast-path (<64KB) — host ส่ง direct {mime:"image/png", data:base64}
                else if (root.TryGetProperty("mime", out var mp2) && mp2.GetString() == "image/png" &&
                         root.TryGetProperty("data", out var dp3) && dp3.GetString() is { Length: > 0 } pngB64)
                {
                    var png = OmniImage.FromBase64(pngB64);
                    if (png.Length > 0)
                    {
                        var hash = ClipboardRouter.ClipHash("image", Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(png)));
                        if (hash != _lastClipHash && (_clipboard?.SetPng(png) ?? false))
                        {
                            _lastClipHash = hash;
                            ClipboardHistoryStore.Default.AddImage(png, "host"); // C2
                            OmniLog.Write("sub", $"[sub] รับรูปจาก host len={png.Length}B");
                        }
                    }
                }
                break;
            }

            case "clip_meta":
            {
                var token = root.TryGetProperty("token", out var tp2) ? tp2.GetString() : null;
                if (!string.IsNullOrEmpty(token))
                {
                    SendToHost(new { type = "clip_fetch", token });
                }
                break;
            }

            case "clip_data":
            {
                if (_remotePolicy == "view") break;
                var mime = root.TryGetProperty("mime", out var mp) ? mp.GetString() : "text/plain";
                var data = root.TryGetProperty("data", out var dp2) ? dp2.GetString() : null;
                if (string.IsNullOrEmpty(data)) break;
                if (mime == "image/png")
                {
                    // B9a: จบ ACK-gate ภาพ — host ส่งข้อมูลเต็มตาม token ที่เราขอ (clip_meta → clip_fetch → clip_data)
                    var png = OmniImage.FromBase64(data);
                    if (png.Length > 0)
                    {
                        var hash = ClipboardRouter.ClipHash("image", Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(png)));
                        if (hash != _lastClipHash && (_clipboard?.SetPng(png) ?? false))
                        {
                            _lastClipHash = hash;
                            ClipboardHistoryStore.Default.AddImage(png, "host"); // C2
                            OmniLog.Write("sub", $"[sub] รับรูป (ACK-gate) len={png.Length}B");
                        }
                    }
                }
                else
                {
                    var text = Encoding.UTF8.GetString(OmniImage.FromBase64(data));
                    var ok = _clipboard?.SetText(text) ?? false;
                    if (ok)
                    {
                        _lastClipHash = ClipboardRouter.ClipHash("text", text);
                        ClipboardHistoryStore.Default.AddTextEntry(text, "host"); // C2
                    }
                }
                break;
            }

            case "clip_fetch":
                // เฟสถัดไป: โหมด sub ยังไม่เสนอ clip ขนาดใหญ่แบบ ACK-gate (ส่ง direct อย่างเดียว)
                break;

            case "file_offer":
            {
                // B9b: รับไฟล์จริง — protocol เดียวกับ agent: offer → accept → chunk* → done* → complete
                if (_remotePolicy != "full") break;
                var manifestJson = root.TryGetProperty("manifest", out var fmp) ? fmp.GetString() : null;
                if (string.IsNullOrEmpty(manifestJson)) break;
                var manifest = FileManifest.FromJson(manifestJson);
                if (manifest is null) break;
                var targetDir = Path.Combine(OmniPaths.Root, "Downloads", "OmniLink"); // กติกา OmniPaths (test ตั้ง root จำลองได้)
                if (_files.Streamer.HandleOffer(manifest, targetDir, out _))
                {
                    SendToHost(new { type = "file_accept", id = manifest.Id });
                    OmniLog.Write("sub", $"[sub] รับ file_offer {manifest.Files.Count} ไฟล์ ({manifest.TotalBytes / 1024}KB) id={manifest.Id}");
                }
                break;
            }

            case "file_chunk":
            {
                if (_remotePolicy != "full") break;
                var cid = root.TryGetProperty("id", out var cip) ? cip.GetString() : null;
                var cidx = root.TryGetProperty("idx", out var cxp) ? cxp.GetInt32() : 0;
                var coff = root.TryGetProperty("offset", out var cop) ? cop.GetInt64() : 0;
                var cdat = root.TryGetProperty("data", out var cdp) ? cdp.GetString() : null;
                if (!string.IsNullOrEmpty(cid) && !string.IsNullOrEmpty(cdat))
                {
                    _files.Streamer.HandleChunk(cid, cidx, coff, cdat);
                }
                break;
            }

            case "file_done":
            {
                if (_remotePolicy != "full") break;
                var did = root.TryGetProperty("id", out var dip) ? dip.GetString() : null;
                var didx = root.TryGetProperty("idx", out var dxp) ? dxp.GetInt32() : 0;
                if (!string.IsNullOrEmpty(did))
                {
                    // protocol: ผู้รับยืนยันต่อไฟล์ด้วย file_result (ฝั่ง host รอก่อนส่งไฟล์ถัดไป)
                    _files.Streamer.HandleFileDone(did, didx, out var status);
                    SendToHost(new { type = "file_result", id = did, idx = didx, status });
                }
                break;
            }

            case "file_complete":
            {
                if (_remotePolicy != "full") break;
                var pid = root.TryGetProperty("id", out var pip) ? pip.GetString() : null;
                if (string.IsNullOrEmpty(pid)) break;
                if (_files.Streamer.FinalizeInboundSession(pid, out var completedPaths, out var effect))
                {
                    _lastClipHash = ClipboardRouter.ClipHash("files", string.Join(";", completedPaths) + "|" + effect); // กัน echo
                    if (completedPaths.Count > 0 && _clipboard is not null)
                    {
                        try
                        {
                            _clipboard.SetFileDropList(completedPaths, effect == "cut");
                            OmniLog.Write("sub", $"[sub] ได้รับไฟล์ {completedPaths.Count} ไฟล์ — ตั้ง clipboard file list แล้ว (วางด้วย Ctrl+V)");
                        }
                        catch (Exception ex)
                        {
                            OmniLog.Warn("[sub] ตั้ง file clipboard ล้มเหลว: " + ex.Message);
                        }
                    }
                }
                break;
            }

            case "history_request":
            {
                // C2d: ลูกขอ metadata ประวัติ (จาก store เดียวของเครื่อง)
                var entries = ClipboardHistoryStore.Default.GetHistoryMetadata();
                SendToHost(new
                {
                    type = "history_list",
                    entries = entries.Select(e => new { id = e.Id, mime = e.Mime, preview = e.Preview, ts = e.Ts, source = e.Source }).ToList(),
                });
                break;
            }

            case "history_get":
            {
                var hid = root.TryGetProperty("id", out var hg) ? hg.GetString() : null;
                if (string.IsNullOrEmpty(hid)) break;
                var entry = ClipboardHistoryStore.Default.GetHistoryEntry(hid);
                if (entry is null)
                {
                    SendToHost(new { type = "history_entry", id = hid, missing = true });
                    break;
                }
                var (mime, data) = entry.Value;
                var b64 = Convert.ToBase64String(data);
                if (b64.Length > 100_000)
                {
                    SendToHost(new { type = "history_entry", id = hid, missing = true, reason = "too_big" });
                    break;
                }
                SendToHost(new { type = "history_entry", id = hid, mime, data = b64 });
                break;
            }

            case "audio_control":
                // เฟสถัดไป: เสียงบนโหมด sub (ต้อง wiring gst ฝั่ง sub + live test — งานแยกตาม roadmap)
                break;

            case "ping":
            {
                var ram = RamMetrics();
                var ts = root.TryGetProperty("ts", out var tsp) && tsp.ValueKind == JsonValueKind.Number ? tsp.GetDouble() : 0.0;
                SendToHost(new
                {
                    type = "pong",
                    ts,
                    lang_group = _gateway.GetForegroundLangGroup(),
                    focused = ActiveFocus,                                     // INCIDENT#2: host ตรวจ desync
                    focused_by_udp = Environment.TickCount64 - _lastUdpMove < 2000,
                    playback_running = false,
                    playback_active = false,
                    mic_running = false,
                    width = _input.ScreenWidth,
                    height = _input.ScreenHeight,
                    ram_pct = ram.Pct,
                    ram_used_gb = ram.Used,
                    ram_total_gb = ram.Total,
                });
                break;
            }
        }
    }

    private (double Pct, double Used, double Total) RamMetrics()
    {
        double ramPct = 0, ramUsed = 0, ramTotal = 0;
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                long total = 0, avail = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = ParseKb(line);
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) avail = ParseKb(line);
                    if (total > 0 && avail > 0) break;
                }
                if (total > 0)
                {
                    ramTotal = Math.Round(total / 1048576.0, 2);
                    ramUsed = Math.Round((total - avail) / 1048576.0, 2);
                    ramPct = Math.Round((total - avail) * 100.0 / total, 1);
                }
            }
            else
            {
                var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (total > 0)
                {
                    ramTotal = Math.Round(total / 1073741824.0, 2);
                    ramUsed = Math.Round(Environment.WorkingSet / 1073741824.0, 2);
                    ramPct = Math.Round(Environment.WorkingSet * 100.0 / total, 1);
                }
            }
        }
        catch
        {
        }
        return (ramPct, ramUsed, ramTotal);

        static long ParseKb(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : 0;
        }
    }

    // ═══════════════════════ UDP input ═══════════════════════

    private void UdpLoop()
    {
        var dropCount = 0;
        while (_running)
        {
            try
            {
                IPEndPoint? from = null;
                byte[] data;
                try
                {
                    data = _udp!.Receive(ref from);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (!_running) break;
                    continue;
                }
                if (from is null || data.Length == 0) continue;
                var ip = from.Address.ToString();

                // F2+B2: ทิ้ง packet จาก IP ที่ไม่เคยยืนยัน token / นอก allowlist
                lock (_authedLock)
                {
                    if (!_authedHosts.Contains(ip) || !IpAllowed(ip))
                    {
                        dropCount++;
                        if (dropCount == 1 || dropCount % 200 == 0)
                        {
                            OmniLog.Warn($"[sub] ทิ้ง {dropCount} UDP packet จาก {ip} (ไม่ผ่าน auth/allowlist)");
                        }
                        continue;
                    }
                }

                // v1.6: HMAC + v1.8 SV-L1: seq freshness ±5s
                var key = _udpKey;
                if (key is not null)
                {
                    if (data.Length <= OmniPacketAuth.SeqSize + OmniPacketAuth.TrailerSize) continue;
                    if (!OmniPacketAuth.VerifyWithSeq(data, key))
                    {
                        dropCount++;
                        if (dropCount == 1 || dropCount % 200 == 0)
                        {
                            OmniLog.Warn($"[sub] ทิ้ง {dropCount} UDP packet จาก {ip} (BAD HMAC)");
                        }
                        continue;
                    }
                    var bodyAndSeq = data[..^OmniPacketAuth.TrailerSize];
                    var seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bodyAndSeq[^OmniPacketAuth.SeqSize..]);
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (Math.Abs(nowMs - (long)seq) > 5000)
                    {
                        continue; // STALE/REPLAYED
                    }
                    data = bodyAndSeq[..^OmniPacketAuth.SeqSize];
                }

                // v1.7 C3: policy matrix ฝั่งรับ (mirror _packet_allowed ของ agent)
                if (!PacketAllowed(_remotePolicy, data[0]))
                {
                    continue;
                }

                var pktType = data[0];
                if (pktType is OmniProtocol.PktMouseMoveRel or OmniProtocol.PktMouseMoveAbs)
                {
                    _lastUdpMove = Environment.TickCount64; // INCIDENT#2: arm edge detection
                }

                if (pktType == OmniProtocol.PktMouseMoveRel && data.Length >= 6)
                {
                    var dx = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(1));
                    var dy = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(3));
                    var mod = data[5];
                    _input.SyncModifiers(mod);
                    var (nx, ny) = _input.MoveRelative(dx, dy);
                    CheckEdgeReturn(nx, ny, dx, dy);
                }
                else if (pktType == OmniProtocol.PktMouseMoveAbs && data.Length >= 6)
                {
                    var ix = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(1));
                    var iy = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(3));
                    var mod = data[5];
                    _input.SyncModifiers(mod);
                    _input.MoveAbsolute((int)(ix / 65535.0 * (_input.ScreenWidth - 1)), (int)(iy / 65535.0 * (_input.ScreenHeight - 1)));
                }
                else if (pktType == OmniProtocol.PktMouseButton && data.Length >= 4)
                {
                    _input.SyncModifiers(data[3]);
                    _input.MouseButton(data[1], data[2] == 1);
                }
                else if (pktType == OmniProtocol.PktMouseWheel && data.Length >= 6)
                {
                    var wx = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(1));
                    var wy = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(3));
                    _input.SyncModifiers(data[5]);
                    _input.MouseWheel(wx, wy);
                }
                else if (pktType == OmniProtocol.PktKeyEvent && data.Length >= 5)
                {
                    var kc = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(1));
                    var down = data[3] == 1;
                    _input.KeyEvent(kc, down);
                }
                else if (pktType == OmniProtocol.PktSetLang && data.Length >= 3)
                {
                    var g = data[1] == 1 ? 1 : 0;
                    Task.Run(() =>
                    {
                        _gateway.RequestLangChange(g);
                        SendToHost(new { type = "lang_ack", lang_group = _gateway.GetForegroundLangGroup() });
                    });
                }
                else if (pktType == OmniProtocol.PktReleaseAll)
                {
                    _input.ReleaseAll();
                }
            }
            catch (Exception ex)
            {
                if (!_running) break;
                OmniLog.Warn("[sub] udp loop: " + ex.Message);
            }
        }
    }

    /// <summary>mirror _packet_allowed ของ agent: view ห้ามหมดยกเว้น RELEASE_ALL · clipboard ห้าม input/set_lang</summary>
    internal static bool PacketAllowed(string policy, byte pktType)
    {
        if (policy == "full") return true;
        if (pktType == OmniProtocol.PktReleaseAll) return true; // safety override เสมอ (กันปุ่มค้าง)
        if (policy == "clipboard")
        {
            return pktType != OmniProtocol.PktSetLang &&
                   pktType is not (OmniProtocol.PktMouseMoveRel or OmniProtocol.PktMouseMoveAbs
                       or OmniProtocol.PktMouseButton or OmniProtocol.PktKeyEvent or OmniProtocol.PktMouseWheel);
        }
        return false;
    }

    // ═══════════════════════ edge machine (A1 — mirror agent) ═══════════════════════

    private void ResetEdgeState()
    {
        _edgePush = 0;
        _atEdgeSince = 0;
        _hadRealPush = false;
        _lastLeaveAt = 0;
        _lastEdgeReturnSent = 0;
    }

    private void CheckEdgeReturn(int newX, int newY, int dx, int dy)
    {
        var now = Environment.TickCount64;
        var armedByFocus = ActiveFocus;
        var armedByUdp = now - _lastUdpMove < 2000;
        if (!(armedByFocus || armedByUdp) || _screenLocked)
        {
            return;
        }

        var w = _input.ScreenWidth;
        var h = _input.ScreenHeight;
        var (rx, ry) = _input.QueryPointer();

        bool exitLeft;
        bool atEdge;
        bool pushing;
        switch (_nodePosition)
        {
            case "left": exitLeft = false; atEdge = rx >= w - 2; pushing = dx > 0; break;
            case "top": exitLeft = false; atEdge = ry >= h - 2; pushing = dy > 0; break;
            case "bottom": exitLeft = false; atEdge = ry <= 1; pushing = dy < 0; break;
            default: exitLeft = true; atEdge = rx <= 1; pushing = dx < 0; break;
        }

        // มุมจอตาย
        if (atEdge && _cornerSize > 0 && InCornerDeadzone(rx, ry, w, h, exitLeft))
        {
            _edgePush = 0;
            _atEdgeSince = 0;
            _hadRealPush = false;
            return;
        }

        if (atEdge)
        {
            if (_atEdgeSince == 0)
            {
                // double-tap: เพิ่งออกจากขอบแล้วกลับเข้าใน window และเคยดันจริง
                var sinceLeaveMs = _lastLeaveAt > 0 ? now - _lastLeaveAt : long.MaxValue;
                if (_hadRealPush && _doubleTapMs > 0 && sinceLeaveMs <= _doubleTapMs)
                {
                    (rx, ry) = _input.QueryPointer();
                    if (StillAtEdge(rx, ry, w, h))
                    {
                        OmniLog.Write("sub", "edge double-tap → ส่งคืน host ทันที");
                        TriggerReturn(rx, ry, "double-tap");
                        return;
                    }
                }
                _atEdgeSince = now;
                _edgePush = 0;
            }
            if (pushing)
            {
                _edgePush += _nodePosition is "top" or "bottom" ? Math.Abs(dy) : Math.Abs(dx);
            }
            var delayMs = Math.Max(0, _switchDelayMs);
            if (_edgePush >= EdgePushThresholdPx && now - _atEdgeSince >= delayMs)
            {
                (rx, ry) = _input.QueryPointer();
                if (!StillAtEdge(rx, ry, w, h))
                {
                    return;
                }
                TriggerReturn(rx, ry, $"push{_edgePush:F0}px+hold{now - _atEdgeSince}ms");
                return;
            }
        }
        else if (_atEdgeSince != 0)
        {
            _lastLeaveAt = now;
            _hadRealPush = _edgePush >= EdgePushThresholdPx * 0.5;
            _edgePush = 0;
            _atEdgeSince = 0;
        }

        // v1.9.1 chain: ขอบอื่นที่มีเพื่อนบ้าน (ไม่ใช่ทางกลับ host) → ขอ host สลับต่อ
        if (NeighborEdgeHit(rx, ry, w, h, now))
        {
            return;
        }
    }

    private bool InCornerDeadzone(int rx, int ry, int w, int h, bool exitLeft)
    {
        if (_nodePosition is "right" or "left")
        {
            var nearTop = ry <= _cornerSize;
            var nearBottom = ry >= h - _cornerSize - 1;
            return exitLeft
                ? (nearTop && _corners.GetValueOrDefault("tl", true)) || (nearBottom && _corners.GetValueOrDefault("bl", true))
                : (nearTop && _corners.GetValueOrDefault("tr", true)) || (nearBottom && _corners.GetValueOrDefault("br", true));
        }
        var nearLeft = rx <= _cornerSize;
        var nearRight = rx >= w - _cornerSize - 1;
        return _nodePosition == "top"
            ? (nearLeft && _corners.GetValueOrDefault("bl", true)) || (nearRight && _corners.GetValueOrDefault("br", true))
            : (nearLeft && _corners.GetValueOrDefault("tl", true)) || (nearRight && _corners.GetValueOrDefault("tr", true));
    }

    private bool StillAtEdge(int rx, int ry, int w, int h) => _nodePosition switch
    {
        "left" => rx >= w - 2,
        "top" => ry >= h - 2,
        "bottom" => ry <= 1,
        _ => rx <= 1,
    };

    private long _lastSwitchRequestAt;

    /// <summary>ขอบบน/ล่าง/ซ้าย/ขวา (นอกจากทางกลับ host) ที่มีเพื่อนบ้านตาม set_layout → switch_request (throttle 400ms)</summary>
    private bool NeighborEdgeHit(int rx, int ry, int w, int h, long now)
    {
        if (_neighbors.Count == 0) return false;
        string? edge = null;
        if (ry <= 1) edge = "top";
        else if (ry >= h - 2) edge = "bottom";
        else if (rx <= 1) edge = "left";
        else if (rx >= w - 2) edge = "right";
        if (edge is null) return false;
        if (!_neighbors.TryGetValue(edge, out var target) || target == TopologyHost.Id) return false;
        if (now - _lastSwitchRequestAt < 400) return true; // กันรัว

        // คำนวณ norm ฝั่งตรงข้าม (เหมือน agent: ดัน top → โผล่ขอบล่างของปลายทาง)
        var normX = Math.Clamp(rx / (double)Math.Max(1, w - 1), 0, 1);
        var normY = Math.Clamp(ry / (double)Math.Max(1, h - 1), 0, 1);
        _lastSwitchRequestAt = now;
        OmniLog.Write("sub", $"[sub] switch_request edge={edge} target={target}");
        SendToHost(new { type = "switch_request", target, edge, norm_x = normX, norm_y = normY });
        return true;
    }

    private void TriggerReturn(int rx, int ry, string reason)
    {
        // dedup burst 50ms (เคยเจอจริง: 7 ครั้งใน 4ms)
        var now = Environment.TickCount64;
        if (now - _lastEdgeReturnSent < EdgeReturnDedupMs)
        {
            return;
        }
        _lastEdgeReturnSent = now;

        var w = _input.ScreenWidth;
        var h = _input.ScreenHeight;
        var normY = Math.Clamp(ry / (double)Math.Max(1, h - 1), 0, 1);
        var normX = Math.Clamp(rx / (double)Math.Max(1, w - 1), 0, 1);
        var edge = _nodePosition switch
        {
            "left" => "right",
            "top" => "bottom",
            "bottom" => "top",
            _ => "left",
        };

        OmniLog.Write("sub", $"[sub] edge_return to host (edge={edge}, reason={reason}, cursor=({rx},{ry}))");
        ForceReturnInternal("edge:" + reason, sendToHost: false);
        SendToHost(new { type = "edge_return", edge, norm_y = normY, norm_x = normX });
        PushClipboardAsync();
    }

    /// <summary>หนีฉุกเฉินจากคีย์บอร์ดจริงของเครื่องนี้ (Ctrl+Alt+Shift+Q)</summary>
    private void ForceReturn(string reason)
    {
        if (!ActiveFocus && Environment.TickCount64 - _lastUdpMove > 2000)
        {
            return; // ไม่ได้ถูกคุมอยู่ — ไม่ต้องทำอะไร
        }
        TriggerReturn(_input.QueryPointer().X, _input.QueryPointer().Y, reason);
    }

    private void ForceReturnInternal(string reason, bool sendToHost)
    {
        if (ActiveFocus)
        {
            ActiveFocus = false;
            _input.ReleaseAll();
            OmniLog.Write("sub", "คืนการคุม (" + reason + ")");
            OnStatus?.Invoke(Loc.T("sub.released", "เครื่องนี้กลับมาใช้งานเองแล้ว"));
        }
        ResetEdgeState();
        if (sendToHost)
        {
            SendToHost(new { type = "edge_return", edge = "left", norm_y = 0.5, norm_x = 0.5 });
        }
    }

    private void PushClipboardAsync()
    {
        if (_clipboard is null || _remotePolicy == "view") return;
        Task.Run(() =>
        {
            try
            {
                var text = _clipboard.GetText();
                if (string.IsNullOrEmpty(text)) return;
                var hash = ClipboardRouter.ClipHash("text", text);
                if (hash == _lastClipHash) return; // A1: กัน echo
                if (Encoding.UTF8.GetByteCount(text) > 5_000_000) return;
                if (SendToHostChecked(new { type = "clipboard", text }))
                {
                    _lastClipHash = hash;
                    OmniLog.Write("sub", $"[sub] push clipboard ไป host len={text.Length}");
                }
            }
            catch (Exception ex)
            {
                OmniLog.Warn("[sub] push clipboard: " + ex.Message);
            }
        });
    }

    private bool SendToHostChecked(object payload)
    {
        lock (_sendLock)
        {
            if (_activeStream is null) return false;
            try
            {
                var bytes = OmniProtocol.EncodeTcpJson(payload);
                _activeStream.Write(bytes, 0, bytes.Length);
                _activeStream.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
