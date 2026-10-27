using System;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Clipboard;
using Xunit;

namespace OmniLink.Tests;

public class ClipboardBridgeTests
{
    private class FakeClipboardBridge : IClipboardBridge
    {
        private string? _text;
        private byte[]? _png;

        public string? GetText() => _text;

        public bool SetText(string text)
        {
            _text = text;
            return true;
        }

        public bool TryGetPng(out byte[] pngBytes)
        {
            if (_png != null && _png.Length > 0)
            {
                pngBytes = _png;
                return true;
            }
            pngBytes = Array.Empty<byte>();
            return false;
        }

        public bool SetPng(byte[] pngBytes)
        {
            _png = pngBytes;
            return true;
        }

        private System.Collections.Generic.List<string>? _files;
        private bool _isCut;

        public bool ContainsFileDropList() => _files != null && _files.Count > 0;

        public System.Collections.Generic.IReadOnlyList<string>? GetFileDropList(out bool isCut)
        {
            isCut = _isCut;
            return _files;
        }

        public bool SetFileDropList(System.Collections.Generic.IReadOnlyList<string> paths, bool isCut)
        {
            _files = new System.Collections.Generic.List<string>(paths);
            _isCut = isCut;
            return true;
        }
    }

    [Fact]
    public void FakeClipboardBridge_ShouldStoreAndRetrieveText()
    {
        var bridge = new FakeClipboardBridge();
        Assert.Null(bridge.GetText());

        var setSuccess = bridge.SetText("OmniLink Text Test");
        Assert.True(setSuccess);
        Assert.Equal("OmniLink Text Test", bridge.GetText());
    }

    [Fact]
    public void FakeClipboardBridge_ShouldStoreAndRetrievePng()
    {
        var bridge = new FakeClipboardBridge();
        var sampleBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        Assert.False(bridge.TryGetPng(out var none));
        Assert.Empty(none);

        var setSuccess = bridge.SetPng(sampleBytes);
        Assert.True(setSuccess);

        var getSuccess = bridge.TryGetPng(out var result);
        Assert.True(getSuccess);
        Assert.Equal(sampleBytes, result);
    }

    [Fact]
    public void WindowsClipboardBridge_TextRoundtrip_ShouldSucceed()
    {
        var bridge = new WindowsClipboardBridge();
        var testText = $"OmniLink_STA_Test_{Guid.NewGuid():N}";

        var setOk = bridge.SetText(testText);
        Assert.True(setOk);

        var retrieved = bridge.GetText();
        Assert.Equal(testText, retrieved);
    }

    [Fact]
    public void WindowsClipboardBridge_PngRoundtrip_ShouldSucceed()
    {
        var bridge = new WindowsClipboardBridge();
        // Valid 1x1 PNG base64
        var pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        var setOk = bridge.SetPng(pngBytes);
        Assert.True(setOk);

        var getOk = bridge.TryGetPng(out var retrievedPng);
        Assert.True(getOk);
        Assert.NotEmpty(retrievedPng);
    }

    [Fact]
    public void FakeClipboardBridge_FileDropListRoundtrip_ShouldSucceed()
    {
        var bridge = new FakeClipboardBridge();
        Assert.False(bridge.ContainsFileDropList());
        Assert.Null(bridge.GetFileDropList(out _));

        var files = new[] { @"C:\test\file1.txt", @"C:\test\file2.png" };
        var setOk = bridge.SetFileDropList(files, isCut: true);
        Assert.True(setOk);
        Assert.True(bridge.ContainsFileDropList());

        var retrieved = bridge.GetFileDropList(out var isCut);
        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved.Count);
        Assert.Equal(files[0], retrieved[0]);
        Assert.Equal(files[1], retrieved[1]);
        Assert.True(isCut);
    }
}
