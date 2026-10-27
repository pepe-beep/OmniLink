using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;

namespace OmniLink.Core.FileExchange;

/// <summary>
/// ตัวจัดการรับ-ส่งสตรีมมิ่งไฟล์แบบแยก Chunk (M4 / BATCH 5)
/// ออกแบบตามมาตรฐาน Zero LOH Allocation (ใช้ ArrayPool ขนาด 48KB -> Base64 64KB)
/// พร้อมการตรวจสอบความถูกต้องด้วย Incremental SHA-256 และ Staging Atomicity
/// </summary>
public sealed class ChunkedFileStreamer
{
    public const int ChunkSize = 32768; // 32KB (Base64 กลายเป็น 43692 ตัวอักษร ปลอดภัยในกรอบ TCP Packet 64KB-128KB และไม่ตก LOH)

    private readonly IOmniEventHub? _eventHub;
    private readonly ConcurrentDictionary<string, InboundSession> _inboundSessions = new();
    private readonly ConcurrentDictionary<string, OutboundSession> _outboundSessions = new();

    public ChunkedFileStreamer(IOmniEventHub? eventHub = null)
    {
        _eventHub = eventHub;
    }

    #region Inbound Session (ฝั่งรับไฟล์)
    private sealed class InboundSession : IDisposable
    {
        public FileManifest Manifest { get; }
        public string StagingDir { get; }
        public string TargetDir { get; }
        public long BytesReceived;
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
        public DateTime LastProgressUtc; // Perf: throttle progress event 10Hz (เดิมยิงทุก chunk 32KB)
        public Dictionary<int, InboundFileState> FileStates { get; } = new();
        public List<string> CompletedTargetPaths { get; } = new();

        public InboundSession(FileManifest manifest, string stagingDir, string targetDir)
        {
            Manifest = manifest;
            StagingDir = stagingDir;
            TargetDir = targetDir;
        }

        public void Dispose()
        {
            foreach (var state in FileStates.Values)
            {
                state.Dispose();
            }
            FileStates.Clear();
        }
    }

    private sealed class InboundFileState : IDisposable
    {
        public FileStream? Stream;
        public IncrementalHash? Hasher;
        public string PartPath = string.Empty;
        public string FinalPath = string.Empty;
        public long WrittenBytes;

        public void Dispose()
        {
            Stream?.Dispose();
            Stream = null;
            Hasher?.Dispose();
            Hasher = null;
        }
    }

