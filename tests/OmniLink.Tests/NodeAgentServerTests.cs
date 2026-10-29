using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Security;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v2.0 เฟส 3: ทดสอบ NodeAgentServer (โหมด sub ฝัง) แบบ end-to-end บน loopback จริงทุกชั้น —
/// TCP+TLS, PIN pairing (TOFU fingerprint), auth fail-closed, enter_screen→ฉีดอินพุตผ่าน injector,
/// UDP อินพุตพร้อม HMAC+seq (สร้าง packet ด้วย OmniPacketAuth ฝั่งส่งจริง), edge_return เมื่อดันขอบ,
/// policy matrix — ต้องไม่มี OmniLink.exe รันอยู่ระหว่างทดสอบ (พอร์ต 24801/24802 จริง)
/// </summary>
/// <summary>รันอนุกรม — ทุกเคสผูกพอร์ตจริง 24801/24802 ห้ามขนานกัน (ReuseAddress บน Windows จะพาไป instance ผิด)</summary>
[CollectionDefinition("SubServerSerial")]
public class SubServerSerialCollection { }

[Collection("SubServerSerial")]
public class NodeAgentServerTests : IDisposable
{
    private readonly string _home;
    private readonly FakeNodeInputInjector _injector = new();
    private readonly OmniConfig _config = new() { Nodes = new List<NodeConfig>() };
    private NodeAgentServer? _server;

