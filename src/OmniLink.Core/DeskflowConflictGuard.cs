using System.Diagnostics;

namespace OmniLink.Core;

/// <summary>
/// ย้ายออกจาก KvmHostService (godclass-K1): กลุ่ม DESK — ตรวจจับ/หยุด Deskflow/Synergy/Barrier
/// ที่แย่ง Hook และพอร์ต โค้ดย้ายแบบ move-verbatim จาก KvmHostService.cs เดิม (commit 45409f9)
/// </summary>
public static class DeskflowConflictGuard
{
    public static bool IsDeskflowServiceRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("deskflow-daemon"))
            {
                using (p) { return true; }
            }
        }
        catch { }
        return false;
    }

    public static void StopConflictingDeskflow()
    {
        if (IsDeskflowServiceRunning())
        {
            OmniLog.Warn("[deskflow] ตรวจพบ service 'Deskflow' (deskflow-daemon) กำลังทำงานในระบบ ซึ่งอาจแย่งจับ Hook และพอร์ต แนะนำให้หยุด service หรือตั้งค่าให้ OmniLink เป็นตัวควบคุมหลัก");
        }

        // ชั้น 1: หยุด Windows service 'Deskflow' (deskflow-daemon) — ใช้ sc.exe แทน powershell.exe (เบากว่ามาก)
        // หมายเหตุ: ต้องมีสิทธิ์ admin ถึงจะหยุด service ได้ ถ้าไม่มีสิทธิ์จะล้มเงียบๆ เหมือนพฤติกรรมเดิม
        try
        {
            using var sc = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "stop Deskflow",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch
        {
        }

        // ชั้น 2: ฆ่าโปรเซส deskflow*/synergy*/barrier* ในเซสชันผู้ใช้
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        var name = p.ProcessName;
                        if (name.StartsWith("deskflow", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("synergy", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("barrier", StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(300);
                        }
                    }
                    catch { }
                }
            }
        }
        catch
        {
        }
    }
}
