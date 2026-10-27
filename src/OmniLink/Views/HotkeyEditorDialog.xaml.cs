using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using OmniLink.Core;

namespace OmniLink.Views;

/// <summary>
/// v1.9 C1: ตัวแก้กฎสูตรลัด — จับปุ่มจริงจากคีย์บอร์ด (แบบ KeySequenceWidget ของ Deskflow)
/// + ประกอบ action หลายตัวต่อกฎ + เตือน conflict (ซ้ำเอง / ชน Scroll Lock / ชนฉุกเฉิน) แบบไม่บังคับ
/// </summary>
public partial class HotkeyEditorDialog : Window
{
    public sealed class ActionRow
    {
        public HotkeyActionConfig Config { get; init; } = new();
        public string Summary { get; init; } = "";
        public override string ToString() => Summary;
    }

    private readonly IReadOnlyList<HotkeyRuleConfig> _allRules; // เช็ค conflict (รวมกฎอื่นทั้งหมด)
    private readonly int _editingIndex;                // -1 = กำลังเพิ่มใหม่
    private readonly ObservableCollection<ActionRow> _actions = new();
    private List<(string Id, string Display)> _nodes = new();
    private string? _canonicalKey;

    public HotkeyRuleConfig? Result { get; private set; }

    public HotkeyEditorDialog(HotkeyRuleConfig? existing, int editingIndex, IReadOnlyList<HotkeyRuleConfig> allRules)
    {
        InitializeComponent();
        _allRules = allRules;
        _editingIndex = editingIndex;

        ActionTypeCombo.ItemsSource = HotkeyActionConfig.AllTypes.Select(t => new
        {
            Value = t,
            Display = t switch
            {
                HotkeyActionConfig.SwitchNode => Loc.T("action.switch_node", "สลับไปเครื่องที่เลือก"),
                HotkeyActionConfig.ToggleLock => Loc.T("action.toggle_lock", "สลับล็อก/ปลดล็อกเมาส์"),
                HotkeyActionConfig.ToggleStream => Loc.T("action.toggle_stream", "เปิด/ปิดเสียงจากเครื่องลูก"),
                HotkeyActionConfig.ToggleMic => Loc.T("action.toggle_mic", "เปิด/ปิดส่งไมโครโฟน"),
                HotkeyActionConfig.CalibrateEn => Loc.T("action.calibrate_en", "ซิงค์คีย์บอร์ดทุกเครื่องเป็น ENG"),
                HotkeyActionConfig.CalibrateTh => Loc.T("action.calibrate_th", "ซิงค์คีย์บอร์ดทุกเครื่องเป็นไทย"),
                _ => t,
            },
        }).ToList();
        ActionTypeCombo.DisplayMemberPath = "Display";
        ActionTypeCombo.SelectedIndex = 0;

        ActionList.ItemsSource = _actions;

        if (existing is not null)
        {
            KeyBox.Text = existing.Key;
            _canonicalKey = existing.Key;
            foreach (var a in existing.Actions)
            {
                _actions.Add(new ActionRow { Config = a, Summary = Summarize(a) });
            }
        }
        UpdateSaveState();
    }

    private static string Summarize(HotkeyActionConfig a) => a.Type switch
    {
        HotkeyActionConfig.SwitchNode => Loc.T("action.switch_node.named", "สลับไปเครื่อง: {0}", a.NodeId ?? "?"),
        HotkeyActionConfig.ToggleLock => Loc.T("action.toggle_lock", "สลับล็อก/ปลดล็อกเมาส์"),
        HotkeyActionConfig.ToggleStream => Loc.T("action.toggle_stream", "เปิด/ปิดเสียงจากเครื่องลูก"),
        HotkeyActionConfig.ToggleMic => Loc.T("action.toggle_mic", "เปิด/ปิดส่งไมโครโฟน"),
        HotkeyActionConfig.CalibrateEn => Loc.T("action.calibrate_en", "ซิงค์คีย์บอร์ดทุกเครื่องเป็น ENG"),
        HotkeyActionConfig.CalibrateTh => Loc.T("action.calibrate_th", "ซิงค์คีย์บอร์ดทุกเครื่องเป็นไทย"),
        _ => a.Type,
    };

    /// <summary>ตั้งค่ารายชื่อเครื่องสำหรับ action สลับจอ (เรียกจาก HotkeysPage ก่อน ShowDialog)</summary>
    public void SetNodes(IReadOnlyList<(string Id, string DisplayName)> nodes)
    {
        _nodes = nodes.Select(n => (n.Id, n.DisplayName)).ToList();
        NodeCombo.ItemsSource = _nodes;
        NodeCombo.DisplayMemberPath = "Display";
        NodeCombo.SelectedIndex = _nodes.Count > 0 ? 0 : -1;
    }

    private static string? KeyToToken(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.F1 and <= Key.F24 => ((int)key - (int)Key.F1 + 1).ToString(),
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Left => "Left",
        Key.Up => "Up",
        Key.Right => "Right",
        Key.Down => "Down",
        Key.PrintScreen => "Print",
        Key.Pause => "Pause",
        Key.Apps => "Context",
        _ => null,
    };

