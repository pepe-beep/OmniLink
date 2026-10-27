using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using OmniLink.Core;

namespace OmniLink.Views;

/// <summary>
/// godclass-F3: ประตูกัน "node agent กับ sub-mode รันบนเครื่องเดียวกัน" — ทั้งคู่ผูกพอร์ต 24801/24802
/// เดิมเคยชนกันจน hostd crash-loop (BUGS-SEEN 24 ต.ค.) · ตรวจตอนผู้ใช้จะเปิด sub-mode จาก UI
/// </summary>
internal static class SubModeGuard
{
    /// <summary>คืนข้อความเหตุผลที่ห้ามเปิด sub-mode (null = เปิดได้)</summary>
    public static string? GetBlockReason()
    {
        // 1) พอร์ต 24801 ถูกถืออยู่จริง = ชนกันทันที (agent รันอยู่หรือโปรแกรมอื่น)
        try
        {
            var probe = new TcpListener(IPAddress.Any, OmniProtocol.KvmTcpPort);
            probe.Start();
            probe.Stop();
        }
        catch (SocketException)
        {
            return Loc.T("submode.block.port",
                "เปิด Sub-mode ไม่ได้: มีโปรแกรมถือพอร์ต 24801 อยู่แล้ว (น่าจะเป็น OmniLink node agent บนเครื่องนี้)\n\n" +
                "Node agent กับ Sub-mode ใช้พอร์ตเดียวกัน ห้ามเปิดพร้อมกัน\n" +
                "หยุด node agent (หรือปิด autostart ของมัน) ก่อน แล้วจึงเปิด Sub-mode ได้");
        }

        // 2) agent ติดตั้งไว้ (แม้ตอนนี้ยังไม่รัน) — autostart ของมันจะชนตอนบูตเสมอ
        var agentFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "omnilink", "omnilink_agent.py");
        if (File.Exists(agentFile))
        {
            return Loc.T("submode.block.installed",
                "เปิด Sub-mode ไม่ได้: เครื่องนี้ติดตั้ง OmniLink node agent ไว้ (~/omnilink/omnilink_agent.py)\n\n" +
                "Agent ตั้งเปิดอัตโนมัติตอนบูต และใช้พอร์ตเดียวกับ Sub-mode — ห้ามเปิดพร้อมกัน\n" +
                "ถอนการติดตั้ง agent (ลบ ~/omnilink + ลบ shortcut ใน Startup) ก่อน แล้วจึงเปิด Sub-mode ได้");
        }

        return null;
    }
}
