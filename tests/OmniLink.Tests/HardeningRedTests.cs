using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OmniLink.Core;
using OmniLink.Core.FileExchange;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// Red tests จาก AUDIT-REPORT-2026-10-22 (Phase 0 ของแผน v1.6.0 Hardening)
/// เขียนให้ FAIL ก่อนแก้ — แต่ละ Fact อ้างรหัสข้อในรายงาน audit กำกับ
/// </summary>
[Collection("OmniPathsSerial")]
public class HardeningRedTests : IDisposable
{
    private readonly string _testRoot;

    public HardeningRedTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "OmniLink_RedTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testRoot, recursive: true); } catch { }
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static FileManifest MakeManifest(string id, string relPath, byte[] payload) => new()
    {
        Id = id,
        Effect = "copy",
        TotalBytes = payload.Length,
        Files =
        {
            new FileEntry
            {
                RelativePath = relPath,
                Size = payload.Length,
                Sha256 = Sha256Hex(payload),
                IsDirectory = false
            }
        }
    };

    // ── SV-H1: รับ chunk ต้องเช็ค offset กับ WrittenBytes กันข้อมูลเสียเงียบ ──
    [Fact]
    public void HandleChunk_OffsetMismatchAgainstWrittenBytes_ShouldBeRejected()
    {
        var streamer = new ChunkedFileStreamer();
        var payload = Encoding.UTF8.GetBytes("AAAAABBBBBCCCCC");
        var manifest = MakeManifest(Guid.NewGuid().ToString("N"), "offset_check.bin", payload);
        streamer.HandleOffer(manifest, Path.Combine(_testRoot, "t1"), out _);

        var first = Convert.ToBase64String(Encoding.UTF8.GetBytes("AAAAA"));
        Assert.True(streamer.HandleChunk(manifest.Id, 0, 0, first));

        // chunk ที่อ้าง offset ไม่ตรงกับที่เขียนไปแล้ว = ต้องปฏิเสธ ไม่ append มั่ว
        var wrong = Convert.ToBase64String(Encoding.UTF8.GetBytes("BBBBB"));
        Assert.False(streamer.HandleChunk(manifest.Id, 0, 999, wrong));
    }

    // ── SV-H2: resume กลางไฟล์ต้อง verify ผ่าน (hash ต้องครอบ prefix เดิมด้วย) ──
    [Fact]
    public void ResumeFromExistingPartFile_ShouldVerifyFinalHash()
    {
        var streamer = new ChunkedFileStreamer();
        var prefix = Encoding.UTF8.GetBytes("PREFIX-PREFIX-");
        var rest = Encoding.UTF8.GetBytes("REST-REST-REST");
        var full = prefix.Concat(rest).ToArray();
        var manifest = MakeManifest(Guid.NewGuid().ToString("N"), "resume.bin", full);

        // รอบแรก: รับได้ครึ่งเดียวแล้วถูก cancel (state หาย แต่ .part ค้างบนดิสก์)
        streamer.HandleOffer(manifest, Path.Combine(_testRoot, "t2"), out _);
        streamer.HandleChunk(manifest.Id, 0, 0, Convert.ToBase64String(prefix));
        streamer.CancelTransfer(manifest.Id);

        // รอบสอง: offer เดิม → resume จาก offset เดิม → ต้อง verify ผ่าน
        Assert.True(streamer.HandleOffer(manifest, Path.Combine(_testRoot, "t2"), out _));
        Assert.True(streamer.HandleChunk(manifest.Id, 0, prefix.Length, Convert.ToBase64String(rest)));

        var doneOk = streamer.HandleFileDone(manifest.Id, 0, out var status);
        Assert.True(doneOk);
        Assert.Equal("ok", status);

        var finalPath = Path.Combine(_testRoot, "t2", "resume.bin");
        Assert.True(File.Exists(finalPath));
        Assert.Equal(full, File.ReadAllBytes(finalPath));
    }

    // ── SV-M1: ไฟล์ 0 byte ต้องส่งสำเร็จ ไม่ใช่ missing_state ──
    [Fact]
    public void HandleFileDone_ZeroByteFile_ShouldFinalizeAsEmptyFile()
    {
        var streamer = new ChunkedFileStreamer();
        var manifest = MakeManifest(Guid.NewGuid().ToString("N"), "empty.bin", Array.Empty<byte>());
        streamer.HandleOffer(manifest, Path.Combine(_testRoot, "t3"), out _);

        // ไม่มี chunk ใดๆ (sender ไม่ยิง chunk สำหรับไฟล์ว่าง) — done ต้องสร้างไฟล์ว่างและผ่าน
        var doneOk = streamer.HandleFileDone(manifest.Id, 0, out var status);
        Assert.True(doneOk);
        Assert.Equal("ok", status);

        var finalPath = Path.Combine(_testRoot, "t3", "empty.bin");
        Assert.True(File.Exists(finalPath));
        Assert.Equal(0, new FileInfo(finalPath).Length);
    }

