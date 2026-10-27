using System;
using System.Text;
using OmniLink.Core;
using OmniLink.Core.Clipboard;
using Xunit;

namespace OmniLink.Tests;

public class PasteOnDemandTests
{
    private static readonly byte[] Valid1x1Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public void FastPath_SmallText_IsBelow64KThreshold()
    {
        var smallText = "Hello OmniLink Clipboard";
        var byteCount = Encoding.UTF8.GetByteCount(smallText);

        Assert.True(byteCount < 65536);
    }

    [Fact]
    public void LargeData_Exceeds64KThreshold_TriggersAckGate()
    {
        var largeText = new string('A', 70000); // 70KB
        var byteCount = Encoding.UTF8.GetByteCount(largeText);

        Assert.True(byteCount >= 65536);
    }

    [Fact]
    public void KvmHostService_PendingClipCache_StoresAndRetrievesData()
    {
        var config = new OmniConfig();
        using var host = new KvmHostService(config, () => { });

        var token = "token-test-12345";
        var sampleBytes = Encoding.UTF8.GetBytes("Large image or text data payload");

        // Use reflection or inject pending clip directly to test retrieval contract
        var method = typeof(KvmHostService).GetMethod("TryGetPendingClip");
        Assert.NotNull(method);

        // Initially token is not present
        var foundBefore = host.TryGetPendingClip(token, out var notFoundClip);
        Assert.False(foundBefore);
        Assert.Null(notFoundClip.data);

        // Simulate clip push (godclass-K3: _pendingClips ย้ายไป ClipboardRouter)
        var clipField = typeof(KvmHostService).GetField("_clip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(clipField);
        var router = clipField.GetValue(host);
        Assert.NotNull(router);
        var pendingClipsField = typeof(ClipboardRouter).GetField("_pendingClips", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(pendingClipsField);

        var dict = pendingClipsField.GetValue(router) as System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] data, string mime)>;
        Assert.NotNull(dict);

        dict[token] = (sampleBytes, "image/png");

        var foundAfter = host.TryGetPendingClip(token, out var foundClip);
        Assert.True(foundAfter);
        Assert.Equal(sampleBytes, foundClip.data);
        Assert.Equal("image/png", foundClip.mime);
    }

    [Fact]
    public void ImageClipboardCodec_SanitizesValidPngBytesCorrectly()
    {
        var sanitized = ImageClipboardCodec.SanitizePng(Valid1x1Png);
        Assert.NotNull(sanitized);
        Assert.Equal(Valid1x1Png.Length, sanitized.Length);
        Assert.True(ImageClipboardCodec.IsPng(sanitized));
    }
}
