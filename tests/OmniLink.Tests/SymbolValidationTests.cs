using System.IO;
using System.Text.RegularExpressions;
using Wpf.Ui.Controls;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v1.9 regression: scan ไฟล์ XAML ทั้งหมดใน src/OmniLink จริง แล้วดึงทุก Symbol ที่ถูกใช้
/// ทั้งสองรูปแบบ — attribute Symbol="X" (ui:SymbolIcon) และ markup {ui:SymbolIcon X} (บน ui:Button.Icon)
/// ตัวไหนไม่มีจริงใน Wpf.Ui.SymbolRegular = XamlParseException ตอน runtime แบบเงียบ
/// (เจอจริง 23 ต.ค.: Memory24 ทำหน้า Advanced Settings เปิดไม่ได้; v1.9 ปิดช่อง markup-form ที่ยังไม่ถูกตรวจ 7 จุด)
/// </summary>
public class SymbolValidationTests
{
    private static readonly Regex AttributeForm = new(
        "Symbol\\s*=\\s*\"([A-Za-z0-9]+)\"", RegexOptions.Compiled);
    private static readonly Regex MarkupForm = new(
        "\\{ui:SymbolIcon\\s+([A-Za-z0-9]+)", RegexOptions.Compiled);

    /// <summary>หา root ของ repo (โฟลเดอร์ที่มี src/OmniLink) จาก working dir ของ test runner</summary>
    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "OmniLink");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "OmniLink.csproj")))
                return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("หา src/OmniLink ไม่เจอจาก " + AppContext.BaseDirectory);
    }

    public static TheoryData<string> SymbolsUsedInXaml
    {
        get
        {
            var data = new TheoryData<string>();
            var root = FindSourceRoot();
            var files = Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains("obj") && !f.Contains("bin"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Assert.True(files.Count >= 4, $"ต้องเจอไฟล์ XAML อย่างน้อย 4 ไฟล์ — เจอ {files.Count} (root={root})");

            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (Match m in AttributeForm.Matches(text)) found.Add(m.Groups[1].Value);
                foreach (Match m in MarkupForm.Matches(text)) found.Add(m.Groups[1].Value);
            }

            Assert.True(found.Count >= 12, $"ต้องเจอ Symbol ใน XAML อย่างน้อย 12 ตัว — เจอ {found.Count}");
            foreach (var s in found) data.Add(s);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SymbolsUsedInXaml))]
    public void Symbol_MustExistInWpfUiEnum(string symbolName)
    {
        Assert.True(
            Enum.TryParse<SymbolRegular>(symbolName, out _),
            $"Symbol '{symbolName}' ไม่มีใน Wpf.Ui.SymbolRegular — จะทำให้หน้าต่างที่ใช้มันเปิดไม่ได้ตอน runtime");
    }
}
