using System.Collections.Concurrent;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;
using OmniLink.Core.Strategies;

namespace OmniLink.Core;

/// <summary>
/// ย้ายออกจาก KvmHostService (godclass-K4): กลุ่ม LANG — language watcher thread,
/// calibrate/toggle broadcast, host↔node language sync
/// โค้ดย้ายแบบ move-verbatim (Config/Sessions/ActiveNodeId/_running/_savedHostLangBeforeClient/
/// _ignoreHostLangPollUntilTick ปรับเป็น accessor ตามที่ระบุ) จาก KvmHostService.cs เดิม (commit 1116a41)
/// MasterLangGroup ย้ายมาอยู่ที่นี่ (เดิมเป็น property ของ KvmHostService)
/// </summary>
internal sealed class LanguageSyncService
{
    private readonly OmniConfig _config;
    private readonly ConcurrentDictionary<string, RemoteNodeClient> _sessions;
    private readonly IHostInputGateway _input;
    private readonly IOmniEventHub _eventHub;
    private readonly Action _onUiUpdate;
    private readonly Func<string?> _activeNodeId;
    private readonly Func<bool> _isRunning;
    private readonly SwitchingState _switch;

    /// <summary>ภาษา "master" ปัจจุบันของระบบ (0 = EN, 1 = TH) — เดิม KvmHostService.MasterLangGroup</summary>
    public int MasterLangGroup { get; internal set; }

    private ILanguageSyncStrategy _strategy;
    /// <summary>strategy ปัจจุบัน (KvmHostService ส่งต่อให้ HandleEdgeReturnToHost เหมือนเดิม)</summary>
    public ILanguageSyncStrategy Strategy => _strategy;

    private Thread? _watcherThread;
    private readonly AutoResetEvent _wakeEvent = new(false);

    // Bug B fix: baseline ของ (หน้าต่าง, layout) สำหรับ language watcher
    private long _langWatchHwnd;
    private int _langWatchLast = -1;
    private int _langWatchPending = -1;
    private long _langWatchPendingSince;
    private int _langMismatchCount;

    public LanguageSyncService(OmniConfig config,
        ConcurrentDictionary<string, RemoteNodeClient> sessions,
        IHostInputGateway input,
        IOmniEventHub eventHub,
        Action onUiUpdate,
        Func<string?> activeNodeId,
        Func<bool> isRunning,
        SwitchingState switchState)
    {
        _config = config;
        _sessions = sessions;
        _input = input;
        _eventHub = eventHub;
        _onUiUpdate = onUiUpdate;
        _activeNodeId = activeNodeId;
        _isRunning = isRunning;
        _switch = switchState;
        _strategy = LanguageSyncStrategyFactory.Create(config.LanguageMode);
    }

    public void UpdateStrategy(string languageMode)
    {
        _strategy = LanguageSyncStrategyFactory.Create(languageMode);
    }

    /// <summary>ปลุก watcher ให้ตรวจทันที (เดิม _langWakeEvent.Set() จาก OnKeyHook/Dispose)</summary>
    public void Wake() => _wakeEvent.Set();

    public void StartWatcher()
    {
        if (_watcherThread is null)
        {
            _watcherThread = new Thread(WatcherLoop)
            {
                IsBackground = true,
                Name = "OmniLink_HostLangWatcher",
            };
            _watcherThread.Start();
        }
    }

    public void RestoreSavedHostLanguage()
    {
        SetWindowsLangGroupTracked(_switch.SavedHostLangBeforeClient);
    }

    /// <summary>กลุ่มภาษาปัจจุบันของเครื่องนี้ (0 = EN, 1 = TH) — อ่านผ่าน input gateway ของ OS</summary>
    public int GetHostLangGroup() => _input.GetForegroundLangGroup();

    public void SetWindowsLangGroupTracked(int group)
    {
        _switch.IgnoreHostLangPollUntilTick = Environment.TickCount64 + 350;
        _input.RequestLangChange(group);
    }

    public void CalibrateAllLanguages(int targetGroup)
    {
        var norm = targetGroup == 1 ? 1 : 0;
        MasterLangGroup = norm;
        if (_activeNodeId() is null)
        {
            SetWindowsLangGroupTracked(norm);
        }
        else if (_config.AutoEngHostOnClient)
        {
            SetWindowsLangGroupTracked(0);
        }

        foreach (var sess in _sessions.Values)
        {
            if (sess.Connected)
            {
                sess.RequestSetLanguage(norm, calibrate: true);
            }
        }
        OmniLog.Write("lang", $"calibrate_all group={norm}");
        try { _eventHub.Publish(new LanguageChangedEvent(norm, true, null)); } catch { }
        _onUiUpdate();
    }

