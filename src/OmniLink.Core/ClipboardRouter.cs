using System.Collections.Concurrent;
using System.IO;
using System.Text;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Clipboard;
using OmniLink.Core.Events;
using OmniLink.Core.FileExchange;

namespace OmniLink.Core;

/// <summary>delegates ที่ MainWindow มาผูกทีหลัง construction — เก็บเป็น holder ที่ KvmHostService
/// เปิด facade property ให้เขียนผ่าน (ไม่ capture ค่า ณ ตอน ctor)</summary>
public delegate IReadOnlyList<string>? HostFileDropGetterDelegate(out bool isCut);

internal sealed class ClipboardPorts
{
    public Func<string?>? HostClipboardGetter;
    public Func<byte[]?>? HostPngGetter;
    public HostFileDropGetterDelegate? HostFileDropGetter;
    public Action<string>? OnClipboardFromClient;
    public Action<byte[]?>? OnClipboardImageFromClient;
    public Action<IReadOnlyList<string>, bool>? OnClipboardFilesFromClient;
}

/// <summary>
/// ย้ายออกจาก KvmHostService (godclass-K3): กลุ่ม CLIP — clipboard สองทิศ + file exchange routing
/// + paste-on-demand pending store + echo-suppression hash
/// โค้ดย้ายแบบ move-verbatim (Config/Sessions/FileTransfer/OnStatusBanner/ActiveNodeId/delegates
/// ปรับเป็น accessor ตามที่ระบุ) จาก KvmHostService.cs เดิม (commit 22a3a5e)
/// </summary>
internal sealed class ClipboardRouter
{
    public readonly ClipboardPorts Ports = new();

    private readonly OmniConfig _config;
    private readonly ConcurrentDictionary<string, RemoteNodeClient> _sessions;
    private readonly FileTransferManager _fileTransfer;
    private readonly IOmniEventHub _eventHub;
    private readonly Func<Action<string>?> _banner;   // KvmHostService.OnStatusBanner (MainWindow ผูกทีหลัง)
    private readonly Func<string?> _activeNodeId;

    private readonly ConcurrentDictionary<string, (byte[] data, string mime)> _pendingClips = new();

    // A1 fix: hash ของ clipboard ล่าสุดที่ "แลกเปลี่ยนแล้ว" (push ไปหรือรับมา) —
    // push เนื้อหาเดิมซ้ำ = echo ทับ clipboard ของอีกฝั่ง (รากของ "copy ไฟล์แล้ว Ctrl+V ไม่วาง")
    private volatile string _lastClipHashExchange = "";

    public ClipboardRouter(OmniConfig config,
        ConcurrentDictionary<string, RemoteNodeClient> sessions,
        FileTransferManager fileTransfer,
        IOmniEventHub eventHub,
        Func<Action<string>?> banner,
        Func<string?> activeNodeId)
    {
        _config = config;
        _sessions = sessions;
        _fileTransfer = fileTransfer;
        _eventHub = eventHub;
        _banner = banner;
        _activeNodeId = activeNodeId;
    }

