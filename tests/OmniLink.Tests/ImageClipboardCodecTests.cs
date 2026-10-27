using System;
using OmniLink.Core.Clipboard;
using Xunit;

namespace OmniLink.Tests;

public class ImageClipboardCodecTests
{
    private static readonly byte[] Valid1x1Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public void IsPng_ValidPng_ReturnsTrue()
    {
        Assert.True(ImageClipboardCodec.IsPng(Valid1x1Png));
    }

    [Fact]
    public void IsPng_InvalidData_ReturnsFalse()
    {
        var invalid = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        Assert.False(ImageClipboardCodec.IsPng(invalid));

        var tooShort = new byte[] { 0x89, 0x50, 0x4E };
        Assert.False(ImageClipboardCodec.IsPng(tooShort));
    }

    [Fact]
    public void IsBmp_ValidBmpHeader_ReturnsTrue()
    {
        var bmpHeader = new byte[] { 0x42, 0x4D, 0x00, 0x00 };
        Assert.True(ImageClipboardCodec.IsBmp(bmpHeader));
    }

    [Fact]
    public void IsBmp_NonBmp_ReturnsFalse()
    {
        Assert.False(ImageClipboardCodec.IsBmp(Valid1x1Png));
    }

    [Fact]
    public void SanitizePng_ValidPng_ReturnsSameBytes()
    {
        var sanitized = ImageClipboardCodec.SanitizePng(Valid1x1Png);
        Assert.Equal(Valid1x1Png, sanitized);
    }

    [Fact]
    public void SanitizePng_Exceeds15MB_ReturnsEmpty()
    {
        // 15MB + 1 byte dummy array
        var oversized = new byte[ImageClipboardCodec.MaxImageSizeBytes + 1];
        oversized[0] = 0x89; oversized[1] = 0x50; oversized[2] = 0x4E; oversized[3] = 0x47;

        var result = ImageClipboardCodec.SanitizePng(oversized);
        Assert.Empty(result);
    }

    [Fact]
    public void Base64_Roundtrip_ShouldMatch()
    {
        var b64 = ImageClipboardCodec.ToBase64(Valid1x1Png);
        Assert.False(string.IsNullOrWhiteSpace(b64));

        var decoded = ImageClipboardCodec.FromBase64(b64);
        Assert.Equal(Valid1x1Png, decoded);
    }
}
