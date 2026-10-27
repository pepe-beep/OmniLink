using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using OmniLink.Core.Events;
using OmniLink.Core.FileExchange;
using Xunit;

namespace OmniLink.Tests;

public class FileExchangeTests : IDisposable
{
    private readonly string _testRoot;

    public FileExchangeTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "OmniLink_FileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }

    [Theory]
    [InlineData("../../etc/passwd", "etc/passwd")]
    [InlineData(@"..\..\Windows\System32\cmd.exe", "Windows/System32/cmd.exe")]
    [InlineData("C:\\test\\folder/file.txt", "test/folder/file.txt")]
    [InlineData("simple.txt", "simple.txt")]
    [InlineData("", "unnamed_file")]
    public void SanitizeRelativePath_ShouldPreventTraversal(string input, string expected)
    {
        var sanitized = FileManifest.SanitizeRelativePath(input);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void FileManifest_Create_ShouldEnumerateFilesAndComputeSha256()
    {
        var srcDir = Path.Combine(_testRoot, "source_dir");
        Directory.CreateDirectory(srcDir);

        var file1 = Path.Combine(srcDir, "doc.txt");
        var content1 = "OmniLink high-speed chunked transfer test content 12345";
        File.WriteAllText(file1, content1);

        var subDir = Path.Combine(srcDir, "sub");
        Directory.CreateDirectory(subDir);
        var file2 = Path.Combine(subDir, "data.bin");
        var content2 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        File.WriteAllBytes(file2, content2);

        var manifest = FileManifest.Create(new[] { srcDir }, isCut: true);

        Assert.NotNull(manifest);
        Assert.Equal("cut", manifest.Effect);
        Assert.True(manifest.TotalBytes > 0);
        Assert.Contains(manifest.Files, f => f.RelativePath.EndsWith("doc.txt") && !f.IsDirectory && !string.IsNullOrEmpty(f.Sha256));
        Assert.Contains(manifest.Files, f => f.RelativePath.EndsWith("data.bin") && !f.IsDirectory && !string.IsNullOrEmpty(f.Sha256));

        // Test Json serialization
        var json = manifest.ToJson();
        var deserialized = FileManifest.FromJson(json);
        Assert.NotNull(deserialized);
        Assert.Equal(manifest.Id, deserialized.Id);
        Assert.Equal(manifest.Files.Count, deserialized.Files.Count);
        Assert.Equal(manifest.TotalBytes, deserialized.TotalBytes);
    }

    [Fact]
    public void ChunkedFileStreamer_InboundFlow_ShouldVerifyHashAndMoveToTarget()
    {
        var eventHub = new OmniEventHub();
        var streamer = new ChunkedFileStreamer(eventHub);

        var testFile = Path.Combine(_testRoot, "transfer_test.dat");
        var payload = Encoding.UTF8.GetBytes("High performance file transfer payload across machines with SHA-256 verification.");
        File.WriteAllBytes(testFile, payload);

        var expectedSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var manifest = new FileManifest
        {
            Id = Guid.NewGuid().ToString("N"),
            Effect = "copy",
            TotalBytes = payload.Length,
            Files =
            {
                new FileEntry
                {
                    RelativePath = "transfer_test.dat",
                    Size = payload.Length,
                    Sha256 = expectedSha,
                    IsDirectory = false
                }
            }
        };

        var targetDir = Path.Combine(_testRoot, "target");
        var accepted = streamer.HandleOffer(manifest, targetDir, out var stagingDir);
        Assert.True(accepted);
        Assert.True(Directory.Exists(stagingDir));

        // Send chunks
        var base64 = Convert.ToBase64String(payload);
        var chunkOk = streamer.HandleChunk(manifest.Id, 0, 0, base64);
        Assert.True(chunkOk);

        // File done -> hash verification
        var doneOk = streamer.HandleFileDone(manifest.Id, 0, out var status);
        Assert.True(doneOk);
        Assert.Equal("ok", status);

        // Finalize
        var finalizeOk = streamer.FinalizeInboundSession(manifest.Id, out var completedPaths, out var effect);
        Assert.True(finalizeOk);
        Assert.Single(completedPaths);
        Assert.Equal("copy", effect);

        var finalPath = Path.Combine(targetDir, "transfer_test.dat");
        Assert.True(File.Exists(finalPath));
        var writtenBytes = File.ReadAllBytes(finalPath);
        Assert.Equal(payload, writtenBytes);
    }

    [Fact]
    public void ChunkedFileStreamer_HashMismatch_ShouldBeRejected()
    {
        var streamer = new ChunkedFileStreamer();

        var manifest = new FileManifest
        {
            Id = Guid.NewGuid().ToString("N"),
            Effect = "copy",
            TotalBytes = 10,
            Files =
            {
                new FileEntry
                {
                    RelativePath = "tampered.dat",
                    Size = 10,
                    Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                    IsDirectory = false
                }
            }
        };

        var targetDir = Path.Combine(_testRoot, "target_mismatch");
        streamer.HandleOffer(manifest, targetDir, out _);

        var dataB64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        streamer.HandleChunk(manifest.Id, 0, 0, dataB64);

        var doneOk = streamer.HandleFileDone(manifest.Id, 0, out var status);
        Assert.False(doneOk);
        Assert.Equal("hash_mismatch", status);
    }

    [Fact]
    public void ChunkSize_MustBeUnder85000_ForZeroLOH()
    {
        // Audit rule: chunks must NOT exceed 85,000 bytes so they stay in Gen 0 / Gen 1
        Assert.True(ChunkedFileStreamer.ChunkSize < 85000);
        var base64Len = (ChunkedFileStreamer.ChunkSize * 4) / 3;
        Assert.True(base64Len < 85000);
    }
}