    public void TriggerLanguageToggle()
    {
        OmniLog.Write("lang",
            $"toggle requested (mode={_config.LanguageMode}, active_node={_activeNodeId() ?? "host"}, master={MasterLangGroup})");
        try
        {
            if (_config.LanguageMode == "global_calibrated")
            {
                var next = MasterLangGroup == 1 ? 0 : 1;
                CalibrateAllLanguages(next);
            }
            else
            {
                if (_activeNodeId() is null)
                {
                    var next = MasterLangGroup == 1 ? 0 : 1;
                    MasterLangGroup = next;
                    SetWindowsLangGroupTracked(next);
                    try { _eventHub.Publish(new LanguageChangedEvent(next, true, null)); } catch { }
                    _onUiUpdate();
                }
                else if (_sessions.TryGetValue(_activeNodeId()!, out var sess) && sess.Connected)
                {
                    var cur = sess.GetEffectiveLangForToggle();
                    var next = cur == 1 ? 0 : 1;
                    OmniLog.Write("lang", $"target-only: {_activeNodeId()} {cur} → {next}");
                    sess.RequestSetLanguage(next, calibrate: false);
                    _onUiUpdate();
                }
            }
        }
        catch (Exception ex)
        {
            OmniLog.Error("[lang] toggle failed", ex);
        }
    }

    /// <summary>
    /// Passively observes Windows HKL changes when the user is on the Host (e.g. via native Win+Space,
    /// Alt+Shift, or taskbar click) and broadcasts to remote Linux clients.
    /// Bug B fix (INCIDENT 2026-10-17): Windows layout เป็น per-window — การสลับหน้าต่างระหว่างแอป
    /// ที่คนละภาษาไม่ใช่ "การเปลี่ยนภาษา" จึงต้องจำ (hwnd, layout) เป็น baseline และ broadcast เฉพาะเมื่อ
    /// หน้าต่างเดิมเปลี่ยนภาษาจริง + ค่าต้องนิ่ง ≥500ms (กัน Alt+Tab รัว/สัญญาณหลอก)
    /// </summary>
    private void WatcherLoop()
    {
        while (_isRunning())
        {
            var waitMs = _langWatchPending != -1 ? 100 : 400;
            _wakeEvent.WaitOne(waitMs);
            if (!_isRunning()) break;

            try
            {
                if (_activeNodeId() is not null || Environment.TickCount64 < _switch.IgnoreHostLangPollUntilTick)
                {
                    continue;
                }

                var hwnd = _input.GetForegroundToken();

                // สลับหน้าต่าง → อัปเดต baseline เงียบๆ (ห้ามตีความเป็นการเปลี่ยนภาษา)
                if (hwnd != _langWatchHwnd)
                {
                    _langWatchHwnd = hwnd;
                    _langWatchLast = _input.GetForegroundLangGroup();
                    _langWatchPending = -1;
                    continue;
                }

                var lang = _input.GetForegroundLangGroup();
                if (lang == _langWatchLast)
                {
                    _langWatchPending = -1;
                    continue;
                }

                // เห็นค่าใหม่ → จับเวลารอความนิ่งก่อนเชื่อ
                if (_langWatchPending != lang)
                {
                    _langWatchPending = lang;
                    _langWatchPendingSince = Environment.TickCount64;
                    continue;
                }
                if (Environment.TickCount64 - _langWatchPendingSince < 500)
                {
                    continue;
                }

                _langWatchLast = lang;
                _langWatchPending = -1;
                MasterLangGroup = lang;
                OmniLog.Write("lang", $"host_native_switch detected_group={lang} (stable 500ms)");
                if (_config.LanguageMode == "global_calibrated")
                {
                    foreach (var sess in _sessions.Values)
                    {
                        if (sess.Connected)
                        {
                            sess.RequestSetLanguage(lang, calibrate: true);
                        }
                    }
                }
                _onUiUpdate();
            }
            catch
            {
            }

            // LANG ENFORCER: ระหว่างควบคุม remote ในโหมด global ถ้าภาษาไม่ตรงกัน
            // - มีคำสั่งค้าง (pending) = คำสั่งหล่น → ส่งซ้ำ
            // - ไม่มีคำสั่งค้าง = ผู้ใช้สลับเองบน Linux (คีย์บอร์ดจริง/Super+Space บนเครื่องนั้น)
            //   → "adopt" ยอมตาม อย่าสู้กับผู้ใช้
            try
            {
                if (_activeNodeId() is not null &&
                    _config.LanguageMode == "global_calibrated" &&
                    _sessions.TryGetValue(_activeNodeId()!, out var esess) &&
                    esess.Connected &&
                    esess.LangGroup != MasterLangGroup)
                {
                    _langMismatchCount++;
                    if (_langMismatchCount >= 2)
                    {
                        _langMismatchCount = 0;
                        var pending = esess.PendingLangRequest();
                        if (pending != -1 && pending != esess.LangGroup)
                        {
                            OmniLog.Warn($"[lang] enforcer: คำสั่งหล่น (agent={esess.LangGroup}, ต้องการ={pending}) → ส่งซ้ำ");
                            esess.RequestSetLanguage(MasterLangGroup, calibrate: true);
                        }
                        else
                        {
                            OmniLog.Write("lang", $"enforcer: adopt — ผู้ใช้สลับบนเครื่อง Linux เอง → master={esess.LangGroup}");
                            MasterLangGroup = esess.LangGroup;
                            foreach (var other in _sessions.Values)
                            {
                                if (other != esess && other.Connected)
                                {
                                    other.RequestSetLanguage(MasterLangGroup, calibrate: false);
                                }
                            }
                            _onUiUpdate();
                        }
                    }
                }
                else
                {
                    _langMismatchCount = 0;
                }
            }
            catch
            {
            }
        }
    }
}
