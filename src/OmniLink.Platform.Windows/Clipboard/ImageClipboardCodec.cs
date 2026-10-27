using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace OmniLink.Core.Clipboard;

/// <summary>
/// Image Clipboard Codec (Pure Fabrication / GoF Adapter)
/// รับและคืนค่าเฉพาะ byte[] ล้วน เพื่อป้องกัน WPF Types หลุดออกนอกคลาส (Low Coupling)
/// รองรับการตรวจจับ PNG Magic Bytes, แปลง BMP/DIB เป็น PNG, และจำกัดขนาดป้องกัน Memory Spike
/// </summary>
public static class ImageClipboardCodec
{
    public const int MaxImageSizeBytes = 15 * 1024 * 1024; // 15MB limit

    private static readonly byte[] PngMagic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] BmpMagic = new byte[] { 0x42, 0x4D }; // 'B', 'M'

    /// <summary>
    /// ตรวจสอบ Magic Bytes ของ PNG: 89 50 4E 47 0D 0A 1A 0A
    /// </summary>
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

    /// <summary>
    /// ตรวจสอบ Magic Bytes ของ BMP: 'B' 'M' (0x42, 0x4D)
    /// </summary>
    public static bool IsBmp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return false;
        return data[0] == BmpMagic[0] && data[1] == BmpMagic[1];
    }

    /// <summary>
    /// แปลงข้อมูลรูปภาพ BMP / DIB ให้เป็น PNG ไบต์อาเรย์
    /// </summary>
    public static byte[] ConvertBmpToPng(byte[] bmpBytes)
    {
        if (bmpBytes == null || bmpBytes.Length < 2)
        {
            return Array.Empty<byte>();
        }

        try
        {
            using var inStream = new MemoryStream(bmpBytes);
            var decoder = BitmapDecoder.Create(inStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return Array.Empty<byte>();
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(decoder.Frames[0]);

            using var outStream = new MemoryStream();
            encoder.Save(outStream);
            return outStream.ToArray();
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[image_codec] ConvertBmpToPng failed: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// ตรวจสอบความถูกต้องและสุขอนามัยของรูปภาพไบต์:
    /// 1. ป้องกัน Memory Spike โดยจำกัดขนาดไม่เกิน 15MB
    /// 2. หากเป็น PNG และถูกต้องอยู่แล้ว ให้ส่งคืน
    /// 3. หากเป็น BMP ให้แปลงเป็น PNG
    /// </summary>
    public static byte[] SanitizePng(byte[] rawBytes)
    {
        if (rawBytes == null || rawBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        if (rawBytes.Length > MaxImageSizeBytes)
        {
            OmniLog.Warn($"[image_codec] Image size exceeds limit: {rawBytes.Length} > {MaxImageSizeBytes} bytes");
            return Array.Empty<byte>();
        }

        if (IsPng(rawBytes))
        {
            return rawBytes;
        }

        if (IsBmp(rawBytes))
        {
            return ConvertBmpToPng(rawBytes);
        }

        // ลองพยายาม decode ผ่าน BitmapDecoder เผื่อ format อื่นๆ แล้ว encode กลับเป็น PNG
        try
        {
            using var inStream = new MemoryStream(rawBytes);
            var decoder = BitmapDecoder.Create(inStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(decoder.Frames[0]);
                using var outStream = new MemoryStream();
                encoder.Save(outStream);
                return outStream.ToArray();
            }
        }
        catch
        {
        }

        return Array.Empty<byte>();
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
