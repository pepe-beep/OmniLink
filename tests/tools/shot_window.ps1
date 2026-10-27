param([string]$OutFile = "D:\OmniLink\logs\shot.png")
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32Shot {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  public struct RECT { public int L, T, R, B; }
  public const uint SWP_NOMOVE = 0x0002;
  public const uint SWP_NOSIZE = 0x0001;
  public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
  public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
# กับดัก DPI (TRAPS): ต้องประกาศ DPI-aware ก่อนใช้พิกัดจอ ไม่งั้น Windows virtualize พิกัด → จับผิดบริเวณ
[Win32Shot]::SetProcessDPIAware() | Out-Null
$p = Get-Process OmniLink -ErrorAction Stop | Select-Object -First 1
# v2.1: wizard/firstrun (ShowInTaskbar=False) ไม่ใช่ MainWindowHandle — ใช้ foreground ถ้าเป็นของ process นี้
$target = $p.MainWindowHandle
$fg = [Win32Shot]::GetForegroundWindow()
if ($fg -ne [IntPtr]::Zero -and $fg -ne $target) {
  $fgPid = 0
  [Win32Shot]::GetWindowThreadProcessId($fg, [ref]$fgPid) | Out-Null
  if ($fgPid -eq $p.Id) { $target = $fg; Write-Output "using foreground window $target" }
}
if ($target -eq [IntPtr]::Zero) { throw "no main window" }
# บทเรียนจาก memory: SetForegroundWindow เพียงอย่างเดียวโดน Windows foreground lock — ต้อง Topmost ก่อน
[Win32Shot]::ShowWindow($target, 9) | Out-Null
[Win32Shot]::SetWindowPos($target, [Win32Shot]::HWND_TOPMOST, 0, 0, 0, 0, [Win32Shot]::SWP_NOMOVE -bor [Win32Shot]::SWP_NOSIZE) | Out-Null
[Win32Shot]::SetForegroundWindow($target) | Out-Null
Start-Sleep -Milliseconds 1200
$r = New-Object Win32Shot+RECT
[Win32Shot]::GetWindowRect($target, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
if ($w -le 0 -or $h -le 0) { throw "bad rect $w x $h" }
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
[Win32Shot]::SetWindowPos($target, [Win32Shot]::HWND_NOTOPMOST, 0, 0, 0, 0, [Win32Shot]::SWP_NOMOVE -bor [Win32Shot]::SWP_NOSIZE) | Out-Null
Write-Output "SAVED $OutFile ${w}x${h}"