    internal static string ClipHash(string kind, string data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + data))).ToLowerInvariant();

    public void OnClipboardFromClientText(string text)
    {
        if (!_config.EnableClipboard)
        {
            return;
        }
        OmniLog.Write("clipboard", $"รับจาก agent len={text.Length}");
        try { _banner()?.Invoke($"รับ clipboard จากเครื่องลูกแล้ว ({text.Length} ตัวอักษร)"); } catch { }
        try { _eventHub.Publish(new StatusBannerEvent(Loc.T("banner.clip.received", "รับ clipboard จากเครื่องลูกแล้ว ({0} ตัวอักษร)", text.Length))); } catch { }
        try { Ports.OnClipboardFromClient?.Invoke(text); } catch (Exception ex) { OmniLog.Warn("[clipboard] set failed: " + ex.Message); }
        try { _eventHub.Publish(new ClipboardReceivedEvent(text, _activeNodeId() ?? "")); } catch { }
    }

    public void OnClipboardReceived(string mimeType, byte[] data)
    {
        // v1.8 Advanced: ปิดทิศ node→host ได้
        if (!_config.ClipboardAcceptFromNode)
        {
            OmniLog.Write("clipboard", "ข้าม (รับจาก node ถูกปิดใน Advanced)");
            return;
        }
        if (mimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
        {
            // A1 fix: บันทึกว่าเนื้อหานี้ "แลกเปลี่ยนแล้ว" — push กลับไปหา node เดิมจะถูก dedup
            _lastClipHashExchange = ClipHash("image", Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(data)));
            OmniLog.Write("clipboard", $"รับภาพ PNG จาก agent len={data.Length}");
            try { _banner()?.Invoke($"รับรูปภาพจากเครื่องลูกแล้ว ({data.Length / 1024} KB)"); } catch { }
            try { _eventHub.Publish(new StatusBannerEvent(Loc.T("banner.image.received", "รับรูปภาพจากเครื่องลูกแล้ว ({0} KB)", data.Length / 1024))); } catch { }
            try { Ports.OnClipboardImageFromClient?.Invoke(data); } catch (Exception ex) { OmniLog.Warn("[clipboard] set image failed: " + ex.Message); }
            return;
        }

        if (mimeType.StartsWith("text", StringComparison.OrdinalIgnoreCase))
        {
            var text = Encoding.UTF8.GetString(data);
            _lastClipHashExchange = ClipHash("text", text); // A1 fix
            OnClipboardFromClientText(text);
        }
    }

    public bool TryGetPendingClip(string token, out (byte[] data, string mime) clip)
    {
        return _pendingClips.TryGetValue(token, out clip);
    }

    public void OnFileOfferReceived(string nodeId, string manifestJson)
    {
        var manifest = FileManifest.FromJson(manifestJson);
        if (manifest == null) return;

        var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "OmniLink");
        if (_fileTransfer.Streamer.HandleOffer(manifest, targetDir, out _))
        {
            if (_sessions.TryGetValue(nodeId, out var sess))
            {
                sess.SendTcp(new { type = "file_accept", id = manifest.Id });
                try { _banner()?.Invoke($"กำลังรับไฟล์ {manifest.Files.Count} รายการ ({manifest.TotalBytes / 1024} KB) จากเครื่องลูก..."); } catch { }
                try { _eventHub.Publish(new StatusBannerEvent(Loc.T("banner.files.receiving", "กำลังรับไฟล์ {0} รายการ ({1} KB) จากเครื่องลูก...", manifest.Files.Count, manifest.TotalBytes / 1024))); } catch { }
            }
        }
    }

    public void OnFileAcceptReceived(string nodeId, string transferId)
    {
        Task.Run(async () =>
        {
            await _fileTransfer.StartStreamingOutboundAsync(nodeId, transferId);
        });
    }

    public void OnFileChunkReceived(string nodeId, string transferId, int fileIndex, long offset, string base64Data)
    {
        // SV-M6 fix: ความผิดพลาดจาก disk / race กับ CancelTransfer ห้ามหลุดไปทำ TCP reader ตาย
        try
        {
            _fileTransfer.Streamer.HandleChunk(transferId, fileIndex, offset, base64Data);
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[file] chunk write failed (id={transferId} idx={fileIndex}): {ex.Message}");
        }
    }

    public void OnFileDoneReceived(string nodeId, string transferId, int fileIndex)
    {
        _fileTransfer.Streamer.HandleFileDone(transferId, fileIndex, out var status);
        if (_sessions.TryGetValue(nodeId, out var sess))
        {
            sess.SendTcp(new { type = "file_result", id = transferId, idx = fileIndex, status });
        }
    }

    public void OnFileResultReceived(string nodeId, string transferId, int fileIndex, string status)
    {
        OmniLog.Write("file", $"[outbound] Remote result for file {fileIndex}: {status}");
    }

    public void OnFileCompleteReceived(string nodeId, string transferId, bool success)
    {
        if (_fileTransfer.Streamer.FinalizeInboundSession(transferId, out var completedPaths, out var effect))
        {
            // A1 fix: ชุดไฟล์นี้แลกเปลี่ยนแล้ว — กัน host push ชุดเดิมกลับไปทับ file clipboard ฝั่งลูก
            _lastClipHashExchange = ClipHash("files", string.Join(";", completedPaths) + "|" + effect);
            if (completedPaths.Count > 0)
            {
                try { Ports.OnClipboardFilesFromClient?.Invoke(completedPaths, effect == "cut"); } catch { }
                try { _banner()?.Invoke($"ได้รับไฟล์ {completedPaths.Count} ไฟล์จากเครื่องลูกแล้ว (วางด้วย Ctrl+V)"); } catch { }
                try { _eventHub.Publish(new StatusBannerEvent(Loc.T("banner.files.received", "ได้รับไฟล์ {0} ไฟล์จากเครื่องลูกแล้ว (วางด้วย Ctrl+V)", completedPaths.Count))); } catch { }
            }
        }
    }

    public void TryPushClipboardTo(RemoteNodeClient sess)
    {
        if (!_config.EnableClipboard || !sess.Connected)
        {
            return;
        }
        // v1.7 C2: ViewOnly = ไม่มีอะไรถูกส่งไปเลย (แม้แต่ clipboard)
        if (!OmniPolicy.CanShareClipboard(sess.Config.Policy))
        {
            return;
        }
        // v1.8 Advanced: ปิดทิศ host→node ได้
        if (!_config.ClipboardPushToNode)
        {
            return;
        }
        Task.Run(() =>
        {
            try
            {
                // 1. ตรวจสอบ File Clipboard เป็นอันดับแรก (BATCH 5 / SPEC-FX)
                bool isCut = false;
                var files = Ports.HostFileDropGetter != null ? Ports.HostFileDropGetter(out isCut) : null;
                if (files != null && files.Count > 0)
                {
                    var manifest = FileManifest.Create(files, isCut);
                    if (manifest.Files.Count > 0)
                    {
                        // A1 fix: ชุดไฟล์เดิมที่เพิ่งแลกไป = ไม่ offer ซ้ำ (ทิ้งไว้จะทับ file clipboard ฝั่งลูก)
                        var filesHash = ClipHash("files", string.Join(";", files) + "|" + manifest.Effect);
                        if (filesHash == _lastClipHashExchange)
                        {
                            OmniLog.Write("file", "skip file_offer (ชุดไฟล์เดิมที่เพิ่งแลกไป — กัน echo)");
                            return;
                        }
                        _fileTransfer.Streamer.RegisterOutbound(manifest, files);
                        _fileTransfer.RegisterNodeSender(sess.Config.NodeId, msg => sess.SendTcp(msg));
                        if (sess.SendTcp(new { type = "file_offer", manifest = manifest.ToJson() }))
                        {
                            _lastClipHashExchange = filesHash;
                            OmniLog.Write("file", $"push file_offer {manifest.Files.Count} files ({manifest.TotalBytes}B) id={manifest.Id}");
                            try { _banner()?.Invoke($"กำลังเสนอส่งไฟล์ {manifest.Files.Count} ไฟล์ ({manifest.TotalBytes / 1024} KB) ไปเครื่องลูก..."); } catch { }
                            try { _eventHub.Publish(new StatusBannerEvent(Loc.T("banner.files.sending", "กำลังเสนอส่งไฟล์ {0} ไฟล์ ({1} KB) ไปเครื่องลูก...", manifest.Files.Count, manifest.TotalBytes / 1024))); } catch { }
                        }
                        return;
                    }
                }

                // 2. ตรวจสอบ Image PNG (TASK-008 & TASK-009)
                var pngBytes = Ports.HostPngGetter?.Invoke();
                if (pngBytes != null && pngBytes.Length > 0)
                {
                    var sanitized = OmniImage.Codec.SanitizePng(pngBytes);
                    if (sanitized.Length > 0)
                    {
                        // A1 fix: dedup ภาพเดิม
                        var imgHash = ClipHash("image", Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(sanitized)));
                        if (imgHash == _lastClipHashExchange)
                        {
                            return;
                        }
                        if (sanitized.Length >= 65536) // >= 64KB: ใช้ Paste-on-Demand ACK-Gate
                        {
                            var token = Guid.NewGuid().ToString("N");
                            _pendingClips[token] = (sanitized, "image/png");
                            if (sess.SendTcp(new { type = "clip_meta", mime = "image/png", token, size = sanitized.Length }))
                            {
                                _lastClipHashExchange = imgHash;
                                OmniLog.Write("clipboard", $"push clip_meta image/png token={token} size={sanitized.Length}");
                                try { _banner()?.Invoke($"ส่งข้อมูลรูปภาพ ({sanitized.Length / 1024} KB) ไปเครื่องลูกแล้ว"); } catch { }
                            }
                        }
                        else
                        {
                            // Fast-path: ภาพขนาดเล็ก (< 64KB) ส่งแบบ direct ทันที
                            if (sess.SendTcp(new { type = "clipboard", mime = "image/png", data = Convert.ToBase64String(sanitized) }))
                            {
                                _lastClipHashExchange = imgHash;
                                OmniLog.Write("clipboard", $"push image/png direct len={sanitized.Length}");
                                try { _banner()?.Invoke($"ส่งรูปภาพ ({sanitized.Length / 1024} KB) ไปเครื่องลูกแล้ว"); } catch { }
                            }
                        }
                        return;
                    }
                }

                // 2. ถ้าไม่มีรูป ตรวจสอบ Text
                var text = Ports.HostClipboardGetter?.Invoke();
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                // A1 fix: dedup ข้อความเดิม — เนื้อหาที่เพิ่งแลกไปไม่ยิงซ้ำ
                var textHash = ClipHash("text", text);
                if (textHash == _lastClipHashExchange)
                {
                    return;
                }
                var byteCount = Encoding.UTF8.GetByteCount(text);
                var max = Math.Clamp(_config.ClipboardMaxBytes, 1000, 5_000_000);
                if (byteCount > max)
                {
                    OmniLog.Write("clipboard", $"ข้าม (ใหญ่เกิน limit): {byteCount} > {max}");
                    return;
                }

                if (byteCount >= 65536) // >= 64KB: ข้อความยาวใช้ ACK-Gate
                {
                    var token = Guid.NewGuid().ToString("N");
                    var textBytes = Encoding.UTF8.GetBytes(text);
                    _pendingClips[token] = (textBytes, "text/plain");
                    if (sess.SendTcp(new { type = "clip_meta", mime = "text/plain", token, size = textBytes.Length }))
                    {
                        _lastClipHashExchange = textHash;
                        OmniLog.Write("clipboard", $"push clip_meta text len={text.Length} size={textBytes.Length}");
                        try { _banner()?.Invoke($"ส่งข้อความยาว ({text.Length} ตัวอักษร) ไปเครื่องลูกแล้ว"); } catch { }
                    }
                }
                else
                {
                    // Fast-path: ข้อความสั้น (< 64KB) ส่งแบบ direct ทันที
                    if (sess.SendTcp(new { type = "clipboard", text }))
                    {
                        _lastClipHashExchange = textHash;
                        OmniLog.Write("clipboard", $"push ไป {sess.Config.NodeId} len={text.Length}");
                        try { _banner()?.Invoke($"ส่ง clipboard ไปเครื่องลูกแล้ว ({text.Length} ตัวอักษร)"); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                OmniLog.Warn("[clipboard] push failed: " + ex.Message);
            }
        });
    }
}
