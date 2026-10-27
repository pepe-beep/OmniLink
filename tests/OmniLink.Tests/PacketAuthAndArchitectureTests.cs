using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OmniLink.Core;
using OmniLink.Core.Security;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v1.6 hardening (SV-H3/SV-H4): แทนที่ test ของ OmniCryptoShield (dead code ที่ถูกลบ)
/// ด้วย OmniPacketAuth — HMAC-SHA256 trailer ที่ใช้จริงบนทาง UDP
/// </summary>
public class PacketAuthAndArchitectureTests
{
    [Fact]
    public void PacketAuth_AppendAndVerify_RoundTrip()
    {
        var key = OmniPacketAuth.DeriveUdpKey("token_abcdef");
        Span<byte> body = stackalloc byte[] { 0x01, 0x00, 0x0A, 0x00, 0x14, 0x00 };

        Span<byte> packet = stackalloc byte[body.Length + OmniPacketAuth.TrailerSize];
        Assert.True(OmniPacketAuth.AppendTrailer(body, key, packet));

        // body ต้องอยู่ต้น packet ครบถ้วน
        Assert.True(body.SequenceEqual(packet[..body.Length]));
        Assert.True(OmniPacketAuth.Verify(packet, key));
    }

    [Fact]
    public void PacketAuth_TamperedBody_Rejected()
    {
        var key = OmniPacketAuth.DeriveUdpKey("token_abcdef");
        Span<byte> body = stackalloc byte[] { 0x03, 0x01, 0x01, 0x00 };

        Span<byte> packet = stackalloc byte[body.Length + OmniPacketAuth.TrailerSize];
        OmniPacketAuth.AppendTrailer(body, key, packet);

        packet[1] ^= 0xFF; // แก้ 1 บิตของ payload

        Assert.False(OmniPacketAuth.Verify(packet, key));
    }

    [Fact]
    public void PacketAuth_WrongKey_Rejected()
    {
        var keyA = OmniPacketAuth.DeriveUdpKey("token_a");
        var keyB = OmniPacketAuth.DeriveUdpKey("token_b");
        Span<byte> body = stackalloc byte[] { 0x05, 0x00, 0x26, 0x01, 0x00 };

        Span<byte> packet = stackalloc byte[body.Length + OmniPacketAuth.TrailerSize];
        OmniPacketAuth.AppendTrailer(body, keyA, packet);

        Assert.True(OmniPacketAuth.Verify(packet, keyA));
        Assert.False(OmniPacketAuth.Verify(packet, keyB));
    }

    [Fact]
    public void PacketAuth_UdpKey_DeterministicPerToken_AndTruncatedTo16()
    {
        var k1 = OmniPacketAuth.DeriveUdpKey("same");
        var k2 = OmniPacketAuth.DeriveUdpKey("same");
        var k3 = OmniPacketAuth.DeriveUdpKey("other");

        Assert.Equal(32, k1.Length);
        Assert.Equal(k1, k2);
        Assert.NotEqual(k1, k3);
        Assert.Equal(OmniPacketAuth.TrailerSize, 16);
    }

    [Fact]
    public void PacketAuth_CertificateFingerprint_IsLowercaseHexSha256()
    {
        using var ecdsa = ECDsa.Create();
        var req = new CertificateRequest("CN=unit-test", ecdsa, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));

