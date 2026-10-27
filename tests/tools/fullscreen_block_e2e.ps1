# E2E: บล็อกสลับจอเมื่อมีหน้าต่าง fullscreen (ต้องรันตอน OmniLink กำลังทำงาน)
# ขั้นตอน: สร้างหน้าต่าง borderless ครอบจอหลักเป๊ะ → ยิงเมาส์ชนขอบขวา → ต้อง "ไม่มี" switch_to_node ใหม่
#          จากนั้นย่อหน้าต่างเป็นขนาดธรรมดา → ยิงขอบอีกครั้ง → ต้องมี switch_to_node
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

$log = "D:\OmniLink\logs\omnilink-$(Get-Date -Format yyyyMMdd).log"
function Count-Switch([int]$afterLine) {
    (Get-Content $log | Select-Object -Skip $afterLine | Select-String "switch_to_node").Count
}
$base = (Get-Content $log).Count

# สร้างหน้าต่าง borderless ครอบจอหลัก (จอหลักของเครื่องนี้: 1920x1080 ที่ 0,0)
$win = New-Object System.Windows.Window
$win.WindowStyle = 'None'
$win.ResizeMode = 'NoResize'
$win.Topmost = $true
$win.Left = 0; $win.Top = 0; $win.Width = 1920; $win.Height = 1080
$win.Background = 'Black'
$win.Content = "FULLSCREEN TEST WINDOW"
$shown = $false
$win.Add_ContentRendered({ $script:shown = $true })
$win.Show()
$win.Activate()
[Win32]::SetForegroundWindow([System.Windows.Interop.WindowInteropHelper]::new($win).Handle) | Out-Null

$deadline = (Get-Date).AddSeconds(6)
while (-not $shown -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 100
    [void]$win.Dispatcher.Invoke([action]{}, 'Background')
}
Start-Sleep -Milliseconds 600

# ยิงเมาส์ชนขอบขวาจอหลัก (VirtualRight=1920)
for ($i = 0; $i -lt 12; $i++) {
    [Win32]::SetCursorPos(1919, 540) | Out-Null
    Start-Sleep -Milliseconds 40
    [Win32]::SetCursorPos(1900, 540) | Out-Null
    Start-Sleep -Milliseconds 20
}
Start-Sleep -Milliseconds 800
$switchWhileFs = Count-Switch $base
Write-Host "RESULT_FS switch_count=$switchWhileFs (ต้องเป็น 0)"

# ย่อหน้าต่าง → ไม่ fullscreen แล้ว → ยิงขอบใหม่
$win.Dispatcher.Invoke({ $win.WindowState = 'Normal'; $win.Width = 800; $win.Height = 600; $win.Left = 100; $win.Top = 100 })
Start-Sleep -Milliseconds 700
$base2 = (Get-Content $log).Count
for ($i = 0; $i -lt 12; $i++) {
    [Win32]::SetCursorPos(1919, 540) | Out-Null
    Start-Sleep -Milliseconds 40
    [Win32]::SetCursorPos(1900, 540) | Out-Null
    Start-Sleep -Milliseconds 20
}
Start-Sleep -Milliseconds 800
$switchAfter = Count-Switch $base2
Write-Host "RESULT_NORMAL switch_count=$switchAfter (ต้อง >= 1)"

$win.Dispatcher.Invoke({ $win.Close() })
if ($switchWhileFs -eq 0 -and $switchAfter -ge 1) { Write-Host "E2E PASS"; exit 0 }
Write-Host "E2E FAIL"; exit 1
