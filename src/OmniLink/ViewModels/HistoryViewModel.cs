using System;
using System.Collections.ObjectModel;
using OmniLink.Core.Clipboard;

namespace OmniLink.ViewModels;

/// <summary>
/// ViewModel สำหรับแสดงรายการประวัติการคัดลอกข้ามเครื่อง (Clipboard History)
/// รองรับการดูประวัติย้อนหลัง 20 รายการล่าสุด
/// </summary>
public sealed class HistoryViewModel : ViewModelBase
{
    private readonly ClipboardHistoryStore _store;
    private ClipboardHistoryEntry? _selectedEntry;

    public ObservableCollection<ClipboardHistoryEntry> Entries { get; } = new();

    public ClipboardHistoryEntry? SelectedEntry
    {
        get => _selectedEntry;
        set => SetProperty(ref _selectedEntry, value);
    }

    public int TotalCount => Entries.Count;

    public HistoryViewModel(ClipboardHistoryStore? store = null)
    {
        _store = store ?? new ClipboardHistoryStore();
        Refresh();
    }

    public void Refresh()
    {
        var snapshot = _store.GetEntries();
        Entries.Clear();
        foreach (var entry in snapshot)
        {
            Entries.Add(entry);
        }
        OnPropertyChanged(nameof(TotalCount));
    }

    public void AddEntry(string mimeType, string text, byte[]? data, string sourceNodeId)
    {
        _store.AddEntry(mimeType, text, data, sourceNodeId);
        Refresh();
    }

    public void Clear()
    {
        _store.Clear();
        Entries.Clear();
        SelectedEntry = null;
        OnPropertyChanged(nameof(TotalCount));
    }
}
