using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

public class AltShiftTriggerTests
{
    [Theory]
    [InlineData(0xA0u, true, false)]   // LShift กดต่อจาก Alt
    [InlineData(0xA1u, true, false)]   // RShift ต่อจาก Alt
    [InlineData(0x10u, true, false)]   // generic Shift ต่อจาก Alt
    [InlineData(0xA4u, false, true)]   // LAlt กดต่อจาก Shift
    [InlineData(0xA5u, false, true)]   // RAlt ต่อจาก Shift
    [InlineData(0x12u, false, true)]   // generic Alt ต่อจาก Shift
    public void SecondModifierOfPair_Triggers(uint vk, bool altHeld, bool shiftHeld)
    {
        Assert.True(KvmHostService.IsAltShiftLanguageTrigger(vk, altHeld, shiftHeld));
    }

    [Theory]
    [InlineData(0xA0u, false, false)]  // Shift เดี่ยวๆ ไม่มี Alt
    [InlineData(0xA4u, false, false)]  // Alt เดี่ยวๆ ไม่มี Shift
    [InlineData(0x41u, true, true)]    // ตัวอักษร A แม้ถือครบ — ไม่ใช่ modifier
    [InlineData(0x11u, true, true)]    // Ctrl (แม้ถือ Alt+Shift อยู่) — ไม่ใช่คู่เป้าหมาย
    [InlineData(0xA2u, true, false)]   // LCtrl กดต่อจาก Alt เพียงตัวเดียว — ไม่ trigger
    public void NonTriggerCases_DoNotTrigger(uint vk, bool altHeld, bool shiftHeld)
    {
        Assert.False(KvmHostService.IsAltShiftLanguageTrigger(vk, altHeld, shiftHeld));
    }
}
