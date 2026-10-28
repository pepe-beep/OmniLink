using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Strategies;

namespace OmniLink.Tests.TestDoubles;

/// <summary>GroupA-P: gateway จำลองที่คืนพิกัดเคอร์เซอร์ที่ตั้งได้ — ใช้ทดสอบ TryPollEdgePosition โดยไม่ต้องมี hook จริง</summary>
public sealed class FakePollGateway : IHostInputGateway
{
    private readonly object _lock = new();
    private int _x;
    private int _y;

    public FakePollGateway(int x, int y) { _x = x; _y = y; }

    public void SetPosition(int x, int y) { lock (_lock) { _x = x; _y = y; } }

    public (int X, int Y) Position { get { lock (_lock) return (_x, _y); } }

    public void Start(Func<KeyHookData, bool>? onKey, Func<MouseHookData, bool> onMouse) { }
    public void Stop() { }
    public void Warmup() { }
    public void RunHealthCheck() { }

    public bool GetCursorPos(out int x, out int y)
    {
        lock (_lock) { x = _x; y = _y; }
        return true;
    }

    public void SetCursorPos(int x, int y) => SetPosition(x, y);
    public ScreenBounds[] GetMonitors() => new[] { new ScreenBounds(0, 0, 1920, 1080) };
    public long GetForegroundToken() => 0;
    public int GetForegroundLangGroup() => 0;
    public void RequestLangChange(int group) { }
    public bool IsForegroundFullscreen() => false;
    public string DescribeForegroundApp() => "(fake-poll-gateway)";
}
