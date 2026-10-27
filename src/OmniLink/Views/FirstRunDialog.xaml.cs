using System.Windows;
using OmniLink.Core;

namespace OmniLink.Views;

/// <summary>v1.9 C4: ทิปครั้งแรกที่เปิด — ฉุกเฉิน/สลับจอ/จับคู่/เปลี่ยนภาษา (tick "ไม่ต้องแสดงอีก" = เก็บ config)</summary>
public partial class FirstRunDialog : Window
{
    public FirstRunDialog()
    {
        InitializeComponent();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>เปิดถ้ายังไม่เคยดู — คืน true ถ้าผู้ใช้เลือก "ไม่ต้องแสดงอีก"</summary>
    /// <summary>v2.1: ผู้ใช้อยากเริ่มเพิ่มเครื่องทันที — ปิด dialog แล้วตัวเรียกเปิด wizard ต่อให้</summary>
    public bool AddNowRequested { get; private set; }

    /// <summary>ผู้ใช้ติ๊ก "ไม่ต้องแสดงอีก" (default ติ๊กอยู่แล้วใน XAML)</summary>
    public bool DontShowAgain => ChkDontShow is not null && ChkDontShow.IsChecked == true;

    private void OnAddNowClick(object sender, RoutedEventArgs e)
    {
        AddNowRequested = true;
        DialogResult = true;
        Close();
    }

    public static bool ShowOnceIfNeeded(Window owner, OmniConfig config, Action persist)
    {
        if (config.FirstRunDone) return false;
        try
        {
            var dialog = new FirstRunDialog { Owner = owner };
            dialog.ShowDialog();
            if (dialog.ChkDontShow.IsChecked == true)
            {
                config.FirstRunDone = true;
                persist();
                return true;
            }
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[ui] first-run dialog failed: " + ex.Message);
        }
        return false;
    }
}
