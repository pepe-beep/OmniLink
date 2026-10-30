# Security Policy — OmniLink

> ภาษาไทยเป็นหลัก (โปรเจกต์ส่วนตัว/LAN) · ปรับปรุงตามแผน M6 (threat model ย่อ)

## Supported Versions

| Version | Supported |
|---------|-----------|
| 2.x     | ✅        |
| < 2.0   | ❌        |

## Reporting a Vulnerability

โปรเจกต์นี้เป็นซอฟต์แวร์ส่วนตัวสำหรับใช้งานใน LAN ของเจ้าของเครื่อง — ไม่มีช่องทางรับรายงานภายนอก
หากพบช่องโหว่: เปิด issue ที่ https://github.com/pepe-beep/OmniLink/issues พร้อมรายละเอียดการทำซ้ำ

## ขอบเขตความเชื่อมั่น (Threat Model — ย่อจาก M6)

**โมเดลภัยคุกคามที่รับไว้:** ผู้ไม่หวังดีใน **LAN เดียวกัน** พยายาม (1) สวมสิทธิ์ควบคุมเมาส์/คีย์บอร์ด (2) ดักฟัง/แก้ประเด็นข้อมูล (3) จับคู่โดยไม่ได้รับอนุญาต

**ตัวป้องกันที่ใช้งานจริง (ปรับปรุงตาม Audit REV4-GOLDEN):**

| ช่องทาง | การป้องกัน |
|---|---|
| TCP (control/input relay) | TLS 1.2+ self-signed cert · host pin fingerprint ต่อ node (TOFU) · รองรับ PowerShell fallback อัตโนมัติ |
| UDP (input packets) | HMAC-SHA256 trailer (truncated 16B) + Anti-Replay Protection ตาม RFC 6479 64-bit Sliding Window Bitmap + Monotonic Sequence Tracking |
| การตรวจสอบ Token | Enforce Fail-Closed: ปฏิเสธทันทีหาก Auth Token ว่างเปล่าหรือเป็น empty/whitespace (ทั้ง C# Core และ Python Node Agent) |
| การจับคู่ | PIN 6 หลัก CSPRNG + rate-limit (10 ครั้ง/10 นาที + บล็อก 5 นาที) + fingerprint โชว์คู่กันบนจอ |
| ความลับ | auth token ต่อ node ไม่ปรากฏใน log · จับคู่ใหม่ = PIN ใหม่ |
| นโยบายต่อ node | `policy` full/clipboard/view — ฝั่งรับ enforce ด้วยตัวเอง (`_packet_allowed` ปฏิเสธเอง) |

**ขอบเขตที่ไม่ป้องกัน (ยอมรับไว้ชัดเจน):**

- ผู้ใช้ privileged บนเครื่องใดเครื่องหนึ่งใน LAN = ความเชื่อมั่นของระบบทั้งหมด (local trust)
- MITM ที่ pin ได้ fingerprint ก่อน (first-connect window ของ TOFU) — ลดได้ด้วยเทียบ fingerprint บนจอตอนจับคู่
- การโจมตีระดับ OS (keylogger บนเครื่องใดเครื่องหนึ่ง) — อยู่นอกขอบเขต

## Hardening สำหรับผู้ติดตั้ง

1. ตรวจ `SHA256SUMS.txt` ก่อนใช้ไฟล์ที่ดาวน์โหลด (`certutil -hashfile OmniLink.exe SHA256` บน Windows)
2. ใช้งานบน LAN ส่วนตัวเท่านั้น (ไม่เปิดพอร์ต 24801/24802/50044-50049 ออกอินเทอร์เน็ต)
3. ลบ node ที่ไม่ใช้จากหน้า Security & Pairing (token เก่าจะถูกเพิกถอนพร้อม config)
