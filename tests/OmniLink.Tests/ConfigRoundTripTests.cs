using System.Text.Json;
using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v1.9: config keys ใหม่ทั้งหมดต้องรอด JSON round-trip และไฟล์ schema เก่า (ก่อน v1.9)
/// ต้องโหลดได้ด้วย default ที่ถูกต้อง ไม่แครช ไม่ทับค่าเดิมของผู้ใช้
/// </summary>
public class ConfigRoundTripTests
{
    private static OmniConfig RoundTrip(OmniConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg);
        return JsonSerializer.Deserialize<OmniConfig>(json)!;
    }

    [Fact]
    public void Hotkeys_SurviveJsonRoundTrip_WithActionsAndOrder()
    {
        var cfg = new OmniConfig();
        cfg.Hotkeys.Add(new HotkeyRuleConfig
        {
            Key = "Ctrl+Alt+F7",
            Actions =
            {
                new HotkeyActionConfig(HotkeyActionConfig.SwitchNode, "psl440-ThinkPad-L440"),
                new HotkeyActionConfig(HotkeyActionConfig.ToggleStream),
            },
        });
        cfg.Hotkeys.Add(new HotkeyRuleConfig
        {
            Key = "Ctrl+Shift+K",
            Actions = { new HotkeyActionConfig(HotkeyActionConfig.CalibrateTh) },
        });

        var loaded = RoundTrip(cfg);
        Assert.Equal(2, loaded.Hotkeys.Count);
        Assert.Equal("Ctrl+Alt+F7", loaded.Hotkeys[0].Key);
        Assert.Equal(2, loaded.Hotkeys[0].Actions.Count);
        Assert.Equal(HotkeyActionConfig.SwitchNode, loaded.Hotkeys[0].Actions[0].Type);
        Assert.Equal("psl440-ThinkPad-L440", loaded.Hotkeys[0].Actions[0].NodeId);
        Assert.Equal(HotkeyActionConfig.CalibrateTh, loaded.Hotkeys[1].Actions[0].Type);
    }

    [Fact]
    public void ModifierMap_SurvivesJsonRoundTrip()
    {
        var cfg = new OmniConfig();
        cfg.Nodes[0].ModifierMap[OmniModifiers.Alt] = OmniModifiers.AltGr;
        cfg.Nodes[0].ModifierMap[OmniModifiers.Super] = OmniModifiers.None;

        var loaded = RoundTrip(cfg);
        var map = loaded.Nodes[0].ModifierMap;
        Assert.Equal(OmniModifiers.AltGr, map[OmniModifiers.Alt]);
        Assert.Equal(OmniModifiers.None, map[OmniModifiers.Super]);
        Assert.Equal(OmniPolicy.Full, loaded.Nodes[0].Policy); // ค่าเดิมไม่โดนกระทบ
    }

    [Fact]
    public void NewV19Keys_SurviveJsonRoundTrip()
    {
        var cfg = new OmniConfig
        {
            UiLanguage = "th-TH",
            CloseButtonAction = "exit",
            LogLevel = "WARN",
            PreventSleep = true,
            FirstRunDone = true,
        };
        var loaded = RoundTrip(cfg);
        Assert.Equal("th-TH", loaded.UiLanguage);
        Assert.Equal("exit", loaded.CloseButtonAction);
        Assert.Equal("WARN", loaded.LogLevel);
        Assert.True(loaded.PreventSleep);
        Assert.True(loaded.FirstRunDone);
    }

    /// <summary>ไฟล์ config ของผู้ใช้เดิม (v1.8, schema_version 2, ไม่มี key ใหม่) ต้องโหลดได้ + ได้ default ถูกต้อง</summary>
    [Fact]
    public void PreV19Config_WithoutNewKeys_LoadsWithSaneDefaults()
    {
        const string oldJson = """
        {
          "schema_version": 2,
          "host_name": "PSPV",
          "host_ip": "192.168.99.55",
          "switch_delay_ms": 123,
          "enable_clipboard": true,
          "nodes": [
            { "node_id": "test-node", "name": "Test", "ip": "192.168.99.2", "position": "left", "enabled": true }
          ]
        }
        """;
        var cfg = JsonSerializer.Deserialize<OmniConfig>(oldJson)!;
        OmniConfig.ValidateAndNormalize(cfg); // ตามที่ Load() เรียกจริง

        Assert.Equal(123, cfg.SwitchDelayMs);          // ค่าเดิมของผู้ใช้ไม่หาย
        Assert.Equal("auto", cfg.UiLanguage);           // key ใหม่ = default
        Assert.Equal("minimize_to_tray", cfg.CloseButtonAction);
        Assert.Equal("INFO", cfg.LogLevel);
        Assert.False(cfg.PreventSleep);
        Assert.False(cfg.FirstRunDone);
        Assert.Empty(cfg.Hotkeys);
        Assert.Empty(cfg.Nodes[0].ModifierMap);
    }

    /// <summary>ValidateAndNormalize ต้องดีดค่าที่ผู้ใช้แก้มือ/ไฟล์เสีย ให้กลับมาปลอดภัย</summary>
    [Fact]
    public void Normalize_RejectsUnknownEnumValues()
    {
        var cfg = new OmniConfig { UiLanguage = "klingon", CloseButtonAction = "explode", LogLevel = "VERBOSE" };
        OmniConfig.ValidateAndNormalize(cfg);
        Assert.Equal("auto", cfg.UiLanguage);
        Assert.Equal("minimize_to_tray", cfg.CloseButtonAction);
        Assert.Equal("INFO", cfg.LogLevel);
    }
}