        var fp = OmniPacketAuth.CertificateFingerprint(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));

        Assert.Equal(64, fp.Length);
        Assert.Equal(fp, fp.ToLowerInvariant());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert))).ToLowerInvariant(), fp);
    }

    [Fact]
    public void PacketAuth_SeqLayout_RoundTripAndFreshness()
    {
        var key = OmniPacketAuth.DeriveUdpKey("tok");
        Span<byte> body = stackalloc byte[] { 0x01, 0xFF, 0xF8 };
        Span<byte> packet = stackalloc byte[body.Length + OmniPacketAuth.SeqSize + OmniPacketAuth.TrailerSize];

        ulong seq = 1_760_000_000_000; // UnixMs
        Assert.True(OmniPacketAuth.AppendTrailerWithSeq(body, key, seq, packet));

        Assert.True(body.SequenceEqual(packet[..body.Length]));
        Assert.Equal(seq, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
            packet.Slice(body.Length, OmniPacketAuth.SeqSize)));
        Assert.True(OmniPacketAuth.VerifyWithSeq(packet, key));
    }

    [Fact]
    public void PacketAuth_TamperedSeq_Rejected()
    {
        var key = OmniPacketAuth.DeriveUdpKey("tok");
        Span<byte> body = stackalloc byte[] { 0x01, 0x00, 0x0A };
        Span<byte> packet = stackalloc byte[body.Length + OmniPacketAuth.SeqSize + OmniPacketAuth.TrailerSize];
        OmniPacketAuth.AppendTrailerWithSeq(body, key, 1234567, packet);

        packet[body.Length] ^= 0x01; // แก้ 1 บิตใน seq
        Assert.False(OmniPacketAuth.VerifyWithSeq(packet, key));
    }

    [Fact]
    public void Protocol_ZeroAlloc_WriteMouseRel_MatchesPackedFormat()
    {
        Span<byte> buffer = stackalloc byte[6];
        var written = OmniProtocol.WriteMouseRel(buffer, 120, -85, 0x04);
        var oldArray = OmniProtocol.PackMouseRel(120, -85, 0x04);

        Assert.Equal(6, written);
        Assert.True(buffer.SequenceEqual(oldArray));
    }

    [Fact]
    public void Protocol_ZeroAlloc_WriteMouseButton_MatchesPackedFormat()
    {
        Span<byte> buffer = stackalloc byte[4];
        var written = OmniProtocol.WriteMouseButton(buffer, 1, true, 0x08);
        var oldArray = OmniProtocol.PackMouseButton(1, true, 0x08);

        Assert.Equal(4, written);
        Assert.True(buffer.SequenceEqual(oldArray));
    }

    [Fact]
    public void Protocol_ZeroAlloc_WriteKeyEvent_MatchesPackedFormat()
    {
        Span<byte> buffer = stackalloc byte[5];
        var written = OmniProtocol.WriteKeyEvent(buffer, 133, true, 0x00);
        var oldArray = OmniProtocol.PackKeyEvent(133, true, 0x00);

        Assert.Equal(5, written);
        Assert.True(buffer.SequenceEqual(oldArray));
    }

    [Fact]
    public void Config_Validates_FourDirectionalPositions_And_DeviceTypes()
    {
        var cfg = new OmniConfig
        {
            Nodes = new()
            {
                new NodeConfig { NodeId = "n1", Position = "top", DeviceType = "server" },
                new NodeConfig { NodeId = "n2", Position = "bottom", DeviceType = "laptop" },
                new NodeConfig { NodeId = "n3", Position = "invalid_pos", DeviceType = "invalid_type" },
            }
        };

        cfg.Validate();

        Assert.Equal("top", cfg.Nodes[0].Position);
        Assert.Equal("server", cfg.Nodes[0].DeviceType);

        Assert.Equal("bottom", cfg.Nodes[1].Position);
        Assert.Equal("laptop", cfg.Nodes[1].DeviceType);

        // Falls back to safe defaults
        Assert.Equal("right", cfg.Nodes[2].Position);
        Assert.Equal("laptop", cfg.Nodes[2].DeviceType);
    }

    [Fact]
    public void Config_Validates_CustomPorts_InRange()
    {
        var cfg = new OmniConfig
        {
            KvmTcpPort = 99999,      // Out of bounds
            KvmUdpPort = 500,        // Privileged / invalid
            DiscoveryPort = 24800,
            PlaybackRtpPort = 50044
        };

        cfg.Validate();

        Assert.Equal(24801, cfg.KvmTcpPort); // Clamped back to default
        Assert.Equal(24802, cfg.KvmUdpPort); // Clamped back to default
        Assert.Equal(24800, cfg.DiscoveryPort);
        Assert.Equal(50044, cfg.PlaybackRtpPort);
    }

    [Fact]
    public void NodeConfig_DisplayName_PrefersCustomName_OverHostname_OverDefaultName()
    {
        var node = new NodeConfig
        {
            NodeId = "psl440-ThinkPad-L440",
            Name = "ThinkPad L440 (Ubuntu)",
            Hostname = null,
            CustomName = null
        };

        // 1. Initial fallback: Name
        Assert.Equal("ThinkPad L440 (Ubuntu)", node.DisplayName);

        // 2. Detected hostname from machine handshake
        node.Hostname = "thinkpad-l440-lab";
        Assert.Equal("thinkpad-l440-lab", node.DisplayName);

        // 3. Custom alias assigned by user
        node.SetCustomName("Primary Coding Laptop");
        Assert.Equal("Primary Coding Laptop", node.DisplayName);

        // 4. Reset reverts back to hostname
        node.ResetName();
        Assert.Equal("thinkpad-l440-lab", node.DisplayName);

        // 5. If hostname is also cleared, falls back to Name
        node.Hostname = null;
        Assert.Equal("ThinkPad L440 (Ubuntu)", node.DisplayName);
    }
}