    /// <summary>
    /// รับการเสนอไฟล์ (file_offer) และสร้างเซสชันรับไฟล์
    /// </summary>
    public bool HandleOffer(FileManifest manifest, string baseTargetDir, out string stagingDir)
    {
        stagingDir = string.Empty;
        if (manifest == null || string.IsNullOrEmpty(manifest.Id)) return false;

        // SV-M3 fix: Id มาจากเครือข่ายและถูกใช้เป็นชื่อโฟลเดอร์ — ต้องจำกัดรูปแบบกัน path traversal
        if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, @"^[0-9a-fA-F\-]{8,64}$"))
        {
            OmniLog.Warn($"[file] ปฏิเสธ manifest.Id ที่ผิดรูปแบบ: '{manifest.Id[..Math.Min(24, manifest.Id.Length)]}'");
            return false;
        }

        var safeTarget = string.IsNullOrWhiteSpace(baseTargetDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "OmniLink")
            : baseTargetDir;

        stagingDir = Path.Combine(safeTarget, ".omni_staging", manifest.Id);
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(safeTarget);

        // SV-L3 fix: session เดิมที่ถูกแทนที่ต้อง dispose (ปิด stream) ไม่ใช่ทิ้งให้ fd ค้าง
        if (_inboundSessions.TryRemove(manifest.Id, out var replaced))
        {
            replaced.Dispose();
        }

        var session = new InboundSession(manifest, stagingDir, safeTarget);
        _inboundSessions[manifest.Id] = session;
        SweepStaleInboundSessions();

        OmniLog.Write("file", $"[Inbound] Offer accepted: id={manifest.Id} files={manifest.Files.Count} total={manifest.TotalBytes}B");
        return true;
    }

    /// <summary>SV-M5 fix: session รับไฟล์ที่ถูกทิ้งกลางทางเกิน 30 นาที = ปิด fd + ลบทิ้ง (กัน leak สะสม)</summary>
    private void SweepStaleInboundSessions()
    {
        foreach (var kv in _inboundSessions)
        {
            if ((DateTime.UtcNow - kv.Value.CreatedAtUtc).TotalMinutes <= 30) continue;
            if (!_inboundSessions.TryRemove(kv.Key, out var stale)) continue;
            OmniLog.Warn($"[file] inbound session {kv.Key} ค้างเกิน 30 นาที — ปิดและเคลียร์ (sender ทิ้ง transfer กลางทาง)");
            stale.Dispose();
        }
    }

    /// <summary>
    /// รับ chunk ไบต์จากเครือข่ายและบันทึกลง staging .part file พร้อมอัปเดตแฮช
    /// </summary>
    public bool HandleChunk(string transferId, int fileIndex, long offset, string base64Data)
    {
        if (!_inboundSessions.TryGetValue(transferId, out var session))
        {
            OmniLog.Warn($"[file] Chunk for unknown session {transferId}");
            return false;
        }

        if (fileIndex < 0 || fileIndex >= session.Manifest.Files.Count)
        {
            OmniLog.Warn($"[file] Invalid fileIndex {fileIndex}");
            return false;
        }

        var fileEntry = session.Manifest.Files[fileIndex];
        if (fileEntry.IsDirectory)
        {
            return true; // Directory doesn't receive chunk
        }

        if (!session.FileStates.TryGetValue(fileIndex, out var state))
        {
            var safeRel = FileManifest.SanitizeRelativePath(fileEntry.RelativePath);
            var partFile = Path.Combine(session.StagingDir, $"{safeRel}.part");
            var finalFile = Path.Combine(session.TargetDir, safeRel);

            var partDir = Path.GetDirectoryName(partFile);
            if (!string.IsNullOrEmpty(partDir)) Directory.CreateDirectory(partDir);

            var fs = new FileStream(partFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536);
            var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            if (offset > 0 && offset <= fs.Length)
            {
                // SV-H2 fix: resume กลางไฟล์ — hash ต้องครอบ prefix ที่เขียนไว้แล้วด้วย
                // (เดิม hasher เริ่มใหม่จากศูนย์ → HandleFileDone ตรวจไม่ผ่านเป็นไปไม่ได้)
                fs.Seek(0, SeekOrigin.Begin);
                var prefixBuf = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    long remaining = offset;
                    while (remaining > 0)
                    {
                        var read = fs.Read(prefixBuf, 0, (int)Math.Min(prefixBuf.Length, remaining));
                        if (read <= 0) break;
                        hasher.AppendData(prefixBuf, 0, read);
                        remaining -= read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(prefixBuf);
                }
                fs.Seek(offset, SeekOrigin.Begin);
            }
            else if (offset == 0)
            {
                fs.SetLength(0);
            }
            else
            {
                // อ้าง offset เกินขนาด .part ที่มีจริง = ประกาศ resume ที่โกง — ปฏิเสธ
                fs.Dispose();
                OmniLog.Warn($"[file] chunk offset {offset} เกินขนาด .part ({fs.Length}B) — reject");
                return false;
            }

            state = new InboundFileState
            {
                Stream = fs,
                Hasher = hasher,
                PartPath = partFile,
                FinalPath = finalFile,
                WrittenBytes = fs.Position
            };
            session.FileStates[fileIndex] = state;
        }
        else if (offset != state.WrittenBytes)
        {
            // SV-H1 fix: chunk ซ้ำ/สลับลำดับ — เดิม append ต่อท้ายเงียบๆ จน hash เพี้ยนไปตรวจที่ปลายทาง
            OmniLog.Warn($"[file] chunk offset {offset} != WrittenBytes {state.WrittenBytes} (out-of-order/duplicate) — reject");
            return false;
        }

        // SV-M5 fix: กันเขียนเกินขนาดที่ manifest ประกาศ (peer พัง/ประสงค์ร้ายยัดจนดิสก์เต็ม)
        if (state.WrittenBytes + (base64Data.Length * 3) / 4 > fileEntry.Size + 4)
        {
            OmniLog.Warn($"[file] chunk เกินขนาดไฟล์ที่ประกาศ ({fileEntry.Size}B) — reject");
            return false;
        }

        var pool = ArrayPool<byte>.Shared;
        // Estimate byte count from base64
        var maxBytes = (base64Data.Length * 3) / 4 + 4;
        var buffer = pool.Rent(maxBytes);

        try
        {
            if (Convert.TryFromBase64String(base64Data, buffer, out var bytesWritten))
            {
                state.Stream!.Write(buffer, 0, bytesWritten);
                state.Hasher!.AppendData(buffer, 0, bytesWritten);
                state.WrittenBytes += bytesWritten;

                Interlocked.Add(ref session.BytesReceived, bytesWritten);

                // Perf: throttle 10Hz — เดิม publish ต่อทุก chunk (subscriber list snapshot + LOH string ทุกครั้ง)
                var nowUtc = DateTime.UtcNow;
                if ((nowUtc - session.LastProgressUtc).TotalMilliseconds >= 100)
                {
                    session.LastProgressUtc = nowUtc;
                    var pct = session.Manifest.TotalBytes > 0
                        ? Math.Min(100.0, (double)session.BytesReceived / session.Manifest.TotalBytes * 100.0)
                        : 100.0;

                    _eventHub?.Publish(new FileTransferProgressEvent(
                        transferId,
                        fileEntry.RelativePath,
                        session.BytesReceived,
                        session.Manifest.TotalBytes,
                        pct
                    ));
                }

                return true;
            }
            return false;
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    /// <summary>
    /// เมื่อรับ chunk ของไฟล์หนึ่งครบแล้ว ตรวจสอบ SHA-256 และย้ายจาก .part ไปยังพาธจริง
    /// </summary>
    public bool HandleFileDone(string transferId, int fileIndex, out string status)
    {
        status = "error";
        if (!_inboundSessions.TryGetValue(transferId, out var session)) return false;

        if (fileIndex < 0 || fileIndex >= session.Manifest.Files.Count) return false;
        var fileEntry = session.Manifest.Files[fileIndex];

        if (fileEntry.IsDirectory)
        {
            var safeRel = FileManifest.SanitizeRelativePath(fileEntry.RelativePath);
            var targetDir = Path.Combine(session.TargetDir, safeRel);
            Directory.CreateDirectory(targetDir);
            session.CompletedTargetPaths.Add(targetDir);
            status = "ok";
            return true;
        }

        if (!session.FileStates.TryGetValue(fileIndex, out var state) || state.Stream == null)
        {
            // SV-M1 fix: ไฟล์ 0 byte — sender ไม่ยิง chunk เลย (loop ไม่รัน) จึงไม่มี state;
            // สร้างไฟล์ว่างตาม manifest แทนการ fail ทั้ง transfer
            if (fileEntry.Size == 0)
            {
                var emptyRel = FileManifest.SanitizeRelativePath(fileEntry.RelativePath);
                var emptyFinal = Path.Combine(session.TargetDir, emptyRel);
                var emptyDir = Path.GetDirectoryName(emptyFinal);
                if (!string.IsNullOrEmpty(emptyDir)) Directory.CreateDirectory(emptyDir);
                File.WriteAllBytes(emptyFinal, Array.Empty<byte>());
                session.CompletedTargetPaths.Add(emptyFinal);
                status = "ok";
                OmniLog.Write("file", $"[Inbound] Empty file created: {emptyFinal}");
                return true;
            }
            status = "missing_state";
            return false;
        }

        state.Stream.Flush();
        state.Stream.Dispose();
        state.Stream = null;

        var computedSha = Convert.ToHexString(state.Hasher!.GetHashAndReset()).ToLowerInvariant();

        if (!string.Equals(computedSha, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            OmniLog.Warn($"[file] SHA-256 mismatch for {fileEntry.RelativePath}: got {computedSha}, expected {fileEntry.Sha256}");
            status = "hash_mismatch";
            return false;
        }

        // Rename atomically — SV-M2 fix: Move(overwrite) แทน Delete-แล้วค่อย-Move
        // (เดิมถ้า crash/AV lock คั่นกลาง = ไฟล์เดิมของผู้ใช้หายแล้วยังไม่มีไฟล์ใหม่มาแทน)
        var finalDir = Path.GetDirectoryName(state.FinalPath);
        if (!string.IsNullOrEmpty(finalDir)) Directory.CreateDirectory(finalDir);

        File.Move(state.PartPath, state.FinalPath, overwrite: true);

        session.CompletedTargetPaths.Add(state.FinalPath);
        status = "ok";
        OmniLog.Write("file", $"[Inbound] File verified and saved: {state.FinalPath} ({fileEntry.Size}B)");
        return true;
    }

    /// <summary>
    /// สรุปผลการรับไฟล์ทั้งหมดใน Manifest
    /// </summary>
    public bool FinalizeInboundSession(string transferId, out List<string> completedPaths, out string effect)
    {
        completedPaths = new List<string>();
        effect = "copy";

        if (!_inboundSessions.TryRemove(transferId, out var session)) return false;

        completedPaths = session.CompletedTargetPaths;
        effect = session.Manifest.Effect;

        try
        {
            if (Directory.Exists(session.StagingDir))
            {
                Directory.Delete(session.StagingDir, true);
            }
        }
        catch { }

        session.Dispose();

        _eventHub?.Publish(new FileTransferCompletedEvent(
            transferId,
            true,
            completedPaths.Count,
            session.Manifest.TotalBytes,
            session.TargetDir,
            null
        ));

        OmniLog.Write("file", $"[Inbound] Transfer {transferId} completed successfully: {completedPaths.Count} items");
        return true;
    }
    #endregion

    #region Outbound Session (ฝั่งส่งไฟล์)
    private sealed class OutboundSession
    {
        public FileManifest Manifest { get; }
        public Dictionary<int, string> SourceFileMap { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public DateTime LastProgressUtc; // Perf: throttle progress event 10Hz

        public OutboundSession(FileManifest manifest)
        {
            Manifest = manifest;
        }
    }

    public void RegisterOutbound(FileManifest manifest, IEnumerable<string> sourcePaths)
    {
        var session = new OutboundSession(manifest);

        // Map manifest files to local absolute paths
        int idx = 0;
        foreach (var entry in manifest.Files)
        {
            foreach (var src in sourcePaths)
            {
                if (File.Exists(src))
                {
                    var fi = new FileInfo(src);
                    if (FileManifest.SanitizeRelativePath(fi.Name) == entry.RelativePath)
                    {
                        session.SourceFileMap[idx] = src;
                        break;
                    }
                }
                else if (Directory.Exists(src))
                {
                    var di = new DirectoryInfo(src);
                    var baseDir = FileManifest.SanitizeRelativePath(di.Name);
                    // SV-M4 fix: ต้องมี separator ต่อท้าย — เดิม "Docs" match "DocsBackup/..." ผิดไฟล์
                    if (entry.RelativePath.StartsWith(baseDir + "/", StringComparison.Ordinal))
                    {
                        var relSub = entry.RelativePath.Substring(baseDir.Length).TrimStart('/');
                        var fullSub = Path.Combine(src, relSub);
                        if (File.Exists(fullSub) || Directory.Exists(fullSub))
                        {
                            session.SourceFileMap[idx] = fullSub;
                            break;
                        }
                    }
                }
            }
            idx++;
        }

        _outboundSessions[manifest.Id] = session;
    }

    /// <summary>
    /// สตรีมไฟล์ออกไปยัง Callback ส่ง TCP ในลักษณะ Chunked Streaming โดยใช้ ArrayPool
    /// </summary>
    public async Task<bool> StreamOutboundAsync(
        string transferId,
        Func<object, bool> sendTcpFunc,
        CancellationToken externalCt = default)
    {
        if (!_outboundSessions.TryGetValue(transferId, out var session))
        {
            OmniLog.Warn($"[file] Outbound session {transferId} not found");
            return false;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token, externalCt);
        var ct = linkedCts.Token;

        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(ChunkSize);

        try
        {
            long totalBytesSent = 0;

            for (int fileIdx = 0; fileIdx < session.Manifest.Files.Count; fileIdx++)
            {
                ct.ThrowIfCancellationRequested();

                var fileEntry = session.Manifest.Files[fileIdx];
                if (fileEntry.IsDirectory)
                {
                    // Directory entry
                    sendTcpFunc(new { type = "file_done", id = transferId, idx = fileIdx });
                    continue;
                }

                if (!session.SourceFileMap.TryGetValue(fileIdx, out var localPath) || !File.Exists(localPath))
                {
                    OmniLog.Warn($"[file] Local source file missing for index {fileIdx}: {fileEntry.RelativePath}");
                    return false;
                }

                using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
                long offset = 0;
                int bytesRead;

                while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(0, ChunkSize), ct)) > 0)
                {
                    var base64 = Convert.ToBase64String(buffer, 0, bytesRead);

                    var sent = sendTcpFunc(new
                    {
                        type = "file_chunk",
                        id = transferId,
                        idx = fileIdx,
                        offset,
                        data = base64
                    });

                    if (!sent)
                    {
                        OmniLog.Warn($"[file] Failed to send file_chunk for {fileEntry.RelativePath} at offset {offset}");
                        return false;
                    }

                    offset += bytesRead;
                    totalBytesSent += bytesRead;

                    // Perf: throttle 10Hz เหมือนฝั่งรับ
                    var nowUtc = DateTime.UtcNow;
                    if ((nowUtc - session.LastProgressUtc).TotalMilliseconds >= 100)
                    {
                        session.LastProgressUtc = nowUtc;
                        var pct = session.Manifest.TotalBytes > 0
                            ? Math.Min(100.0, (double)totalBytesSent / session.Manifest.TotalBytes * 100.0)
                            : 100.0;

                        _eventHub?.Publish(new FileTransferProgressEvent(
                            transferId,
                            fileEntry.RelativePath,
                            totalBytesSent,
                            session.Manifest.TotalBytes,
                            pct
                        ));
                    }

                    // Small non-blocking yield to allow other socket operations
                    await Task.Yield();
                }

                // Finish this file
                sendTcpFunc(new { type = "file_done", id = transferId, idx = fileIdx });
            }

            sendTcpFunc(new { type = "file_complete", id = transferId, success = true });
            OmniLog.Write("file", $"[Outbound] Finished streaming transfer {transferId} ({totalBytesSent}B)");
            return true;
        }
        catch (OperationCanceledException)
        {
            OmniLog.Write("file", $"[Outbound] Transfer {transferId} cancelled");
            return false;
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[file] Outbound stream exception: {ex.Message}");
            return false;
        }
        finally
        {
            pool.Return(buffer);
            _outboundSessions.TryRemove(transferId, out _);
        }
    }

    public void CancelTransfer(string transferId)
    {
        if (_outboundSessions.TryGetValue(transferId, out var outSess))
        {
            outSess.Cts.Cancel();
        }
        if (_inboundSessions.TryRemove(transferId, out var inSess))
        {
            inSess.Dispose();
        }
    }
    #endregion
}
