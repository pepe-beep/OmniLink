using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;
using OmniLink.Core.Strategies;
using OmniLink.Interop;
using OmniLink.ViewModels;
using OmniLink.Core.FileExchange;
using OmniLink.Themes;

namespace OmniLink.Views;

// godclass-M2: ย้ายมาจาก MainWindow.xaml.cs แบบ move-verbatim (partial class — โค้ดเดิมทุกตัวอักษร)
// กลุ่ม MAP/GEST/DRAG: render ผัง topology + ghost + drag&drop + pan/zoom/fit + badge cache
public partial class MainWindow
{
    // ================= v1.9.1: DYNAMIC TOPOLOGY MAP (Canvas — สไตล์เดิม: ghost "Place..." + seam) =================

    public class ScreenDragPayload
    {
        public string NodeId { get; set; } = "";
        public string SourcePosition { get; set; } = "";
    }

    private string? _mapSignature;
    private Point _mapDragStart;
    private string? _mapDragNode;
    private const double MapCellW = 300, MapCellH = 190, MapCardW = 240, MapCardH = 118, MapPad = 28;

    // ── v1.9.1 Pan/Zoom: screen = scale * canvasPoint + (tx, ty) — MapViewportMath ──
    private readonly MatrixTransform _mapTransform = new(MapViewportMath.Matrix(1, 0, 0));
    private readonly MapGestureState _mapGesture = new();
    private bool _mapFitPending = true;

    /// <summary>วาดผังใหม่เมื่อ layout/status เปลี่ยน (เทียบ signature ก่อน — กัน rebuild ทุก tick 900ms)</summary>
    private void RenderTopologyMap()
    {
        if (TopologyCanvas is null) return;
        var graph = _kvm.TopologyView;

        var sig = "v4";
        foreach (var n in _config.Nodes)
        {
            _kvm.SessionViews.TryGetValue(n.NodeId, out var sess);
            sig += "|" + n.NodeId + "," + n.Anchor + "," + n.Position + "," + n.Enabled + ","
                 + (sess is not null && sess.Connected ? 1 : 0) + "," + (_kvm.ActiveNodeId == n.NodeId ? 1 : 0);
        }
        sig += "|hostActive=" + (_kvm.ActiveNodeId is null ? 1 : 0);
        if (sig == _mapSignature) return;
        _mapSignature = sig;

        TopologyCanvas.Children.Clear();

        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var kv in graph.Cells)
        {
            if (kv.Key == TopologyHost.Id) continue;
            minX = Math.Min(minX, kv.Value.X); maxX = Math.Max(maxX, kv.Value.X);
            minY = Math.Min(minY, kv.Value.Y); maxY = Math.Max(maxY, kv.Value.Y);
        }
        var spanX = maxX - minX + 1;
        var spanY = maxY - minY + 1;
        TopologyCanvas.Width = Math.Max(600, spanX * MapCellW);
        TopologyCanvas.Height = Math.Max(300, spanY * MapCellH);
        if (_mapFitPending) ApplyMapFit(); // วาดครั้งแรก = จัดพอดี viewport ทันที
        double OriginX(double cx) => MapPad + (cx - minX) * MapCellW + (MapCardW / 2);
        double OriginY(double cy) => MapPad + (cy - minY) * MapCellH + (MapCardH / 2);

