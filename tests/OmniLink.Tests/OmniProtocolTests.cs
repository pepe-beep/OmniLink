using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// ทดสอบตารางแปลง keycode และ binary packing — INV-1 ของ REMEDIATION-PLAN
/// ค่าทุกตัวอ้างอิงจาก audit 2026-10-17 และ protocol ที่ agent ฝั่ง Linux ใช้จริง
/// </summary>
public class OmniProtocolTests
{
    [Theory]
    [InlineData(0xA0, 0, false, 50)]   // VK_LSHIFT
    [InlineData(0xA1, 0, false, 62)]   // VK_RSHIFT
    [InlineData(0xA2, 0, false, 37)]   // VK_LCONTROL
    [InlineData(0xA3, 0, false, 105)]  // VK_RCONTROL
    [InlineData(0xA4, 0, false, 64)]   // VK_LMENU
    [InlineData(0xA5, 0, false, 108)]  // VK_RMENU
    [InlineData(0x5B, 0, false, 133)]  // VK_LWIN
    [InlineData(0x5C, 0, false, 134)]  // VK_RWIN
    [InlineData(0x13, 0, false, 127)]  // VK_PAUSE
    [InlineData(0x1D, 0x1D, true, 105)]  // extended Control_R
    [InlineData(0x38, 0x38, true, 108)]  // extended Alt_R
    [InlineData(0x1C, 0x1C, true, 104)]  // extended KP_Enter
    [InlineData(0x35, 0x35, true, 106)]  // extended KP_Divide
    [InlineData(0x47, 0x47, true, 110)]  // extended Home
    [InlineData(0x53, 0x53, true, 119)]  // extended Delete
    [InlineData(0x00, 30, false, 38)]  // scan 30 ('Q' row) → evdev 38
    [InlineData(0x00, 1, false, 9)]    // scan 1 (Esc) → evdev 9
    [InlineData(0x00, 88, false, 96)]  // scan 88 ขอบบน
    [InlineData(0x00, 89, false, 0)]   // scan 89 เกินขอบ → 0 (ถูกละทิ้ง)
    [InlineData(0x00, 0, false, 0)]    // scan 0 → 0
    public void Win32ToX11Keycode_ShouldMap(uint vk, uint scan, bool ext, int expected)
    {
        Assert.Equal(expected, OmniProtocol.Win32ToX11Keycode(vk, scan, ext));
    }

    [Fact]
    public void PackMouseRel_ShouldBeBigEndian6Bytes()
    {
        var b = OmniProtocol.PackMouseRel(300, -300, 0x03);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x2C, 0xFE, 0xD4, 0x03 }, b);
    }

    [Fact]
    public void PackMouseRel_ShouldClampToShort()
    {
        var b = OmniProtocol.PackMouseRel(100000, -100000, 0);
        Assert.Equal(0x01, b[0]);
        Assert.Equal(0x7F, b[1]); // 32767 = 0x7FFF
        Assert.Equal(0xFF, b[2]);
        Assert.Equal(0x80, b[3]); // -32767 = 0x8001
        Assert.Equal(0x01, b[4]);
    }

    [Fact]
    public void PackKeyEvent_ShouldFollowBHBBLayout()
    {
        var b = OmniProtocol.PackKeyEvent(38, true, 1);
        Assert.Equal(new byte[] { 0x05, 0x00, 0x26, 0x01, 0x01 }, b);
    }

    [Fact]
    public void PackMouseButton_ShouldBe4Bytes()
    {
        var b = OmniProtocol.PackMouseButton(3, true, 0x10);
        Assert.Equal(new byte[] { 0x03, 3, 1, 0x10 }, b);
    }

    [Fact]
    public void PackMouseWheel_ShouldBeBigEndian6Bytes()
    {
        var b = OmniProtocol.PackMouseWheel(0, 120, 0);
        Assert.Equal(new byte[] { 0x04, 0x00, 0x00, 0x00, 0x78, 0x00 }, b);
    }

    [Fact]
    public void PackSetLang_ShouldNormalizeGroup()
    {
        Assert.Equal(new byte[] { 0x06, 1, 1 }, OmniProtocol.PackSetLang(1));
        Assert.Equal(new byte[] { 0x06, 0, 1 }, OmniProtocol.PackSetLang(0));
        Assert.Equal(new byte[] { 0x06, 0, 1 }, OmniProtocol.PackSetLang(99)); // normalize อื่นๆ → EN
        Assert.Equal(new byte[] { 0x06, 1, 0 }, OmniProtocol.PackSetLang(1, calibrate: false));
    }

    [Fact]
    public void PackReleaseAll_ShouldBe2Bytes()
    {
        Assert.Equal(new byte[] { 0x07, 0x00 }, OmniProtocol.PackReleaseAll());
    }

    [Fact]
    public void EncodeTcpJson_ShouldPrefixBigEndianLength()
    {
        var b = OmniProtocol.EncodeTcpJson(new { type = "ping" });
        var len = (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        Assert.Equal(b.Length - 4, len);
        Assert.Equal((byte)'{', b[4]); // เริ่มด้วย JSON
    }
}
