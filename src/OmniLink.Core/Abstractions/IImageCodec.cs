namespace OmniLink.Core.Abstractions;

/// <summary>
/// v2.0 Platform SPI: ตัวเข้ารหัส/ตรวจภาพของ clipboard — ฝั่ง Windows ใช้ WPF BitmapDecoder,
/// ฝั่ง headless/OS อื่นเริ่มด้วย PassThrough (PNG ผ่านตรง, BMP ไม่แปลง)
/// </summary>
public interface IImageCodec
{
    /// <summary>ตรวจความถูกต้อง + จำกัดขนาด + แปลงเป็น PNG ให้พร้อมส่งข้ามเครื่อง (ผิดพลาด = คืน array ว่าง)</summary>
    byte[] SanitizePng(byte[] rawBytes);

    /// <summary>แปลง BMP/DIB เป็น PNG (ไม่รองรับ = คืน array ว่าง)</summary>
    byte[] ConvertBmpToPng(byte[] bmpBytes);
}

/// <summary>
/// ส่วนที่พกพาได้ของ image codec (ย้ายจาก ImageClipboardCodec ฝั่ง WPF):
/// magic-byte sniffing + base64 + ลิมิตขนาด — ใช้ได้ทุก OS โดยไม่แตะไลบรารีภาพ
/// ตัว decode จริงอยู่ที่ OmniImage.Codec ซึ่ง UI บูตแล้วเสียบของจริง
/// </summary>
public static class OmniImage
{
    public const int MaxImageSizeBytes = 15 * 1024 * 1024; // 15MB limit

    private static readonly byte[] PngMagic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] BmpMagic = new byte[] { 0x42, 0x4D }; // 'B', 'M'

    /// <summary>codec จริงของแพลตฟอร์ม — UI ต้องเสียบตอนบูตก่อนใช้งานจริง</summary>
    public static IImageCodec Codec { get; set; } = PassThroughImageCodec.Instance;

    public static bool IsPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;
        return data[0] == PngMagic[0] &&
               data[1] == PngMagic[1] &&
               data[2] == PngMagic[2] &&
               data[3] == PngMagic[3] &&
               data[4] == PngMagic[4] &&
               data[5] == PngMagic[5] &&
               data[6] == PngMagic[6] &&
               data[7] == PngMagic[7];
    }

    public static bool IsBmp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return false;
        return data[0] == BmpMagic[0] && data[1] == BmpMagic[1];
    }

    public static string ToBase64(byte[] data)
    {
        if (data == null || data.Length == 0) return string.Empty;
        return Convert.ToBase64String(data);
    }

    public static byte[] FromBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return Array.Empty<byte>();
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }
}

/// <summary>codec สำหรับ headless / แพลตฟอร์มที่ไม่มีตัว decode ภาพ: PNG ผ่านตรง, อย่างอื่นปฏิเสธ</summary>
public sealed class PassThroughImageCodec : IImageCodec
{
    public static readonly PassThroughImageCodec Instance = new();

    public byte[] SanitizePng(byte[] rawBytes)
    {
        if (rawBytes == null || rawBytes.Length == 0) return Array.Empty<byte>();
        if (rawBytes.Length > OmniImage.MaxImageSizeBytes)
        {
            OmniLog.Warn($"[image_codec] Image size exceeds limit: {rawBytes.Length} > {OmniImage.MaxImageSizeBytes} bytes");
            return Array.Empty<byte>();
        }
        return OmniImage.IsPng(rawBytes) ? rawBytes : Array.Empty<byte>();
    }

    public byte[] ConvertBmpToPng(byte[] bmpBytes) => Array.Empty<byte>();
}
