namespace OmniLink.Core;

/// <summary>
/// godclass-G2 fix: สิทธิ์ "engine เดียวของเครื่องนี้" — กัน hostd (autostart) กับ WPF (ui_role=engine)
/// รันพร้อมกันจนเกิด engine war (kick-on-new-connection สลับกันทุก ~2s) · ใช้ named mutex แทนการเช็คพอร์ต
/// (engine เป็น TCP client — ไม่มีพอร์ต TCP ให้เช็ค, และ knock 24800 ตั้ง ReuseAddress ให้ bind ซ้อนได้)
/// </summary>
public static class EngineSingleton
{
    public const string MutexName = @"Local\OmniLink-Engine-Singleton";

    private static Mutex? _held;

    /// <summary>พยายามครองสิทธิ์ engine เดียวของเครื่อง — false = มี engine อื่น (hostd/อีก WPF) รันอยู่</summary>
    public static bool TryAcquire()
    {
        var m = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            _held = m;
            return true;
        }
        m.Dispose(); // มีเจ้าของอยู่แล้ว — ไม่ถือต่อ
        return false;
    }

    public static void Release()
    {
        try { _held?.ReleaseMutex(); } catch { }
        try { _held?.Dispose(); } catch { }
        _held = null;
    }
}
