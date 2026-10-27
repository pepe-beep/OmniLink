using System.Security.Cryptography;
using System.Text;

namespace OmniLink.Core.Security;

/// <summary>
/// OmniPacketAuth — ยืนยันตัวตน packet UDP ด้วย HMAC-SHA256 (truncated 16 ไบต์)
/// (v1.6 hardening: แทนที่ OmniCryptoShield ที่เป็น dead code — ตาม AUDIT-REPORT-2026-10-22 SV-H3)
/// ทำไม HMAC ไม่ใช่ AES-GCM บน UDP: input events ไม่มีความลับ (พิกัดเมาส์) แต่ต้องกันการปลอม
/// โดยคนที่ไม่รู้ token · HMAC ใช้ hashlib เท่านั้นฝั่ง Linux (ศูนย์ dependency) และความลับจริง
/// (token/ไฟล์/คลิปบอร์ด/PIN) เดินทางผ่าน TCP ที่เข้ารหัส TLS แล้ว
/// กัน nonce-reuse แบบ SV-H4 โดยสถานะ: HMAC ไม่มีสถานะ จึงไม่มี nonce ให้ชน
/// </summary>
public static class OmniPacketAuth
{
    public const int TrailerSize = 16;
    private const string KeyDomain = "omnilink-udp-v2|";

    /// <summary>คีย์ต่อ session จาก auth token — สองฝั่ง derive เหมือนกันจาก token เดียวกัน</summary>
    public static byte[] DeriveUdpKey(string? token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(KeyDomain + (token ?? "")));

    /// <summary>
    /// เขียน body ตามด้วย HMAC trailer ลงใน dest (dest.Length ต้อง >= body.Length + TrailerSize)
    /// หมายเหตุ: HMACSHA256.TryHashData ไม่ truncate ให้ — ต้อง hash เต็ม 32 ไบต์แล้วตัดเอง
    /// </summary>
    public static bool AppendTrailer(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key, Span<byte> dest)
    {
        if (dest.Length < body.Length + TrailerSize) return false;
        Span<byte> mac = stackalloc byte[32];
        if (!HMACSHA256.TryHashData(key, body, mac, out var written) || written != 32)
        {
            return false;
        }
        mac[..TrailerSize].CopyTo(dest.Slice(body.Length, TrailerSize));
        body.CopyTo(dest);
        return true;
    }

    /// <summary>ตรวจ packet ที่มี trailer — ผ่านเงื่อนไข: ยาวพอ + HMAC ตรง (constant-time)</summary>
    public static bool Verify(ReadOnlySpan<byte> packetWithTrailer, ReadOnlySpan<byte> key)
    {
        if (packetWithTrailer.Length <= TrailerSize) return false;
        var body = packetWithTrailer[..^TrailerSize];
        var mac = packetWithTrailer[^TrailerSize..];
        Span<byte> expected = stackalloc byte[32];
        if (!HMACSHA256.TryHashData(key, body, expected, out var written) || written != 32)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expected[..TrailerSize], mac);
    }

    /// <summary>คำนวณ fingerprint (SHA-256, lowercase hex ไม่มีตัวคั่น) จาก certificate DER สำหรับ TOFU pin</summary>
    public static string CertificateFingerprint(byte[] derCert) =>
        Convert.ToHexString(SHA256.HashData(derCert)).ToLowerInvariant();

    /// <summary>SV-L1: ความยาว sequence 8 ไบต์ (BE) ที่แทรกระหว่าง body กับ HMAC — วัตถุประสงค์ anti-replay</summary>
    public const int SeqSize = 8;

    /// <summary>
    /// v1.8 SV-L1: เขียน body + seq(8B BE) ตามด้วย HMAC(body+seq) 16 ไบต์ — anti-replay โดยไม่พึ่งนาฬิกา
    /// (seq = UnixMs seed + per-session counter → โตมอโนโทนิกแม้ข้ามการรีสตาร์ต)
    /// </summary>
    public static bool AppendTrailerWithSeq(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key, ulong seq, Span<byte> dest)
    {
        if (dest.Length < body.Length + SeqSize + TrailerSize) return false;
        body.CopyTo(dest);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(dest.Slice(body.Length, SeqSize), seq);
        var macSource = dest.Slice(0, body.Length + SeqSize);
        Span<byte> mac = stackalloc byte[32];
        if (!HMACSHA256.TryHashData(key, macSource, mac, out var written) || written != 32)
        {
            return false;
        }
        mac[..TrailerSize].CopyTo(dest.Slice(body.Length + SeqSize, TrailerSize));
        return true;
    }

    /// <summary>ตรวจ packet v2 (มี seq) — HMAC ครอบ body+seq ครบ</summary>
    public static bool VerifyWithSeq(ReadOnlySpan<byte> packetWithSeqAndMac, ReadOnlySpan<byte> key)
    {
        if (packetWithSeqAndMac.Length <= SeqSize + TrailerSize) return false;
        var macSource = packetWithSeqAndMac[..^TrailerSize];
        var mac = packetWithSeqAndMac[^TrailerSize..];
        Span<byte> expected = stackalloc byte[32];
        if (!HMACSHA256.TryHashData(key, macSource, expected, out var written) || written != 32)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expected[..TrailerSize], mac);
    }
}