    // ── SV-M3: manifest.Id จากเครือข่ายห้ามถูกใช้เป็น path (path traversal) ──
    [Fact]
    public void HandleOffer_MaliciousManifestId_ShouldBeRejected()
    {
        var streamer = new ChunkedFileStreamer();
        var manifest = MakeManifest(@"..\..\evil_target", "x.bin", new byte[] { 1 });

        var accepted = streamer.HandleOffer(manifest, Path.Combine(_testRoot, "t4"), out _);

        Assert.False(accepted);
        Assert.False(Directory.Exists(Path.Combine(_testRoot, "evil_target")));
    }

    // ── SV-M10: AtomicFile ต้องไม่เขียนทับเมื่อได้ครอง mutex ไม่สำเร็จ ──
    [Fact]
    public void WriteAllText_WhenMutexHeldByOthers_MustNotWriteWithoutOwnership()
    {
        var path = Path.Combine(_testRoot, "guarded.json");
        File.WriteAllText(path, "old-content");

        // จำลอง writer อีกตัวถือ mutex ของไฟล์นี้ — ต้องคนละ THREAD (named mutex เป็น reentrant
        // ต่อ thread เดียวกัน ถ้า thread หลักถือเอง WaitOne จะผ่านทันทีและ test ไม่มีวันตรวจจับได้)
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
        using var external = new Mutex(initiallyOwned: false, $@"Local\OMNILINK-{hash}");
        var holderStarted = new ManualResetEventSlim(false);
        var releaseHolder = new ManualResetEventSlim(false);
        var holderReleased = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            external.WaitOne();
            holderStarted.Set();
            // เดิม Sleep(7000): test จบ ~5.2s → using dispose mutex ก่อน thread ตื่น
            // → ReleaseMutex โยน ObjectDisposedException ที่ไม่มีใครจับ → crash testhost (Test Run Aborted)
            releaseHolder.Wait(TimeSpan.FromSeconds(15)); // ถือจนกว่า main ยืนยันเสร็จ (ให้ AtomicFile ชน timeout 5 วิ)
            try { external.ReleaseMutex(); } catch { }
            holderReleased.Set();
        })
        { IsBackground = true };
        holder.Start();
        Assert.True(holderStarted.Wait(TimeSpan.FromSeconds(2)));

        var threw = false;
        try
        {
            AtomicFile.WriteAllText(path, "new-content");
        }
        catch (Exception)
        {
            threw = true; // ต้อง fail อย่างชัดเจน ไม่ใช่เขียนทับแบบไม่มีเจ้าของ
        }

        Assert.True(threw);
        Assert.Equal("old-content", File.ReadAllText(path)); // ไฟล์เดิมต้องไม่ถูกแตะ

        releaseHolder.Set();
        Assert.True(holderReleased.Wait(TimeSpan.FromSeconds(5))); // dispose mutex หลัง holder ปล่อยแล้วเท่านั้น
    }

    // ── SV-M15: JSON พัง ต้องไม่ถูกทับด้วย default config ──
    [Fact]
    public void Load_CorruptJson_MustNotOverwriteConfigFile()
    {
        var root = Path.Combine(_testRoot, "cfgroot_corrupt");
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "omnilink_config.json");
        const string garbage = "{ this is not valid json !!";
        File.WriteAllText(configPath, garbage);

        OmniPaths.SetRootForTest(root);
        try
        {
            OmniConfig.Load(); // ต้องคืน default ใน memory แต่ห้ามเขียนทับไฟล์เดิม

            Assert.Equal(garbage, File.ReadAllText(configPath));
        }
        finally
        {
            OmniPaths.SetRootForTest(null);
        }
    }

    // ── SV-M15: nodes = 0 เป็นสถานะที่ถูกต้องของผู้ใช้ — ห้าม regenerate ทับ ──
    [Fact]
    public void Load_EmptyNodes_MustPreserveUserFile()
    {
        var root = Path.Combine(_testRoot, "cfgroot_empty");
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "omnilink_config.json");
        const string userJson = @"{""schema_version"":2,""host_name"":""USER"",""auth_token"":""MARKER_TOKEN_123"",""nodes"":[]}";
        File.WriteAllText(configPath, userJson);

        OmniPaths.SetRootForTest(root);
        try
        {
            var loaded = OmniConfig.Load();

            Assert.Empty(loaded.Nodes);                          // ต้องเชื่อผู้ใช้ว่าลบจนหมดจริง
            Assert.Equal("MARKER_TOKEN_123", loaded.AuthToken);  // token ห้ามหาย
            Assert.Contains("MARKER_TOKEN_123", File.ReadAllText(configPath)); // ไฟล์เดิมต้องอยู่ครบ
        }
        finally
        {
            OmniPaths.SetRootForTest(null);
        }
    }
}
