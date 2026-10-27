using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OmniLink.Core;
using OmniLink.Core.Events;
using OmniLink.Core.FileExchange;
using OmniLink.Interop;
using OmniLink.ViewModels;
using OmniLink.Views.Settings;

namespace OmniLink.Views;

// godclass-M3: ย้ายมาจาก MainWindow.xaml.cs แบบ move-verbatim (partial class)
// กลุ่ม NODE: per-node editors (chain placement/anchor/policy/device/modmap/position/rename/delete)
public partial class MainWindow
{
    private static string? HostEdgeOf(string? position) => position?.ToLowerInvariant() switch
    {
        "right" => "left",
        "left" => "right",
        "top" => "bottom",
        "bottom" => "top",
        _ => "left",
    };

    /// <summary>v1.9.1: ตั้ง anchor+side พร้อมตรวจวงจร — ถ้าจุดวางมีเจ้าของเดิม สลับที่กัน</summary>
    private void ApplyChainPlacement(string nodeId, string anchorId, string side)
    {
        var node = _config.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null) return;
        if (anchorId == TopologyHost.Id) anchorId = "";

        // วงจร: เดิน anchor chain จาก candidate — ห้ามย้อนมาเจอ node เอง
        var cursor = anchorId;
        var hops = 0;
        while (!string.IsNullOrWhiteSpace(cursor) && hops < 16)
        {
            if (cursor == node.NodeId)
            {
                SetBanner(Loc.T("banner.anchor.cycle", "ปฏิเสธ — anchor นี้จะสร้างวงจร (เครื่องต่อกันเป็นวง)"));
                return;
            }
            cursor = _config.Nodes.FirstOrDefault(n => n.NodeId == cursor)?.Anchor ?? "";
            hops++;
        }

        var oldAnchor = node.Anchor;
        var oldPosition = node.Position;
        node.Anchor = anchorId;
        node.Position = side;

        // เจ้าของเดิมของจุดวาง → สลับไปยืมตำแหน่งเดิมของเรา (เหมือน swap ของ grid เดิม)
        var occupant = _config.Nodes.FirstOrDefault(n =>
            n.NodeId != node.NodeId && n.Anchor == anchorId && n.Position == side);
        if (occupant is not null)
        {
            occupant.Anchor = oldAnchor;
            occupant.Position = oldPosition;
        }

