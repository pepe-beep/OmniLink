using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>v1.9 C2: แม็ป modifier ต่อเครื่อง — remap ฝั่ง host ต้องแม่นและไม่แตะคีย์ปกติ/keyspace ไทย (INV-2)</summary>
public class OmniModifiersTests
{
    [Theory]
    [InlineData(50, OmniModifiers.Shift)]   // LShift
    [InlineData(62, OmniModifiers.Shift)]   // RShift
    [InlineData(37, OmniModifiers.Ctrl)]    // LCtrl
    [InlineData(105, OmniModifiers.Ctrl)]   // RCtrl
    [InlineData(64, OmniModifiers.Alt)]     // LAlt
    [InlineData(108, OmniModifiers.AltGr)]  // RAlt = AltGr ตามธรรมเนียม X11
    [InlineData(133, OmniModifiers.Super)]  // LSuper
    [InlineData(134, OmniModifiers.Super)]  // RSuper
    public void SlotOf_IdentifiesModifierKeycodes(int keycode, string expectedSlot)
    {
        Assert.Equal(expectedSlot, OmniModifiers.SlotOf(keycode));
    }

    [Fact]
    public void SlotOf_NonModifier_ReturnsNull()
    {
        Assert.Null(OmniModifiers.SlotOf(38)); // 'a'
        // INV-2: keyspace ไทย เช่น XK_Thai_fofan = 0x0DBF (3519) ไม่ใช่ modifier — ต้องผ่านไม่ถูกจับ
        Assert.Null(OmniModifiers.SlotOf(0x0DBF));
    }

    [Fact]
    public void Remap_EmptyOrAbsentMap_ReturnsOriginal()
    {
        Assert.Equal(64, OmniModifiers.RemapKeycode(64, null));
        Assert.Equal(64, OmniModifiers.RemapKeycode(64, new Dictionary<string, string>()));
    }

    [Fact]
    public void Remap_AltToAltGr_LAltBecomesRAltKeycode()
    {
        var map = new Dictionary<string, string> { [OmniModifiers.Alt] = OmniModifiers.AltGr };
        Assert.Equal(OmniModifiers.RAlt, OmniModifiers.RemapKeycode(OmniModifiers.LAlt, map));
        // ปุ่มอื่นที่ไม่ใช่ alt ยังเหมือนเดิม
        Assert.Equal(OmniModifiers.LCtrl, OmniModifiers.RemapKeycode(OmniModifiers.LCtrl, map));
    }

    [Fact]
    public void Remap_SameSlot_ReturnsOriginal()
    {
        var map = new Dictionary<string, string> { [OmniModifiers.Ctrl] = OmniModifiers.Ctrl };
        Assert.Equal(OmniModifiers.LCtrl, OmniModifiers.RemapKeycode(OmniModifiers.LCtrl, map));
    }

    /// <summary>target "none" = กลืนปุ่ม (คืน 0 → caller ข้ามส่ง)</summary>
    [Fact]
    public void Remap_ToNone_ReturnsZero()
    {
        var map = new Dictionary<string, string> { [OmniModifiers.Super] = OmniModifiers.None };
        Assert.Equal(0, OmniModifiers.RemapKeycode(OmniModifiers.LSuper, map));
        Assert.Equal(0, OmniModifiers.RemapKeycode(OmniModifiers.RSuper, map));
    }

    /// <summary>หัวใจของ INV-2/11: คีย์ปกติ+keyspace ไทยห้ามถูก remap แม้ map เต็มจอ</summary>
    [Fact]
    public void Remap_NeverTouchesNonModifierKeys_IncludingThaiKeyspace()
    {
        var map = OmniModifiers.Sources.ToDictionary(s => s, _ => OmniModifiers.None);
        Assert.Equal(38, OmniModifiers.RemapKeycode(38, map));          // 'a'
        Assert.Equal(0x0DBF, OmniModifiers.RemapKeycode(0x0DBF, map));  // XK_Thai_fofan (INV-2)
        Assert.Equal(0x0DBA, OmniModifiers.RemapKeycode(0x0DBA, map));  // Thai range
    }

    [Fact]
    public void IsValidTarget_RejectsUnknownSlot()
    {
        Assert.True(OmniModifiers.IsValidTarget(OmniModifiers.None));
        Assert.True(OmniModifiers.IsValidTarget(OmniModifiers.AltGr));
        Assert.False(OmniModifiers.IsValidTarget("banana"));
        Assert.False(OmniModifiers.IsValidTarget(null));
    }
}

