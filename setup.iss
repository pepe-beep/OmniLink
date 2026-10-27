; Inno Setup 7 script — OmniLink Mesh KVM & Two-Way Low-Latency Audio
; ขั้นตอนก่อนคอมไพล์: รัน publish.ps1 เพื่อสร้าง publish\ (self-contained single file)
; คอมไพล์: "C:\Program Files\Inno Setup 7\ISCC.exe" setup.iss
;
; สถาปัตยกรรมแบบเดียวกับ AppGroupLauncher:
;   - PrivilegesRequired=lowest: ติดตั้งต่อผู้ใช้ (%LOCALAPPDATA%\Programs\OmniLink) ไม่ต้องขอ Admin / UAC
;   - CloseApplications=yes: สั่งปิด OmniLink.exe ที่กำลังทำงานก่อนติดตั้งทับ (กันปัญหาไฟล์ล็อก)
;   - ChangesAssociations=yes: รีเฟรช Explorer และ Shell Icon Cache ทันทีหลังติดตั้ง
;   - Self-contained Single File: ฝัง .NET runtime และ dependencies ทั้งหมด ไม่ต้องติดตั้ง runtime เพิ่ม

#define MyAppName "OmniLink"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "OmniLink Project"
#define MyAppExeName "OmniLink.exe"

[Setup]
AppId={{8B73E12D-9F08-41C2-8EA9-38B0B48A1842}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=Assets\app.ico
Compression=lzma2
SolidCompression=yes
OutputDir=InstallerOutput
OutputBaseFilename=OmniLink_Setup
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Self-contained single file: เครื่องปลายทางไม่ต้องติดตั้ง .NET runtime เพิ่มเติม
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "publish\node_agent\*"; DestDir: "{app}\node_agent"; Flags: ignoreversion recursesubdirs createallsubdirs
; v2.0: host daemon headless (omnilink-hostd.exe) — ใช้จาก terminal/สคริปต์
Source: "publish\hostd-win\omnilink-hostd.exe"; DestDir: "{app}"; Flags: ignoreversion
; v2.1 onboarding: ตัวติดตั้งพกพาสำหรับเครื่อง Windows ข้างๆ (มี Python ฝัง) — ปุ่ม "เพิ่มเครื่องแรก" เปิดโฟลเดอร์นี้ให้ผู้ใช้
Source: "publish\installers\OmniLink-Node-Win-Portable.zip"; DestDir: "{app}\installers"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; Start Menu Shortcut
Name: "{autoprograms}\OmniLink"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
; Desktop Shortcut (Optional Task)
Name: "{autodesktop}\OmniLink"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; เก็บกวาดไฟล์ log และ runstate ตอนถอนการติดตั้ง
Type: files; Name: "{autodesktop}\OmniLink.lnk"
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\runstate"
Type: filesandordirs; Name: "{app}"