        _mapSignature = null; // บังคับวาดใหม่
        ScheduleConfigSave();
        _kvm.ReloadLayout();
        RefreshUiState();
        SetBanner(Loc.T("banner.anchor.moved", "ย้าย {0} ไปต่อที่ {1} ด้าน{2} แล้ว — สลับขอบอัปเดตทันที",
            node.DisplayName,
            string.IsNullOrWhiteSpace(anchorId) ? Loc.T("node.host", "Host") : (_config.Nodes.FirstOrDefault(n => n.NodeId == anchorId)?.DisplayName ?? anchorId),
            side.ToUpperInvariant()));
    }

    // v1.8 C-lean: cached frozen brushes + icon cache — เดิมสร้าง SolidColorBrush/ImageSource
    // ใหม่ ~8 ชิ้น ต่อ node ต่อ tick (900ms) ทำให้ row "เปลี่ยนเสมอ" + handle churn
    private NodeConfig? GetSelectedTargetNode()
    {
        var nodeId = (NodeListBox?.SelectedItem as NodeListRow)?.NodeId;
        var node = _config.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null)
        {
            SetBanner(Loc.T("banner.select.first", "เลือกเครื่องจากรายการ Connected Devices ก่อน"));
        }
        return node;
    }

    private void OnDeleteNodeClick(object sender, RoutedEventArgs e)
    {
        var selectedRow = NodeListBox?.SelectedItem as NodeListRow;
        var nodeId = selectedRow?.NodeId;
        if (string.IsNullOrEmpty(nodeId))
        {
            SetBanner(Loc.T("banner.select.delete", "กรุณาเลือกอุปกรณ์ที่ต้องการลบจากรายการ Connected Devices"));
            return;
        }

        var node = _config.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node == null) return;

        var displayName = node.DisplayName;
        var confirm = MessageBox.Show(
            Loc.T("delete.confirm.body", "คุณต้องการลบอุปกรณ์ '{0}' ({1}) ออกจาก OmniLink หรือไม่?\n\nการเชื่อมต่อ KVM ของอุปกรณ์นี้จะถูกตัดทันที และข้อมูลจะถูกลบออกจากการตั้งค่า", displayName, node.Ip),
            Loc.T("delete.confirm.title", "ยืนยันการลบอุปกรณ์"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        _kvm.RemoveNode(nodeId);
        PersistConfig(); // S3
        _kvm.ReloadLayout();
        RefreshUiState();
        SetBanner(Loc.T("banner.deleted", "ลบอุปกรณ์ '{0}' ออกจากระบบเรียบร้อยแล้ว", displayName));
    }

    private void OnRenameNodeClick(object sender, RoutedEventArgs e)
    {
        var targetNode = GetSelectedTargetNode();
        if (targetNode is null) return;

        var dialog = new RenameDeviceDialog(targetNode) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.WasChanged)
        {
            PersistConfig(); // S3
            RefreshUiState();
            SetBanner(Loc.T("banner.renamed", "เปลี่ยนชื่อเครื่องเป็น \"{0}\" เรียบร้อยแล้ว", targetNode.DisplayName));
        }
    }

    private void OnResetNodeNameClick(object sender, RoutedEventArgs e)
    {
        var targetNode = GetSelectedTargetNode();
        if (targetNode is null) return;

        targetNode.ResetName();
        PersistConfig(); // S3
        RefreshUiState();
        SetBanner(Loc.T("banner.namerest", "รีเซ็ตชื่อเครื่องกลับเป็นค่าเริ่มต้น \"{0}\" เรียบร้อยแล้ว", targetNode.DisplayName));
    }

    // ================= v1.9.1: CHAIN TOPOLOGY (anchor ต่อเครื่องได้ไม่จำกัด) =================

    /// <summary>เติมรายการ anchor = Host + node อื่นทั้งหมด (ยกเว้นตัวเอง) — เรียกตอน init และ node list เปลี่ยน</summary>
    private void RefreshAnchorCombo()
    {
        if (AnchorCombo is null) return;
        var selected = AnchorCombo.SelectedIndex;
        AnchorCombo.Items.Clear();
        AnchorCombo.Items.Add(Loc.T("devices.anchor.host", "Host (เครื่องแม่)"));
        foreach (var n in _config.Nodes)
        {
            AnchorCombo.Items.Add(n.DisplayName);
        }
        if (selected >= 0 && selected < AnchorCombo.Items.Count)
        {
            AnchorCombo.SelectedIndex = selected;
        }
        else
        {
            AnchorCombo.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// v1.9.1: เปลี่ยน anchor ของ node ที่เลือก — ตรวจวงจร (anchor chain ต้องไม่ย้อนกลับมาที่ตัวเอง)
    /// แล้ว rebuild กราฟ + ยิง set_layout ให้ agent ทุกตัวทันที
    /// </summary>
    private void OnAnchorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingControls) return;
        var node = GetSelectedTargetNode();
        if (node is null) return;

        // index 0 = Host · 1..N = _config.Nodes (ตรงลำดับ RefreshAnchorCombo)
        string newAnchor = AnchorCombo.SelectedIndex <= 0 ? "" :
            (_config.Nodes.ElementAtOrDefault(AnchorCombo.SelectedIndex - 1)?.NodeId ?? "");

        // วงจร: เดินโยง anchor จาก candidate — ถ้าย้อนกลับมาเจอ node เอง = ปฏิเสธ
        var cursor = newAnchor;
        var hops = 0;
        while (!string.IsNullOrWhiteSpace(cursor) && hops < 16)
        {
            if (cursor == node.NodeId)
            {
                SetBanner(Loc.T("banner.anchor.cycle", "ปฏิเสธ — anchor นี้จะสร้างวงจร (เครื่องต่อกันเป็นวง)"));
                _initializingControls = true;
                RefreshAnchorComboSelection(node);
                _initializingControls = false;
                return;
            }
            cursor = _config.Nodes.FirstOrDefault(n => n.NodeId == cursor)?.Anchor ?? "";
            hops++;
        }

        node.Anchor = newAnchor;
        ScheduleConfigSave();
        _kvm.ReloadLayout();
        RefreshUiState();
        SetBanner(Loc.T("banner.anchor.set", "{0} ต่อจาก {1} ด้าน{2} แล้ว — ลากขอบตามผังได้เลย",
            node.DisplayName,
            string.IsNullOrWhiteSpace(newAnchor) ? Loc.T("node.host", "Host") : (_config.Nodes.FirstOrDefault(n => n.NodeId == newAnchor)?.DisplayName ?? newAnchor),
            node.Position));
    }

    /// <summary>sync ตัวเลือก anchor กับ node ที่เลือก (ตอนคลิกรายการอุปกรณ์)</summary>
    private void SyncAnchorComboSelection(NodeConfig node)
    {
        if (AnchorCombo is null) return;
        _initializingControls = true;
        if (string.IsNullOrWhiteSpace(node.Anchor))
        {
            AnchorCombo.SelectedIndex = 0;
        }
        else
        {
            var idx = _config.Nodes.FindIndex(n => n.NodeId == node.Anchor);
            AnchorCombo.SelectedIndex = idx >= 0 ? idx + 1 : 0;
        }
        _initializingControls = false;
    }

    private void RefreshAnchorComboSelection(NodeConfig node)
    {
        if (AnchorCombo is null) return;
        _initializingControls = true;
        if (string.IsNullOrWhiteSpace(node.Anchor))
        {
            AnchorCombo.SelectedIndex = 0;
        }
        else
        {
            var idx = _config.Nodes.FindIndex(n => n.NodeId == node.Anchor);
            AnchorCombo.SelectedIndex = idx >= 0 ? idx + 1 : 0;
        }
        _initializingControls = false;
    }

    /// <summary>v1.9: ประเภทเครื่องของ node ที่เลือก (ย้ายมาจาก popup Advanced เดิม)</summary>
    private void OnDeviceTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingControls) return;
        var node = GetSelectedTargetNode();
        if (node is null) return;
        node.DeviceType = DeviceTypeCombo.SelectedIndex switch
        {
            1 => "desktop",
            2 => "server",
            _ => "laptop"
        };
        ScheduleConfigSave();
        RefreshUiState();
        SetBanner(Loc.T("banner.devicetype", "ปรับประเภทเครื่องของ {0} แล้ว", node.DisplayName));
    }

    // ================= v1.9 C2: MODIFIER REMAP ต่อเครื่อง =================

    /// <summary>index 0 = ไม่แม็ป (ไม่มี entry) · 1-6 = OmniModifiers.Targets (shift/ctrl/alt/super/altgr/none)</summary>
    private static string? ModMapIndexToTarget(int idx) => idx <= 0 ? null : OmniModifiers.Targets[idx - 1];

    private static int ModMapTargetToIndex(string? target) => target is null ? 0 : Array.IndexOf(OmniModifiers.Targets, target) + 1;

    private void SyncModMapControls(NodeConfig node)
    {
        var map = node.ModifierMap ?? new Dictionary<string, string>();
        ModMapShift.SelectedIndex = ModMapTargetToIndex(map.GetValueOrDefault(OmniModifiers.Shift));
        ModMapCtrl.SelectedIndex = ModMapTargetToIndex(map.GetValueOrDefault(OmniModifiers.Ctrl));
        ModMapAlt.SelectedIndex = ModMapTargetToIndex(map.GetValueOrDefault(OmniModifiers.Alt));
        ModMapSuper.SelectedIndex = ModMapTargetToIndex(map.GetValueOrDefault(OmniModifiers.Super));
        ModMapAltGr.SelectedIndex = ModMapTargetToIndex(map.GetValueOrDefault(OmniModifiers.AltGr));
    }

    private void OnModMapChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializingControls) return;
        var node = GetSelectedTargetNode();
        if (node is null) return;
        node.ModifierMap ??= new Dictionary<string, string>();

        void Apply(ComboBox combo, string slot)
        {
            var target = ModMapIndexToTarget(combo.SelectedIndex);
            if (target is null)
            {
                node.ModifierMap.Remove(slot);
            }
            else
            {
                node.ModifierMap[slot] = target;
            }
        }
        Apply(ModMapShift, OmniModifiers.Shift);
        Apply(ModMapCtrl, OmniModifiers.Ctrl);
        Apply(ModMapAlt, OmniModifiers.Alt);
        Apply(ModMapSuper, OmniModifiers.Super);
        Apply(ModMapAltGr, OmniModifiers.AltGr);

        ScheduleConfigSave();
        RefreshUiState();
        SetBanner(Loc.T("banner.modmap", "ปรับแม็ปปุ่ม modifier ของ {0} แล้ว — มีผลทันทีที่คุมเครื่องนี้", node.DisplayName));
    }

    // ================= EVENT HANDLERS =================

    private void OnCalibrateEnClick(object sender, RoutedEventArgs e)
    {
        _kvm.CalibrateAllLanguages(0);
        SetBanner(Loc.T("banner.kb.eng", "ซิงค์ภาษาทุกเครื่องเป็น ENG (US) เรียบร้อยแล้ว"));
    }

    private void OnCalibrateThClick(object sender, RoutedEventArgs e)
    {
        _kvm.CalibrateAllLanguages(1);
        SetBanner(Loc.T("banner.kb.th", "ซิงค์ภาษาทุกเครื่องเป็น TH (ไทย) เรียบร้อยแล้ว"));
    }

    private void OnDeployAgentsClick(object sender, RoutedEventArgs e)
    {
        Task.Run(() =>
        {
            SetBanner(Loc.T("banner.deploying", "กำลังอัปโหลดและรีสตาร์ท Linux Agent ผ่าน SSH..."));
            foreach (var ncfg in _config.Nodes)
            {
                if (ncfg.Enabled)
                {
                    var (_, msg) = _kvm.DeployRemoteAgent(ncfg);
                    SetBanner(msg);
                }
            }
            ScheduleUiRefresh();
        });
    }

    private void OnJumpToSelectedNodeClick(object sender, RoutedEventArgs e)
    {
        if (NodeListBox.SelectedItem is NodeListRow row)
        {
            _kvm.SwitchToNode(row.NodeId, 0.5);
        }
        else
        {
            SetBanner(Loc.T("banner.select.first", "เลือกเครื่องจากรายการ Connected Devices ก่อน"));
        }
    }

    private void OnNodeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodeListBox?.SelectedItem is NodeListRow row)
        {
            var node = _config.Nodes.FirstOrDefault(n => n.NodeId == row.NodeId);
            if (node != null)
            {
                _initializingControls = true;
                L440PositionCombo.SelectedIndex = (node.Position?.ToLowerInvariant()) switch
                {
                    "left" => 1,
                    "top" => 2,
                    "bottom" => 3,
                    _ => 0
                };
                DeviceTypeCombo.SelectedIndex = (node.DeviceType?.ToLowerInvariant()) switch
                {
                    "desktop" => 1,
                    "server" => 2,
                    _ => 0
                };
                RefreshAnchorCombo();
                SyncAnchorComboSelection(node);
                SyncModMapControls(node);
                // v1.7: sync policy combo กับ node ที่เลือก
                if (NodePolicyCombo is not null)
                {
                    _policyComboInitializing = true;
                    NodePolicyCombo.SelectedIndex = OmniPolicy.Normalize(node.Policy) switch
                    {
                        OmniPolicy.ClipboardFiles => 1,
                        OmniPolicy.ViewOnly => 2,
                        _ => 0,
                    };
                    _policyComboInitializing = false;
                }
                _initializingControls = false;
                NodeTargetHeaderText.Text = node.DisplayName;
            }
        }
    }

    private bool _policyComboInitializing;

    /// <summary>v1.7 C4: เปลี่ยน capability ของ node จาก UI — host เว้นส่ง + agent ปฏิเสธเอง (set_policy)</summary>
    private void OnNodePolicyChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_policyComboInitializing || _initializingControls) return;
        var nodeId = (NodeListBox?.SelectedItem as NodeListRow)?.NodeId;
        if (string.IsNullOrEmpty(nodeId)) return;
        var policy = NodePolicyCombo.SelectedIndex switch
        {
            1 => OmniPolicy.ClipboardFiles,
            2 => OmniPolicy.ViewOnly,
            _ => OmniPolicy.Full,
        };
        _kvm.SetNodePolicy(nodeId, policy);
        ScheduleConfigSave();
        SetBanner(Loc.T("banner.policy", "ปรับสิทธิ์ของอุปกรณ์ '{0}' เป็น '{1}' แล้ว — มีผลทันทีทั้งสองฝั่ง", nodeId, policy));
    }

    private void OnL440PositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingControls) return;
        var pos = L440PositionCombo.SelectedIndex switch
        {
            1 => "left",
            2 => "top",
            3 => "bottom",
            _ => "right"
        };
        ApplyNodePosition(pos);
    }

    private void ApplyNodePosition(string pos)
    {
        var targetNode = GetSelectedTargetNode();
        if (targetNode is not null)
        {
            var oldPos = targetNode.Position ?? "right";
            var occupant = _config.Nodes.FirstOrDefault(n => string.Equals(n.Position, pos, StringComparison.OrdinalIgnoreCase) && n.NodeId != targetNode.NodeId);
            if (occupant != null)
            {
                occupant.Position = oldPos;
            }
            targetNode.Position = pos;

            _initializingControls = true;
            var idx = pos switch { "left" => 1, "top" => 2, "bottom" => 3, _ => 0 };
            L440PositionCombo.SelectedIndex = idx;
            _initializingControls = false;
            ScheduleConfigSave();
            _kvm.ReloadLayout();
            RefreshUiState();
            SetBanner(Loc.T("banner.positioned", "จัดตำแหน่งหน้าจอ {0} ไปยัง {1} สำเร็จ", targetNode.DisplayName, pos.ToUpper()));
        }
    }

}
