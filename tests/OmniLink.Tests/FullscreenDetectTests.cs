using OmniLink.Interop;
using Xunit;
using RECT = OmniLink.Interop.NativeMethods.RECT;

namespace OmniLink.Tests;

public class FullscreenDetectTests
{
    private static RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Fact]
    public void ExactCover_IsFullscreen()
    {
        var mon = R(0, 0, 1920, 1080);
        Assert.True(NativeMethods.IsRectFullScreen(R(0, 0, 1920, 1080), mon));
    }

    [Fact]
    public void BorderlessSlightOverscan_IsFullscreen()
    {
        var mon = R(0, 0, 1920, 1080);
        Assert.True(NativeMethods.IsRectFullScreen(R(-2, -2, 1922, 1082), mon));
        Assert.True(NativeMethods.IsRectFullScreen(R(1, 1, 1919, 1079), mon)); // ใน tol=2
    }

    [Fact]
    public void Maximized_WithTaskbar_IsNotFullscreen()
    {
        // หน้าต่าง maximize ปกติจะไม่ทับ taskbar → สูงไม่ครบ = ไม่นับเป็น fullscreen
        var mon = R(0, 0, 1920, 1080);
        Assert.False(NativeMethods.IsRectFullScreen(R(0, 0, 1920, 1040), mon));
    }

    [Fact]
    public void PartialWindow_IsNotFullscreen()
    {
        var mon = R(0, 0, 1920, 1080);
        Assert.False(NativeMethods.IsRectFullScreen(R(100, 100, 1820, 980), mon));
        Assert.False(NativeMethods.IsRectFullScreen(R(0, 0, 960, 1080), mon));
    }

    [Fact]
    public void SecondaryMonitorCoordinates_Work()
    {
        // จอที่สองวางที่ x=-1920 (ซ้าย) — fullscreen ต้องครอบตามพิกัดจอนั้น
        var mon = R(-1920, 0, 0, 1080);
        Assert.True(NativeMethods.IsRectFullScreen(R(-1920, 0, 0, 1080), mon));
        Assert.False(NativeMethods.IsRectFullScreen(R(-1920, 0, 0, 1040), mon));
    }

    [Fact]
    public void DegenerateRect_IsNotFullscreen()
    {
        var mon = R(0, 0, 1920, 1080);
        Assert.False(NativeMethods.IsRectFullScreen(R(0, 0, 0, 0), mon));
    }
}