    private void OnKeyCapture(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            KeyBox.Text = DescribeHeld(mods) + "…";
            return;
        }

        var token = KeyToToken(key);
        if (token is null)
        {
            KeyBox.Text = DescribeHeld(mods) + "…";
            return;
        }

        var text = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "")
                 + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")
                 + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "")
                 + token;

        if (!HotkeyKey.TryParse(text, out _, out _, out var canonical))
        {
            KeyBox.Text = text;
            _canonicalKey = null;
            ShowKeyError(Loc.T("hotkey.editor.key.invalid", "ปุ่มนี้ใช้เป็นสูตรลัดไม่ได้ — ต้องมี Ctrl/Alt/Shift อย่างน้อย 1 ปุ่ม และไม่รับ Win (ชนกับระบบสลับภาษา)"));
            return;
        }

        KeyBox.Text = canonical;
        _canonicalKey = canonical;
        HideKeyError();
        UpdateConflicts();
        UpdateSaveState();
    }

    private static string DescribeHeld(ModifierKeys mods)
    {
        var s = "";
        if (mods.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Alt)) s += "Alt+";
        if (mods.HasFlag(ModifierKeys.Shift)) s += "Shift+";
        return s;
    }

    private void ShowKeyError(string message)
    {
        KeyErrorText.Text = message;
        KeyErrorText.Visibility = Visibility.Visible;
        UpdateSaveState();
    }

    private void HideKeyError()
    {
        KeyErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnActionTypeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var type = ActionTypeCombo.SelectedIndex >= 0 && ActionTypeCombo.SelectedIndex < HotkeyActionConfig.AllTypes.Length
            ? HotkeyActionConfig.AllTypes[ActionTypeCombo.SelectedIndex]
            : HotkeyActionConfig.SwitchNode;
        NodeCombo.Visibility = type == HotkeyActionConfig.SwitchNode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddActionClick(object sender, RoutedEventArgs e)
    {
        if (ActionTypeCombo.SelectedIndex < 0) return;
        var type = HotkeyActionConfig.AllTypes[ActionTypeCombo.SelectedIndex];
        string? nodeId = null;
        if (type == HotkeyActionConfig.SwitchNode)
        {
            if (NodeCombo.SelectedItem is null)
            {
                ConflictText.Text = Loc.T("hotkey.editor.err.node", "เลือกเครื่องปลายทางก่อนเพิ่ม action สลับจอ");
                ConflictText.Visibility = Visibility.Visible;
                return;
            }
            nodeId = NodeCombo.SelectedIndex >= 0 && NodeCombo.SelectedIndex < _nodes.Count
                ? _nodes[NodeCombo.SelectedIndex].Id
                : null;
        }
        _actions.Add(new ActionRow { Config = new HotkeyActionConfig(type, nodeId), Summary = Summarize(new HotkeyActionConfig(type, nodeId)) });
        HideConflict();
        UpdateSaveState();
    }

    private void OnRemoveActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ActionRow row })
        {
            _actions.Remove(row);
            UpdateSaveState();
        }
    }

    private void UpdateSaveState()
    {
        SaveButton.IsEnabled = _canonicalKey is not null && _actions.Count > 0;
    }

    private void UpdateConflicts()
    {
        if (_canonicalKey is null) return;
        var others = _allRules.Where((_, i) => i != _editingIndex).ToList();
        others.Add(new HotkeyRuleConfig { Key = _canonicalKey });
        var conflicts = HotkeyKey.FindConflicts(others)
            .Where(c => c.StartsWith("duplicate:", StringComparison.Ordinal) && c.EndsWith(_canonicalKey, StringComparison.Ordinal)
                     || c.StartsWith("reserved.", StringComparison.Ordinal))
            .ToList();
        if (conflicts.Count == 0)
        {
            HideConflict();
            return;
        }
        var texts = conflicts.Select(c =>
        {
            var parts = c.Split(':', 2);
            var kind = parts[0];
            return kind == "reserved.scrolllock"
                ? Loc.T("hotkey.conflict.scrolllock", "ชนกับ Scroll Lock (ล็อกเมาส์) — กฎนี้จะทำงานแทนปุ่มเดิม")
                : kind == "reserved.emergency"
                    ? Loc.T("hotkey.conflict.emergency", "ชนกับปุ่มฉุกเฉิน Ctrl+Alt+Backspace — แนะนำเปลี่ยน")
                    : Loc.T("hotkey.conflict.duplicate", "ปุ่มนี้ซ้ำกับกฎอื่น — กฎที่เลือกทั้งหมดจะทำงานพร้อมกัน");
        }).Distinct().ToList();
        ConflictText.Text = string.Join("\n", texts);
        ConflictText.Visibility = Visibility.Visible;
    }

    private void HideConflict() => ConflictText.Visibility = Visibility.Collapsed;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_canonicalKey is null || _actions.Count == 0) return;
        Result = new HotkeyRuleConfig
        {
            Key = _canonicalKey,
            Actions = _actions.Select(r => r.Config).ToList(),
        };
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
