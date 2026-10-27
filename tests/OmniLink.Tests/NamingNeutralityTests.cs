using System.IO;
using System.Reflection;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v2.0 OS-Neutrality gate: ห้ามมีคำผูก role ของโปรแกรมเข้ากับ OS ใน "ระดับผลิตภัณฑ์"
/// (ข้อความ UI, สคริปต์ installer, เอกสารที่ผู้ใช้อ่าน) — host/sub ต้องเป็นกลาง
/// ข้อยกเว้นที่ยอมได้: platform adapter ภายใน (WindowsHostInput, LinuxHostInput,
/// WindowsClipboardBridge, X11Controller) ที่บอกความจริงของ implementation
/// </summary>
[Collection("OmniPathsSerial")]
public class NamingNeutralityTests
{
    private static readonly string[] BannedPhrases =
    {
        "Windows Host",
        "Windows host",
        "windows host",
        "Linux Agent",
        "linux agent",
        "Linux Client",
        "linux client",
        "Linux Daemon",
        "linux_agent",
        "Windows PC",
        "เครื่องลูก Linux",
        "เครื่องหลัก Windows",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OmniLink.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("หา repo root ไม่เจอจาก " + AppContext.BaseDirectory);
    }

    public static IEnumerable<object[]> NeutralFiles()
    {
        var root = RepoRoot();
        var targets = new[]
        {
            Path.Combine(root, "src", "OmniLink", "Localization", "Strings.th-TH.xaml"),
            Path.Combine(root, "src", "OmniLink", "Localization", "Strings.en-US.xaml"),
            Path.Combine(root, "node_agent", "install_node.sh"),
            Path.Combine(root, "node_agent", "install_node.bat"),
            Path.Combine(root, "linux_host", "install_host.sh"),
        };
        foreach (var t in targets)
        {
            if (File.Exists(t)) yield return new object[] { t };
        }
    }

    [Theory]
    [MemberData(nameof(NeutralFiles))]
    public void ProductFacingText_HasNoOsRoleWording(string path)
    {
        var content = File.ReadAllText(path);

        foreach (var phrase in BannedPhrases)
        {
            Assert.False(
                content.Contains(phrase, StringComparison.Ordinal),
                $"{Path.GetFileName(path)} มีคำต้องห้าม '{phrase}' — role ของโปรแกรมต้องไม่ถูกผูกกับ OS (เฟส 4 ของแผน OS-Neutral)");
        }
    }

    [Fact]
    public void AgentFolder_IsNeutral()
    {
        var root = RepoRoot();
        Assert.True(Directory.Exists(Path.Combine(root, "node_agent")), "โฟลเดอร์ node_agent ต้องมี (เปลี่ยนชื่อจาก linux_agent แล้ว)");
        Assert.False(Directory.Exists(Path.Combine(root, "linux_agent")), "โฟลเดอร์ linux_agent ต้องถูกเปลี่ยนชื่อเป็น node_agent แล้ว");
    }

    [Fact]
    public void NodeAgentPath_ApiIsNeutral()
    {
        // API กลางชื่อ NodeAgentPath (ไม่ใช่ LinuxAgentPath) — มองโฟลเดอร์ใหม่ก่อน และยังรองรับโฟลเดอร์เก่า
        var prop = typeof(OmniLink.Core.OmniPaths).GetProperty("NodeAgentPath", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(prop);
        Assert.Null(typeof(OmniLink.Core.OmniPaths).GetProperty("LinuxAgentPath", BindingFlags.Public | BindingFlags.Static));
    }
}
