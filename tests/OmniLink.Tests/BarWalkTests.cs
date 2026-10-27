using System.IO;
using System.Text;
using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// Phase 4a — "A Software Tester Walks Into a Bar" ชุด unit:
/// ทุก parser/validator/loader ต้องรอดจาก input ประหลาด (0, -1, 99999999, อิโมจิ, ขยะ, ค่าว่าง, ไฟล์พัง)
/// โดยแยก root ผ่าน OmniPaths.SetRootForTest — ไม่แตะ config จริงของผู้ใช้
/// </summary>
[Collection("OmniPathsSerial")]
public class BarWalkTests : IDisposable
{
    private readonly string _root;

    public BarWalkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "omnilink-bar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        OmniPaths.SetRootForTest(_root);
        OmniLog.Silence();
    }

    public void Dispose()
    {
        OmniPaths.SetRootForTest(null);
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void WriteConfig(string content)
    {
        File.WriteAllText(Path.Combine(_root, "omnilink_config.json"), content, new UTF8Encoding(false));
    }

    private string ReadConfigFile() => File.ReadAllText(Path.Combine(_root, "omnilink_config.json"));

    // ── ไฟล์ config ประหลาด ──

    [Theory]
    [InlineData("{\"schema_version\":2,\"host_na")]                    // ตัดครึ่งกลาง key
    [InlineData("[]")]                                                  // array แทน object
    [InlineData("((((((((((")]                                          // ขยะล้วน
    [InlineData("null")]                                                // null literal
    [InlineData("{\"nodes\": [{\"node_id\":}")]
    public void Load_GarbageConfigFile_FallsBackToDefaults_WithoutOverwriting(string content)
    {
        WriteConfig(content);
        var cfg = OmniConfig.Load();
        Assert.NotNull(cfg);
        Assert.True(cfg.KvmTcpPort > 1024);              // ค่า default ที่ใช้งานได้
        Assert.False(string.IsNullOrWhiteSpace(cfg.AuthToken)); // token ถูกสร้างในหน่วยความจำ
        // SV-M15: ไฟล์พังห้ามถูกทับเงียบๆ — เนื้อหาเดิมต้องอยู่ครบ
        Assert.Equal(content, ReadConfigFile());
    }

    [Fact]
    public void Load_EmptyConfigFile_FallsBackToDefaults()
    {
        WriteConfig("");
        var cfg = OmniConfig.Load();
        Assert.NotNull(cfg);
        Assert.Equal("INFO", cfg.LogLevel);
    }

    /// <summary>UTF-16/BOM ปนกัน — ต้องไม่แครช (อ่านไม่ออก = ใช้ default)</summary>
    [Fact]
    public void Load_Utf16Garbage_DoesNotThrow()
    {
        File.WriteAllBytes(Path.Combine(_root, "omnilink_config.json"),
            Encoding.Unicode.GetBytes("{\"schema_version\":2}"));
        var cfg = OmniConfig.Load();
        Assert.NotNull(cfg);
    }

    // ── ค่าที่ผู้ใช้แก้มือ/จากไฟล์ภายนอกแบบโหด ──

    [Fact]
    public void ValidateAndNormalize_BarValues_AllClamped()
    {
        var cfg = new OmniConfig
        {
            KvmTcpPort = -1,
            KvmUdpPort = 99999999,
            DiscoveryPort = 0,
            PlaybackRtpPort = 65536,
            SwitchDelayMs = -500,
            CornerSize = 99999,
            ClipboardMaxBytes = -42,
            LogMaxBytes = int.MaxValue,
            PingActiveMs = 0,
        };
        cfg.Nodes.Add(new NodeConfig
        {
            Ip = "999.999.999.999",
            NodeId = "",
            Position = "diagonal",
            DeviceType = "toaster",
            Width = 99999,
            Height = -5,
            Policy = "root",
        });
        OmniConfig.ValidateAndNormalize(cfg);

        Assert.Equal(24801, cfg.KvmTcpPort);
        Assert.Equal(24802, cfg.KvmUdpPort);
        Assert.Equal(24800, cfg.DiscoveryPort);
        Assert.Equal(50044, cfg.PlaybackRtpPort);
        Assert.Equal(0, cfg.SwitchDelayMs);
        Assert.Equal(200, cfg.CornerSize);
        Assert.Equal(1000, cfg.ClipboardMaxBytes);
        Assert.Equal(200, cfg.PingActiveMs);

        var node = cfg.Nodes.First(n => n.Ip == "999.999.999.999"); // node ที่เพิ่มทดสอบ (Nodes[0] คือ default ของ config)
        Assert.False(node.Enabled);        // IP ปลอม → ปิด node
        Assert.Equal("right", node.Position);
        Assert.Equal("laptop", node.DeviceType);
        Assert.Equal(960, node.Width);
        Assert.Equal(720, node.Height);
        Assert.Equal(OmniPolicy.Full, node.Policy); // policy ขยะ → fail-open เป็น full
    }

    /// <summary>แผนที่ modifier เป็นขยะ — ต้องคืน keycode เดิม ไม่มีทางแตะคีย์อื่น</summary>
    [Fact]
    public void RemapKeycode_GarbageTargets_NeverCrashNeverTouchOthers()
    {
        var garbage = new Dictionary<string, string>
        {
            ["shift"] = "banana",
            ["ctrl"] = "",
            ["alt"] = "format_c_drive",
            ["super"] = "\u0000\u0001",
        };
        Assert.Equal(OmniModifiers.LAlt, OmniModifiers.RemapKeycode(OmniModifiers.LAlt, garbage));
        Assert.Equal(OmniModifiers.LCtrl, OmniModifiers.RemapKeycode(OmniModifiers.LCtrl, garbage));
        Assert.Equal(0x0DBF, OmniModifiers.RemapKeycode(0x0DBF, garbage)); // INV-2 keyspace ไทย
    }

    // ── Hotkey parser กับของประหลาด ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Ctrl")]
    [InlineData("👍")]
    [InlineData("Ctrl+👍")]
    [InlineData("Ctrl+Win+K")]
    [InlineData("Ctrl++")]
    [InlineData("CTRL+ALT+F1+F2+F3+F4+F5")]
    public void HotkeyParse_BarInputs_NeverThrow_ReturnFalse(string? input)
    {
        var ok = HotkeyKey.TryParse(input, out _, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void HotkeyParse_LongGarbage_NoThrowNoTrue()
    {
        var junk = "Ctrl+" + new string('ز', 300);
        Assert.False(HotkeyKey.TryParse(junk, out _, out _, out _));
        var conflicts = HotkeyKey.FindConflicts(new List<HotkeyRuleConfig>
        {
            new() { Key = junk },
            new() { Key = "👍" },
            new() { Key = "" },
        });
        Assert.Empty(conflicts); // กฎที่ parse ไม่ได้ = ไม่มี conflict (ถูกข้ามตอน build index)
    }

    [Fact]
    public void FindConflicts_EmptyAndGarbageRules_NoCrash()
    {
        var rules = new List<HotkeyRuleConfig>
        {
            new() { Key = "", Actions = { new HotkeyActionConfig(HotkeyActionConfig.ToggleLock) } },
            new() { Key = "not-a-hotkey-🦎" },
            new() { Key = "Ctrl+Alt+Del" }, // Del ไม่ใช่ Backspace — ไม่ชนฉุกเฉิน แต่ parse ได้? Del = 0x2E ไม่อยู่ใน named keys → parse ไม่ผ่าน
        };
        var conflicts = HotkeyKey.FindConflicts(rules);
        Assert.Empty(conflicts);
    }

    // ── ชื่อ/ข้อความ อิโมจิ + control chars ──

    [Fact]
    public void NodeName_EmojiAndUnicode_SurviveJsonRoundTrip()
    {
        var cfg = new OmniConfig();
        cfg.Nodes[0].SetCustomName("🦎 เครื่องทดสอบ 🚀 ภาษาไทย");
        var loaded = RoundTrip(cfg);
        Assert.Equal("🦎 เครื่องทดสอบ 🚀 ภาษาไทย", loaded.Nodes[0].CustomName);
    }

    [Fact]
    public void NodeName_ControlChars_DontCrash()
    {
        var cfg = new OmniConfig();
        cfg.Nodes[0].SetCustomName("bad\u0000\u0001name");
        var loaded = RoundTrip(cfg);
        Assert.NotNull(loaded.Nodes[0].CustomName); // JSON escape ได้ — ไม่แครชพอ
    }

    /// <summary>ชื่อ action ที่ไม่รู้จัก (จากไฟล์ที่แก้มือ) — ต้องเก็บไว้ได้โดยไม่พัง (execution ข้ามเป็น no-op)</summary>
    [Fact]
    public void UnknownActionType_RoundTripsAndIsInert()
    {
        var cfg = new OmniConfig();
        cfg.Hotkeys.Add(new HotkeyRuleConfig
        {
            Key = "Ctrl+Alt+F9",
            Actions = { new HotkeyActionConfig("format_c_drive") },
        });
        var loaded = RoundTrip(cfg);
        Assert.Equal("format_c_drive", loaded.Hotkeys[0].Actions[0].Type);
        Assert.False(HotkeyActionConfig.AllTypes.Contains("format_c_drive"));
    }

    // ── Locale ──

    [Theory]
    [InlineData("th-TH", LocalizationService.Thai)]
    [InlineData("en-US", LocalizationService.English)]
    [InlineData("ja-JP", LocalizationService.English)]
    [InlineData("ar-SA", LocalizationService.English)]
    [InlineData("de-DE", LocalizationService.English)]
    public void Resolve_Auto_FollowsWindowsLanguageWithSaneFallback(string culture, string expected)
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
            Assert.Equal(expected, LocalizationService.Resolve("auto"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = original;
        }
    }

    // ── Log guard ──

    [Fact]
    public void SetMaxLogBytes_BarValues_NeverThrow()
    {
        OmniLog.SetMaxLogBytes(0);
        OmniLog.SetMaxLogBytes(-5);
        OmniLog.SetMaxLogBytes(int.MaxValue);
        OmniLog.SetMaxLogBytes(999_999_999);
        OmniLog.SetMaxLogBytes(5_000_000); // คืนค่าปกติ
    }

    private static OmniConfig RoundTrip(OmniConfig cfg)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(cfg);
        return System.Text.Json.JsonSerializer.Deserialize<OmniConfig>(json)!;
    }
}
