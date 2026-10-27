using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniLink.Core.FileExchange;

/// <summary>
/// รายการเอกสารกำกับการโอนย้ายไฟล์ (File Transfer Manifest)
/// กำหนดข้อมูลรวมของชุดไฟล์, แฮชความปลอดภัย, สิทธิ์ และโครงสร้างโฟลเดอร์สัมพัทธ์
/// </summary>
public sealed class FileManifest
{
    private const int BufferSize = 65536; // 64KB (strictly below 85,000 bytes LOH threshold)

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("effect")]
    public string Effect { get; set; } = "copy"; // "copy" or "cut"

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("files")]
    public List<FileEntry> Files { get; set; } = new();

    /// <summary>
    /// ป้องกัน Directory Traversal Attack โดยกรองอักขระอันตราย (.., :, leading slashes)
    /// </summary>
    public static string SanitizeRelativePath(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
        {
            return "unnamed_file";
        }

        var normalized = relPath.Replace('\\', '/').Trim();

        // Strip drive letters if present e.g. "C:"
        var colonIdx = normalized.IndexOf(':');
        if (colonIdx >= 0)
        {
            normalized = normalized[(colonIdx + 1)..].TrimStart('/');
        }

        // Split by segments and drop empty or navigation tokens
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safeParts = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            var p = part.Trim();
            if (p == "." || p == "..")
            {
                continue; // drop traversal
            }
            // Sanitize invalid filename chars
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                p = p.Replace(invalidChar, '_');
            }
            if (!string.IsNullOrWhiteSpace(p))
            {
                safeParts.Add(p);
            }
        }

        return safeParts.Count > 0 ? string.Join('/', safeParts) : "unnamed_file";
    }

    /// <summary>
    /// คำนวณ SHA-256 ของไฟล์โดยใช้ ArrayPool เพื่อไม่ให้เกิด Gen 2 LOH Allocation
    /// </summary>
    public static string ComputeFileSha256(string filePath)
    {
        if (!File.Exists(filePath)) return string.Empty;

        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(BufferSize);

        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

            int bytesRead;
            while ((bytesRead = fs.Read(buffer, 0, BufferSize)) > 0)
            {
                sha.AppendData(buffer, 0, bytesRead);
            }

            var hash = sha.GetHashAndReset();
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    /// <summary>
    /// สร้าง Manifest จากรายชื่อไฟล์/โฟลเดอร์ต้นทาง
    /// </summary>
    public static FileManifest Create(IEnumerable<string> sourcePaths, bool isCut = false)
    {
        var manifest = new FileManifest
        {
            Id = Guid.NewGuid().ToString("N"),
            Effect = isCut ? "cut" : "copy"
        };

        long totalSize = 0;

        foreach (var rawPath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;

            if (File.Exists(rawPath))
            {
                var fi = new FileInfo(rawPath);
                var safeName = SanitizeRelativePath(fi.Name);
                var sha = ComputeFileSha256(rawPath);

                manifest.Files.Add(new FileEntry
                {
                    RelativePath = safeName,
                    Size = fi.Length,
                    Sha256 = sha,
                    IsDirectory = false,
                    LastModifiedUtcTicks = fi.LastWriteTimeUtc.Ticks
                });

                totalSize += fi.Length;
            }
            else if (Directory.Exists(rawPath))
            {
                var di = new DirectoryInfo(rawPath);
                var baseDirName = SanitizeRelativePath(di.Name);

                // Add directory itself
                manifest.Files.Add(new FileEntry
                {
                    RelativePath = baseDirName,
                    Size = 0,
                    Sha256 = string.Empty,
                    IsDirectory = true,
                    LastModifiedUtcTicks = di.LastWriteTimeUtc.Ticks
                });

                // Recursively enumerate files
                var files = Directory.GetFiles(rawPath, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var fi = new FileInfo(file);
                    var relToRoot = Path.GetRelativePath(rawPath, file).Replace('\\', '/');
                    var combinedRel = SanitizeRelativePath($"{baseDirName}/{relToRoot}");
                    var sha = ComputeFileSha256(file);

                    manifest.Files.Add(new FileEntry
                    {
                        RelativePath = combinedRel,
                        Size = fi.Length,
                        Sha256 = sha,
                        IsDirectory = false,
                        LastModifiedUtcTicks = fi.LastWriteTimeUtc.Ticks
                    });

                    totalSize += fi.Length;
                }
            }
        }

        manifest.TotalBytes = totalSize;
        return manifest;
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }

    public static FileManifest? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FileManifest>(json);
        }
        catch
        {
            return null;
        }
    }
}
