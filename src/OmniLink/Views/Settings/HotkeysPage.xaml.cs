using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using OmniLink.Core;

namespace OmniLink.Views.Settings;

/// <summary>v1.9 C1: หน้าจัดการสูตรลัด (hotkey → action หลายตัว) — แก้เสร็จ reload ดัชนีใน hook ทันที</summary>
public partial class HotkeysPage : UserControl, ISettingsPage
{
    public sealed class HotkeyRow
    {
        public HotkeyRuleConfig Rule { get; init; } = new();
        public int Index { get; init; }
        public string KeyText { get; init; } = "";
        public string ActionsText { get; init; } = "";
    }

    private readonly ISettingsHost Host;
    private readonly ObservableCollection<HotkeyRow> _rows = new();
    public bool IsDirty => false; // หน้าจัดการรายการ (add/edit/remove เกิดและบันทึกทันที) — ไม่มีสถานะแก้ค้าง

    public HotkeysPage(ISettingsHost host)
    {
        InitializeComponent();
        Host = host;
        RuleList.ItemsSource = _rows;
        Loaded += (_, _) => LoadFromConfig();
    }

    public string TitleKey => "settings.section.hotkeys";
    public string IconSymbol => "Keyboard24";

    public void LoadFromConfig()
    {
        _rows.Clear();
        foreach (var rule in Host.Config.Hotkeys)
        {
            _rows.Add(MakeRow(rule, Host.Config.Hotkeys.IndexOf(rule)));
        }
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowConflicts();
    }

    public void RestoreDefaults()
    {
        Host.Config.Hotkeys.Clear();
        Host.Kvm.ReloadHotkeys();
        Host.SaveConfig();
        LoadFromConfig();
        Host.SetBanner(Loc.T("banner.defaults.hotkeys", "ล้างสูตรลัดทั้งหมดแล้ว (คืนค่าเริ่มต้น)"));
    }

    private HotkeyRow MakeRow(HotkeyRuleConfig rule, int index)
    {
        var actionsText = string.Join(" + ", rule.Actions.Select(a => a.Type switch
        {
            HotkeyActionConfig.SwitchNode => Loc.T("action.switch_node.named", "สลับไปเครื่อง: {0}", NodeName(a.NodeId)),
            HotkeyActionConfig.ToggleLock => Loc.T("action.toggle_lock", "สลับล็อก/ปลดล็อกเมาส์"),
            HotkeyActionConfig.ToggleStream => Loc.T("action.toggle_stream", "เปิด/ปิดเสียงจากเครื่องลูก"),
            HotkeyActionConfig.ToggleMic => Loc.T("action.toggle_mic", "เปิด/ปิดส่งไมโครโฟน"),
            HotkeyActionConfig.CalibrateEn => Loc.T("action.calibrate_en", "ซิงค์คีย์บอร์ดทุกเครื่องเป็น ENG"),
            HotkeyActionConfig.CalibrateTh => Loc.T("action.calibrate_th", "ซิงค์คีย์บอร์ดทุกเครื่องเป็นไทย"),
            _ => a.Type,
        }));
        return new HotkeyRow { Rule = rule, Index = index, KeyText = rule.Key, ActionsText = actionsText };

        string NodeName(string? id) =>
            id is null ? "?" : Host.Config.Nodes.FirstOrDefault(n => n.NodeId == id)?.DisplayName ?? id;
    }

    private void Persist()
    {
        Host.Kvm.ReloadHotkeys(); // ดัชนีใน hook ใช้ทันที ไม่ต้องรีสตาร์ต
        Host.SaveConfig();
        LoadFromConfig();
    }

    private void ShowConflicts()
    {
        var conflicts = HotkeyKey.FindConflicts(Host.Config.Hotkeys);
        if (conflicts.Count == 0)
        {
            ConflictText.Visibility = Visibility.Collapsed;
            return;
        }
        var texts = conflicts.Select(c =>
        {
            var parts = c.Split(':', 2);
            var key = parts.Length > 1 ? parts[1] : "";
            return parts[0] switch
            {
                "duplicate" => Loc.T("hotkey.conflict.duplicate.list", "ปุ่ม {0} ถูกใช้ซ้ำหลายกฎ — กฎทั้งหมดจะทำงานพร้อมกันตอนกด", key),
                "reserved.scrolllock" => Loc.T("hotkey.conflict.scrolllock.list", "{0} ชนกับ Scroll Lock (ล็อกเมาส์) — กฎนี้ทำงานแทนปุ่มเดิม", key),
                "reserved.emergency" => Loc.T("hotkey.conflict.emergency.list", "{0} ชนกับปุ่มฉุกเฉิน Ctrl+Alt+Backspace — แนะนำเปลี่ยน", key),
                _ => c,
            };
        });
        ConflictText.Text = Loc.T("hotkey.conflict.header", "⚠ ปุ่มลัดชนกัน:") + "\n" + string.Join("\n", texts);
        ConflictText.Visibility = Visibility.Visible;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new HotkeyEditorDialog(null, -1, Host.Config.Hotkeys) { Owner = Host.Self };
        dialog.SetNodes(Host.Config.Nodes.Select(n => (n.NodeId, n.DisplayName)).ToList());
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        Host.Config.Hotkeys.Add(dialog.Result);
        Persist();
        Host.SetBanner(Loc.T("banner.hotkey.added", "เพิ่มสูตรลัด {0} แล้ว — ใช้ได้ทันที", dialog.Result.Key));
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HotkeyRow row }) Edit(row);
    }

    private void Edit(HotkeyRow row)
    {
        var dialog = new HotkeyEditorDialog(row.Rule, row.Index, Host.Config.Hotkeys) { Owner = Host.Self };
        dialog.SetNodes(Host.Config.Nodes.Select(n => (n.NodeId, n.DisplayName)).ToList());
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        Host.Config.Hotkeys[row.Index] = dialog.Result;
        Persist();
        Host.SetBanner(Loc.T("banner.hotkey.updated", "อัปเดตสูตรลัด {0} แล้ว — ใช้ได้ทันที", dialog.Result.Key));
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HotkeyRow row }) return;
        var key = row.Rule.Key;
        Host.Config.Hotkeys.RemoveAt(row.Index);
        Persist();
        Host.SetBanner(Loc.T("banner.hotkey.removed", "ลบสูตรลัด {0} แล้ว", key));
    }

    public void ApplyLanguage() => LoadFromConfig();
}
