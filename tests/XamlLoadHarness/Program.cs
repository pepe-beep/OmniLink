using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OmniLink.Core;
using OmniLink.Views;
using OmniLink.Views.Settings;

namespace OmniLink.XamlLoadHarness;

/// <summary>
/// Visual harness — โหมดปกติ (ไม่มี args): โหลด App+MainWindow+ทุกหน้า settings แบบไม่ชน single-instance
/// โหมด --shots &lt;dir&gt;: แสดงหน้าต่างนอกจอ จับ PNG ทุกแท็บ/ทุกหน้า/สองภาษา + **ตรวจสีจริง**:
/// ทุก TextBox/ComboBox บนหน้าต้องพื้นหลังมืด (กันบั๊ก "กล่องขาวมองไม่เห็น" — เจอจริงในภาพผู้ใช้)
/// harness ปิด AutoStartKvm/Stream/Mic ในหน่วยความจำเสมอ — ไม่ติด hook ไม่ยิง deploy ไม่แตะ agent
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        OmniLink.Core.OmniLog.Silence(); // อย่าเขียน log ทับของ production instance
        var shotsDir = args.Length >= 2 && args[0] == "--shots" ? args[1] : null;
        var mapShotsDir = args.Length >= 2 && args[0] == "--mapshots" ? args[1] : null;
        var hitMap = args.Length >= 1 && args[0] == "--hitmap";
        try
        {
            // pack://application:,,,/ resolve กับ entry assembly — harness ต้องชี้กลับไปที่ OmniLink.dll
            if (Application.ResourceAssembly is null)
            {
                Application.ResourceAssembly = typeof(OmniLink.App).Assembly;
            }
            var app = new OmniLink.App(); // App.xaml: themes + controls + SettingsStyles + brushes
            app.InitializeComponent();    // Program.cs (custom Main) เรียกเองในแอปจริง — harness ต้องเรียกเอง

            var win = new OmniLink.Views.MainWindow(); // MainWindow.xaml + LocalizationService + หน้า settings ทั้งหมด
            var host = (ISettingsHost)win;

            // ป้องกัน harness ไปกระทบระบบจริง: ไม่ติด hook, ไม่ deploy, ไม่สั่งเสียง (ค่าในหน่วยความจำเท่านั้น)
            host.Config.AutoStartKvm = false;
            host.Config.AutoStartStream = false;
            host.Config.AutoStartMic = false;

            if (mapShotsDir is not null)
            {
                return RunMapShots(win, host, mapShotsDir);
            }
            if (hitMap)
            {
                return RunHitMap(win, host);
            }
            if (shotsDir is null)
            {
                RunAssertions(win);
            }
            else
            {
                RunShots(win, shotsDir);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("VISUAL_FAILED: " + ex);
            return 1;
        }
        finally
        {
            (Application.Current as OmniLink.App)?.Shutdown();
        }
    }

    // ─────────── โหมดเดิม: assertions ───────────

    private static void RunAssertions(OmniLink.Views.MainWindow win)
    {
        var pages = win.SettingsSectionList.Items.Count;
        var railFirst = RailTitle(win, 0);
        Console.WriteLine($"XAML_LOAD_OK tabs={win.MainTabs.Items.Count} settings_sections={pages} rail_first=\"{railFirst}\" lang={LocalizationService.Current}");
        Console.WriteLine($"banner_initial=\"{win.StatusBannerText.Text}\"");
        Console.WriteLine($"nodes_in_list={(win.NodeListBox.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().Count() ?? 0}");

        LocalizationService.SetLanguage(LocalizationService.English);
        var railEn = RailTitle(win, 0);
        LocalizationService.SetLanguage(LocalizationService.Thai);
        var railTh = RailTitle(win, 0);
        Console.WriteLine($"rail_first_en=\"{railEn}\" rail_first_th=\"{railTh}\"");

        for (var i = 0; i < pages; i++)
        {
            win.SettingsSectionList.SelectedIndex = i;
        }
        Console.WriteLine("ALL_SETTINGS_PAGES_LOADED_OK");

        // dirty tracking lifecycle
        win.SettingsSectionList.SelectedIndex = 1; // Switching
        var swPage = (ISettingsPage)win.SettingsPageContent.Content!;
        var swControls = (SwitchingPage)win.SettingsPageContent.Content;
        Console.WriteLine($"dirty_initial={swPage.IsDirty} reset_enabled_initial={win.BtnResetPage.IsEnabled}");
        swControls.SwitchDelayBox.Text = "321";
        win.UpdateSettingsFooterButtons();
        Console.WriteLine($"dirty_after_edit={swPage.IsDirty} reset_enabled_after_edit={win.BtnResetPage.IsEnabled}");
        win.BtnResetPage.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Console.WriteLine($"dirty_after_discard={swPage.IsDirty} reset_enabled_after_discard={win.BtnResetPage.IsEnabled} box_back_to={swControls.SwitchDelayBox.Text}");
    }

    // ─────────── โหมด --shots: จับภาพ + ตรวจสี ───────────

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RectStruct rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>จับภาพ "หน้าจอจริง" ของหน้าต่าง (WYSIWYG — ผ่าน DWM จริง ตรงสิ่งที่ผู้ใช้เห็น)</summary>
    private static Bitmap CaptureWindow(OmniLink.Views.MainWindow win)
    {
        win.UpdateLayout();
        var handle = new WindowInteropHelper(win).Handle;
        GetWindowRect(handle, out var r);
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
        return bmp;
    }

    private static void RunShots(OmniLink.Views.MainWindow win, string dir)
    {
        Directory.CreateDirectory(dir);
        win.Left = 40;
        win.Top = 40;
        win.Topmost = true; // กันหน้าต่างอื่นของผู้ใช้ทับระหว่างจับภาพ (เคยจับได้หน้าเบราว์เซอร์)
        win.Show();
        win.Activate();
        Pump(600);

        var failures = new List<string>();
        var captured = new List<string>();

        void Snap(string name)
        {
            Pump(180);
            using var bmp = CaptureWindow(win);

            // ตรวจสี input ทุกตัวบนภาพจริง — sample 5 จุดรอบศูนย์กลางแล้วเลือก "มืดสุด"
            // (จุดกึ่งกลางอาจโดนตัวอักษรขาว; พื้นหลังคือส่วนที่มืดที่สุดเสมอ)
            foreach (var (control, kind) in CollectInputs(win))
            {
                if (!IsVisibleIn(control, win)) continue;
                var point = control.TransformToAncestor(win).Transform(new System.Windows.Point(control.ActualWidth / 2, control.ActualHeight / 2));
                var dpi = VisualTreeHelper.GetDpi(control);
                var px = (int)(point.X * dpi.DpiScaleX);
                var py = (int)(point.Y * dpi.DpiScaleY);
                var minLum = 255;
                System.Windows.Media.Color minColor = System.Windows.Media.Color.FromRgb(255, 255, 255);
                for (var dy = -3; dy <= 3; dy += 3)
                {
                    for (var dx = -3; dx <= 3; dx += 3)
                    {
                        var c = bmp.GetPixel(Math.Clamp(px + dx, 0, bmp.Width - 1), Math.Clamp(py + dy, 0, bmp.Height - 1));
                        var lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                        if (lum < minLum)
                        {
                            minLum = lum;
                            minColor = System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
                        }
                    }
                }
                if (minLum > 120)
                {
                    failures.Add($"{name}: {kind} พื้นหลังสว่างผิดปกติ (min RGB {minColor.R},{minColor.G},{minColor.B} lum={minLum}) — ธีมขาวหลุดมาอีกแล้ว");
                }
            }

            var path = Path.Combine(dir, name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            captured.Add(path);
        }

        foreach (var lang in new[] { LocalizationService.English, LocalizationService.Thai })
        {
            LocalizationService.SetLanguage(lang);
            var tag = lang == LocalizationService.Thai ? "th" : "en";

            for (var tab = 0; tab < win.MainTabs.Items.Count; tab++)
            {
                win.MainTabs.SelectedIndex = tab;
                Pump(150);
                Snap($"tab{tab + 1}-{tag}");
            }

            win.MainTabs.SelectedIndex = 3; // Settings
            Pump(120);
            for (var i = 0; i < win.SettingsSectionList.Items.Count; i++)
            {
                win.SettingsSectionList.SelectedIndex = i;
                Pump(150);
                Snap($"settings-{i + 1:00}-{tag}");
            }
        }

        win.Topmost = false;
        Console.WriteLine($"SHOTS_OK count={captured.Count} dir={Path.GetFullPath(dir)}");
        foreach (var f in failures)
        {
            Console.WriteLine("CONTRAST_FAIL " + f);
        }
        Console.WriteLine(failures.Count == 0 ? "CONTRAST_ALL_DARK_OK" : $"CONTRAST_FAILED_COUNT={failures.Count}");
        if (failures.Count > 0)
        {
            Environment.ExitCode = 2; // CI-able: มีกล่องสว่าง = fail
        }
    }

    private static string RailTitle(OmniLink.Views.MainWindow win, int index)
        => win.SettingsSectionList.Items.Count > index
            ? ((SettingsSectionRow?)win.SettingsSectionList.Items[index])?.Title ?? "?"
            : "?";

    /// <summary>เก็บ TextBox/ComboBox ทุกตัว (เจาะลึก visual tree)</summary>
    private static List<(FrameworkElement Control, string Kind)> CollectInputs(DependencyObject root)
    {
        var found = new List<(FrameworkElement, string)>();
        void Walk(DependencyObject node)
        {
            if (node is TextBox tb) found.Add((tb, "TextBox"));
            else if (node is ComboBox cb) found.Add((cb, "ComboBox"));
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
        Walk(root);
        return found;
    }

    private static bool IsVisibleIn(FrameworkElement control, FrameworkElement root)
    {
        if (control.Visibility != Visibility.Visible) return false;
        for (DependencyObject? node = control; node is not null && node != root; node = (node as FrameworkElement)?.Parent ?? (node as FrameworkElement)?.TemplatedParent)
        {
            if (node is FrameworkElement fe && fe.Visibility != Visibility.Visible) return false;
        }
        return control.ActualWidth > 8 && control.ActualHeight > 6;
    }

    private static System.Windows.Media.Color SamplePixel(Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(Math.Clamp(x, 0, bmp.Width - 1), Math.Clamp(y, 0, bmp.Height - 1));
        return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
    }

    // ─────────── โหมด --hitmap: ยิง InputHitTest เป็นกริดทั่วพื้นที่ผัง ───────────

    private static int RunHitMap(OmniLink.Views.MainWindow win, OmniLink.Views.Settings.ISettingsHost host)
    {
        win.Left = 40; win.Top = 40; win.Topmost = true;
        win.Show();
        win.Activate();
        win.MainTabs.SelectedIndex = 0;
        win.RefreshUiState();
        Pump(600);
        win.UpdateLayout();
        Pump(200);

        var grid = win.MapHostGrid;
        var failures = new List<string>();
        var counts = new System.Collections.Generic.Dictionary<string, int>();
        var panable = 0;
        var total = 0;
        const int step = 40;
        for (var y = 10; y < grid.ActualHeight - 5; y += step)
        {
            var row = "";
            for (var x = 10; x < grid.ActualWidth - 5; x += step)
            {
                total++;
                var hit = grid.InputHitTest(new System.Windows.Point(x, y)) as FrameworkElement;
                string kind;
                if (hit is null) { kind = "??"; failures.Add($"({x},{y}): hit null"); }
                else
                {
                    // ไต่ขึ้นหาเจ้าของ
                    string Classify(System.Windows.DependencyObject el)
                    {
                        var walker = el;
                        while (walker is not null)
                        {
                            if (walker == win.TopologyCanvas) return "canvas";
                            if (walker == grid) return "grid";
                            if (walker is System.Windows.Controls.Border bb && bb.Tag is string tg)
                            {
                                if (tg.StartsWith("ghost|", StringComparison.Ordinal)) return "ghost";
                                if (tg.StartsWith("card|", StringComparison.Ordinal)) return "card";
                                if (tg == "seam") return "seam";
                            }
                            walker = System.Windows.Media.VisualTreeHelper.GetParent(walker);
                        }
                        return "other";
                    }
                    kind = Classify(hit);
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
                    if (kind is "canvas" or "grid" or "ghost") panable++;
                    else if (kind == "other") failures.Add($"({x},{y}): hit 'other' ({hit.GetType().Name}) — จุดนี้ pan/คลิกไม่ได้");
                }
                row += kind switch
                {
                    "canvas" => ".", "grid" => "g", "ghost" => "G", "card" => "C",
                    "seam" => "s", "??" => "?", _ => "o",
                };
            }
            Console.WriteLine(row);
        }
        Console.WriteLine($"legend: .=Canvas pan g=Grid pan G=ghost C=card s=seam o=other ?=none");
        Console.WriteLine($"counts: {string.Join(", ", counts.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value))}");
        Console.WriteLine($"pan-able points (canvas+grid+ghost): {panable}/{total}");
        Console.WriteLine(failures.Count == 0 ? "HITMAP_ALL_PANABLE_OK" : "HITMAP_FAIL " + string.Join(" | ", failures.Take(6)));
        win.Topmost = false;
        return failures.Count == 0 ? 0 : 2;
    }

    // ─────────── โหมด --mapshots: 6 scenarios + visual-tree assertions ───────────

    private sealed record MapScenario(string Name, string Tag, List<OmniLink.Core.NodeConfig> Nodes, string Lang);

    private static int RunMapShots(OmniLink.Views.MainWindow win, OmniLink.Views.Settings.ISettingsHost host, string dir)
    {
        Directory.CreateDirectory(dir);
        win.Topmost = true;
        win.Show();
        win.Activate();
        Pump(500);

        var originalNodes = host.Config.Nodes.ToList();
        var failures = new List<string>();
        var captured = new List<string>();

        List<OmniLink.Core.NodeConfig> N(params (string id, string pos, string anchor, bool enabled)[] defs)
        {
            var list = new List<OmniLink.Core.NodeConfig>();
            foreach (var d in defs)
            {
                list.Add(new OmniLink.Core.NodeConfig
                {
                    NodeId = d.id, Name = d.id.ToUpperInvariant() + " Machine", Ip = "192.168.99.2",
                    Position = d.pos, Anchor = d.anchor, Enabled = d.enabled,
                    DeviceType = "laptop",
                });
            }
            return list;
        }

        var scenarios = new List<MapScenario>
        {
            new("single-right", "1-right", N(("l440", "right", "", true)), "en"),
            new("single-top", "2-top", N(("l440", "top", "", true)), "en"),
            new("chain-on-disabled", "3-chain", N(("e5", "right", "", false), ("l440", "top", "e5", true)), "en"),
            new("grid-2x2", "4-grid", N(("l440", "right", "", true), ("e5", "top", "", true), ("a", "top", "l440", true)), "en"),
            new("disabled-present", "5-disabled", N(("l440", "right", "", true), ("e5", "left", "", false)), "en"),
            new("thai-lang", "6-thai", N(("l440", "right", "", true)), "th"),
        };

        foreach (var sc in scenarios)
        {
            OmniLink.Core.LocalizationService.SetLanguage(sc.Lang == "th"
                ? OmniLink.Core.LocalizationService.Thai : OmniLink.Core.LocalizationService.English);
            host.Config.Nodes = sc.Nodes;
            host.Kvm.ReloadLayout();
            win.MainTabs.SelectedIndex = 0;
            win.RefreshUiState();
            Pump(350);
            win.UpdateLayout();
            Pump(150);

            var graph = host.Kvm.TopologyView;
            var canvas = win.TopologyCanvas;
            Console.WriteLine($"[dbg] {sc.Tag}: matrix=({win.MapMatrix.M11:0.00},{win.MapMatrix.OffsetX:0},{win.MapMatrix.OffsetY:0}) canvas={canvas.Width:0}x{canvas.Height:0} grid={win.MapHostGrid.ActualWidth:0}x{win.MapHostGrid.ActualHeight:0} visible={canvas.IsVisible}");

            // ── เก็บ elements ทั้งหมด (Tag string) ──
            var tagged = new List<(FrameworkElement El, string Tag, Rect Rect)>();
            CollectTagged(canvas, tagged);

            // 1) ห้ามมีลูกศรลอย (marker บั๊กเดิม)
            if (tagged.Any(t => t.Tag.StartsWith("arrow", StringComparison.Ordinal)))
            {
                failures.Add(sc.Tag + ": ยังมีลูกศรลอย (tag arrow)");
            }

            // 2) การ์ด: ไม่ทับกัน + อยู่ในขอบเขต
            var cards = tagged.Where(t => t.Tag.StartsWith("card|", StringComparison.Ordinal)).ToList();
            if (cards.Count < 1) failures.Add(sc.Tag + ": ไม่มีการ์ดเลย");
            for (var i = 0; i < cards.Count; i++)
            {
                if (cards[i].Rect.X < 0 || cards[i].Rect.Y < 0 ||
                    cards[i].Rect.Right > canvas.Width + 1 || cards[i].Rect.Bottom > canvas.Height + 1)
                {
                    failures.Add(sc.Tag + ": การ์ด " + cards[i].Tag + " หลุดขอบเขต canvas");
                }
                for (var j = i + 1; j < cards.Count; j++)
                {
                    if (cards[i].Rect.IntersectsWith(cards[j].Rect))
                    {
                        failures.Add(sc.Tag + ": การ์ดทับกัน " + cards[i].Tag + " x " + cards[j].Tag);
                    }
                }
            }

            // 3) ghost: จำนวน = free sides ที่ปลายทาง cell ว่าง + ทิศ label ถูก + ไม่ทับการ์ด
            var ghosts = tagged.Where(t => t.Tag.StartsWith("ghost|", StringComparison.Ordinal)).ToList();
            var expectedGhosts = new List<(string anchor, string side)>();
            var claimed = new HashSet<(int X, int Y)>();
            // one-ghost-per-cell first-wins — ลำดับเดียวกับ renderer (host ก่อน ตามลำดับ config)
            void Claim(string id, (int X, int Y) cell)
            {
                foreach (var edge in new[] { OmniLink.Core.Strategies.ScreenEdge.Left, OmniLink.Core.Strategies.ScreenEdge.Right,
                    OmniLink.Core.Strategies.ScreenEdge.Top, OmniLink.Core.Strategies.ScreenEdge.Bottom })
                {
                    if (graph.GetNeighbor(id, edge) is not null) continue;
                    var off = OmniLink.Core.TopologyGraph.Offset(edge);
                    var target = (cell.X + off.X, cell.Y + off.Y);
                    if (graph.Cells.Values.Any(c => c == target)) continue;
                    if (!claimed.Add(target)) continue;
                    expectedGhosts.Add((id, OmniLink.Core.TopologyGraph.EdgeToString(edge)));
                }
            }
            Claim(OmniLink.Core.TopologyHost.Id, (0, 0));
            foreach (var n in sc.Nodes)
            {
                if (graph.Cells.TryGetValue(n.NodeId, out var c2)) Claim(n.NodeId, c2);
            }
            if (ghosts.Count != expectedGhosts.Count)
            {
                failures.Add(sc.Tag + ": ghost count=" + ghosts.Count + " expected=" + expectedGhosts.Count);
            }
            foreach (var g in ghosts)
            {
                var parts = g.Tag.Split('|');
                var anchor = parts[1];
                var sideCode = parts[2];
                if (!graph.TryGetCell(anchor, out var acell))
                {
                    failures.Add(sc.Tag + ": ghost anchor ไม่มี cell: " + g.Tag);
                    continue;
                }
                var off = OmniLink.Core.TopologyGraph.Offset(OmniLink.Core.TopologyGraph.ParseEdge(sideCode));
                var target = (acell.X + off.X, acell.Y + off.Y);
                if (graph.Cells.Values.Any(c => c == target))
                {
                    failures.Add(sc.Tag + ": ghost ชี้ cell ที่มีการ์ดครอง (ลูกศรคู่ซ้อน marker): " + g.Tag);
                }
                if (cards.Any(c => c.Rect.IntersectsWith(g.Rect)))
                {
                    failures.Add(sc.Tag + ": ghost ทับการ์ด: " + g.Tag);
                }
                var expectedLabel = OmniLink.Core.Loc.T("map.place." + sideCode);
                var label = FindDescendantText(canvas, el => el.Text == expectedLabel && IsInside(el, g.Rect));
                if (label is null)
                {
                    failures.Add(sc.Tag + ": ghost label ผิด/หาย (คาด: '" + expectedLabel + "')");
                }
            }

            // 4) seam: จำนวน = คู่ที่มี edge และทั้งคู่มี cell (ไม่ซ้ำทิศ) + ห้ามทับการ์ด
            var seams = tagged.Where(t => t.Tag == "seam").ToList();
            var expectedSeams = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in graph.Cells)
            {
                foreach (var edge in new[] { OmniLink.Core.Strategies.ScreenEdge.Right, OmniLink.Core.Strategies.ScreenEdge.Bottom })
                {
                    var nb = graph.GetNeighbor(kv.Key, edge);
                    if (nb is null || !graph.Cells.ContainsKey(nb)) continue;
                    expectedSeams.Add(kv.Key + "|" + nb);
                }
            }
            if (seams.Count != expectedSeams.Count)
            {
                failures.Add(sc.Tag + ": seam count=" + seams.Count + " expected=" + expectedSeams.Count);
            }
            foreach (var sm in seams)
            {
                if (cards.Any(c => c.Rect.IntersectsWith(sm.Rect)))
                {
                    failures.Add(sc.Tag + ": seam ทับการ์ด");
                }
            }

            // 4b) hit-test ใต้ ghost ทุกใบ: กลาง ghost ต้องโดน ghost เอง (ไม่ใช่ element อื่นทับ)
            // — ดักปัญหา hit-area ที่ทำให้ "pan/วางได้แค่บางจุด" ตั้งแต่ build
            foreach (var g in ghosts)
            {
                var center = new System.Windows.Point(g.Rect.X + g.Rect.Width / 2, g.Rect.Y + g.Rect.Height / 2);
                var hit = win.TopologyCanvas.InputHitTest(center) as FrameworkElement;
                var walker = hit;
                var isGhostSubtree = false;
                while (walker is not null)
                {
                    if (walker == g.El) { isGhostSubtree = true; break; }
                    walker = System.Windows.Media.VisualTreeHelper.GetParent(walker) as FrameworkElement;
                }
                if (!isGhostSubtree)
                {
                    failures.Add(sc.Tag + ": ghost " + g.Tag + " hit-test โดน '" +
                        (hit?.GetType().Name ?? "null") + "' ไม่ใช่ตัว ghost เอง (hit-area ถูกบัง)");
                }
            }

            // 5) title ไม่ถูกตัด (วัดความกว้างที่ต้องการ เทียบ MaxWidth)
            foreach (var tb in FindTextBlocks(canvas))
            {
                if (tb.TextTrimming != TextTrimming.CharacterEllipsis || tb.MaxWidth <= 0) continue;
                var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
                var ft = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture,
                    tb.FlowDirection, typeface, tb.FontSize, System.Windows.Media.Brushes.Black,
                    VisualTreeHelper.GetDpi(tb).PixelsPerDip);
                if (ft.Width > tb.MaxWidth + 0.5)
                {
                    failures.Add(sc.Tag + ": title ถูกตัด ('" + tb.Text + "' ต้องการ " + ft.Width.ToString("0") + "px > " + tb.MaxWidth.ToString("0") + "px)");
                }
            }

            var path = Path.Combine(dir, "map-" + sc.Tag + ".png");
            var handle = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            if (GetWindowRect(handle, out var r))
            {
                using var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
                using var gr = System.Drawing.Graphics.FromImage(bmp);
                gr.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(r.Right - r.Left, r.Bottom - r.Top));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                captured.Add(path);
            }
            Console.WriteLine("scenario " + sc.Name + ": cards=" + cards.Count + " ghosts=" + ghosts.Count + " seams=" + seams.Count);
        }

        // ── pan/zoom/fit assertions (จุดที่แก้ "เกินขอบ") ──
        host.Config.Nodes = originalNodes;
        host.Kvm.ReloadLayout();
        win.MainTabs.SelectedIndex = 0;
        win.RefreshUiState();
        Pump(350);
        win.UpdateLayout();
        Pump(150);

        win.ApplyMapFit();
        var vw = win.MapHostGrid.ActualWidth;
        var vh = win.MapHostGrid.ActualHeight;
        var m = win.MapMatrix;
        // วัดจาก cards จริงบน canvas (tag card|) — transform แล้วต้องอยู่ใน viewport ทั้งหมด
        var cardRects = new List<Rect>();
        var tagged2List = new List<(FrameworkElement El, string Tag, Rect Rect)>();
        CollectTagged(win.TopologyCanvas, tagged2List);
        foreach (var t2 in tagged2List)
        {
            if (!t2.Tag.StartsWith("card|", StringComparison.Ordinal)) continue;
            var tr = OmniLink.Core.MapViewportMath.TransformRect(m, t2.Rect);
            cardRects.Add(tr);
            if (tr.X < 0 || tr.Y < 0 || tr.Right > vw + 1 || tr.Bottom > vh + 1)
            {
                failures.Add("fit: การ์ด '" + t2.Tag + "' หลุด viewport หลัง fit (X=" + tr.X.ToString("0") + " Y=" + tr.Y.ToString("0") + ")");
            }
        }
        if (cardRects.Count == 0) failures.Add("fit: ไม่มีการ์ดบนผัง");

        // zoom-in จนสุด = clamp ที่ 3.0
        for (var i = 0; i < 12; i++) win.MapZoomAtPoint(1.15, vw / 2, vh / 2);
        if (win.MapMatrix.M11 > OmniLink.Core.MapViewportMath.MaxScale + 0.001)
        {
            failures.Add("zoom: scale เกิน max (" + win.MapMatrix.M11.ToString("0.00") + ")");
        }
        // zoom-out จนสุด = clamp ที่ 0.4
        for (var i = 0; i < 30; i++) win.MapZoomAtPoint(1 / 1.15, vw / 2, vh / 2);
        if (win.MapMatrix.M11 < OmniLink.Core.MapViewportMath.MinScale - 0.001)
        {
            failures.Add("zoom: scale ต่ำกว่า min (" + win.MapMatrix.M11.ToString("0.00") + ")");
        }
        // pan: offset ขยับตาม delta เป๊ะ
        var before = win.MapMatrix;
        win.MapPanBy(120, -80);
        var afterPan = win.MapMatrix;
        if (Math.Abs(afterPan.OffsetX - (before.OffsetX + 120)) > 0.01 ||
            Math.Abs(afterPan.OffsetY - (before.OffsetY + -80)) > 0.01)
        {
            failures.Add("pan: offset ขยับผิด");
        }
        win.ApplyMapFit(); // กลับ fit
        Console.WriteLine("panzoom: scale=" + win.MapMatrix.M11.ToString("0.00") + " cards_in_viewport=" + cardRects.Count + "/" + cardRects.Count);

        // คืน config เดิม + ภาษา
        host.Config.Nodes = originalNodes;
        host.Kvm.ReloadLayout();
        OmniLink.Core.LocalizationService.SetLanguage(OmniLink.Core.LocalizationService.Thai);
        win.RefreshUiState();
        win.Topmost = false;

        Console.WriteLine("MAPSHOTS_OK count=" + captured.Count + " dir=" + Path.GetFullPath(dir));
        foreach (var f2 in failures)
        {
            Console.WriteLine("MAP_ASSERT_FAIL " + f2);
        }
        Console.WriteLine(failures.Count == 0 ? "MAP_ASSERT_ALL_OK" : "MAP_ASSERT_FAILED_COUNT=" + failures.Count);
        return failures.Count == 0 ? 0 : 2;
    }

    private static void CollectTagged(System.Windows.DependencyObject root,
        List<(FrameworkElement, string, Rect)> outList)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Tag is string tag && fe.Visibility == Visibility.Visible)
            {
                outList.Add((fe, tag, new Rect(
                    System.Windows.Controls.Canvas.GetLeft(fe),
                    System.Windows.Controls.Canvas.GetTop(fe),
                    fe.ActualWidth, fe.ActualHeight)));
            }
            CollectTagged(child, outList);
        }
    }

    private static TextBlock? FindDescendantText(System.Windows.DependencyObject root, Func<TextBlock, bool> match)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb && match(tb)) return tb;
            var deeper = FindDescendantText(child, match);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private static bool IsInside(TextBlock tb, Rect container)
    {
        // หาตำแหน่ง TextBlock เทียบ canvas ผ่านพ่อแม่สะสม — ใช้ Rect เปลี่ยนพิกัดไม่ได้ตรงนี้
        // เช็คแบบหยาบ: tb ต้องอยู่ใน subtree ที่ขอบเขตครอบ (caller ส่ง ghost rect มาแล้ว)
        var el = (FrameworkElement)VisualTreeHelper.GetParent(tb);
        while (el is not null)
        {
            var left = System.Windows.Controls.Canvas.GetLeft(el);
            var top = System.Windows.Controls.Canvas.GetTop(el);
            if (!double.IsNaN(left) && !double.IsNaN(top))
            {
                var r = new Rect(left + tb.Margin.Left, top + tb.Margin.Top, tb.ActualWidth, tb.ActualHeight);
                return container.IntersectsWith(r);
            }
            el = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(el);
        }
        return false;
    }

    private static List<TextBlock> FindTextBlocks(System.Windows.DependencyObject root)
    {
        var list = new List<TextBlock>();
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb) list.Add(tb);
            list.AddRange(FindTextBlocks(child));
        }
        return list;
    }

    /// <summary>ปล่อยให้ dispatcher ทำงานจริง ~ms มิลลิวินาที (layout/binding ตามทัน)</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
