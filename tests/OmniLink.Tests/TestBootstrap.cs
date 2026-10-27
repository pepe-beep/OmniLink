using System.Runtime.CompilerServices;
using OmniLink.Core;

namespace OmniLink.Tests;

/// <summary>A4: ปิด log ของ production ระหว่างรัน tests — กันคำเตือน config จาก tests ปนไฟล์ log จริง</summary>
internal static class TestBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        OmniLog.Silence();
    }
}
