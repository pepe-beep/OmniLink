@echo off
REM ==============================================================================
REM OmniLink Node Agent Installer for Windows (Vista+ / Win7+ / ทุกรุ่น, ไม่ต้อง admin)
REM ต้องมี Python 3.8 ขึ้นไป (รัน "python --version" เช็คได้) — ไม่ต้องลงอย่างอื่น
REM ==============================================================================
setlocal EnableDelayedExpansion
title OmniLink Node Agent Installer

echo.
echo   ___                 _ _     _       _     _
echo  / _ \ _ __ ___  _ __ (_) |   (_)_ __ | | __
echo ^| ^| ^| '_ ` _ \^| '_ `^| ^| ^|   ^| ^| '_ ^| ^|/ /
echo ^| ^|_^| ^| ^| ^| ^| ^| ^| ^| ^| ^|_^|   ^| ^| ^| ^|   ^|
echo  \___/_^| ^|_^| ^|_^|_^| ^|_^|\__, ^|_^|_^|_^|\_\  Node Agent (Windows)
echo                           ^|___/
echo.

where python >nul 2>nul
if errorlevel 1 (
    echo [!] ไม่พบ Python บนเครื่อง — ติดตั้ง Python 3.8+ ก่อนแล้วรันใหม่
    echo     (ดาวน์โหลด: https://www.python.org/downloads/windows/ - เลือก "Add python.exe to PATH")
    pause
    exit /b 1
)

set INSTALL_DIR=%USERPROFILE%\omnilink
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

echo [1/3] ติดตั้งไฟล์ agent...
copy /y "%~dp0omnilink_agent.py" "%INSTALL_DIR%\omnilink_agent.py" >nul
if errorlevel 1 (
    echo [!] คัดลอกไฟล์ไม่สำเร็จ (วาง install_node.bat ไว้ข้าง omnilink_agent.py)
    pause
    exit /b 1
)

echo [2/3] สร้าง identity + PIN จับคู่...
python -c "import uuid,secrets,os,pathlib;d=pathlib.Path(os.path.expanduser('~/omnilink'));d.mkdir(exist_ok=True);p=d/'machine_id';p.write_text('node-'+str(uuid.uuid4())[:8]) if not p.exists() else None;k=d/'pairing_pin';k.write_text(f'{secrets.randbelow(900)+100}-{secrets.randbelow(900)+100}') if not k.exists() else None;print('OK')"

echo [3/3] ตั้งเปิดอัตโนมัติตอนบูต (Startup folder - ไม่ต้อง admin)...
set STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
> "%STARTUP%\omnilink-agent.bat" echo @echo off
>> "%STARTUP%\omnilink-agent.bat" echo start "" /min python "%INSTALL_DIR%\omnilink_agent.py"

echo.
echo ==============================================================================
echo   ติดตั้งเสร็จแล้ว - กำลังเริ่มทำงาน (แถบหน้าต่างจะย่อเก็บ)
echo ==============================================================================
start "" /min python "%INSTALL_DIR%\omnilink_agent.py"
timeout /t 3 >nul

echo   PIN จับคู่ + fingerprint อยู่ในไฟล์: %INSTALL_DIR%\agent.log
echo   ดูได้ด้วยคำสั่ง:  type "%INSTALL_DIR%\agent.log"
echo.
echo   ขั้นต่อไปที่เครื่อง HOST:
echo     1) เปิด OmniLink ที่เครื่องหลัก
echo     2) Scan LAN / Add Manually ชี้ IP เครื่องนี้
echo     3) กรอก PIN จาก agent.log
echo ==============================================================================
pause
