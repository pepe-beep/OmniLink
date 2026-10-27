using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace OmniLink.Core.Events;

/// <summary>
/// Event Records (Data Transfer Objects)
/// </summary>
public record NodeConnectionEvent(string NodeId, string Ip, bool Connected, int LangGroup);
public record StatusBannerEvent(string Message);

/// <summary>v1.9 C1: คำขอ toggle เสียงจาก user hotkey — Kvm hook จับปุ่มแล้วส่งต่อให้ผู้ถือ audio (MainWindow)</summary>
public record HotkeyAudioRequestedEvent(string ActionType); // toggle_stream | toggle_mic
public record ClipboardReceivedEvent(string Text, string SourceNodeId);
public record LanguageChangedEvent(int LangGroup, bool IsHost, string? NodeId);

/// <summary>
/// Observer Pattern (GoF Behavioral / CP353002):
/// ศูนย์กลางกระจายข่าวสาร (Event Aggregator / Subject)
/// แยกความรับผิดชอบ (Decoupling) ให้ UI และ Background Services สื่อสารกันผ่านเหตุการณ์
/// </summary>
public interface IOmniEventHub
{
    void Publish<T>(T eventData);
    IDisposable Subscribe<T>(Action<T> handler);
}

/// <summary>
/// Thread-safe In-Memory Event Hub (Pure Fabrication)
/// </summary>
public sealed class OmniEventHub : IOmniEventHub
{
    private static readonly Lazy<OmniEventHub> _defaultInstance = new(() => new OmniEventHub());
    public static OmniEventHub Default => _defaultInstance.Value;

    private readonly ConcurrentDictionary<Type, List<object>> _subscribers = new();
    private readonly object _lock = new();

    public void Publish<T>(T eventData)
    {
        if (eventData is null) return;

        List<object>? handlersCopy = null;
        if (_subscribers.TryGetValue(typeof(T), out var handlers))
        {
            lock (_lock)
            {
                handlersCopy = new List<object>(handlers);
            }
        }

        if (handlersCopy is not null)
        {
            foreach (var handlerObj in handlersCopy)
            {
                if (handlerObj is Action<T> action)
                {
                    try
                    {
                        action(eventData);
                    }
                    catch (Exception ex)
                    {
                        OmniLog.Warn($"[event_hub] handler error for {typeof(T).Name}: {ex.Message}");
                    }
                }
            }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        var type = typeof(T);
        lock (_lock)
        {
            var list = _subscribers.GetOrAdd(type, _ => new List<object>());
            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                if (_subscribers.TryGetValue(type, out var list))
                {
                    list.Remove(handler);
                }
            }
        });
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _unsubscribe;
        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _unsubscribe, null);
            action?.Invoke();
        }
    }
}