    public NodeAgentServerTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "omnilink-sub-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
        OmniPaths.SetRootForTest(_home);
    }

    public void Dispose()
    {
        _server?.Dispose();
        OmniPaths.SetRootForTest(null);
        try { Directory.Delete(_home, true); } catch { }
    }

    private NodeAgentServer StartServer()
    {
        _server = new NodeAgentServer(_config, _injector, new NullHostInputGateway(), null, new NullEscapeHotkeyHook());
        _server.Start();
        return _server;
    }

    private static byte[] Frame(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var body = Encoding.UTF8.GetBytes(json);
        var buf = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)body.Length);
        body.CopyTo(buf, 4);
        return buf;
    }

    private static (string Type, JsonElement Root) ReadMessage(SslStream ssl)
    {
        var header = new byte[4];
        Assert.True(ReadExact(ssl, header));
        var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        Assert.InRange(len, 1, 65536);
        var body = new byte[len];
        Assert.True(ReadExact(ssl, body));
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
        return (doc.RootElement.GetProperty("type").GetString() ?? "?", doc.RootElement.Clone());
    }

    private static bool ReadExact(Stream s, byte[] buf)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = s.Read(buf, off, buf.Length - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    private static SslStream ConnectTls(NodeAgentServer server)
    {
        var tcp = new TcpClient("127.0.0.1", OmniProtocol.KvmTcpPort);
        tcp.NoDelay = true;
        var ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errors) =>
        {
            if (cert is null) return false;
            var fp = OmniPacketAuth.CertificateFingerprint(cert.Export(X509ContentType.Cert));
            return fp == server.CertFingerprint; // TOFU: ตรวจ fingerprint แบบเดียวกับ host จริง
        });
        ssl.AuthenticateAsClient("127.0.0.1");
        return ssl;
    }

    [Fact]
    public void PacketAllowed_Matrix_MatchesAgentSemantics()
    {
        // full = ทุกอย่าง
        foreach (var t in new byte[] { 1, 2, 3, 4, 5, 6, 7 })
        {
            Assert.True(NodeAgentServer.PacketAllowed("full", t));
        }
        // view = ห้ามหมด ยกเว้น RELEASE_ALL (safety override กันปุ่มค้าง)
        Assert.True(NodeAgentServer.PacketAllowed("view", OmniProtocol.PktReleaseAll));
        Assert.False(NodeAgentServer.PacketAllowed("view", OmniProtocol.PktMouseMoveRel));
        Assert.False(NodeAgentServer.PacketAllowed("view", OmniProtocol.PktKeyEvent));
        // clipboard = ห้าม input/set_lang แต่ release ได้
        Assert.False(NodeAgentServer.PacketAllowed("clipboard", OmniProtocol.PktMouseMoveRel));
        Assert.False(NodeAgentServer.PacketAllowed("clipboard", OmniProtocol.PktSetLang));
        Assert.True(NodeAgentServer.PacketAllowed("clipboard", OmniProtocol.PktReleaseAll));
    }

    [Fact]
    public void Auth_WithWrongToken_IsRejected()
    {
        var server = StartServer();

        using var ssl = ConnectTls(server);
        ssl.Write(Frame(new { type = "auth", token = "wrong-token-entirely" }));
        ssl.Flush();

        // fail-closed: ไม่มี auth_ok — connection โดนปิด (Read คืน 0 / exception)
        var buf = new byte[64];
        var read = AttemptRead(ssl, buf);
        Assert.True(read <= 0, "ต้องไม่มีข้อความตอบกลับเมื่อ token ผิด (fail-closed)");
    }

    private static int AttemptRead(SslStream ssl, byte[] buf)
    {
        try
        {
            ssl.ReadTimeout = 3000;
            return ssl.Read(buf, 0, buf.Length);
        }
        catch
        {
            return -1;
        }
    }

    [Fact]
    public void PairAuthEnterUdpEdge_FullFlow_Works()
    {
        var server = StartServer();
        var pin = server.PairingPin;

        // ── 1) จับคู่ด้วย PIN → ได้ token + fingerprint (TOFU) ──
        string token;
        {
            using var ssl = ConnectTls(server);
            ssl.Write(Frame(new { type = "pair_request", pin, host_name = "test-host" }));
            ssl.Flush();
            var (type, root) = ReadMessage(ssl);
            Assert.Equal("pair_accept", type);
            token = root.GetProperty("token").GetString()!;
            Assert.Equal(64, token.Length);
            Assert.Equal(server.CertFingerprint, root.GetProperty("cert_fingerprint").GetString());
            Assert.Equal(server.MachineUuid, root.GetProperty("uuid").GetString());
        }
        Assert.NotEqual(pin, server.PairingPin); // PIN หมุนใหม่หลังจับคู่สำเร็จ

        // ── 2) auth ด้วย token ใหม่ → auth_ok + hello ──
        var ssl2 = ConnectTls(server);
        ssl2.Write(Frame(new { type = "auth", token }));
        ssl2.Flush();
        var (t1, _) = ReadMessage(ssl2);
        Assert.Equal("auth_ok", t1);
        var (t2, hello) = ReadMessage(ssl2);
        Assert.Equal("hello", t2);
        Assert.Equal(KvmHostService.ExpectedAgentVersion, hello.GetProperty("version").GetString());
        Assert.True(hello.GetProperty("auth").GetBoolean());
        Assert.Equal(1920, hello.GetProperty("width").GetInt32());

        // ── 3) enter_screen → injector วาร์ปเคอร์เซอร์เข้าจอ + ActiveFocus ──
        ssl2.Write(Frame(new { type = "enter_screen", position = "right", norm_y = 0.5, norm_x = 0.5, lang_group = 0, switch_delay_ms = 0, double_tap_ms = 0, corner_size = 0 }));
        ssl2.Flush();
        SpinWait.SpinUntil(() => server.ActiveFocus, 3000);
        Assert.True(server.ActiveFocus);
        SpinWait.SpinUntil(() =>
        {
            lock (_injector.Lock)
            {
                return _injector.Calls.Any(c => c.Op == "abs");
            }
        }, 3000);
        lock (_injector.Lock)
        {
            var abs = _injector.Calls.First(c => c.Op == "abs");
            Assert.Equal(24, abs.A); // เข้าจอฝั่งขวาของ host → โผล่ x=24
        }

        // ── 4) UDP mouse rel (HMAC + seq จริง) → injector ขยับ + ดันขอบซ้ายจนส่ง edge_return ──
        var udpKey = OmniPacketAuth.DeriveUdpKey(token);
        using var udp = new UdpClient();
        var endp = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, OmniProtocol.KvmUdpPort);

        void SendPacket(byte[] body)
        {
            var seq = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var buf = new byte[body.Length + OmniPacketAuth.SeqSize + OmniPacketAuth.TrailerSize];
            Assert.True(OmniPacketAuth.AppendTrailerWithSeq(body, udpKey, seq, buf));
            udp.Send(buf, buf.Length, endp);
        }

        var rel = new byte[6];
        OmniProtocol.WriteMouseRel(rel, -300, 0, 0);
        _injector.Pointer = (100, _injector.ScreenHeight / 2);
        SendPacket(rel);
        SpinWait.SpinUntil(() =>
        {
            lock (_injector.Lock) return _injector.Calls.Any(c => c.Op == "rel");
        }, 3000);

        // ดันสะสมทางซ้าย (position=right → exit edge = left) จนเกิน threshold
        var edgeReturned = false;
        for (var i = 0; i < 30 && !edgeReturned; i++)
        {
            _injector.Pointer = (2, _injector.ScreenHeight / 2);
            var push = new byte[6];
            OmniProtocol.WriteMouseRel(push, -40, 0, 0);
            SendPacket(push);
            Thread.Sleep(30);
            edgeReturned = !server.ActiveFocus;
        }

        // ── 5) ได้ edge_return บน TCP (edge="left" กลับเครื่องหลัก) ──
        var deadline = Environment.TickCount64 + 5000;
        var got = false;
        while (Environment.TickCount64 < deadline && !got)
        {
            try
            {
                ssl2.ReadTimeout = 500;
                var (type, root) = ReadMessage(ssl2);
                if (type == "edge_return")
                {
                    Assert.Equal("left", root.GetProperty("edge").GetString());
                    Assert.True(root.GetProperty("norm_y").GetDouble() is > 0 and < 1);
                    got = true;
                }
            }
            catch
            {
            }
        }
        Assert.True(got, "ต้องได้รับ edge_return หลังดันขอบสะสมเกิน 60px");

        // ปล่อยปุ่มทั้งหมดเมื่อคืนการคุม
        lock (_injector.Lock)
        {
            Assert.Contains(_injector.Calls, c => c.Op == "release");
        }

        ssl2.Dispose();
    }

    [Fact]
    public void Pong_ReportsFocusedAndSize()
    {
        var server = StartServer();
        // จับคู่ก่อนเพื่อรู้ token (ใช้ helper จากเทสหลัก แต่คนละ connection)
        string token;
        {
            using var ssl = ConnectTls(server);
            ssl.Write(Frame(new { type = "pair_request", pin = server.PairingPin, host_name = "t" }));
            ssl.Flush();
            var (type, root) = ReadMessage(ssl);
            Assert.Equal("pair_accept", type);
            token = root.GetProperty("token").GetString()!;
        }

        var ssl2 = ConnectTls(server);
        ssl2.Write(Frame(new { type = "auth", token }));
        ssl2.Flush();
        ReadMessage(ssl2); // auth_ok
        ReadMessage(ssl2); // hello

        ssl2.Write(Frame(new { type = "ping", ts = 12345.0 }));
        ssl2.Flush();
        var (type2, pong) = ReadMessage(ssl2);
        Assert.Equal("pong", type2);
        Assert.Equal(12345.0, pong.GetProperty("ts").GetDouble());
        Assert.False(pong.GetProperty("focused").GetBoolean()); // ยังไม่ถูกคุม
        Assert.Equal(1080, pong.GetProperty("height").GetInt32());
        ssl2.Dispose();
    }

    // ─────────────── B9 (sub-mode clipboard image + file receive) ───────────────

    private sealed class RecordingClipboard : IClipboardBridge
    {
        public byte[]? PngSet;
        public List<string> FilePathsSet = new();
        public bool FilesCut;

        public string? GetText() => null;
        public bool SetText(string text) => true;
        public bool TryGetPng(out byte[] pngBytes) { pngBytes = PngSet ?? Array.Empty<byte>(); return PngSet != null; }
        public bool SetPng(byte[] pngBytes) { PngSet = pngBytes; return true; }
        public bool ContainsFileDropList() => FilePathsSet.Count > 0;
        public System.Collections.Generic.IReadOnlyList<string>? GetFileDropList(out bool isCut) { isCut = FilesCut; return FilePathsSet; }
        public bool SetFileDropList(System.Collections.Generic.IReadOnlyList<string> paths, bool isCut)
        {
            FilePathsSet = new List<string>(paths); FilesCut = isCut; return true;
        }
    }

    /// <summary>pair + auth + คืน connection ที่พร้อมส่งคำสั่ง (full policy default)</summary>
    private (SslStream Ssl, NodeAgentServer Server) AuthenticatedConnection(RecordingClipboard clip)
    {
        _server = new NodeAgentServer(_config, _injector, new NullHostInputGateway(), clip, new NullEscapeHotkeyHook());
        _server.Start();
        var pin = _server.PairingPin;
        string token;
        using (var sslPair = ConnectTls(_server))
        {
            sslPair.Write(Frame(new { type = "pair_request", pin, host_name = "test-host" }));
            sslPair.Flush();
            var (t, root) = ReadMessage(sslPair);
            Assert.Equal("pair_accept", t);
            token = root.GetProperty("token").GetString()!;
        }
        var ssl = ConnectTls(_server);
        ssl.Write(Frame(new { type = "auth", token }));
        ssl.Flush();
        var (ok, _) = ReadMessage(ssl);
        Assert.Equal("auth_ok", ok);
        var (hello, _) = ReadMessage(ssl);
        Assert.Equal("hello", hello);
        return (ssl, _server);
    }

    [Fact]
    public void SubClipboard_ImageFastPath_SetsPng()
    {
        var clip = new RecordingClipboard();
        var (ssl, server) = AuthenticatedConnection(clip);
        using var _ = server;
        try
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
            ssl.Write(Frame(new { type = "clipboard", mime = "image/png", data = Convert.ToBase64String(png) }));
            ssl.Flush();
            Assert.True(SpinUntil(() => clip.PngSet != null, 5000), "SetPng ต้องถูกเรียกจาก fast-path");
            Assert.Equal(png, clip.PngSet);
        }
        finally { ssl.Dispose(); }
    }

    [Fact]
    public void SubClipboard_ImageAckGate_CompletesViaClipFetch()
    {
        var clip = new RecordingClipboard();
        var (ssl, server) = AuthenticatedConnection(clip);
        try
        {
            var png = new byte[70000]; // >64KB = host ใช้ ACK-gate
            for (var i = 0; i < png.Length; i++) png[i] = (byte)(i % 251);

            // host: clip_meta → sub ต้องตอบ clip_fetch กลับ (ขอข้อมูลเต็ม)
            ssl.Write(Frame(new { type = "clip_meta", mime = "image/png", token = "tok-ack", size = png.Length }));
            ssl.Flush();
            var (ft, fr) = ReadMessage(ssl);
            Assert.Equal("clip_fetch", ft);
            Assert.Equal("tok-ack", fr.GetProperty("token").GetString());

            // host: clip_data (ข้อมูลเต็ม) → sub SetPng
            ssl.Write(Frame(new { type = "clip_data", mime = "image/png", data = Convert.ToBase64String(png) }));
            ssl.Flush();
            Assert.True(SpinUntil(() => clip.PngSet != null, 5000), "SetPng ต้องถูกเรียกจาก clip_data (จบ ACK-gate)");
            Assert.Equal(png, clip.PngSet);
        }
        finally { ssl.Dispose(); }
    }

    [Fact]
    public void SubFiles_FullReceiveCycle_WritesFileAndSetsDropList()
    {
        var clip = new RecordingClipboard();
        var (ssl, server) = AuthenticatedConnection(clip);
        try
        {
            var content = new byte[8000]; // < chunk size
            for (var i = 0; i < content.Length; i++) content[i] = (byte)(i * 7 % 256);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

            var manifest = new OmniLink.Core.FileExchange.FileManifest
            {
                Effect = "copy",
                TotalBytes = content.Length,
                Files = new List<OmniLink.Core.FileExchange.FileEntry>
                {
                    new() { RelativePath = "test-file.bin", Size = content.Length, Sha256 = sha },
                },
            };

            ssl.Write(Frame(new { type = "file_offer", manifest = manifest.ToJson() }));
            ssl.Flush();
            var (ft, fr) = ReadMessage(ssl);
            Assert.Equal("file_accept", ft);
            Assert.Equal(manifest.Id, fr.GetProperty("id").GetString());

            ssl.Write(Frame(new { type = "file_chunk", id = manifest.Id, idx = 0, offset = 0, data = Convert.ToBase64String(content) }));
            ssl.Write(Frame(new { type = "file_done", id = manifest.Id, idx = 0 }));
            ssl.Flush();
            var (rt, rr) = ReadMessage(ssl);
            Assert.Equal("file_result", rt);
            Assert.Equal("ok", rr.GetProperty("status").GetString());

            ssl.Write(Frame(new { type = "file_complete", id = manifest.Id }));
            ssl.Flush();
            Assert.True(SpinUntil(() => clip.FilePathsSet.Count > 0, 5000), "file_complete ต้องตั้ง clipboard file list");
            Assert.Contains("test-file.bin", clip.FilePathsSet[0]);
            Assert.True(File.Exists(clip.FilePathsSet[0]), "ไฟล์ต้องถูกเขียนจริง");
            Assert.Equal(content, File.ReadAllBytes(clip.FilePathsSet[0]));
            Assert.False(clip.FilesCut);
        }
        finally { ssl.Dispose(); }
    }

    [Fact]
    public void SubHistory_RequestAndGet_RoundTrip()
    {
        var clip = new RecordingClipboard();
        var (ssl, server) = AuthenticatedConnection(clip);
        try
        {
            // เติมประวัติผ่าน clipboard fast-path (ฝั่ง sub รับจาก host = เข้า store เดียวกัน)
            ssl.Write(Frame(new { type = "clipboard", text = "hist-alpha" }));
            ssl.Write(Frame(new { type = "clipboard", text = "hist-beta" }));
            ssl.Flush();
            Assert.True(SpinUntil(() => OmniLink.Core.Clipboard.ClipboardHistoryStore.Default.GetEntries().Count >= 2, 5000),
                "sub ต้องเก็บ text 2 รายการเข้า store เดียวกัน");

            // history_request → history_list (metadata)
            ssl.Write(Frame(new { type = "history_request" }));
            ssl.Flush();
            var (t1, r1) = ReadMessage(ssl);
            Assert.Equal("history_list", t1);
            var list = r1.GetProperty("entries");
            Assert.True(list.GetArrayLength() >= 2, "ต้องมีอย่างน้อย 2 รายการ");
            var firstId = list[0].GetProperty("id").GetString()!;
            var preview = list[0].GetProperty("preview").GetString()!;
            Assert.Equal("hist-beta", preview); // ใหม่สุดหัวแถว

            // history_get → history_entry (เนื้อหาเต็ม)
            ssl.Write(Frame(new { type = "history_get", id = firstId }));
            ssl.Flush();
            var (t2, r2) = ReadMessage(ssl);
            Assert.Equal("history_entry", t2);
            Assert.Equal(firstId, r2.GetProperty("id").GetString());
            Assert.Equal("text/plain", r2.GetProperty("mime").GetString());
            Assert.Equal("hist-beta", Encoding.UTF8.GetString(OmniImage.FromBase64(r2.GetProperty("data").GetString()!)));

            // history_get id ไม่มี → missing
            ssl.Write(Frame(new { type = "history_get", id = "no-such-id" }));
            ssl.Flush();
            var (t3, r3) = ReadMessage(ssl);
            Assert.Equal("history_entry", t3);
            Assert.True(r3.GetProperty("missing").GetBoolean());
        }
        finally { ssl.Dispose(); }
    }

    private static bool SpinUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(20);
        }
        return cond();
    }
}
