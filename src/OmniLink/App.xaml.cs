using System.Windows;

namespace OmniLink;

public partial class App : Application
{
    // Global exception handlers อยู่ที่ Program.Main เท่านั้น (Single Source of Truth)
    // การบันทึก crash รวมศูนย์ที่ Program.LogCrash → logs/crash.log เท่านั้น
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
    }
}
