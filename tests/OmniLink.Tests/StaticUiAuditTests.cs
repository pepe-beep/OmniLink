using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v1.9.1 Static UI audit — จับคลาสบั๊กที่ build ไม่มีทางจับ:
/// 1) style ที่ target ธีม-able control ต้องมี BasedOn (ไม่งั้นทับ dark style ของ Wpf.Ui → กล่องขาวมองไม่เห็น — เจอจริงในภาพผู้ใช้)
/// 2) IconSymbol ที่หน้า settings ส่งผ่าน binding ต้องมีจริงใน SymbolRegular (เจอจริง: ArrowLeftRight24 → icon การสลับจอหาย)
/// 3) x:Name ที่ "ไม่มีสายไฟ" (ไม่มี handler/binding/โค้ด/trigger อ้าง) = ตายเงียบ (เจอจริง: InstallCommandBox กล่องว่างไม่เคยมีโค้ด populate)
/// </summary>
public class StaticUiAuditTests
{
    private static string FindViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "OmniLink", "Views");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("หา src/OmniLink/Views ไม่เจอจาก " + AppContext.BaseDirectory);
    }

    private static List<string> ViewsXaml()
    {
        var root = FindViewsDir();
        return Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>App.xaml เป็น legacy ที่ render ถูกต้องมาตลอด — ขอบเขต audit ครอบเฉพาะ Views/</summary>
    [Fact]
    public void ThemedTypeStyles_InViews_MustHaveBasedOn()
    {
        var offenders = new List<string>();
        foreach (var file in ViewsXaml())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, "<Style\\b[^>]*>"))
            {
                var tag = m.Value;
                var type = Regex.Match(tag, "TargetType=\"(?:\\{x:Type )?(TextBox|ComboBox|CheckBox|Button)\\}?\"");
                if (!type.Success) continue;
                if (!tag.Contains("BasedOn=", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: <Style TargetType=\"{type.Groups[1].Value}\"> ไม่มี BasedOn (จะทับธีมมืดของ Wpf.Ui)");
                }
            }
        }
        Assert.True(offenders.Count == 0, "style ไม่มี BasedOn:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void PageIconSymbols_MustExistInWpfUiEnum()
    {
        var srcDir = Path.GetFullPath(Path.Combine(FindViewsDir(), ".."));
        var bad = new List<string>();
        var count = 0;
        foreach (var file in Directory.GetFiles(srcDir, "*.xaml.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "IconSymbol\\s*=>\\s*\"([A-Za-z0-9]+)\""))
            {
                count++;
                if (!Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(m.Groups[1].Value, out _))
                {
                    bad.Add($"{Path.GetFileName(file)}: '{m.Groups[1].Value}' ไม่มีใน SymbolRegular (icon จะหายเงียบๆ)");
                }
            }
        }
        Assert.True(count >= 10, "ต้องเจอ IconSymbol อย่างน้อย 10 หน้า — เจอ " + count);
        Assert.True(bad.Count == 0, "icon symbol ผิด:\n" + string.Join("\n", bad));
    }

    /// <summary>x:Name ถือว่า "มีสายไฟ" ถ้าข้อใดข้อหนึ่ง:
    /// (a) โค้ด (.cs ทุกไฟล์) อ้างถึง (b) element ตัวเองมี event handler attr (c) element มี {Binding}
    /// (d) ชื่อปรากฏ ≥2 ครั้งในไฟล์เดียวกัน (trigger TargetName / ElementName) (e) อยู่ใน allowlist (label แสดงผลล้วน)</summary>
    [Fact]
    public void XamlNames_MustBeWired_OrAllowlisted()
    {
        // label/ขอบประดับที่ตั้งใจให้เป็น purely-visual — ผ่านการพิจารณาแล้ว
        var allowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "StreamStatusLabel", "MicStatusLabel", // ป้ายสถานะคงที่ (สถานะจริงโชว์ที่ปุ่ม ACTIVE/STANDBY)
        };

        var handlerAttr = new Regex(
            "\\b(?:Click|Checked|Unchecked|SelectionChanged|TextChanged|ValueChanged|MouseDown|MouseUp|" +
            "MouseLeftButtonUp|MouseLeftButtonDown|PreviewMouseLeftButtonDown|PreviewMouseMove|" +
            "PreviewMouseLeftButtonUp|PreviewKeyDown|KeyDown|DragEnter|DragLeave|Drop|Loaded|" +
            "MouseWheel|ScrollChanged|SelectedItemChanged|IsVisibleChanged|PreviewMouseWheel)\\s*=",
            RegexOptions.Compiled);

        var srcDir = Path.GetFullPath(Path.Combine(FindViewsDir(), ".."));
        var allCode = new StringBuilder();
        foreach (var file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            allCode.Append(File.ReadAllText(file));
        }
        var code = allCode.ToString();

        var dead = new List<string>();
        var total = 0;
        foreach (var file in ViewsXaml())
        {
            var text = File.ReadAllText(file);
            var occurrencesInFile = new Dictionary<string, int>();
            foreach (Match m in Regex.Matches(text, "x:Name=\"([A-Za-z_][A-Za-z0-9_]*)\""))
            {
                total++;
                var name = m.Groups[1].Value;
                occurrencesInFile[name] = occurrencesInFile.TryGetValue(name, out var c) ? c + 1 : 1;
            }

            foreach (var (name, _) in occurrencesInFile)
            {
                if (allowlist.Contains(name)) continue;
                if (code.Contains(name, StringComparison.Ordinal)) continue;   // (a)
                if (occurrencesInFile[name] >= 2) continue;                    // (d) trigger/ElementName

                // หา element ที่ประกาศ x:Name นี้ แล้วดูว่าตัวเองมี handler หรือ binding ไหม (b)(c)
                var elemRx = new Regex("<(?![?!])[\\w.:]+[^<>]*?x:Name=\"" + Regex.Escape(name) + "\"[^<>]*?>", RegexOptions.Singleline);
                var wired = false;
                foreach (Match em in elemRx.Matches(text))
                {
                    var elem = em.Value;
                    if (handlerAttr.IsMatch(elem) || elem.Contains("{Binding", StringComparison.Ordinal))
                    {
                        wired = true;
                        break;
                    }
                }
                if (wired) continue;

                // (e2) อยู่ใน ControlTemplate = part name ของ template (trigger ใน template อ้างผ่าน TargetName)
                foreach (Match tm in Regex.Matches(text, "<ControlTemplate[\\s\\S]*?</ControlTemplate>"))
                {
                    if (tm.Value.Contains("x:Name=\"" + name + "\"", StringComparison.Ordinal))
                    {
                        wired = true;
                        break;
                    }
                }
                if (!wired)
                {
                    dead.Add($"{Path.GetFileName(file)}: x:Name \"{name}\" ไม่มีสายไฟ (handler/binding/โค้ด/trigger)");
                }
            }
        }
        Assert.True(total >= 50, "x:Name น้อยผิดปกติ: " + total);
        Assert.True(dead.Count == 0, "x:Name ไร้สายไฟ:\n" + string.Join("\n", dead));
    }
}
