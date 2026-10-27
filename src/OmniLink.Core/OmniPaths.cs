using System.IO;

namespace OmniLink.Core;

/// <summary>
/// ศูนย์กลางพาธทั้งหมดของโปรแกรม (Single Source of Truth สำหรับ file system layout)
/// ห้าม hardcode absolute path ในไฟล์อื่น — ให้อ้างผ่านคลาสนี้เสมอ
/// ลำดับการค้นหารากโปรเจกต์: env OMNILINK_HOME → walk-up หา marker (omnilink_config.json / AGENTS.md) → beside exe
/// </summary>
public static class OmniPaths
{
    private static readonly object Lock = new();
    private static string? _root;

    public static string Root
    {
        get
        {
            if (_root is null)
            {
                lock (Lock)
                {
                    _root ??= DiscoverRoot();
                }
            }
            return _root;
        }
    }

    public static string ConfigPath => Path.Combine(Root, "omnilink_config.json");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string CrashLogPath => Path.Combine(LogsDir, "crash.log");
    /// <summary>node agent (sub) แบบ Python ที่ host deploy ให้เครื่องลูก — v2.0 เปลี่ยนชื่อจาก linux_agent (สำรองมองโฟลเดอร์เก่าให้ผู้ใช้เดิม)</summary>
    public static string NodeAgentPath
    {
        get
        {
            var newPath = Path.Combine(Root, "node_agent", "omnilink_agent.py");
            if (File.Exists(newPath)) return newPath;
            var legacyPath = Path.Combine(Root, "linux_agent", "omnilink_agent.py");
            return File.Exists(legacyPath) ? legacyPath : newPath;
        }
    }
    public static string StreamDebugLog => Path.Combine(LogsDir, "omnilink_stream.log");
    public static string MicDebugLog => Path.Combine(LogsDir, "omnilink_mic.log");
    public static string StatusJsonPath => Path.Combine(LogsDir, "status.json");
    public static string RunStateDir => Path.Combine(Root, "runstate");

    /// <summary>
    /// v2.0: ตัวค้นหา gst-launch ของแพลตฟอร์ม — แอป Windows เสียบ GstLocator (Registry/PATH/PATH มาตรฐาน)
    /// ตอนบูตก่อนโหลด config ครั้งแรก · ค่าเริ่มต้น = ชื่อไฟล์จาก PATH (ใช้ได้ทั้ง Linux และ Windows ที่ gst อยู่ใน PATH)
    /// </summary>
    public static Func<string> DiscoverGstExe { get; set; } = () =>
        OperatingSystem.IsWindows() ? "gst-launch-1.0.exe" : "gst-launch-1.0";

    /// <summary>ใช้เฉพาะ unit test — บังคับรากโปรเจกต์ (null = ให้ค้นหาใหม่)</summary>
    public static void SetRootForTest(string? path)
    {
        lock (Lock)
        {
            _root = path;
        }
    }

    internal static string DiscoverRoot() => DiscoverRoot(AppContext.BaseDirectory);

    internal static string DiscoverRoot(string startDir)
    {
        var env = Environment.GetEnvironmentVariable("OMNILINK_HOME");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }

        var dir = new DirectoryInfo(startDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "omnilink_config.json")) ||
                File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                return dir.FullName;
            }
        }

        return startDir;
    }
}
