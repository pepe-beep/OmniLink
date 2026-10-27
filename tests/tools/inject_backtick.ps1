Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class K {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, UIntPtr e);
}
"@
# กดและปล่อยปุ่ม backtick (~) scanCode 0x29
[K]::keybd_event(0xC0, 0x29, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 60
[K]::keybd_event(0xC0, 0x29, 2, [UIntPtr]::Zero)
Write-Host "backtick injected"
