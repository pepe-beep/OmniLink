using System.IO;
using OmniLink.Core;
using OmniLink.Core.Clipboard;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// C2: ClipboardHistoryStore — ring 100 text (≤100KB) + 20 png (ไฟล์แยก runstate/history)
/// dedupe sha256 + persist ผ่าน AtomicFile + lazy png read (ทดสอบด้วย temp dir ล้วน ไม่แตะ runstate จริง)
/// (test รุ่นแรกใช้ in-memory 20 — C2 29 ต.ค. ขยายตาม spec: text/image แยก ring + persist)
/// </summary>
public class ClipboardHistoryStoreTests : IDisposable
{
    private readonly string _dir;

    public ClipboardHistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "omnilink-hist-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ClipboardHistoryStore Make() => new(_dir);

    [Fact]
    public void AddEntry_Text_StoresPreviewAndMimeCorrectly()
    {
        var store = Make();
        store.AddEntry("text/plain", "Hello OmniLink", null, "thinkpad-l440");

        var entries = store.GetEntries();
        Assert.Single(entries);
        Assert.Equal("Hello OmniLink", entries[0].PreviewText);
        Assert.Equal("text/plain", entries[0].MimeType);
        Assert.Equal("thinkpad-l440", entries[0].SourceNodeId);
        Assert.Equal("Hello OmniLink", store.GetText(entries[0]));
    }

    [Fact]
    public void AddEntry_Image_PersistsToPngFile_LazyRead()
    {
        var store = Make();
        var samplePng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        store.AddEntry("image/png", "", samplePng, "host-pc");

        var entries = store.GetEntries();
        Assert.Single(entries);
        Assert.Equal("image/png", entries[0].MimeType);
        Assert.Contains("[Image:", entries[0].PreviewText);
        Assert.NotNull(entries[0].PngPath); // ภาพ persist เป็นไฟล์แยก (C2 spec)
        Assert.True(SpinUntil(() => File.Exists(entries[0].PngPath!), 5000), "png ต้องถูกเขียนบน background");
        Assert.Equal(samplePng, store.GetPng(entries[0])); // lazy read จากไฟล์
    }

    [Fact]
    public void AddEntry_Deduplication_MovesExistingToFrontWithoutDuplicate()
    {
        var store = Make();
        store.AddEntry("text/plain", "Item 1", null, "node-1");
        store.AddEntry("text/plain", "Item 2", null, "node-1");
        store.AddEntry("text/plain", "Item 1", null, "node-1"); // Duplicate of Item 1

        var entries = store.GetEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Item 1", entries[0].PreviewText);
        Assert.Equal("Item 2", entries[1].PreviewText);
    }

    [Fact]
    public void Text_Ring_Keeps_Max100_NewestFirst()
    {
        var store = Make();
        for (var i = 1; i <= 105; i++)
        {
            store.AddEntry("text/plain", "text-" + i, null, "local");
        }
        var entries = store.GetEntries().Where(e => e.MimeType == "text/plain").ToList();
        Assert.Equal(100, entries.Count);
        Assert.Equal("text-105", store.GetText(entries[0])); // ใหม่สุดอยู่หัว
        Assert.Equal("text-6", store.GetText(entries[^1]));  // เก่าสุดที่รอด = 6 (1-5 ถูก ring ออก)
    }

    [Fact]
    public void Text_Over100KB_IsRejected()
    {
        var store = Make();
        Assert.Null(store.AddTextEntry(new string('x', 100_001), "local"));
        store.AddTextEntry(new string('x', 99_000), "local");
        Assert.Single(store.GetEntries());
    }

    [Fact]
    public void Image_Ring_Keeps_Max20_AndDeletesOldestFile()
    {
        var store = Make();
        for (var i = 0; i < 25; i++)
        {
            store.AddImage(new byte[] { 1, 2, 3, (byte)i, 5, 6, 7, 8 }, "client");
        }
        Assert.Equal(20, store.GetEntries().Count(e => e.MimeType == "image/png"));
    }

    [Fact]
    public void PersistAndReload_TextSurvives()
    {
        var store = Make();
        store.AddEntry("text/plain", "hello-persist", null, "local");
        Assert.True(SpinUntil(() => File.Exists(Path.Combine(_dir, "clipboard_history.json")), 5000));

        var reloaded = Make(); // instance ใหม่จาก dir เดิม = load จาก json
        var entries = reloaded.GetEntries();
        Assert.Single(entries);
        Assert.Equal("hello-persist", reloaded.GetText(entries[0]));
        Assert.Equal("local", entries[0].SourceNodeId);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var store = Make();
        store.AddEntry("text/plain", "Item A", null, "node-1");
        store.AddEntry("image/png", "", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 1, 1, 1 }, "node-1");

        store.Clear();
        Assert.Empty(store.GetEntries());
    }

    private static bool SpinUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(20);
        }
        return cond();
    }
}
