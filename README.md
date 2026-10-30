# OmniLink
[![CI](https://github.com/pepe-beep/OmniLink/actions/workflows/ci.yml/badge.svg)](https://github.com/pepe-beep/OmniLink/actions/workflows/ci.yml)

KVM ระหว่างเครื่องผ่าน LAN: ควบคุมเมาส์/คีย์บอร์ดข้ามเครื่อง, clipboard สองทิศ (ข้อความ/รูป/ไฟล์), สตรีมเสียงสองทาง (GStreamer RTP), topology map, จับคู่แบบ TLS+TOFU+PIN
Windows host (WPF) · Linux host daemon (headless) · Windows/Linux node agent (Python, ไม่มี dependency ภายนอก)

## Build

```powershell
dotnet build OmniLink.slnx
```

## Test

```powershell
# รันชุดทดสอบทั้งหมด (C# Unit Tests + Python Agent Test Suite)
python tests/run_all_tests.py
```

## Setup Node Agent (เครื่องลูกข่าย)

- **Windows:** รัน `node_agent\install_node.bat` หรือ `python node_agent/omnilink_agent.py`
  * ต้องการเพียง Python 3.8+ (ไม่ต้องติดตั้งโมดูลหรือ dependency ภายนอกเพิ่มเติม)
  * รองรับ PowerShell Self-Signed Fallback อัตโนมัติสำหรับสร้างใบรับรอง TLS ปลอดภัยในตัว โดยไม่ต้องติดตั้ง OpenSSL เพิ่มเติม
- **Linux:** รัน `node_agent/install_node.sh` หรือ `python3 node_agent/omnilink_agent.py`

## Publish (Windows, self-contained single-file)

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

## หมายเหตุ

- `omnilink_config.json` / `logs/` / `runstate/` เป็นไฟล์ runtime ของเครื่อง (มี token จับคู่) — ไม่ได้เก็บใน repo ตามดุลยพินิจ
- โปรเจกต์นี้เป็นโปรเจกต์ส่วนตัว — เอกสารภายในอยู่นอก repo
