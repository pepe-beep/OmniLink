using System.Globalization;

namespace OmniLink.Core;

/// <summary>
/// ตัวย่อสำหรับโค้ด C# ทุกฝั่ง — key หาย = ได้ fallback กลับ ไม่มีวันโยน exception
/// v2.0: ย้ายมาอยู่ในแกนกลางเพื่อให้ headless host ใช้ได้ — ฝั่ง UI บูตแล้วเสียบ Resolver
/// (อ่านจาก WPF ResourceDictionary), ฝั่ง headless เรียกตรงๆ ก็ได้ fallback ภาษาไทยตามค่าเดิม
/// </summary>
public static class Loc
{
    /// <summary>UI เสียบตอนบูต (คืนข้อความจาก resource หรือ null) — null = ใช้ fallback ล้วน</summary>
    public static Func<string, string?>? Resolver { get; set; }

    /// <summary>ดึงข้อความ localized — ใส่ fallback ไว้เสมอสำหรับ path วิกฤต (crash handler ฯลฯ)</summary>
    public static string T(string key, string? fallback = null, params object[] args)
    {
        var text = key;
        try
        {
            if (Resolver?.Invoke("Loc." + key) is string s && !string.IsNullOrEmpty(s))
            {
                text = s;
            }
            else if (fallback is not null)
            {
                text = fallback;
            }
        }
        catch
        {
            if (fallback is not null) text = fallback;
        }
        try
        {
            return args.Length > 0 ? string.Format(CultureInfo.CurrentUICulture, text, args) : text;
        }
        catch
        {
            return text;
        }
    }
}
