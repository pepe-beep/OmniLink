using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OmniLink.Core.Clipboard;

/// <summary>
/// ประวัติรายการคลิปบอร์ดข้ามเครื่อง (Data Transfer Object)
/// BinaryData = null เมื่อเป็นภาพที่ persist แล้ว (อ่านจาก PngPath แทน — lazy)
/// </summary>
public sealed record ClipboardHistoryEntry(
    string Id,
    string MimeType,
    string PreviewText,
    byte[]? BinaryData,
    long TimestampUtc,
    string SourceNodeId,
    string Hash,
    string? PngPath = null)
{
    /// <summary>เวลาท้องถิ่นสำหรับแสดงผลใน UI (bind จาก XAML — UI-H2 fix)</summary>
    public DateTime TimestampLocal => DateTimeOffset.FromUnixTimeMilliseconds(TimestampUtc).LocalDateTime;
}

/// <summary>
/// ระบบจัดเก็บและจัดการประวัติคลิปบอร์ดข้ามเครื่อง (Pure Fabrication)
/// C2 spec: text 100 รายการ (≤100KB) + ภาพ 20 รายการ (PNG ไฟล์แยก) · dedupe sha256
/// persist: runstate/clipboard_history.json (ring, เขียนผ่าน AtomicFile — INV-9)
/// + runstate/history/&lt;sha&gt;.png — เขียนไฟล์บน background เสมอ (ห้าม I/O บน UI thread — UI-H4)
/// </summary>
public sealed class ClipboardHistoryStore
{
    public const int MaxTextEntries = 100;
    public const int MaxImageEntries = 20;
    public const long MaxTextBytes = 100_000; // spec: รายการ ≤100KB

    private readonly List<ClipboardHistoryEntry> _entries = new();
    private readonly object _lock = new();
    private readonly string _jsonPath;
    private readonly string _historyDir;
    private readonly Action<string>? _log;

    public ClipboardHistoryStore(string? runStateDir = null, Action<string>? log = null)
    {
        var dir = runStateDir ?? OmniPaths.RunStateDir;
        _jsonPath = Path.Combine(dir, "clipboard_history.json");
        _historyDir = Path.Combine(dir, "history");
        _log = log;
        Load();
    }

    /// <summary>API เดิม (คง caller เดิมทั้ง 3 จุดใน UI) — route ตาม mimeType</summary>
    public void AddEntry(string mimeType, string text, byte[]? data, string sourceNodeId)
    {
        if (string.IsNullOrEmpty(text) && (data == null || data.Length == 0))
        {
            return;
        }

        if (mimeType.StartsWith("image/", StringComparison.Ordinal) && data is { Length: > 0 })
        {
            AddImage(data, sourceNodeId);
            return;
        }
        AddTextEntry(text, sourceNodeId, mimeType);
    }

    public ClipboardHistoryEntry? AddTextEntry(string text, string sourceNodeId, string mimeType = "text/plain")
    {
        if (string.IsNullOrEmpty(text)) return null;
        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > MaxTextBytes) return null; // spec: รายการ ≤100KB

