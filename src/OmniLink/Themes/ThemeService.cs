using System;
using System.Windows;
using Wpf.Ui.Appearance;

namespace OmniLink.Themes;

/// <summary>
/// v2.0: ระบบธีมของแอป — แทน "วัสดุหน้าต่าง (Mica/Acrylic)" ที่ถอนออกแล้ว
/// หน้าต่างเป็นสีทึบตลอด (ประหยัดพลังงานสุด เหมาะเครื่อง Low-Mem) เหลือแค่เลือกโทนสี:
/// 0 = ตามปัจจุบัน (น้ำเงินเข้ม) · 1 = สีดำ · 2 = สว่าง
/// กลไก: สลับ ResourceDictionary ของ palette (คีย์ชุดเดียวกันทุกไฟล์) + สั่ง WPF-UI ตามระดับความสว่าง
/// </summary>
public static class ThemeService
{
    public const int Current = 0; // น้ำเงินเข้ม — ค่าเริ่มต้น/หน้าตาเดิมของแอป
    public const int Black = 1;
    public const int Light = 2;

    public static int CurrentMode { get; private set; } = Current;

    /// <summary>ธีมนี้เป็นโทนสว่างหรือไม่ (ใช้ตัดสิน DWM dark-mode attribute)</summary>
    public static bool IsLight(int mode) => mode == Light;

    public static void Apply(int mode)
    {
        CurrentMode = Math.Clamp(mode, 0, 2);
        var app = Application.Current;
        if (app is null) return;
        try
        {
            var uri = new Uri($"pack://application:,,,/OmniLink;component/Themes/Theme.{NameOf(CurrentMode)}.xaml", UriKind.Absolute);
            var existing = app.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/Theme.") == true);
            if (existing is not null)
            {
                if (existing.Source == uri) return;
                app.Resources.MergedDictionaries.Remove(existing);
            }
            app.Resources.MergedDictionaries.Insert(0, new ResourceDictionary { Source = uri });

            // ควบคุมไลบรารี WPF-UI ให้เป็นโทนเดียวกัน (ปุ่ม/อินพุตของ lib ที่ไม่ผ่าน palette ของเรา)
            ApplicationThemeManager.Apply(CurrentMode == Light ? ApplicationTheme.Light : ApplicationTheme.Dark);
        }
        catch (Exception ex)
        {
            OmniLink.Core.OmniLog.Warn("[theme] สลับธีมไม่สำเร็จ: " + ex.Message);
        }
    }

    private static string NameOf(int mode) => mode switch
    {
        Black => "Black",
        Light => "Light",
        _ => "Dark",
    };
}
