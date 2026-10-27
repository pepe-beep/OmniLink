using System;
using System.Collections.Generic;

namespace OmniLink.Core.Strategies;

public enum ScreenEdge
{
    Left,
    Right,
    Top,
    Bottom
}

/// <summary>
/// Domain Model (CP353002): พิกัดขอบเขตของหน้าจอแบบบริสุทธิ์ ไม่ผูกติดกับ Win32 RECT
/// </summary>
public readonly record struct ScreenBounds(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Strategy Pattern (GoF Behavioral / CP353002):
/// อินเทอร์เฟซสำหรับกำหนดพฤติกรรมการตัดสินใจข้ามขอบจอ (Edge Switching Physics)
/// รองรับการสลับอัลกอริทึมได้ขณะ Runtime ตาม Open/Closed Principle (OCP)
/// </summary>
public interface IEdgeSwitchStrategy
{
    string Name { get; }
    bool ShouldSwitch(int cursorX, int cursorY, ScreenEdge edge, ScreenBounds monitor, OmniConfig config, long nowMs = 0);
    void Reset();
}

/// <summary>
/// Helper ร่วมสำหรับตรวจสอบ Corner Deadzone (Pure Fabrication)
/// </summary>
public static class EdgeStrategyHelper
{
    public static bool IsInCornerDeadzone(int cursorX, int cursorY, ScreenBounds mon, OmniConfig config)
    {
        var radius = config.CornerSize;
        if (radius <= 0) return false;

        // Top-Left
        if (config.CornerTopLeft && cursorX <= mon.Left + radius && cursorY <= mon.Top + radius)
            return true;

        // Top-Right
        if (config.CornerTopRight && cursorX >= mon.Right - radius && cursorY <= mon.Top + radius)
            return true;

        // Bottom-Left
        if (config.CornerBottomLeft && cursorX <= mon.Left + radius && cursorY >= mon.Bottom - radius)
            return true;

        // Bottom-Right
        if (config.CornerBottomRight && cursorX >= mon.Right - radius && cursorY >= mon.Bottom - radius)
            return true;

        return false;
    }
}

/// <summary>
/// 1. Instant Edge Strategy: สลับหน้าจอทันทีเมื่อเคอร์เซอร์ชนขอบ (SwitchDelayMs == 0)
/// </summary>
public sealed class InstantEdgeStrategy : IEdgeSwitchStrategy
{
    public string Name => "Instant";

    public bool ShouldSwitch(int cursorX, int cursorY, ScreenEdge edge, ScreenBounds monitor, OmniConfig config, long nowMs = 0)
    {
        if (EdgeStrategyHelper.IsInCornerDeadzone(cursorX, cursorY, monitor, config))
        {
            return false;
        }

        return edge switch
        {
            ScreenEdge.Right => cursorX >= monitor.Right - 4,
            ScreenEdge.Left => cursorX <= monitor.Left + 4,
            ScreenEdge.Top => cursorY <= monitor.Top + 4,
            ScreenEdge.Bottom => cursorY >= monitor.Bottom - 4,
            _ => false
        };
    }

    public void Reset() { }
}

/// <summary>
/// 2. Dwell Delay Strategy: ต้องดันเคอร์เซอร์แช่ไว้ที่ขอบจอต่อเนื่องอย่างน้อย SwitchDelayMs
/// เพื่อป้องกันการสลับจอโดยไม่ตั้งใจขณะทำงานใกล้ขอบ
/// </summary>
public sealed class DwellDelayEdgeStrategy : IEdgeSwitchStrategy
{
    public string Name => "DwellDelay";

    private ScreenEdge? _activeEdge;
    private long _dwellStartTick;

    public bool ShouldSwitch(int cursorX, int cursorY, ScreenEdge edge, ScreenBounds monitor, OmniConfig config, long nowMs = 0)
    {
        if (EdgeStrategyHelper.IsInCornerDeadzone(cursorX, cursorY, monitor, config))
        {
            Reset();
            return false;
        }

        var onEdge = edge switch
        {
            ScreenEdge.Right => cursorX >= monitor.Right - 4,
            ScreenEdge.Left => cursorX <= monitor.Left + 4,
            ScreenEdge.Top => cursorY <= monitor.Top + 4,
            ScreenEdge.Bottom => cursorY >= monitor.Bottom - 4,
            _ => false
        };

        if (!onEdge)
        {
            Reset();
            return false;
        }

        var currentMs = nowMs > 0 ? nowMs : Environment.TickCount64;

        if (_activeEdge != edge)
        {
            _activeEdge = edge;
            _dwellStartTick = currentMs;
            return false;
        }

        var elapsed = currentMs - _dwellStartTick;
        if (elapsed >= config.SwitchDelayMs)
        {
            Reset();
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _activeEdge = null;
        _dwellStartTick = 0;
    }
}

/// <summary>
/// 3. Double Tap Strategy: ต้องดันเคอร์เซอร์ชนขอบ 2 ครั้งภายในระยะเวลา DoubleTapMs
/// นิยมใช้สำหรับเกมมิ่งเพื่อไม่ให้เมาส์หลุดจอ
/// </summary>
public sealed class DoubleTapEdgeStrategy : IEdgeSwitchStrategy
{
    public string Name => "DoubleTap";

    private ScreenEdge? _lastEdge;
    private long _lastTapTick;
    private bool _hasLeftEdge;

    public bool ShouldSwitch(int cursorX, int cursorY, ScreenEdge edge, ScreenBounds monitor, OmniConfig config, long nowMs = 0)
    {
        if (EdgeStrategyHelper.IsInCornerDeadzone(cursorX, cursorY, monitor, config))
        {
            Reset();
            return false;
        }

        var onEdge = edge switch
        {
            ScreenEdge.Right => cursorX >= monitor.Right - 4,
            ScreenEdge.Left => cursorX <= monitor.Left + 4,
            ScreenEdge.Top => cursorY <= monitor.Top + 4,
            ScreenEdge.Bottom => cursorY >= monitor.Bottom - 4,
            _ => false
        };

        var currentMs = nowMs > 0 ? nowMs : Environment.TickCount64;

        if (!onEdge)
        {
            if (_lastEdge == edge)
            {
                _hasLeftEdge = true;
            }
            return false;
        }

        // Tap 1
        if (_lastEdge != edge || (currentMs - _lastTapTick) > config.DoubleTapMs)
        {
            _lastEdge = edge;
            _lastTapTick = currentMs;
            _hasLeftEdge = false;
            return false;
        }

        // Tap 2 (ต้องเคยถอยเคอร์เซอร์ออกจากขอบมาก่อน)
        if (_hasLeftEdge && (currentMs - _lastTapTick) <= config.DoubleTapMs)
        {
            Reset();
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _lastEdge = null;
        _lastTapTick = 0;
        _hasLeftEdge = false;
    }
}

/// <summary>
/// 4. Combined Edge Strategy (Deskflow / Synergy Standard):
/// รองรับทั้งการดันแช่ขอบจอ (Dwell Delay) และการเคาะขอบสองครั้ง (Double-Tap) พร้อมกัน
/// เงื่อนไขใดเงื่อนไขหนึ่งถึงก่อน จะทำการสลับหน้าจอทันที
/// แก้ไขปัญหา BUG-2 ที่ผู้ใช้ดันเมาส์แช่ขอบจอแต่ระบบไม่สลับเพราะ Factory เลือก DoubleTap อย่างเดียว
/// </summary>
public sealed class CombinedEdgeStrategy : IEdgeSwitchStrategy
{
    public string Name => "Combined";

    private readonly DwellDelayEdgeStrategy _dwell = new();
    private readonly DoubleTapEdgeStrategy _doubleTap = new();

    public bool ShouldSwitch(int cursorX, int cursorY, ScreenEdge edge, ScreenBounds monitor, OmniConfig config, long nowMs = 0)
    {
        if (EdgeStrategyHelper.IsInCornerDeadzone(cursorX, cursorY, monitor, config))
        {
            Reset();
            return false;
        }

        bool dwellTriggered = _dwell.ShouldSwitch(cursorX, cursorY, edge, monitor, config, nowMs);
        bool doubleTapTriggered = _doubleTap.ShouldSwitch(cursorX, cursorY, edge, monitor, config, nowMs);

        if (dwellTriggered || doubleTapTriggered)
        {
            Reset();
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _dwell.Reset();
        _doubleTap.Reset();
    }
}

/// <summary>
/// Factory Pattern (GoF Creational / CP353002):
/// เลือกว่าควรใช้ Edge Strategy ใดตามคอนฟิกของผู้ใช้
/// </summary>
public static class EdgeStrategyFactory
{
    public static IEdgeSwitchStrategy Create(OmniConfig config)
    {
        if (config.DoubleTapMs > 0 && config.SwitchDelayMs > 0)
        {
            return new CombinedEdgeStrategy();
        }

        // F3: ทั้งคู่เปิด = Combined (hold หรือ double-tap อย่างใดอย่างหนึ่งพอ — Deskflow semantics)
        if (config.DoubleTapMs > 0 && config.SwitchDelayMs > 0)
        {
            return new CombinedEdgeStrategy();
        }

        if (config.DoubleTapMs > 0)
        {
            return new DoubleTapEdgeStrategy();
        }

        if (config.SwitchDelayMs > 0)
        {
            return new DwellDelayEdgeStrategy();
        }

        return new InstantEdgeStrategy();
    }
}