        // ── Seam bars สไตล์เดิม: แท่งมนระหว่างคู่ที่ติดกัน (แทนลูกศรคู่ซ้อน) ──
        var seamAccent = (Brush?)TryFindResource("FluentAccentPrimaryBrush") ?? Brushes.DodgerBlue;
        var seamIdle = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        var drawnPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in graph.Cells)
        {
            // ทิศ Right/Bottom ครอบทุก adjacency (แนวนอน = ฝั่งซ้ายมี Right, แนวตั้ง = ตัวบนมี Bottom)
            // dedupe ด้วย unordered pair key — คู่ host/ลูกบางทิศมีขอบเดียว (ลูกมี Bottom, host มีแต่ Top)
            foreach (var edge in new[] { ScreenEdge.Right, ScreenEdge.Bottom })
            {
                var nb = graph.GetNeighbor(kv.Key, edge);
                if (nb is null || !graph.Cells.TryGetValue(nb, out var cb)) continue;
                var pairKey = string.CompareOrdinal(kv.Key, nb) < 0 ? kv.Key + "|" + nb : nb + "|" + kv.Key;
                if (!drawnPairs.Add(pairKey)) continue;
                var ca = kv.Value;
                _kvm.SessionViews.TryGetValue(kv.Key, out var sa);
                _kvm.SessionViews.TryGetValue(nb, out var sb);
                var bothOnline = sa is not null && sa.Connected && sb is not null && sb.Connected;
                System.Windows.Shapes.Shape seam;
                double sx, sy;
                if (edge == ScreenEdge.Right)
                {
                    var aRight = OriginX(ca.X) + MapCardW / 2;
                    var bLeft = OriginX(cb.X) - MapCardW / 2;
                    sx = (aRight + bLeft) / 2 - 3;
                    sy = (OriginY(ca.Y) + OriginY(cb.Y)) / 2 - 25;
                    seam = new System.Windows.Shapes.Rectangle { Width = 6, Height = 50, RadiusX = 3, RadiusY = 3 };
                }
                else
                {
                    var aBottom = OriginY(ca.Y) + MapCardH / 2;
                    var bTop = OriginY(cb.Y) - MapCardH / 2;
                    sx = (OriginX(ca.X) + OriginX(cb.X)) / 2 - 30;
                    sy = (aBottom + bTop) / 2 - 3;
                    seam = new System.Windows.Shapes.Rectangle { Width = 60, Height = 6, RadiusX = 3, RadiusY = 3 };
                }
                seam.Fill = bothOnline ? seamAccent : seamIdle;
                seam.IsHitTestVisible = false;
                seam.Tag = "seam";
                System.Windows.Controls.Canvas.SetLeft(seam, sx);
                System.Windows.Controls.Canvas.SetTop(seam, sy);
                TopologyCanvas.Children.Add(seam);
            }
        }

        // ── Ghost "Place..." สไตล์เดิม: ด้านว่างของ host + ทุกเครื่อง (จอว่าง + ลูกศร + ข้อความ) ──
        // กฎ one-ghost-per-cell: cell ว่าง 1 ช่อง = ghost 1 ใบ (first-wins: host ก่อน ตามลำดับ config)
        // — กัน ghost 2 ใบซ้อนกันเมื่อสองเครื่องมีด้านว่างชี้ cell เดียวกัน (เจอจริงจาก mapshots)
        var claimedGhostCells = new HashSet<(int X, int Y)>();
        foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
        {
            if (graph.GetNeighbor(TopologyHost.Id, edge) is not null) continue;
            var off = TopologyGraph.Offset(edge);
            var target = (X: 0 + off.X, Y: 0 + off.Y);
            if (graph.Cells.Values.Any(c => c == target)) continue; // cell ถูกครอง (disabled/orphan) — ไม่วาดทับ
            if (!claimedGhostCells.Add(target)) continue;
            AddGhostCard(edge, OriginX(target.X), OriginY(target.Y), TopologyHost.Id);
        }
        foreach (var n in _config.Nodes)
        {
            if (!graph.Cells.TryGetValue(n.NodeId, out var ca)) continue;
            foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
            {
                if (graph.GetNeighbor(n.NodeId, edge) is not null) continue;
                var off = TopologyGraph.Offset(edge);
                var target = (X: ca.X + off.X, Y: ca.Y + off.Y);
                if (graph.Cells.Values.Any(c => c == target)) continue;
                if (!claimedGhostCells.Add(target)) continue;
                AddGhostCard(edge, OriginX(target.X), OriginY(target.Y), n.NodeId);
            }
        }

        AddHostCard(graph, OriginX(0), OriginY(0));
        foreach (var n in _config.Nodes)
        {
            if (!graph.Cells.TryGetValue(n.NodeId, out var cell)) continue; // orphan/วงจร: ยังโชว์ในรายการซ้าย
            _kvm.SessionViews.TryGetValue(n.NodeId, out var sess);
            AddNodeCard(graph, n, OriginX(cell.X), OriginY(cell.Y),
                sess is not null && sess.Connected, _kvm.ActiveNodeId == n.NodeId);
        }

        // v2.1 onboarding: ยังไม่มีเครื่องลูกเลย → การ์ดเชิญ "เพิ่มเครื่องแรก" กลางแผนที่ (จบปัญหาจอว่างเปล่าไร้ทางเดิน)
        if (_config.Nodes.Count == 0)
        {
            AddEmptyStateHero(OriginX(0) + MapCellW, OriginY(0));
        }
    }

    /// <summary>Overlay เชิญผู้ใช้ใหม่: ชื่อ + คำอธิบาย + ปุ่มใหญ่เปิด wizard (กด ghost ก็พาไปทางเดียวกัน)</summary>
    private void AddEmptyStateHero(double cx, double cy)
    {
        var card = new Border
        {
            Width = 300,
            Padding = new Thickness(18, 14, 18, 14),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x30, 0x48)),
            BorderBrush = (Brush?)TryFindResource("FluentAccentPrimaryBrush") ?? Brushes.DodgerBlue,
            BorderThickness = new Thickness(1.2),
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = Loc.T("hero.empty.title", "ยังไม่มีเครื่องลูกในผังของคุณ"),
            FontSize = 14, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.T("hero.empty.desc", "เพิ่มคอมพิวเตอร์ข้างๆ เครื่องนี้ แล้วใช้เมาส์ข้ามจอได้เลยเหมือนจอเดียวกัน"),
            FontSize = 11,
            Foreground = (Brush?)TryFindResource("TextSecondaryBrush") ?? Brushes.LightGray,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10), TextAlignment = TextAlignment.Center,
        });
        var btn = new Button
        {
            Content = Loc.T("hero.empty.button", "＋ เพิ่มเครื่องแรกของคุณ"),
            Style = (Style?)TryFindResource("ApplePrimaryButton") ?? null,
            Padding = new Thickness(16, 7, 16, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        if (btn.Style is null)
        {
            btn.Background = (Brush?)TryFindResource("FluentAccentPrimaryBrush") ?? Brushes.DodgerBlue;
            btn.Foreground = Brushes.White;
        }
        btn.Click += (_, _) => OpenAddFirstNodeWizard();
        stack.Children.Add(btn);
        card.Child = stack;
        card.Tag = "empty-hero";
        System.Windows.Controls.Canvas.SetLeft(card, cx - 150);
        System.Windows.Controls.Canvas.SetTop(card, cy - 60);
        TopologyCanvas.Children.Add(card);
    }

    /// <summary>
    /// Ghost slot สไตล์เดิม (DropZone): จอว่าง + ลูกศร + "วางด้าน..." —
    /// คลิก = วางเครื่องที่เลือกในรายการ · รับ drop = วางเครื่องที่ลากมา
    /// </summary>
    private void AddGhostCard(ScreenEdge edge, double cx, double cy, string anchorId)
    {
        var (symbol, labelKey) = edge switch
        {
            ScreenEdge.Top => (Wpf.Ui.Controls.SymbolRegular.ArrowUp24, "map.place.top"),
            ScreenEdge.Bottom => (Wpf.Ui.Controls.SymbolRegular.ArrowDown24, "map.place.bottom"),
            ScreenEdge.Left => (Wpf.Ui.Controls.SymbolRegular.ArrowLeft24, "map.place.left"),
            _ => (Wpf.Ui.Controls.SymbolRegular.ArrowRight24, "map.place.right"),
        };
        var ghost = new Border
        {
            Width = MapCardW, Height = MapCardH,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Background = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.5),
            Cursor = Cursors.Hand,
            Tag = "ghost|" + anchorId + "|" + TopologyGraph.EdgeToString(edge),
        };
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
        };
        stack.Children.Add(new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = symbol,
            FontSize = 16,
            Foreground = (Brush?)TryFindResource("TextMutedBrush") ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.T(labelKey),
            FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush?)TryFindResource("TextMutedBrush") ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
        });
        ghost.Child = stack;
        ghost.AllowDrop = true;
        ghost.DragEnter += OnMapGhostDragEnter;
        ghost.DragLeave += OnMapGhostDragLeave;
        ghost.Drop += OnMapGhostDrop;
        // v1.9.1: gesture state machine — กดค้าง + ลากเกิน 8px = pan ต่อเนื่อง (ไม่ตายหลังขยับแรก)
        // ปล่อยโดยไม่ลาก = วางเครื่องที่เลือก (click) — ตัดสินใจทั้งหมดผ่าน MapGestureState (unit tested)
        ghost.MouseLeftButtonDown += (_, e2) =>
        {
            if (e2.LeftButton != MouseButtonState.Pressed) return;
            _mapGesture.Press(e2.GetPosition(MapHostGrid), _mapTransform.Matrix, threshold: true);
            ghost.CaptureMouse();
        };
        ghost.MouseMove += (_, e2) =>
        {
            var m = _mapGesture.Move(e2.GetPosition(MapHostGrid), e2.LeftButton == MouseButtonState.Pressed);
            if (m is not null) _mapTransform.Matrix = m.Value;
        };
        ghost.MouseLeftButtonUp += (_, e2) =>
        {
            var wasClick = _mapGesture.Release();
            if (ghost.IsMouseCaptured) ghost.ReleaseMouseCapture();
            if (wasClick)
            {
                PlaceFromGhost("ghost|" + anchorId + "|" + TopologyGraph.EdgeToString(edge)); // คลิก ghost = วางเครื่องที่เลือก
            }
        };
        System.Windows.Controls.Canvas.SetLeft(ghost, cx - MapCardW / 2);
        System.Windows.Controls.Canvas.SetTop(ghost, cy - MapCardH / 2);
        TopologyCanvas.Children.Add(ghost);
    }

    /// <summary>วางเครื่องที่ "เลือกอยู่ในรายการ" ลง ghost (คลิก ghost = เหมือน OnDropZoneClick เดิม)</summary>
    private void PlaceFromGhost(string tag)
    {
        var parts = tag.Split('|');
        if (parts.Length != 3 || parts[0] != "ghost") return;
        var selected = (NodeListBox?.SelectedItem as NodeListRow)?.NodeId;
        if (string.IsNullOrEmpty(selected))
        {
            // v2.1 onboarding: ไม่มีเครื่องให้เลือกเลย = ผู้ใช้ใหม่ — เปิด wizard เพิ่มเครื่องแรกแทน banner ตวาด
            if (_config.Nodes.Count == 0)
            {
                OpenAddFirstNodeWizard();
                return;
            }
            SetBanner(Loc.T("banner.select.first", "เลือกเครื่องจากรายการ Connected Devices ก่อน"));
            return;
        }
        ApplyChainPlacement(selected, parts[1], parts[2]);
    }

    /// <summary>v2.1 onboarding: wizard เพิ่มเครื่องแรก (idempotent — กด hero/ghost/FirstRun กี่ทางก็เปิด dialog เดียว)</summary>
    public void OpenAddFirstNodeWizard()
    {
        if (_addFirstNodeWizard is { IsLoaded: true })
        {
            _addFirstNodeWizard.Activate();
            return;
        }
        _addFirstNodeWizard = new AddFirstNodeWizard(this) { Owner = this };
        _addFirstNodeWizard.ShowDialog();
        _addFirstNodeWizard = null;
        _mapSignature = ""; // บังคับ redraw — wizard อาจเพิ่ม node แรก
        RefreshUiState();
    }

    private AddFirstNodeWizard? _addFirstNodeWizard;

    private void OnMapGhostDragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border b)
        {
            b.Background = new SolidColorBrush(Color.FromArgb(0x35, 0x00, 0x78, 0xD4));
            b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        }
    }

    private void OnMapGhostDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b)
        {
            b.Background = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
            b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>วางเครื่องบน ghost = anchor ไปที่เครื่องการ์ดนั้น ด้านลูกศร</summary>
    private void OnMapGhostDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string tag) return;
        var parts = tag.Split('|');
        if (parts.Length != 3 || parts[0] != "ghost") return;
        if (e.Data.GetData("OmniScreenDrag") is not ScreenDragPayload payload) return;
        ApplyChainPlacement(payload.NodeId, parts[1], parts[2]);
    }

    // ลากแถวจากรายการ Connected Devices ไปวางบน ghost ของผัง
    private void OnNodeListBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _listDragStart = e.GetPosition(NodeListBox);
            _mapGesture.Cancel(); // รอ drop — ไม่ pan จากรายการ
            NodeListBox.CaptureMouse();
        }
    }

    private void OnNodeListBoxPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (NodeListBox.IsMouseCaptured && e.LeftButton != MouseButtonState.Pressed)
        {
            NodeListBox.ReleaseMouseCapture();
            return;
        }
        if (!NodeListBox.IsMouseCaptured) return;
        var pos = e.GetPosition(NodeListBox);
        if (Math.Abs(pos.X - _listDragStart.X) > 8 || Math.Abs(pos.Y - _listDragStart.Y) > 8)
        {
            NodeListBox.ReleaseMouseCapture();
            if (NodeListBox?.SelectedItem is NodeListRow row)
            {
                var node = _config.Nodes.FirstOrDefault(n => n.NodeId == row.NodeId);
                if (node != null)
                {
                    try
                    {
                        DragDrop.DoDragDrop(NodeListBox, new DataObject("OmniScreenDrag",
                            new ScreenDragPayload { NodeId = node.NodeId, SourcePosition = node.Position ?? "right" }), DragDropEffects.Move);
                    }
                    catch { }
                }
            }
        }
    }

    private Point _listDragStart;

    private void AddHostCard(TopologyView graph, double cx, double cy)
    {
        var hostActive = _kvm.ActiveNodeId is null;
        var isTh = _kvm.MasterLangGroup == 1;
        var card = new Border
        {
            Width = MapCardW, Height = MapCardH,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(hostActive ? Color.FromArgb(0x35, 0x00, 0x95, 0xFF) : Color.FromArgb(0x18, 0x0A, 0x0E, 0x1A)),
            BorderBrush = new SolidColorBrush(hostActive ? Color.FromRgb(0x00, 0xD2, 0xFF) : Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.5),
            Tag = "card|" + TopologyHost.Id,
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Desktop28,
            FontSize = 22,
            Foreground = (Brush?)TryFindResource("FluentAccentLightBrush") ?? Brushes.DodgerBlue,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2),
        });
        stack.Children.Add(new TextBlock
        {
            Text = _config.HostName + " (" + Loc.T("node.host", "Host") + ")",
            FontWeight = FontWeights.Bold, FontSize = 12,
            Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = _config.HostIp + " • " + Loc.T("node.console", "Console"),
            FontSize = 10,
            Foreground = (Brush?)TryFindResource("TextSecondaryBrush") ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
        });
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1.5, 6, 1.5),
            Background = (Brush?)TryFindResource("StatusOnlineBgBrush") ?? Brushes.Transparent,
            BorderBrush = (Brush?)TryFindResource("StatusOnlineBrush") ?? Brushes.Green,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
        };
        pill.Child = new TextBlock
        {
            Text = hostActive ? Loc.T("map.focus.active", "Active Cursor") : Loc.T("map.focus.client", "Client Controlled"),
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush?)TryFindResource("StatusOnlineBrush") ?? Brushes.Green,
        };
        stack.Children.Add(pill);
        stack.Children.Add(new TextBlock
        {
            Text = isTh ? Loc.T("map.lang.th", "Layout: TH (Thai)") : Loc.T("map.lang.en", "Layout: ENG (US)"),
            FontSize = 9.5, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush?)TryFindResource("FluentAccentLightBrush") ?? Brushes.DodgerBlue,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
        });
        card.Child = stack;
        System.Windows.Controls.Canvas.SetLeft(card, cx - MapCardW / 2);
        System.Windows.Controls.Canvas.SetTop(card, cy - MapCardH / 2);
        TopologyCanvas.Children.Add(card);
    }

    private void AddNodeCard(TopologyView graph, NodeConfig node, double cx, double cy, bool online, bool focused)
    {
        _kvm.SessionViews.TryGetValue(node.NodeId, out var sess);
        var card = new Border
        {
            Width = MapCardW, Height = MapCardH,
            CornerRadius = new CornerRadius(8),
            Background = (Brush?)TryFindResource("FluentCardBrush") ?? Brushes.Transparent,
            BorderBrush = new SolidColorBrush(focused ? Color.FromRgb(0x00, 0xD2, 0xFF) : Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(focused ? 1.5 : 1),
            Cursor = Cursors.Hand,
            Tag = "card|" + node.NodeId,
            ToolTip = node.DisplayName,
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        titleRow.Children.Add(new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = string.Equals(node.DeviceType, "desktop", StringComparison.OrdinalIgnoreCase)
                ? Wpf.Ui.Controls.SymbolRegular.Desktop24
                : Wpf.Ui.Controls.SymbolRegular.Laptop24,
            FontSize = 18,
            Foreground = (Brush?)TryFindResource("FluentAccentLightBrush") ?? Brushes.DodgerBlue,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = node.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 11.5,
            Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = MapCardW - 74,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var pencil = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(3, 0, 0, 0), Cursor = Cursors.Hand,
            Tag = node.NodeId,
            ToolTip = Loc.T("devices.rename.tip", "เปลี่ยนชื่อแสดงของเครื่อง"),
        };
        pencil.Content = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Edit24, FontSize = 10,
            Foreground = (Brush?)TryFindResource("FluentAccentLightBrush") ?? Brushes.DodgerBlue,
        };
        pencil.Click += OnRenameMapNodeClick;
        titleRow.Children.Add(pencil);
        stack.Children.Add(titleRow);

        stack.Children.Add(new TextBlock
        {
            Text = online && sess is not null
                ? node.Ip + " • Ping " + sess.PingMs.ToString("0") + "ms"
                : node.Ip + " • " + (string.IsNullOrWhiteSpace(node.DeviceType) ? Loc.T("node.client", "Client") : node.DeviceType),
            FontSize = 9.5, Foreground = (Brush?)TryFindResource("TextSecondaryBrush") ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
        });

        var statusText = focused ? Loc.T("map.cursor.active", "Active Cursor")
            : online ? Loc.T("map.connected", "Connected")
            : (node.Enabled ? Loc.T("map.offline", "Offline") : Loc.T("map.disabled", "ปิดใช้งาน"));
        var pillColor = focused ? Color.FromRgb(0x00, 0xD2, 0xFF)
            : online ? Color.FromRgb(0x27, 0xAE, 0x60)
            : (node.Enabled ? Color.FromRgb(0xD9, 0x77, 0x06) : Color.FromRgb(0xE1, 0x1D, 0x48));
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 1.5, 8, 1.5),
            Background = new SolidColorBrush(Color.FromArgb(0x20, pillColor.R, pillColor.G, pillColor.B)),
            BorderBrush = new SolidColorBrush(pillColor),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
        };
        pill.Child = new TextBlock
        {
            Text = statusText, FontSize = 9.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(pillColor),
        };
        stack.Children.Add(pill);

        card.Child = stack;

        // คลิก = เลือกในรายการ · ลาก = ย้ายผัง (ไปวางบน ghost)
        card.MouseLeftButtonUp += (_, _) =>
        {
            for (var i = 0; i < NodeListBox.Items.Count; i++)
            {
                if (NodeListBox.Items[i] is NodeListRow row && row.NodeId == node.NodeId)
                {
                    NodeListBox.SelectedIndex = i;
                    break;
                }
            }
        };
        card.MouseLeftButtonDown += (_, e2) =>
        {
            if (e2.LeftButton == MouseButtonState.Pressed)
            {
                _mapDragStart = e2.GetPosition(this);
                _mapDragNode = node.NodeId;
            }
        };
        card.MouseMove += (_, e2) =>
        {
            if (_mapDragNode != node.NodeId || e2.LeftButton != MouseButtonState.Pressed) return;
            var pos = e2.GetPosition(this);
            if (Math.Abs(pos.X - _mapDragStart.X) <= 8 && Math.Abs(pos.Y - _mapDragStart.Y) <= 8) return;
            _mapDragNode = null;
            try
            {
                DragDrop.DoDragDrop(card, new DataObject("OmniScreenDrag",
                    new ScreenDragPayload { NodeId = node.NodeId, SourcePosition = node.Position ?? "right" }), DragDropEffects.Move);
            }
            catch { }
        };

        System.Windows.Controls.Canvas.SetLeft(card, cx - MapCardW / 2);
        System.Windows.Controls.Canvas.SetTop(card, cy - MapCardH / 2);
        TopologyCanvas.Children.Add(card);
    }

    // ================= v1.9.1: MAP PAN / ZOOM / FIT (MapGestureState) =================

    /// <summary>ผังทั้งก้อนพอดี viewport (กึ่งกลาง) — เรียกตอน render ครั้งแรก + ปุ่ม reset + double-click</summary>
    public void ApplyMapFit()
    {
        if (TopologyCanvas is null || MapHostGrid is null) return;
        if (TopologyCanvas.Width <= 0 || double.IsNaN(TopologyCanvas.Width) ||
            TopologyCanvas.Height <= 0 || double.IsNaN(TopologyCanvas.Height) ||
            MapHostGrid.ActualWidth <= 0) return; // ยังไม่พร้อม — คง _mapFitPending รอ SizeChanged/render ถัดไป
        var fit = MapViewportMath.Fit(
            TopologyCanvas.Width, TopologyCanvas.Height, MapHostGrid.ActualWidth, MapHostGrid.ActualHeight);
        if (double.IsNaN(fit.OffsetX) || double.IsNaN(fit.OffsetY)) return; // ยังไม่พร้อม — คง pending รอ SizeChanged ถัดไป
        _mapTransform.Matrix = fit;
        _mapFitPending = false;
    }

    private void OnResetMapViewClick(object sender, RoutedEventArgs e) => ApplyMapFit();

    // ── ให้ harness/unit tests ตรวจ pan/zoom ได้โดยตรง ──
    internal System.Windows.Media.Matrix MapMatrix => _mapTransform.Matrix;
    internal void MapZoomAtPoint(double factor, double cx, double cy)
        => _mapTransform.Matrix = MapViewportMath.ZoomAt(_mapTransform.Matrix, factor, cx, cy);
    internal void MapPanBy(double dx, double dy)
        => _mapTransform.Matrix = MapViewportMath.Pan(_mapTransform.Matrix, dx, dy);


    /// <summary>พื้นที่ "ว่าง" = Canvas เปล่า / Grid background / การ์ดพื้นหลังใหญ่ — เฉพาะนี้ถึงเริ่ม pan ได้
    /// การ์ดเครื่อง (ลากย้าย) กับ ghost (กด=วาง ลาก=pan) มี handler ตัวเอง</summary>
    private bool IsMapEmptyArea(object source)
        => source == TopologyCanvas || source == MapHostGrid || source == ScreenMapCard;

    private void OnMapAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsMapEmptyArea(e.OriginalSource)) return;
        if (e.ClickCount == 2)
        {
            ApplyMapFit(); // double-click พื้นว่าง = พอดีจอ
            _mapGesture.Cancel();
            return;
        }
        _mapGesture.Press(e.GetPosition(MapHostGrid), _mapTransform.Matrix, threshold: false);
        MapHostGrid.CaptureMouse();
    }

    private void OnMapAreaMouseMove(object sender, MouseEventArgs e)
    {
        if (MapHostGrid.IsMouseCaptured || _mapGesture.Current != MapGestureState.Phase.None)
        {
            var m = _mapGesture.Move(e.GetPosition(MapHostGrid), e.LeftButton == MouseButtonState.Pressed);
            if (m is not null) _mapTransform.Matrix = m.Value;
        }
    }

    private void OnMapAreaMouseUp(object sender, MouseButtonEventArgs e)
    {
        _mapGesture.Release();
        if (MapHostGrid.IsMouseCaptured) MapHostGrid.ReleaseMouseCapture();
    }

    private void OnMapAreaWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(MapHostGrid);
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        _mapTransform.Matrix = MapViewportMath.ZoomAt(_mapTransform.Matrix, factor, pos.X, pos.Y);
        e.Handled = true;
    }

    /// <summary>ปุ่มดินสอบนการ์ด = เปลี่ยนชื่อเครื่อง (เหมือนการ์ดเดิม)</summary>
    private void OnRenameMapNodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string nodeId) return;
        var node = _config.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null) return;
        var dialog = new RenameDeviceDialog(node) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.WasChanged)
        {
            Task.Run(() => { try { _config.Save(); } catch { } }); // UI-H4 fix
            _mapSignature = null;
            ScheduleConfigSave();
            RefreshUiState();
            SetBanner(Loc.T("banner.renamed", "เปลี่ยนชื่อเครื่องเป็น \"{0}\" เรียบร้อยแล้ว", node.DisplayName));
        }
    }

    private static readonly Brush FocusedBadgeBg = MakeFrozen(0x35, 0x00, 0xD2, 0xFF);
    private static readonly Brush FocusedBadgeFg = MakeFrozen(0xFF, 0x00, 0xD2, 0xFF);
    private static readonly Brush OnlineBadgeBg = MakeFrozen(0x30, 0x00, 0xF5, 0xA0);
    private static readonly Brush OnlineBadgeFg = MakeFrozen(0xFF, 0x00, 0xF5, 0xA0);
    private static readonly Brush OfflineBadgeBg = MakeFrozen(0x30, 0xFF, 0x3B, 0x5C);
    private static readonly Brush OfflineBadgeFg = MakeFrozen(0xFF, 0xFF, 0x3B, 0x5C);

    private static Brush MakeFrozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> NodeBadgeCache = new();

    private static ImageSource GetCachedNodeBadge(bool online, bool focused, int langGroup)
    {
        var key = $"{online}|{focused}|{langGroup}";
        return NodeBadgeCache.GetOrAdd(key, _ => OmniIconBuilder.BuildNodeBadge(isHost: false, online: online, focused: focused, langGroup: langGroup));
    }

    // ================= NODE ACTIONS (v1.9: ต้องเลือกเครื่องก่อนเสมอ — ไม่มี silent fallback) =================

    /// <summary>node เป้าหมาย = แถวที่เลือกในรายการเท่านั้น — ไม่เลือก = แจ้งผู้ใช้ (เดิมเงียบๆ หยิบเครื่องแรกให้)</summary>
}
