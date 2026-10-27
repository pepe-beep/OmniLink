using System;

namespace OmniLink.Core.Strategies;

/// <summary>
/// Strategy Pattern (GoF Behavioral / CP353002):
/// อินเทอร์เฟซสำหรับกำหนดนโยบายการสลับภาษาข้ามเครื่อง (Language Synchronization Policy)
/// </summary>
public interface ILanguageSyncStrategy
{
    string ModeName { get; }

    /// <summary>เมื่อผู้ใช้กดสลับภาษาบนฝั่ง Host</summary>
    void HandleHostLanguageSwitched(int newGroup, KvmHostService host);

    /// <summary>เมื่อเคอร์เซอร์เมาส์กระโดดเข้าไปยังหน้าจอของเครื่อง Client</summary>
    void HandleSwitchToNode(string nodeId, KvmHostService host);

    /// <summary>เมื่อเคอร์เซอร์เมาส์กระโดดกลับมายังหน้าจอของ Host</summary>
    void HandleEdgeReturnToHost(string nodeId, KvmHostService host);
}

/// <summary>
/// 1. Global Calibrated Strategy (โหมดแนะนำ):
/// บังคับให้ภาษาของทั้ง Windows Host และ Client ทุกตัวตรงกันอย่างสมมาตรเสมอ
/// </summary>
public sealed class GlobalCalibratedSyncStrategy : ILanguageSyncStrategy
{
    public string ModeName => "global_calibrated";

    public void HandleHostLanguageSwitched(int newGroup, KvmHostService host)
    {
        host.CalibrateAllLanguages(newGroup);
    }

    public void HandleSwitchToNode(string nodeId, KvmHostService host)
    {
        // ในโหมด Global Calibrated เครื่องลูกจะถูกตั้งเป็นภาษาเดียวกับ Host เสมอ
        if (host.Sessions.TryGetValue(nodeId, out var sess))
        {
            sess.RequestSetLanguage(host.MasterLangGroup, calibrate: true);
        }
    }

    public void HandleEdgeReturnToHost(string nodeId, KvmHostService host)
    {
        // ยืนยันให้ Host กลับมาใช้ Master Language
        host.SetWindowsLangGroupTracked(host.MasterLangGroup);
    }
}

/// <summary>
/// 2. Target Only Strategy:
/// สลับภาษาเฉพาะเครื่องที่เคอร์เซอร์เมาส์กำลังโฟกัสอยู่เท่านั้น แยกอิสระจากกัน
/// </summary>
public sealed class TargetOnlySyncStrategy : ILanguageSyncStrategy
{
    public string ModeName => "target_only";

    public void HandleHostLanguageSwitched(int newGroup, KvmHostService host)
    {
        // ถ้าเมาส์อยู่บนฝั่ง Host สลับเฉพาะ Host
        if (host.ActiveNodeId is null)
        {
            host.SetWindowsLangGroupTracked(newGroup);
        }
        else if (host.Sessions.TryGetValue(host.ActiveNodeId, out var activeSess))
        {
            // ถ้าเมาส์อยู่บนเครื่องลูก สลับเฉพาะเครื่องลูก
            var targetGroup = activeSess.GetEffectiveLangForToggle() == 1 ? 0 : 1;
            activeSess.RequestSetLanguage(targetGroup, calibrate: false);
        }
    }

    public void HandleSwitchToNode(string nodeId, KvmHostService host)
    {
        if (host.Config.AutoEngHostOnClient)
        {
            // ล็อกแป้นพิมพ์ Host ไว้ที่ EN กันคำสั่งลัดเพี้ยน
            host.SetWindowsLangGroupTracked(0);
        }
    }

    public void HandleEdgeReturnToHost(string nodeId, KvmHostService host)
    {
        if (host.Config.AutoEngHostOnClient)
        {
            // คืนค่าภาษาเดิมของ Host ก่อนที่เมาส์จะหลุดออกไป
            host.RestoreSavedHostLanguage();
        }
    }
}

/// <summary>
/// Factory สำหรับสร้าง Language Synchronization Strategy
/// </summary>
public static class LanguageSyncStrategyFactory
{
    public static ILanguageSyncStrategy Create(string? mode)
    {
        if (string.Equals(mode, "target_only", StringComparison.OrdinalIgnoreCase))
        {
            return new TargetOnlySyncStrategy();
        }

        return new GlobalCalibratedSyncStrategy();
    }
}