/// <summary>v1.9 C1: parser + conflict ของสูตรลัด</summary>
public class HotkeyKeyTests
{
    [Fact]
    public void Parse_CtrlAltF1_ProducesCanonicalAndMask()
    {
        var ok = HotkeyKey.TryParse("Ctrl+Alt+F1", out var vk, out var mask, out var canonical);
        Assert.True(ok);
        Assert.Equal(0x70u, vk);
        Assert.Equal(OmniProtocol.ModLCtrl | OmniProtocol.ModRCtrl | OmniProtocol.ModLAlt | OmniProtocol.ModRAlt, mask);
        Assert.Equal("Ctrl+Alt+F1", canonical);
    }

    [Fact]
    public void Parse_NormalizesOrderAndCase()
    {
        Assert.True(HotkeyKey.TryParse("shift+alt+K", out _, out _, out var canonical));
        Assert.Equal("Alt+Shift+K", canonical); // ลำดับมาตรฐาน Ctrl+Alt+Shift+key
    }

    [Theory]
    [InlineData("K")]             // ไม่มี modifier — ห้าม (กันแย่งปุ่มพิมพ์ธรรมดา)
    [InlineData("Ctrl")]          // modifier ล้วน
    [InlineData("Ctrl+Win+K")]    // v1 ไม่รับ Win (ชนระบบสลับภาษา)
    [InlineData("Ctrl+F1+F2")]    // trigger สองปุ่ม
    [InlineData("Ctrl+")]         // ปุ่มหาย
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RejectsInvalidForms(string? input)
    {
        Assert.False(HotkeyKey.TryParse(input, out _, out _, out _));
    }

    [Fact]
    public void Parse_AcceptsLettersDigitsAndNamedKeys()
    {
        Assert.True(HotkeyKey.TryParse("Ctrl+Q", out var vkQ, out _, out _));
        Assert.Equal(0x51u, vkQ);
        Assert.True(HotkeyKey.TryParse("Alt+7", out var vk7, out _, out _));
        Assert.Equal(0x37u, vk7);
        Assert.True(HotkeyKey.TryParse("Ctrl+Shift+Space", out var vkSpace, out _, out _));
        Assert.Equal(0x20u, vkSpace);
        Assert.True(HotkeyKey.TryParse("Ctrl+F12", out var vkF12, out _, out _));
        Assert.Equal(0x7Bu, vkF12);
    }

    [Fact]
    public void Matches_RequiresAllRuleModifiers_HonorsLeftRightVariants()
    {
        var ctrlAlt = (OmniProtocol.ModLCtrl | OmniProtocol.ModRCtrl) | (OmniProtocol.ModLAlt | OmniProtocol.ModRAlt);
        Assert.True(HotkeyKey.Matches(ctrlAlt, OmniProtocol.ModLCtrl | OmniProtocol.ModLAlt)); // ฝั่งซ้ายก็ตรง
        Assert.True(HotkeyKey.Matches(ctrlAlt, OmniProtocol.ModRCtrl | OmniProtocol.ModRAlt)); // ฝั่งขวาก็ตรง
        Assert.False(HotkeyKey.Matches(ctrlAlt, OmniProtocol.ModLCtrl));                       // ขาด Alt
        // กดเกิน (มี Shift เพิ่ม) ยังถือว่าตรงกฎ — กฎต้องการแค่ Ctrl+Alt ครบ
        Assert.True(HotkeyKey.Matches(ctrlAlt, OmniProtocol.ModLCtrl | OmniProtocol.ModLAlt | OmniProtocol.ModLShift));
    }

    [Fact]
    public void FindConflicts_DetectsDuplicatesAndReservedKeys()
    {
        var rules = new List<HotkeyRuleConfig>
        {
            new() { Key = "Ctrl+Alt+F1" },
            new() { Key = "Ctrl+Alt+F1" },                    // ซ้ำ
            new() { Key = "Ctrl+Shift+ScrollLock".Replace("ScrollLock", "") + "Pause" }, // ปกติ (ไม่ชน)
            new() { Key = "Ctrl+Alt+Backspace" },             // ชนฉุกเฉิน
        };
        var conflicts = HotkeyKey.FindConflicts(rules);
        Assert.Contains("duplicate:Ctrl+Alt+F1", conflicts);
        Assert.Contains("reserved.emergency:Ctrl+Alt+Backspace", conflicts);
        Assert.DoesNotContain(conflicts, c => c.Contains("Pause"));
    }

    [Fact]
    public void VkToName_RoundTripsWithParse()
    {
        foreach (var name in new[] { "K", "F12", "Space", "PageUp" })
        {
            Assert.True(HotkeyKey.TryParse("Ctrl+" + name, out var vk, out _, out var canonical));
            Assert.Equal(name, HotkeyKey.VkToName(vk));
            Assert.EndsWith(name, canonical, StringComparison.Ordinal);
        }
    }
}
