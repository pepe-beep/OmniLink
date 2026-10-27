using System.IO;

namespace OmniLink.Core;

/// <summary>
/// v2.1 onboarding: บอกตำแหน่ง "ของที่ต้องเอาไปเครื่องข้างๆ" — single source of truth
/// ให้ทั้ง wizard และปุ่มเปิดโฟลเดอร์ใช้ร่วมกัน พร้อม fallback ตามลำดับความพร้อม:
/// 1. portable zip (ฝัง Python — เครื่องข้างๆ ไม่ต้องลงอะไรเลย)
/// 2. โฟลเดอร์ node_agent (install_node.bat สำหรับเครื่องที่มี Python แล้ว / .sh สำหรับ Linux)
/// </summary>
public static class NodeInstallers
{
    /// <summary>zip portable สำหรับเครื่อง Windows ข้างๆ — null ถ้าไม่มี (โหมด build เอง/ล้างไฟล์)</summary>
    public static string? PortableZipPath
    {
        get
        {
            var p = Path.Combine(OmniPaths.Root, "installers", "OmniLink-Node-Win-Portable.zip");
            return File.Exists(p) ? p : null;
        }
    }

    /// <summary>โฟลเดอร์ที่ควรเปิดให้ผู้ใช้หยิบของ: มี zip → โฟลเดอร์ installers · ไม่มี → โฟลเดอร์ node_agent</summary>
    public static string FolderToOpen
    {
        get
        {
            var installersDir = Path.Combine(OmniPaths.Root, "installers");
            if (PortableZipPath is not null && Directory.Exists(installersDir)) return installersDir;
            var agentDir = Path.Combine(OmniPaths.Root, "node_agent");
            if (Directory.Exists(agentDir)) return agentDir;
            return OmniPaths.Root;
        }
    }

    /// <summary>โหมดที่ผู้ใช้ได้รับ: "portable" (zip พร้อม) หรือ "manual" (ต้องมี Python บนเครื่องปลายทาง)</summary>
    public static string Mode => PortableZipPath is not null ? "portable" : "manual";
}
