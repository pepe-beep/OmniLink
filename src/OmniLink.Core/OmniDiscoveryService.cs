using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniLink.Core.Security;

namespace OmniLink.Core;

public sealed record DiscoveredNode(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("paired")] bool Paired,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("zone")] string Zone = "");

public sealed record PairingResult(
    bool Success,
    string Message,
    string? Token = null,
    string? NodeId = null,
    string? Name = null,
    int Width = 1920,
    int Height = 1080,
    string? Fingerprint = null);

/// <summary>
/// ระบบค้นหาและจับคู่อุปกรณ์ในวง LAN แบบ Zero-Config (Zero Configuration Discovery & Pairing)
/// ทำงานผ่าน UDP Broadcast (Port 24800) และ TCP Pairing Handshake
/// </summary>
public sealed class OmniDiscoveryService : IDisposable
{
    private readonly OmniConfig _config;
    private readonly Action<DiscoveredNode>? _onNodeDiscovered;
    private volatile bool _disposed;

    public OmniDiscoveryService(OmniConfig config, Action<DiscoveredNode>? onNodeDiscovered = null)
    {
        _config = config;
        _onNodeDiscovered = onNodeDiscovered;
    }

    /// <summary>
    /// ส่ง UDP Broadcast Beacon ออกไปในวง LAN เพื่อค้นหาโหนดลูกข่ายทั้งหมดที่เปิดรออยู่
    /// A2 fix: ยิง directed broadcast ต่อ subnet ของ "ทุกการ์ดเน็ต" (เดิมส่งแค่ 255.255.255.255
    /// ซึ่ง Windows ส่งออกเฉพาะ default NIC — เครื่องที่ LAN อยู่บนการ์ดรองจึงไม่มีวันเจอ)
    /// พร้อม unicast ตรงไปยัง node ที่ config รู้จักแล้ว (ครอบ VPN เช่น ZeroTier/Radmin ที่ broadcast ไม่ทำงาน)
    /// </summary>
    public async Task<List<DiscoveredNode>> ScanNetworkAsync(int timeoutMs = 2500)
    {
        var foundNodes = new Dictionary<string, DiscoveredNode>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var client = new UdpClient();
            client.EnableBroadcast = true;
            client.Client.ReceiveTimeout = timeoutMs;

            var probePayload = JsonSerializer.Serialize(new
            {
                cmd = "DISCOVER_OMNILINK",
                version = "2.0",
                host_name = _config.HostName,
                host_ip = _config.HostIp,
            });

            var bytes = Encoding.UTF8.GetBytes(probePayload);

            // รวบรวมเป้ายิง: directed broadcast ต่อทุก IPv4 subnet + 255.255.255.255 + unicast node ที่รู้จัก
            var targets = new List<IPAddress>();
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork ||
                        u.PrefixLength is < 0 or > 31)
                    {
                        continue;
                    }
                    var octets = u.Address.GetAddressBytes();
                    if (octets.Length != 4) continue;
                    var addr = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(octets);
                    var mask = u.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - u.PrefixLength);
                    var broadcastOctets = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(broadcastOctets, (addr & mask) | ~mask);
                    var broadcastAddr = new IPAddress(broadcastOctets);
                    if (!broadcastAddr.Equals(u.Address))
                    {
                        targets.Add(broadcastAddr);
                    }
                }
            }
            targets.Add(IPAddress.Broadcast);
            foreach (var n in _config.Nodes)
            {
                if (!string.IsNullOrWhiteSpace(n.Ip) && IPAddress.TryParse(n.Ip, out var nodeIp))
                {
                    targets.Add(nodeIp); // unicast ตรง — ใช้ได้แม้บน VPN ที่ broadcast ไม่ทำงาน
                }
            }

            var endpoints = targets.Distinct().Select(ip => new IPEndPoint(ip, OmniProtocol.DiscoveryPort)).ToList();

            // ส่ง 2 ระลอก ห่างกัน 100ms เพื่อป้องกัน packet drop บน Wi-Fi
            foreach (var ep in endpoints)
            {
                try { await client.SendAsync(bytes, bytes.Length, ep); } catch { }
            }
            await Task.Delay(100);
            foreach (var ep in endpoints)
            {
                try { await client.SendAsync(bytes, bytes.Length, ep); } catch { }
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                var remaining = (int)Math.Max(10, (deadline - DateTime.UtcNow).TotalMilliseconds);
                var receiveTask = client.ReceiveAsync();

                if (await Task.WhenAny(receiveTask, Task.Delay(remaining)) == receiveTask)
                {
                    var result = await receiveTask;
                    var text = Encoding.UTF8.GetString(result.Buffer);

                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("cmd", out var cmdProp) && cmdProp.GetString() == "ANNOUNCE")
                        {
                            // SV-M9: ANNOUNCE มาจากใครก็ได้ใน LAN — ตรวจทุก field ก่อนใช้ (กันค่าติดลบ/สตริงยาวเกิน)
                            var uuid = SanitizeBeaconText(root.TryGetProperty("uuid", out var up) ? up.GetString() ?? "" : "", 64);
                            var name = SanitizeBeaconText(root.TryGetProperty("name", out var np) ? np.GetString() ?? "Unknown Node" : "Unknown Node", 64);
                            if (name.Length == 0) name = "Unknown Node";
                            var os = SanitizeBeaconText(root.TryGetProperty("os", out var op) ? op.GetString() ?? "Linux" : "Linux", 32);
                            if (os.Length == 0) os = "Linux";
                            var port = root.TryGetProperty("port", out var pp) && pp.TryGetInt32(out var pRaw) && pRaw is >= 1 and <= 65535
                                ? pRaw : OmniProtocol.KvmTcpPort;
                            var width = root.TryGetProperty("width", out var wp) && wp.TryGetInt32(out var wRaw) ? Math.Clamp(wRaw, 100, 32767) : 1920;
                            var height = root.TryGetProperty("height", out var hp) && hp.TryGetInt32(out var hRaw) ? Math.Clamp(hRaw, 100, 32767) : 1080;
                            var paired = root.TryGetProperty("paired", out var prp) && prp.GetBoolean();
                            var ver = SanitizeBeaconText(root.TryGetProperty("version", out var vp) ? vp.GetString() ?? "?" : "?", 32);
                            if (ver.Length == 0) ver = "?";

                            var senderIp = result.RemoteEndPoint.Address.ToString();
                            if (senderIp.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                            {
                                senderIp = senderIp[7..];
                            }

                            // B3: ระบุว่า node นี้เจอผ่านเครือข่ายไหน (LAN / VPN / Wi-Fi) จากการ์ดที่ subnet ครอบ IP ผู้ส่ง
                            var zone = DescribeZoneForIp(senderIp);

                            var node = new DiscoveredNode(
                                Uuid: uuid,
                                Name: name,
                                Os: os,
                                Ip: senderIp,
                                Port: port,
                                Width: width,
                                Height: height,
                                Paired: paired,
                                Version: ver,
                                Zone: zone);

                            // F1 fix: dedupe ตาม uuid — เครื่องเดียวหลาย NIC (LAN + VPN เช่น ZeroTier) โพล่หลายแถว
                            // เดิม key = "{senderIp}:{uuid}" ทำให้เห็น 2 แถว แล้วจับคู่ผิดแถว VPN (TLS พังกลางทาง — BUGS-SEEN 25 ต.ค.)
                            // uuid ว่าง (beacon ไม่สมบูรณ์) = คุมด้วย IP แทน กันรวมเครื่องแปลกปรอมันเป็นแถวเดียว
                            var key = uuid.Length > 0 ? uuid : $"ip:{senderIp}";
                            if (foundNodes.TryGetValue(key, out var existing))
                            {
                                if (PreferIncomingNode(existing, node))
                                {
                                    foundNodes[key] = node;
                                    _onNodeDiscovered?.Invoke(node);
                                    OmniLog.Write("discovery", $"dedupe: '{name}' uuid={uuid} เปลี่ยน {existing.Ip} ({existing.Zone}) -> {senderIp} (LAN นิ่งกว่าสำหรับจับคู่)");
                                }
                            }
                            else
                            {
                                foundNodes[key] = node;
                                _onNodeDiscovered?.Invoke(node);
                                OmniLog.Write("discovery", $"พบอุปกรณ์ในวง LAN: '{name}' ({os}) ที่ IP {senderIp} (uuid={uuid})");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OmniLog.Warn($"[discovery] packet parse error from {result.RemoteEndPoint}: {ex.Message}");
                    }
                }
                else
                {
                    break; // หมดเวลารอรับข้อมูล
                }
            }
        }
        catch (Exception ex)
        {
            OmniLog.Error("[discovery] scan network error", ex);
        }

        return foundNodes.Values.ToList();
    }

    /// <summary>
    /// B3: จำแนก "โซน" จาก IP — หาการ์ดที่ subnet ครอบ IP นั้น แล้วติดป้ายตามชนิด/ชื่อการ์ด
    /// (LAN = Ethernet, VPN = ZeroTier/Radmin/WireGuard/Tailscale, Wi-Fi = การ์ดไร้สาย)
    /// </summary>
    /// <summary>F1: เมื่อ uuid เดียวโผล่หลาย NIC — คงแถวเดิม ยกเว้นแถวใหม่มาจากโซน LAN ทับ VPN/Wi-Fi/อื่น (จับคู่ผ่าน LAN นิ่งกว่า)</summary>
    internal static bool PreferIncomingNode(DiscoveredNode existing, DiscoveredNode incoming)
        => incoming.Zone == "LAN" && !string.Equals(existing.Zone, "LAN", StringComparison.Ordinal);

    internal static string DescribeZoneForIp(string ip)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var target)) return "";
            var targetBytes = target.GetAddressBytes();

            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork ||
                        u.PrefixLength is < 0 or > 31)
                    {
                        continue;
                    }
                    var localBytes = u.Address.GetAddressBytes();
                    if (localBytes.Length != 4 || targetBytes.Length != 4) continue;
                    var localAddr = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(localBytes);
                    var targetAddr = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(targetBytes);
                    var mask = u.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - u.PrefixLength);
                    if ((localAddr & mask) != (targetAddr & mask)) continue;

                    var name = ni.Name ?? "";
                    var desc = ni.Description ?? "";
                    var combined = name + " " + desc;
                    if (combined.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)) return "VPN (ZeroTier)";
                    if (combined.Contains("Radmin", StringComparison.OrdinalIgnoreCase)) return "VPN (Radmin)";
                    if (combined.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                        combined.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
                    {
                        return "VPN";
                    }
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                    {
                        return "Wi-Fi";
                    }
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)
                    {
                        return "LAN";
                    }
                    return string.IsNullOrWhiteSpace(name) ? "LAN" : name;
                }
            }
        }
        catch
        {
        }
        return "";
    }

    /// <summary>
    /// SV-M9: ล้างข้อความจาก beacon — ตัด control chars + cap ความยาว (กัน log/UI injection)
    /// </summary>
    private static string SanitizeBeaconText(string? raw, int maxLen)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var clean = new string(raw.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > maxLen ? clean[..maxLen] : clean;
    }

    /// <summary>
    /// สุ่มรหัสยืนยัน 6 หลัก (6-digit Pairing PIN) ในรูปแบบ '123-456'
    /// </summary>
    public static string GeneratePairingPin()
    {
        var num = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000);
        return $"{num / 1000:D3}-{num % 1000:D3}";
    }

    /// <summary>seam สำหรับ unit test — คืน (stream, fingerprint ของ peer|null); production = TLS handshake เสมอ</summary>
    internal delegate Task<(Stream stream, string? remoteFingerprint)> PairingStreamFactory(
        TcpClient client, string targetHost, CancellationToken ct);

    private static async Task<(Stream stream, string? remoteFingerprint)> DefaultTlsPairingStreamFactory(
        TcpClient client, string targetHost, CancellationToken ct)
    {
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            // จงใจยอม cert ใดก็ได้ "เฉพาะขั้นจับคู่" — การตัดสินใจเชื่อใจคือมนุษย์ผ่าน confirmFingerprint
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        }, ct);

        if (ssl.RemoteCertificate is null)
        {
            throw new AuthenticationException(Loc.T("pairing.err.nocert", "เครื่องลูกไม่เสนอ TLS certificate (agent เก่ากว่า v1.6 หรือถูกแทรก)"));
        }

        var fingerprint = OmniPacketAuth.CertificateFingerprint(
            ssl.RemoteCertificate.Export(X509ContentType.Cert));
        return (ssl, fingerprint);
    }

    /// <summary>
    /// ร้องขอการจับคู่ความปลอดภัยกับโหนดปลายทาง (Pairing Request Handshake) — v1.6 TLS
    /// • ทุก connection เป็น TLS · ขั้นจับคู่ "ยอมรับ cert ใดก็ได้" เพราะความเชื่อใจมาจาก
    ///   มนุษย์เทียบ fingerprint ที่โชว์บนจอเครื่องลูก (TOFU แบบ Deskflow) — callback
    ///   confirmFingerprint คือจุดให้มนุษย์ตัดสิน (false = ยกเลิก)
    /// • pair_accept ต้องรายงาน fingerprint ตรงกับ cert ที่ใช้จริง (กัน payload โกง)
    /// • SV-H5: ทุก Read/Write ผูก CancellationToken — peer หนวด = ตัดตาม timeout
    /// </summary>
    public Task<PairingResult> RequestPairingAsync(
        DiscoveredNode node, string pin, int timeoutSec = 25, Func<string, bool>? confirmFingerprint = null)
        => RequestPairingAsync(node, pin, timeoutSec, confirmFingerprint, DefaultTlsPairingStreamFactory);

    internal async Task<PairingResult> RequestPairingAsync(
        DiscoveredNode node, string pin, int timeoutSec,
        Func<string, bool>? confirmFingerprint, PairingStreamFactory streamFactory)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, timeoutSec)));

            await client.ConnectAsync(node.Ip, node.Port, cts.Token);
            client.NoDelay = true;

            using var ctsLink = cts.Token.Register(() => { try { client.Close(); } catch { } });

            var (stream, fingerprint) = await streamFactory(client, node.Ip, cts.Token);
            if (string.IsNullOrEmpty(fingerprint))
            {
                return new PairingResult(false, Loc.T("pairing.err.nocert.reject", "เครื่องลูกไม่เสนอ TLS certificate (agent เก่ากว่า v1.6 หรือถูกแทรก) — ปฏิเสธการจับคู่"));
            }

            if (confirmFingerprint is not null && !confirmFingerprint(fingerprint))
            {
                OmniLog.Warn($"[pairing] ผู้ใช้ปฏิเสธ fingerprint ของ {node.Name} ({node.Ip}) — ยกเลิก");
                return new PairingResult(false, Loc.T("pairing.err.fingerprint.mismatch", "ยกเลิกการจับคู่ — fingerprint บนจอเครื่องลูกไม่ตรงกับที่ได้รับ (อย่าจับคู่เมื่อไม่ตรง!)"));
            }

            var pairReq = JsonSerializer.Serialize(new
            {
                type = "pair_request",
                host_name = _config.HostName,
                pin = pin,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });

            var reqBytes = OmniProtocol.EncodeTcpJson(JsonDocument.Parse(pairReq).RootElement);
            await stream.WriteAsync(reqBytes, cts.Token);
            await stream.FlushAsync(cts.Token);

            OmniLog.Write("pairing", $"ส่งคำขอจับคู่ไปยัง {node.Name} ({node.Ip}) ผ่าน TLS (รอการตอบรับสูงสุด {timeoutSec}s)");

            // อ่านผลลัพธ์ตอบกลับจากโหนดปลายทาง
            var header = new byte[4];
            var bytesRead = await stream.ReadAsync(header.AsMemory(0, 4), cts.Token);
            if (bytesRead < 4)
            {
                return new PairingResult(false, Loc.T("pairing.err.dropped", "การเชื่อมต่อถูกตัดขาดระหว่างรอการยืนยันรหัส PIN"));
            }

            var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            if (len == 0 || len > 1024 * 64)
            {
                return new PairingResult(false, Loc.T("pairing.err.badreply", "ได้รับรูปแบบข้อมูลตอบกลับที่ไม่ถูกต้อง"));
            }

            var body = new byte[len];
            var totalRead = 0;
            while (totalRead < len)
            {
                var r = await stream.ReadAsync(body.AsMemory(totalRead, (int)len - totalRead), cts.Token);
                if (r <= 0) break;
                totalRead += r;
            }

            if (totalRead < len)
            {
                return new PairingResult(false, Loc.T("pairing.err.incomplete", "ข้อมูลตอบกลับไม่สมบูรณ์"));
            }

            using var respDoc = JsonDocument.Parse(body);
            var root = respDoc.RootElement;
            var respType = root.TryGetProperty("type", out var tp) ? tp.GetString() : "";

            if (respType == "pair_accept")
            {
                // ตรวจว่า fingerprint ที่ agent รายงานตรงกับ cert ที่ใช้จริงบนสาย (กัน payload ปลอม)
                var claimedFp = (root.TryGetProperty("cert_fingerprint", out var cfp) ? cfp.GetString() : null)?
                    .Replace(":", "").Replace("-", "").ToLowerInvariant();
                if (string.IsNullOrEmpty(claimedFp) || !string.Equals(claimedFp, fingerprint, StringComparison.Ordinal))
                {
                    OmniLog.Error($"[pairing] {node.Name}: fingerprint ใน pair_accept ไม่ตรงกับ TLS cert จริง — ปฏิเสธ");
                    return new PairingResult(false, Loc.T("pairing.err.badcert", "ปฏิเสธการจับคู่ — ข้อมูล certificate จากเครื่องลูกไม่น่าเชื่อถือ"));
                }

                var token = root.TryGetProperty("token", out var tkp) ? tkp.GetString() : "";
                var w = root.TryGetProperty("width", out var wp) ? Math.Clamp(wp.GetInt32(), 100, 32767) : node.Width;
                var h = root.TryGetProperty("height", out var hp) ? Math.Clamp(hp.GetInt32(), 100, 32767) : node.Height;
                var confirmedName = root.TryGetProperty("name", out var np) ? np.GetString() : node.Name;

                OmniLog.Write("pairing", $"จับคู่กับ {node.Name} สำเร็จผ่าน TLS! pin fingerprint={fingerprint[..12]}..");
                return new PairingResult(true, Loc.T("pairing.ok", "จับคู่อุปกรณ์สำเร็จ"), token, node.Uuid, confirmedName, w, h, fingerprint);
            }
            else
            {
                var reason = root.TryGetProperty("reason", out var rp) ? rp.GetString() : Loc.T("pairing.reason.rejected", "ผู้ใช้ปลายทางปฏิเสธการเชื่อมต่อ");
                OmniLog.Warn($"[pairing] โหนด {node.Name} ปฏิเสธการจับคู่: {reason}");
                return new PairingResult(false, Loc.T("pairing.err.rejected", "การจับคู่ถูกปฏิเสธ: {0}", reason ?? ""));
            }
        }
        catch (OperationCanceledException)
        {
            return new PairingResult(false, $"หมดเวลารอการยืนยันรหัส PIN จาก {node.Name} ({timeoutSec} วินาที)");
        }
        catch (Exception ex)
        {
            OmniLog.Error($"[pairing] เกิดข้อผิดพลาดขณะจับคู่กับ {node.Name}", ex);
            return new PairingResult(false, $"เกิดข้อผิดพลาดในการเชื่อมต่อ: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
