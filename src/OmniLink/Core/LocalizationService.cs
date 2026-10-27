using System.Globalization;
using System.Windows;

namespace OmniLink.Core;

/// <summary>
/// v1.9 i18n: ระบบภาษา TH/EN ของ UI — ตรวจจับจากภาษา Windows ครั้งแรก (ui_language="auto"),
/// สลับ runtime ได้ทันทีโดยสลับ merged ResourceDictionary (XAML อ้างด้วย {DynamicResource Loc.*})
/// ฝั่ง C# ใช้ Loc.T(key) สำหรับ banner/MessageBox/tray
/// หมายเหตุ: ปุ่ม ENG/ไทย บนหน้าจอเป็น "คีย์บอร์ด layout" ของเครื่องลูก — ไม่เกี่ยวกับภาษา UI นี้
/// </summary>
public static class LocalizationService
{
    public const string Auto = "auto";
    public const string Thai = "th-TH";
    public const string English = "en-US";

    /// <summary>แจ้งเมื่อภาษาเปลี่ยน — ผู้ฟัง (tray/หน้า settings) ต้อง rebuild ข้อความที่เขียนด้วยโค้ด</summary>
    public static event Action? LanguageChanged;

    public static string Current { get; private set; } = English;

    /// <summary>แปลงค่า config ("auto"/"th-TH"/"en-US") เป็นภาษาจริง — auto = ตามภาษา Windows (th* → ไทย)</summary>
    public static string Resolve(string setting)
    {
        if (setting == Thai || setting == English) return setting;
        var iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.Equals(iso, "th", StringComparison.OrdinalIgnoreCase) ? Thai : English;
    }

    /// <summary>เรียกครั้งเดียวตอนบูต ก่อน InitializeComponent ของหน้าต่างแรก</summary>
    public static void Initialize(string configValue)
    {
        // v2.0: เสียบ resolver ให้แกนกลาง (Loc ใน OmniLink.Core) อ่านข้อความจาก ResourceDictionary ของ UI
        Loc.Resolver = key => Application.Current?.TryFindResource(key) as string;
        Current = Resolve(configValue);
        ApplyDictionary(Current);
        try { CultureInfo.CurrentUICulture = new CultureInfo(Current); } catch { }
    }

    /// <summary>สลับภาษาทันที (รวมค่า "auto") — เขียน config เองโดยผู้เรียก</summary>
    public static void SetLanguage(string configValue)
    {
        var code = Resolve(configValue);
        var changed = code != Current;
        Current = code;
        ApplyDictionary(code);
        try { CultureInfo.CurrentUICulture = new CultureInfo(code); } catch { }
        if (changed) LanguageChanged?.Invoke();
    }

    private static void ApplyDictionary(string code)
    {
        var app = Application.Current;
        if (app is null) return;
        try
        {
            var source = new Uri($"pack://application:,,,/OmniLink;component/Localization/Strings.{code}.xaml", UriKind.Absolute);
            var existing = app.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source?.OriginalString.Contains("/Localization/Strings.") == true);
            if (existing is not null)
            {
                if (existing.Source == source) return;
                app.Resources.MergedDictionaries.Remove(existing);
            }
            app.Resources.MergedDictionaries.Insert(0, new ResourceDictionary { Source = source });
        }
        catch (Exception ex)
        {
            OmniLog.Error("[i18n] โหลดคลังภาษาไม่สำเร็จ: " + code, ex);
        }
    }
}
