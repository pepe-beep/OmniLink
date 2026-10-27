using System.Runtime.InteropServices;

namespace OmniLink.Core;

/// <summary>
/// v1.9 C4: กันเครื่องเข้าโหมด sleep ขณะเป็น KVM host (Settings → General)
/// กับดัก: SetThreadExecutionState เป็น per-thread — ต้องมีเธรดค้างไว้ตลอดที่เปิดใช้
/// (ยิงครั้งเดียวจาก UI thread แล้ว thread ตาย = ค่าหายทันที) เธรดแบบ background จึง sleep วนรอ
/// </summary>
public static class PreventSleepService
{
    private const uint EsContinuous = 0x8000_0000;
    private const uint EsSystemRequired = 0x0000_0001;

    private static Thread? _keeper;
    private static volatile bool _stop;

    public static bool IsEnabled => _keeper is not null && _keeper.IsAlive;

    public static void SetEnabled(bool enabled)
    {
        Stop();
        if (enabled) Start();
        OmniLog.Write("power", "prevent_sleep = " + (enabled ? "ON (ES_CONTINUOUS|ES_SYSTEM_REQUIRED on keeper thread)" : "OFF"));
    }

    private static void Start()
    {
        _stop = false;
        _keeper = new Thread(() =>
        {
            try
            {
                SetThreadExecutionState(EsContinuous | EsSystemRequired);
                while (!_stop)
                {
                    Thread.Sleep(1000);
                }
            }
            finally
            {
                SetThreadExecutionState(EsContinuous); // ปล่อย flag ค้างไว้ให้ระบบกลับมา sleep ได้ตามปกติ
            }
        })
        {
            IsBackground = true,
            Name = "OmniLink-PreventSleep",
        };
        _keeper.Start();
    }

    private static void Stop()
    {
        _stop = true;
        _keeper = null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
