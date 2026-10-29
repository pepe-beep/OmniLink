using System.Diagnostics;
using OmniLink.Core;
using OmniLink.Core.Abstractions;

namespace OmniLink.Platform.Linux;

/// <summary>
/// v2.0: clipboard ของ Linux (โหมด sub / headless) ผ่าน xclip ที่ติดตั้งแล้วกับ agent
/// บทเรียนจาก memory: xclip -i fork daemon ถือ selection ค้าง → ต้องจำกัดเวลารอ + ไม่แตะ stdio
/// v2.1 (B9): ครบ text + ภาพ (image/png) + ไฟล์ (x-special/gnome-copied-files + text/uri-list)
/// คำสั่ง/รูปแบบ payload ลอกมาจาก node_agent/omnilink_agent.py (set_png/get_files/set_files) เป๊ะ
/// </summary>
public sealed class LinuxClipboardBridge : IClipboardBridge
{
    private const int TimeoutMs = 2500;

    public string? GetText()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xclip",
                Arguments = "-selection clipboard -o",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(TimeoutMs);
            return p.HasExited && p.ExitCode == 0 ? text : (text.Length > 0 ? text : null);
        }
        catch
        {
            return null;
        }
    }

    public bool SetText(string text)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xclip",
                Arguments = "-selection clipboard -i",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            // xclip daemonizes เพื่อถือ selection — อย่ารอจนเข้าอกเข้าใจ: ให้เวลาจำกัดแล้วปล่อย
            p.WaitForExit(800);
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            return true;
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[sub-clipboard] set text: " + ex.Message);
            return false;
        }
    }

    public bool TryGetPng(out byte[] pngBytes)
    {
        pngBytes = Array.Empty<byte>();
        try
        {
            using var p = StartXclip("-o -selection clipboard -t image/png", readStdOut: true, out _);
            if (p is null) return false;
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(TimeoutMs);
            if (p.HasExited && p.ExitCode == 0 && ms.Length > 0)
            {
                pngBytes = ms.ToArray();
                return true;
            }
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[sub-clipboard] get png: " + ex.Message);
        }
        return false;
    }

    public bool SetPng(byte[] pngBytes)
    {
        try
        {
            using var p = StartXclip("-selection clipboard -t image/png", readStdOut: false, out var stdin);
            if (p is null) return false;
            stdin.BaseStream.Write(pngBytes, 0, pngBytes.Length);
            stdin.BaseStream.Close();
            p.WaitForExit(TimeoutMs); // เทียบเท่า communicate(timeout=3.0) ของ agent
            return true;
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[sub-clipboard] set png: " + ex.Message);
            return false;
        }
    }

    public bool ContainsFileDropList()
    {
        var files = ReadFileList(out _);
        return files is { Count: > 0 };
    }

    public IReadOnlyList<string>? GetFileDropList(out bool isCut)
    {
        var files = ReadFileList(out isCut);
        return files is { Count: > 0 } ? files : null;
    }

    public bool SetFileDropList(IReadOnlyList<string> paths, bool isCut)
    {
        if (paths.Count == 0) return false;

        // uris แบบ file:///... (path.resolve().as_uri() ของ python = Uri.AbsoluteUri ของ .NET)
        var uris = new List<string>();
        foreach (var path in paths)
        {
            try { uris.Add(new Uri(path).AbsoluteUri); } catch { }
        }
        if (uris.Count == 0) return false;

        var action = isCut ? "cut" : "copy";
        try
        {
            // 1) x-special/gnome-copied-files
            using (var p = StartXclip("-selection clipboard -t x-special/gnome-copied-files", readStdOut: false, out var stdin))
            {
                if (p is null) return false;
                var payload = System.Text.Encoding.UTF8.GetBytes(action + "\n" + string.Join("\n", uris) + "\n");
                stdin.BaseStream.Write(payload, 0, payload.Length);
                stdin.BaseStream.Close();
                p.WaitForExit(1500);
            }

            // 2) text/uri-list (สำรองให้ non-GNOME)
            using (var p = StartXclip("-selection clipboard -t text/uri-list", readStdOut: false, out var stdin))
            {
                if (p is null) return false;
                var payload = System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", uris) + "\r\n");
                stdin.BaseStream.Write(payload, 0, payload.Length);
                stdin.BaseStream.Close();
                p.WaitForExit(1500);
            }
            return true;
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[sub-clipboard] set files: " + ex.Message);
            return false;
        }
    }

    // ── helpers ──

    /// <summary>อ่านรายการไฟล์: x-special/gnome-copied-files ก่อน แล้ว fallback text/uri-list (ตาม agent)</summary>
    private static List<string>? ReadFileList(out bool isCut)
    {
        isCut = false;
        // 1) gnome-copied-files: บรรทัดแรก = copy/cut ที่เหลือ = file:// uri
        try
        {
            using var p = StartXclip("-o -selection clipboard -t x-special/gnome-copied-files", readStdOut: true, out _);
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(TimeoutMs);
                if (p.HasExited && p.ExitCode == 0 && output.Length > 0)
                {
                    var lines = output.Trim().Split('\n');
                    if (lines.Length > 0)
                    {
                        isCut = lines[0].Trim().ToLowerInvariant() == "cut";
                        var files = new List<string>();
                        for (var i = 1; i < lines.Length; i++)
                        {
                            var path = UriToPath(lines[i].Trim());
                            if (path is not null) files.Add(path);
                        }
                        if (files.Count > 0) return files;
                    }
                }
            }
        }
        catch { }

        // 2) fallback: text/uri-list
        try
        {
            using var p = StartXclip("-o -selection clipboard -t text/uri-list", readStdOut: true, out _);
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(TimeoutMs);
                if (p.HasExited && p.ExitCode == 0 && output.Length > 0)
                {
                    isCut = false;
                    var files = new List<string>();
                    foreach (var line in output.Split('\n'))
                    {
                        var path = UriToPath(line.Trim());
                        if (path is not null) files.Add(path);
                    }
                    if (files.Count > 0) return files;
                }
            }
        }
        catch { }
        return null;
    }

    private static string? UriToPath(string line)
    {
        if (!line.StartsWith("file://", StringComparison.Ordinal)) return null;
        try
        {
            var path = Uri.UnescapeDataString(new Uri(line).LocalPath);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static Process? StartXclip(string arguments, bool readStdOut, out StreamWriter stdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "xclip",
            Arguments = arguments,
            RedirectStandardInput = !readStdOut,
            RedirectStandardOutput = readStdOut,
            RedirectStandardError = !readStdOut, // DEVNULL ของ agent = กลืน stderr
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        stdin = null!;
        var p = Process.Start(psi);
        if (p is not null && !readStdOut) stdin = p.StandardInput;
        return p;
    }
}
