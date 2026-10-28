# โปรแกรมช่วยเผยแพร่ OmniLink — self-contained single-file
# เครื่องปลายทางไม่ต้องมี .NET runtime ติดตั้ง
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\publish"
)

# ปิด instance ที่กำลังรันอยู่เพื่อป้องกัน Windows file lock
$running = Get-Process OmniLink -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running OmniLink process ($($running.Count) instance(s))..." -ForegroundColor Yellow
    Stop-Process -Name OmniLink -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

Write-Host "Publishing OmniLink ($Configuration win-x64 self-contained single-file)..." -ForegroundColor Cyan

dotnet publish "$PSScriptRoot\src\OmniLink\OmniLink.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    throw "publish failed"
}

# คัดลอก assets ที่จำเป็น เช่น node_agent และ app.ico ไปยัง publish directory
if (Test-Path "$PSScriptRoot\node_agent") {
    $targetAgentDir = Join-Path $OutputDir "node_agent"
    if (!(Test-Path $targetAgentDir)) { New-Item -ItemType Directory -Path $targetAgentDir | Out-Null }
    Copy-Item "$PSScriptRoot\node_agent\*" -Destination $targetAgentDir -Recurse -Force
}

# v2.1 onboarding: สร้าง portable zip สำหรับเครื่อง Windows ข้างๆ (ฝัง Python 3.8 — ไม่ต้องลงอะไรเพิ่ม)
$portableScript = Join-Path $PSScriptRoot "tests\tools\build_win_portable.ps1"
if (Test-Path $portableScript) {
    # ให้ zip ไปอยู่ใน publish\installers\ เพื่อแนบตาม setup.iss และให้ปุ่ม "เปิดโฟลเดอร์ตัวติดตั้ง" ในแอปหาเจอ
    & $portableScript -OutZip (Join-Path $OutputDir "installers\OmniLink-Node-Win-Portable.zip")
    if ($LASTEXITCODE -ne 0) { Write-Warning "portable zip build failed (onboarding installer จะขาด)" }
}

if (Test-Path "$PSScriptRoot\Assets\app.ico") {
    $targetAssetsDir = Join-Path $OutputDir "Assets"
    if (!(Test-Path $targetAssetsDir)) { New-Item -ItemType Directory -Path $targetAssetsDir | Out-Null }
    Copy-Item "$PSScriptRoot\Assets\app.ico" -Destination $targetAssetsDir -Force
}

# B10 (distribution hardening): SHA256SUMS — ผู้ใช้/เครื่องลูกตรวจของก่อนติดตั้งได้เสมอ
$sumsTargets = @(
    (Join-Path $OutputDir "OmniLink.exe"),
    (Join-Path $OutputDir "installers\OmniLink-Node-Win-Portable.zip")
)
$hostdExe = Join-Path $OutputDir "hostd\omnilink-hostd.exe"
if (Test-Path $hostdExe) { $sumsTargets += $hostdExe }
$sumsLines = @()
foreach ($t in $sumsTargets) {
    if (Test-Path $t) {
        $rel = [System.IO.Path]::GetFullPath($t).Substring([System.IO.Path]::GetFullPath($OutputDir).Length).Replace('\', '/').TrimStart('/')
        $h = (Get-FileHash $t -Algorithm SHA256).Hash.ToLowerInvariant()
        $sumsLines += "$h  $rel"
    }
}
if ($sumsLines.Count -gt 0) {
    $sumsPath = Join-Path $OutputDir "SHA256SUMS.txt"
    [System.IO.File]::WriteAllLines($sumsPath, $sumsLines)
    Write-Host "SHA256SUMS written: $sumsPath ($($sumsLines.Count) entries)" -ForegroundColor Green
}

Write-Host "Published to: $OutputDir" -ForegroundColor Green
$sizeMb = ((Get-ChildItem $OutputDir -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host ("Size: {0:N1} MB" -f $sizeMb) -ForegroundColor Green
$exe = Join-Path $OutputDir "OmniLink.exe"
if (Test-Path $exe) { Write-Host ("Exe timestamp: {0:yyyy-MM-dd HH:mm:ss} — เช็คตรงนี้ก่อนเชื่อว่า publish สด" -f (Get-Item $exe).LastWriteTime) -ForegroundColor Green }
