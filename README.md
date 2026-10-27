# OmniLink

KVM ระหว่างเครื่องผ่าน LAN: ควบคุมเมาส์/คีย์บอร์ดข้ามเครื่อง, clipboard สองทิศ (ข้อความ/รูป/ไฟล์), สตรีมเสียงสองทาง (GStreamer RTP), topology map, จับคู่แบบ TLS+TOFU+PIN
Windows host (WPF) · Linux host daemon (headless) · Windows/Linux node agent (Python, ไม่มี dependency)

## Build

```powershell
dotnet build OmniLink.slnx
```

## Test

```powershell
dotnet test tests/OmniLink.Tests/OmniLink.Tests.csproj
python tests/unit_agent/test_agent_edge_machine.py
```

## Publish (Windows, self-contained single-file)

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

## หมายเหตุ

- `omnilink_config.json` / `logs/` / `runstate/` เป็นไฟล์ runtime ของเครื่อง (มี token จับคู่) — ไม่ได้เก็บใน repo ตามดุลยินสอบ
- โปรเจกต์นี้เป็นโปรเจกต์ส่วนตัว — เอกสารภายในอยู่นอก repo
