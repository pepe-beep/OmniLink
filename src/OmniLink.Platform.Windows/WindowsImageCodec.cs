using OmniLink.Core.Abstractions;

namespace OmniLink.Platform.Windows;

/// <summary>
/// v2.0: image codec ของ Windows — ตัวจริงคือ ImageClipboardCodec (WPF BitmapDecoder/PngBitmapEncoder)
/// ย้ายมาจาก Core เพื่อให้แกนกลางใช้ได้ทั้ง headless · พฤติกรรมเดิม 100% ผ่าน OmniImage.Codec
/// </summary>
public sealed class WindowsImageCodec : IImageCodec
{
    public byte[] SanitizePng(byte[] rawBytes) => OmniLink.Core.Clipboard.ImageClipboardCodec.SanitizePng(rawBytes);

    public byte[] ConvertBmpToPng(byte[] bmpBytes) => OmniLink.Core.Clipboard.ImageClipboardCodec.ConvertBmpToPng(bmpBytes);
}
