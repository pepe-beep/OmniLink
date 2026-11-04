# WIRE.md — OmniLink Wire Protocol (v2.1.1 baseline)

> **เอกสารนี้พอเขียน implementation ที่สองได้** จากการไล่โค้ดจริงทั้งสองฝั่ง (C# `src/OmniLink.Core/` + Python `node_agent/omnilink_agent.py`)
> อ้างแบบเดียวกับ WIRE.md ของ Beamer: นิยาม term, กติกา malformed, versioning, ตาราง message ครบ
> **ขอบเขต:** TCP control wire (v2.0 ปัจจุบัน) · UDP input wire v2 · File transfer · C2d history (ใหม่)
> **ยังไม่ครอบ:** IPC pipe ภายในเครื่อง (`OmniLink.Core.Ipc` — ดู `IPC-PROTOCOL-CONTRACT-S0.md`)

---

## 1. Terms

- **Host.** เครื่องควบคุมหลัก (Windows, `OmniLink.exe`/`omnilink-hostd`) — เปิด connection ออกหา node
- **Node.** เครื่องถูกควบคุม (`omnilink_agent.py`) — เปิด TLS server รับที่ TCP 24801 / UDP 24802
- **Pairing.** กระบวนการสร้าง token แรกด้วย PIN 6 หลัก + fingerprint โชว์คู่ (TOFU)
- **Peer token.** `auth_token` ต่อ node ที่ host ถือ — ใช้ derive UDP key
- **Link.** connection TCP หนึ่งเส้น; input ไหล host→node เสมอ (node ส่งกลับได้เฉพาะ event)
- **Route/Lifecycle.** Hot/Cold/Freeze + retry แบบ adaptive (ดู `ConnectionLifecycleState`)

## 2. Encodings

- **Machine id / uuid:** text จาก `secrets` (node-XXXXXXXX)
- **Tokens:** hex 64 ตัว (32 bytes) — จับคู่แล้วมีชีวิตจนกว่าจะลบ node
- **Messages:** UTF-8 JSON object `{"type": <string>, ...fields}` — ไม่มี BOM · ห้าม key ซ้ำ
- **Not a message:** ไม่ใช่ JSON / top-level ไม่ใช่ object = ปิด connection หลัง handshake
- **Malformed:** ไม่มี `type` string หรือ field นอกสเปก = ignore ทั้งข้อความ (ยกเว้น 3 กรณี: malformed หลัง handshake, `ack`, `focus` ที่ชี้ผิด — ปิด link)

## 3. Framing (TCP)

```
[4-byte length: big-endian uint32][UTF-8 JSON body]
```
- ต้อง `len` อยู่ในช่วง 1..131072 (128KB cap ทั้งสองฝั่ง — `RemoteNodeClient` + agent)
- หลัง TLS handshake เสร็จ ข้อความแรก**ต้องเป็น auth** (SV-L1: กัน order race)

## 4. Handshake & Pairing

```
node→host:  pair_request {pin, host_name}          (x3 fail = block 5 นาที)
host→node:  pair_accept {token[64hex], cert_fingerprint, uuid}
          | pair_reject
node→host:  auth {token}
host→node:  auth_ok {}
node→host:  hello {}   (แวบแรกเท่านั้น)
```
- จับคู่สำเร็จแล้ว **PIN หมุนใหม่ทันที** (พฤติกรรม by-design — กัน reuse)
- TLS: node เป็น server, cert self-signed per-machine (openssl หรือ PowerShell fallback), host pin SHA-256 fingerprint ต่อ node (TOFU — ไม่ตรง = ตัด)

## 5. TCP Messages (ครบชุดจากโค้ดทั้งสองฝั่ง)

| type | ทิศ | fields | หมายเหตุ |
|---|---|---|---|
| `auth` | node→host | token | ต้องเป็นข้อความแรกสุดหลัง TLS |
| `auth_ok` | host→node | — | หลังนี้ถือว่า authed |
| `hello` | host→node | — | |
| `ping` | host→node | ts | node ตอบ `pong` แนบ ram/focused/lang |
| `pong` | node→host | ts, ram_pct, focused, lang_group | host ใช้วัด latency |
| `enter_screen` | host→node | position, norm_x, norm_y, lang_group, policy, switch_delay_ms, double_tap_ms, corner_size, corners{tl,tr,bl,br}, locked | เริ่มควบคุม node |
| `leave_screen` | host→node | — | จบการควบคุม |
| `edge_return` | node→host | edge, norm_x, norm_y | node ดันกลับ host (signature ใหม่ normX,normY — 29 ต.ค. อนุมัติ) |
| `switch_request` | node→host | target, edge, norm_x, norm_y | chain topology: node ขอสลับต่อ |
| `lock` | host→node | on | ล็อก edge switching |
| `set_lang` | host→node | group | node ตอบ `lang_ack` |
| `lang_ack` | node→host | lang_group | |
| `toggle_lang` | host→node | — | |
| `set_layout` | host→node | layout | |
| `set_policy` | host→node | policy | full/clipboard/view — agent enforce ตัวเอง |
| `key_event` | host→node | keycode, down | TCP สำรอง (หลักคือ UDP) |
| `clipboard` | host→node | text **หรือ** {mime:"image/png", data:b64} | ≤128KB |
| `clip_meta` | host→node | mime, token, size | >128KB: node ขอต่อด้วย `clip_fetch` |
| `clip_fetch` | node→host | token | |
| `clip_data` | host→node | mime, data(b64) | |
| `file_offer` | host→node | manifest(json) | ตาราง File (§7) |
| `file_accept` | node→host | id | |
| `file_chunk` | host→node | id, idx, offset, data(b64 ≤43KB) | |
| `file_done` | host→node | id, idx | node ตอบ `file_result` |
| `file_result` | host→node | id, idx, status | |
| `file_complete` | host→node | id, success | จบ session |
| `audio_control` | host→node | payload | เสียง (gst ผ่าน stdin JSON) |
| `history_request` | node→host | — | **C2d (ใหม่)** |
| `history_list` | host→node | entries[{id, mime, preview, ts, source}] | metadata เท่านั้น |
| `history_get` | node→host | id | |
| `history_entry` | host→node | id, mime, data(b64) **หรือ** missing,reason | >100KB b64 = missing/too_big |

## 6. UDP Input Wire (v2 — port 24802)

```
[Payload][Seq: 8B big-endian UnixMs][HMAC-SHA256 trailer: 16B]
```
- Key = HKDF จาก `auth_token` (domain `omnilink-udp-v2|`)
- Payload ไบต์แรก = packet type:

| byte | packet | เนื้อหา |
|---|---|---|
| 0x01 | MouseMoveRel | dx,dy |
| 0x02 | MouseMoveAbs | x,y (บนจอ node) |
| 0x03 | MouseButton | button, down |
| 0x04 | MouseWheel | delta |
| 0x05 | KeyEvent | keycode, down |
| 0x06 | SetLang | group |
| 0x07 | ReleaseAll | — (safety override: ผ่านได้แม้ policy=view) |

- Agent ตรวจ: HMAC (fail-closed ถ้าไม่มี key) → replay (RFC 6479 sliding-window 64-bit bitmap — แทน freshness ±5s ตั้งแต่ REV4)
- Agent กรอง injected events (`LLMHF_INJECTED` เทียบเท่าฝั่ง Windows)

## 7. File Transfer (ผ่าน TCP เดิม)

```
host: file_offer {manifest{ id, effect(copy|cut), total_bytes, files[{rel_path, size, sha256, is_dir, mtime_utc}] }}
node: file_accept {id}
host: file_chunk {id, idx, offset, data(b64 32KB)} ...จนครบไฟล์
node: file_done {id, idx} → host: file_result {id, idx, status}
host: file_complete {id, success}   (node: ย้ายจาก staging .part → target + set_files)
```
- SHA-256 ตรวจที่ node ก่อนยอมรับ · staging `.part` ใน `~/.omnilink/staging` (F4: resume ค้างอยู่ในแผน)

## 8. C2d History (ใหม่ — ดู §5 แถว history)

- metadata อย่างเดียวใน `history_list` — เนื้อหาเต็มดึงรายชิ้นด้วย `history_get`
- ขนาด >100KB b64 = `history_entry {id, missing:true, reason:"too_big"}` (ACK-gate อยู่ในแผน)

## 9. Known Departures & Open Questions

- edge_return signature: **(normX, normY)** หลังปรับ 29 ต.ค. (อนุมัติ) — เอกสารเก่าบางฉบับเขียน (normY, normX)
- UDP replay: REV4 ใช้ RFC 6479 bitmap — เอกสารเก่าระบุ freshness ±5s (deprecated)
- `set_files`: ตั้ง **เฉพาะ gnome-copied-files** (uri-list ถูกตัด — xclip ตัวที่สองยึด selection ทับ)
- Open: Nautilus paste ไฟล์ยัง error "Operation not supported" (ดู BUGS-SEEN 4 พ.ย.) — ต้องเพิ่ม `application/x-gnome-copied-files` + ครบชุด targets ผ่าน GTK multi-target
- Open: resume .part / file_delete_remote (F4)
