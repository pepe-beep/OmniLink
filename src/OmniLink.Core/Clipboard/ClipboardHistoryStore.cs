using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OmniLink.Core.Clipboard;

/// <summary>
/// ประวัติรายการคลิปบอร์ดข้ามเครื่อง (Data Transfer Object)
/// </summary>
public sealed record ClipboardHistoryEntry(
    string Id,
    string MimeType,
    string PreviewText,
    byte[]? BinaryData,
    long TimestampUtc,
    string SourceNodeId,
    string Hash)
{
    /// <summary>เวลาท้องถิ่นสำหรับแสดงผลใน UI (bind จาก XAML — UI-H2 fix)</summary>
    public DateTime TimestampLocal => DateTimeOffset.FromUnixTimeMilliseconds(TimestampUtc).LocalDateTime;
}

/// <summary>
/// ระบบจัดเก็บและจัดการประวัติคลิปบอร์ดข้ามเครื่อง (Pure Fabrication)
/// จำกัดสูงสุด 20 รายการ พร้อมระบบตรวจสอบและป้องกันข้อมูลซ้ำซ้อน (Deduplication)
/// </summary>
public sealed class ClipboardHistoryStore
{
    public const int MaxEntries = 20;

    private readonly List<ClipboardHistoryEntry> _entries = new();
    private readonly object _lock = new();

    public void AddEntry(string mimeType, string text, byte[]? data, string sourceNodeId)
    {
        if (string.IsNullOrEmpty(text) && (data == null || data.Length == 0))
        {
            return;
        }

        var hash = ComputeHash(mimeType, text, data);
        var preview = !string.IsNullOrEmpty(text) 
            ? (text.Length > 100 ? text.Substring(0, 97) + "..." : text) 
            : $"[Image: {data?.Length / 1024} KB]";

        var entry = new ClipboardHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            MimeType: mimeType,
            PreviewText: preview,
            BinaryData: data,
            TimestampUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SourceNodeId: sourceNodeId ?? "local",
            Hash: hash);

        lock (_lock)
        {
            // Deduplication: หากมีรายการเดิมที่ตรงกัน ให้ลบตัวเก่าออกแล้วดันตัวใหม่ขึ้นหัวแถว
            _entries.RemoveAll(e => e.Hash == hash);

            _entries.Insert(0, entry);

            // จำกัดสูงสุด 20 รายการ
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }
    }

    public IReadOnlyList<ClipboardHistoryEntry> GetEntries()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private static string ComputeHash(string mimeType, string text, byte[]? data)
    {
        using var sha = SHA256.Create();
        if (data != null && data.Length > 0)
        {
            var hashBytes = sha.ComputeHash(data);
            return Convert.ToHexString(hashBytes);
        }

        var textBytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        var textHash = sha.ComputeHash(textBytes);
        return Convert.ToHexString(textHash);
    }
}