        var hash = ComputeHash(mimeType, text, null);
        var preview = text.Length > 100 ? text.Substring(0, 97) + "..." : text;
        var entry = new ClipboardHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            MimeType: mimeType,
            PreviewText: preview,
            BinaryData: Encoding.UTF8.GetBytes(text),
            TimestampUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SourceNodeId: sourceNodeId ?? "local",
            Hash: hash);

        lock (_lock)
        {
            _entries.RemoveAll(e => e.Hash == hash); // dedupe
            var texts = _entries.Count(e => !IsImage(e));
            if (texts >= MaxTextEntries)
            {
                var oldestText = _entries.LastOrDefault(e => !IsImage(e));
                if (oldestText is not null) _entries.Remove(oldestText);
            }
            _entries.Insert(0, entry);
        }
        SchedulePersist();
        return entry;
    }

    public ClipboardHistoryEntry? AddImage(byte[] png, string sourceNodeId)
    {
        if (png is not { Length: > 0 }) return null;
        var hash = ComputeHash("image/png", null, png);
        var pngPath = Path.Combine(_historyDir, hash.Substring(0, Math.Min(hash.Length, 40)) + ".png");
        ClipboardHistoryEntry entry;
        lock (_lock)
        {
            if (_entries.Any(e => e.Hash == hash))
            {
                return null; // dedupe: ภาพเดิมไม่เขียนซ้ำ
            }
            entry = new ClipboardHistoryEntry(
                Id: Guid.NewGuid().ToString("N"),
                MimeType: "image/png",
                PreviewText: $"[Image: {png.Length / 1024} KB]",
                BinaryData: null,
                TimestampUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SourceNodeId: sourceNodeId ?? "local",
                Hash: hash,
                PngPath: pngPath);

            var images = _entries.Count(IsImage);
            if (images >= MaxImageEntries)
            {
                var oldest = _entries.LastOrDefault(IsImage);
                if (oldest is not null)
                {
                    _entries.Remove(oldest);
                    TryDeletePng(oldest.PngPath);
                }
            }
            _entries.Insert(0, entry);
        }
        // เขียน PNG บน background (I/O นอก caller thread)
        var bytes = png;
        var path = pngPath;
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_historyDir);
                if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex) { _log?.Invoke("[history] write png: " + ex.Message); }
        });
        SchedulePersist();
        return entry;
    }

    /// <summary>อ่านเนื้อหาเต็มของภาพ (lazy จากไฟล์) — null = อ่านไม่ได้</summary>
    public byte[]? GetPng(ClipboardHistoryEntry entry)
    {
        if (entry.BinaryData is { Length: > 0 }) return entry.BinaryData;
        if (entry.PngPath is not null && File.Exists(entry.PngPath))
        {
            try { return File.ReadAllBytes(entry.PngPath); } catch { }
        }
        return null;
    }

    /// <summary>เนื้อหา text เต็ม (null สำหรับภาพ)</summary>
    public string? GetText(ClipboardHistoryEntry entry)
        => IsImage(entry) ? null : entry.BinaryData is { Length: > 0 } ? Encoding.UTF8.GetString(entry.BinaryData) : entry.PreviewText;

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
            foreach (var e in _entries.Where(IsImage))
            {
                TryDeletePng(e.PngPath);
            }
            _entries.Clear();
        }
        SchedulePersist();
    }

    // ── persist (C2: ring ใน runstate/clipboard_history.json — AtomicFile INV-9) ──

    private static bool IsImage(ClipboardHistoryEntry e) => e.MimeType == "image/png";

    private int _persistPending; // 0 = ไม่มีคิว, 1 = มีคิว (เขียนรวม batch)

    /// <summary>รวมการเขียนที่ถี่ (clipboard เปลี่ยนบ่อย) — เขียนล่าสุดชนะ บน background</summary>
    private void SchedulePersist()
    {
        if (Interlocked.Exchange(ref _persistPending, 1) == 1) return;
        Task.Run(() =>
        {
            do
            {
                Interlocked.Exchange(ref _persistPending, 0);
                try
                {
                    string json;
                    lock (_lock)
                    {
                        json = System.Text.Json.JsonSerializer.Serialize(
                            _entries.Select(e => new PersistRow(
                                e.Id, e.MimeType, e.PreviewText, e.TimestampUtc, e.SourceNodeId, e.Hash,
                                e.PngPath,
                                !IsImage(e) ? GetText(e) : null)).ToList());
                    }
                    AtomicFile.WriteAllText(_jsonPath, json);
                }
                catch (Exception ex) { _log?.Invoke("[history] persist: " + ex.Message); }
            }
            while (Interlocked.CompareExchange(ref _persistPending, 1, 1) == 1);
        });
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_jsonPath)) return;
            var json = File.ReadAllText(_jsonPath);
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<PersistRow>>(json);
            if (rows is null) return;
            lock (_lock)
            {
                foreach (var r in rows)
                {
                    var entry = new ClipboardHistoryEntry(
                        r.Id, r.MimeType, r.PreviewText, null, r.TimestampUtc, r.SourceNodeId, r.Hash,
                        r.PngPath);
                    if (r.Text is { Length: > 0 })
                    {
                        entry = entry with { BinaryData = Encoding.UTF8.GetBytes(r.Text) };
                    }
                    _entries.Add(entry);
                }
            }
        }
        catch (Exception ex) { _log?.Invoke("[history] load: " + ex.Message); }
    }

    private void TryDeletePng(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch { }
    }

    private static string ComputeHash(string mimeType, string? text, byte[]? data)
    {
        using var sha = SHA256.Create();
        if (data != null && data.Length > 0)
        {
            return Convert.ToHexString(sha.ComputeHash(data));
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(mimeType + "\n" + (text ?? string.Empty))));
    }

    private sealed record PersistRow(
        string Id, string MimeType, string PreviewText, long TimestampUtc,
        string SourceNodeId, string Hash, string? PngPath, string? Text);
}
