using System.Diagnostics;
using OmniLink.Core;
using OmniLink.Core.Abstractions;

namespace OmniLink.Platform.Linux;

/// <summary>
/// v2.0: clipboard ของ Linux (โหมด sub / headless) ผ่าน xclip ที่ติดตั้งแล้วกับ agent
/// บทเรียนจาก memory: xclip -i fork daemon ถือ selection ค้าง → ต้องจำกัดเวลารอ + ไม่แตะ stdio
/// หลังปิด · v1 รองรับ text เท่านั้น (ภาพ/ไฟล์ = เฟสถัดไปของโหมด sub)
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
        return false; // เฟสถัดไปของโหมด sub
    }

    public bool SetPng(byte[] pngBytes) => false;

    public bool ContainsFileDropList() => false;

    public IReadOnlyList<string>? GetFileDropList(out bool isCut)
    {
        isCut = false;
        return null;
    }

    public bool SetFileDropList(IReadOnlyList<string> paths, bool isCut) => false;
}
