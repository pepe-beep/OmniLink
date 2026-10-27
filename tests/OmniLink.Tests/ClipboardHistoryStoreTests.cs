using OmniLink.Core.Clipboard;
using Xunit;

namespace OmniLink.Tests;

public class ClipboardHistoryStoreTests
{
    [Fact]
    public void AddEntry_Text_StoresPreviewAndMimeCorrectly()
    {
        var store = new ClipboardHistoryStore();
        store.AddEntry("text/plain", "Hello OmniLink", null, "thinkpad-l440");

        var entries = store.GetEntries();
        Assert.Single(entries);
        Assert.Equal("Hello OmniLink", entries[0].PreviewText);
        Assert.Equal("text/plain", entries[0].MimeType);
        Assert.Equal("thinkpad-l440", entries[0].SourceNodeId);
        Assert.Null(entries[0].BinaryData);
    }

    [Fact]
    public void AddEntry_Image_StoresBinaryDataCorrectly()
    {
        var store = new ClipboardHistoryStore();
        var samplePng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        store.AddEntry("image/png", "", samplePng, "host-pc");

        var entries = store.GetEntries();
        Assert.Single(entries);
        Assert.Equal("image/png", entries[0].MimeType);
        Assert.Equal(samplePng, entries[0].BinaryData);
        Assert.Contains("[Image:", entries[0].PreviewText);
    }

    [Fact]
    public void AddEntry_Deduplication_MovesExistingToFrontWithoutDuplicate()
    {
        var store = new ClipboardHistoryStore();
        store.AddEntry("text/plain", "Item 1", null, "node-1");
        store.AddEntry("text/plain", "Item 2", null, "node-1");
        store.AddEntry("text/plain", "Item 1", null, "node-1"); // Duplicate of Item 1

        var entries = store.GetEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Item 1", entries[0].PreviewText);
        Assert.Equal("Item 2", entries[1].PreviewText);
    }

    [Fact]
    public void AddEntry_CapAt20_RemovesOldestEntries()
    {
        var store = new ClipboardHistoryStore();
        for (int i = 1; i <= 25; i++)
        {
            store.AddEntry("text/plain", $"Item {i}", null, "node-1");
        }

        var entries = store.GetEntries();
        Assert.Equal(ClipboardHistoryStore.MaxEntries, entries.Count);
        Assert.Equal("Item 25", entries[0].PreviewText);
        Assert.Equal("Item 6", entries[^1].PreviewText);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var store = new ClipboardHistoryStore();
        store.AddEntry("text/plain", "Item A", null, "node-1");
        store.AddEntry("text/plain", "Item B", null, "node-1");

        store.Clear();
        Assert.Empty(store.GetEntries());
    }
}
