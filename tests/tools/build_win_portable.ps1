param([string]$OutZip = "D:\OmniLink\dist\OmniLink-Node-Win-Portable-2.0.0.zip")
# v2.0 เฟส 6: ประกอบ portable zip สำหรับ Windows เครื่องเก่า (Vista/Win7+)
# ประกอบจาก: python 3.8 embeddable (32MB) + node_agent + README — แตกแล้วรัน start-node.bat ได้เลย
$ErrorActionPreference = "Stop"
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent  # tests/tools -> tests -> repo root
$work = Join-Path $env:TEMP "omnilink-winport"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null
$pyDir = Join-Path $work "python"

# 1) python embeddable (โหลดครั้งแรกแล้วแคชใน temp)
$embedZip = Join-Path $env:TEMP "py38-embed.zip"
if (-not (Test-Path $embedZip)) {
    Invoke-WebRequest -Uri "https://www.python.org/ftp/python/3.8.10/python-3.8.10-embed-amd64.zip" -OutFile $embedZip
}
Expand-Archive -Path $embedZip -DestinationPath $pyDir -Force

# เปิด import site (embeddable ปิดอยู่ default — ปลดคอขวด _python_path)
$pth = Join-Path $pyDir "python38._pth"
if (Test-Path $pth) {
    (Get-Content $pth) -replace "^#import site", "import site" | Set-Content $pth
}

# 2) ไฟล์ agent + สคริปต์รัน
Copy-Item (Join-Path $repo "node_agent\omnilink_agent.py") $work -Force

@'
@echo off
REM OmniLink Node Agent (Windows portable) - รันตรงนี้ได้เลยไม่ต้องติดตั้ง Python
setlocal
cd /d "%~dp0"
python\python.exe omnilink_agent.py %*
pause
'@ | Set-Content (Join-Path $work "start-node.bat") -Encoding ASCII

@'
OmniLink Node Agent - Windows Portable (v2.0.0)
===============================================
ใช้ได้ทันทีบน Windows Vista SP2 / 7 / 8 / 10 / 11 (x64) โดยไม่ต้องติดตั้งอะไรเพิ่ม

วิธีใช้:
  1) แตก zip นี้ไว้ที่ไหนก็ได้ (เช่น Desktop)
  2) ดับเบิลคลิก start-node.bat
  3) อ่าน PIN จับคู่จากหน้าต่างที่เปิดขึ้น (รูปแบบ xxx-xxx)
  4) ที่เครื่อง HOST: เปิด OmniLink -> Scan LAN / Add Manually -> ใส่ PIN

หมายเหตุ:
  - สถานะ (token/cert/PIN) ถูกเก็บที่ %USERPROFILE%\omnilink\ (เหมือนฉบับติดตั้งปกติ)
  - เครื่อง 32-bit (Vista x86): ใช้ install_node.bat จาก repo แทน (ต้องมี Python 3.8 ติดตั้ง)
  - เสียง: ยังไม่รองรับบน node ฝั่ง Windows (v2.0)
'@ | Set-Content (Join-Path $work "README-TH.txt") -Encoding UTF8

# 3) แพ็ก
$dist = Split-Path $OutZip -Parent
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
if (Test-Path $OutZip) { Remove-Item $OutZip -Force }
Compress-Archive -Path (Join-Path $work "*") -DestinationPath $OutZip
$sizeMb = [math]::Round((Get-Item $OutZip).Length / 1MB, 1)
Write-Output "BUILT $OutZip (${sizeMb} MB)"
