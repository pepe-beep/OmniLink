using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// v1.9 i18n guard — 3 ชั้น:
/// 1) key parity: ทุก key ใน Strings.th-TH ต้องมีคู่ใน Strings.en-US และกลับกัน (ห้ามขาดภาษาใด)
/// 2) key coverage: ทุก key ที่ XAML ({DynamicResource Loc.X}) และ C# (Loc.T("x")) อ้าง
///    ต้องมีอยู่ในคลังจริง — DynamicResource หา key ไม่เจอ = ข้อความหายเงียบๆ
/// 3) format parity: placeholder {0} {1} ต้องตรงกันทั้งสองภาษา (กัน string.Format พัง runtime)
/// </summary>
public class LocalizationTests
{
    private static string FindLocalizationDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "OmniLink", "Localization");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("หา src/OmniLink/Localization ไม่เจอจาก " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> LoadTable(string fileName)
    {
        var path = Path.Combine(FindLocalizationDir(), fileName);
        Assert.True(File.Exists(path), "ไฟล์คลังภาษาหายไป: " + path);
        var text = File.ReadAllText(path);
        var map = new Dictionary<string, string>();
        var rx = new Regex("""x:Key="Loc\.([A-Za-z0-9_.]+)"\s*>(.*?)</sys:String>""", RegexOptions.Singleline);
        foreach (Match m in rx.Matches(text))
        {
            var key = m.Groups[1].Value;
            // XAML ห้ามมี key ซ้ำ — ResourceDictionary จะโยน exception ตอน runtime
            Assert.True(!map.ContainsKey(key), $"key '{key}' ซ้ำใน {fileName}");
            map[key] = m.Groups[2].Value;
        }
        return map;
    }

    private static HashSet<string> LoadThKeys() => new(LoadTable("Strings.th-TH.xaml").Keys);
    private static HashSet<string> LoadEnKeys() => new(LoadTable("Strings.en-US.xaml").Keys);

    [Fact]
    public void Dictionary_MustHaveAtLeast300Keys()
    {
        Assert.True(LoadThKeys().Count >= 300, "คลังไทยมี key น้อยผิดปกติ: " + LoadThKeys().Count);
        Assert.True(LoadEnKeys().Count >= 300, "คลังอังกฤษมี key น้อยผิดปกติ: " + LoadEnKeys().Count);
    }

    [Fact]
    public void Thai_And_English_MustHaveIdenticalKeySets()
    {
        var th = LoadThKeys();
        var en = LoadEnKeys();
        var missingInEn = th.Except(en).OrderBy(k => k).ToList();
        var missingInTh = en.Except(th).OrderBy(k => k).ToList();
        Assert.True(missingInEn.Count == 0, "key เหล่านี้มีในไทยแต่ไม่มีในอังกฤษ: " + string.Join(", ", missingInEn));
        Assert.True(missingInTh.Count == 0, "key เหล่านี้มีในอังกฤษแต่ไม่มีในไทย: " + string.Join(", ", missingInTh));
    }

    /// <summary>ทุก key ที่ source อ้างต้องมีจริงในคลัง — กันข้อความหายเงียบจาก DynamicResource</summary>
    [Fact]
    public void EveryReferencedKey_MustExistInBothDictionaries()
    {
        var th = LoadThKeys();
        var en = LoadEnKeys();
        var srcDir = Path.GetFullPath(Path.Combine(FindLocalizationDir(), ".."));

        var referenced = new HashSet<string>();
        foreach (var file in Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue; // ไฟล์ generate — เทียบกับ source จริงเท่านั้น
            }
            if (!file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (file.Contains(Path.Combine("Localization", "Strings."), StringComparison.OrdinalIgnoreCase))
            {
                continue; // ไฟล์คลังเองไม่นับ (มี x:Key ของตัวเอง)
            }
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\{DynamicResource Loc\.([A-Za-z0-9_.]+)\}"))
            {
                referenced.Add(m.Groups[1].Value);
            }
            foreach (Match m in Regex.Matches(text, "Loc\\.T\\(\"([A-Za-z0-9_.]+)\""))
            {
                var key = m.Groups[1].Value;
                if (key.EndsWith(".", StringComparison.Ordinal))
                {
                    continue; // prefix แบบ dynamic เช่น "policy." + value — key จริงถูกเช็คผ่าน parity อยู่แล้ว
                }
                referenced.Add(key);
            }
            // Loc.T(p.TitleKey) ของหน้า settings — TitleKey ประกาศเป็น string literal ใน .cs
            foreach (Match m in Regex.Matches(text, "TitleKey\\s*=>\\s*\"([A-Za-z0-9_.]+)\""))
            {
                referenced.Add(m.Groups[1].Value);
            }
        }

        Assert.True(referenced.Count >= 250, "key ที่ source อ้างน้อยผิดปกติ: " + referenced.Count);
        var badTh = referenced.Except(th).OrderBy(k => k).ToList();
        var badEn = referenced.Except(en).OrderBy(k => k).ToList();
        Assert.True(badTh.Count == 0, "key ที่ source ใช้แต่ไม่มีในคลังไทย: " + string.Join(", ", badTh));
        Assert.True(badEn.Count == 0, "key ที่ source ใช้แต่ไม่มีในคลังอังกฤษ: " + string.Join(", ", badEn));
    }

    /// <summary>placeholder {0} {1} ต้องตรงกันทั้งสองภาษา — string.Format พังถ้าไม่ตรง</summary>
    [Fact]
    public void Placeholders_MustMatchAcrossLanguages()
    {
        var th = LoadTable("Strings.th-TH.xaml");
        var en = LoadTable("Strings.en-US.xaml");
        var bad = new List<string>();
        foreach (var (key, thValue) in th)
        {
            var thSlots = Regex.Matches(thValue, @"\{(\d+)\}").Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            var enSlots = Regex.Matches(en[key], @"\{(\d+)\}").Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
            if (!thSlots.SequenceEqual(enSlots))
            {
                bad.Add($"{key}: th=[{string.Join(",", thSlots)}] en=[{string.Join(",", enSlots)}]");
            }
        }
        Assert.True(bad.Count == 0, "placeholder ไม่ตรงกัน:\n" + string.Join("\n", bad));
    }
}
