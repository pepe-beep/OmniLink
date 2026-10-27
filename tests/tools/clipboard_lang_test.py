"""Clipboard + IBus language live test — agent 1.2.6 (ต้องหยุด host ก่อนรัน)
ทดสอบ:
  1) host→linux: ส่ง clipboard → xclip -o บน ThinkPad ต้องได้ข้อความเดิม
  2) linux→host: ใส่ clipboard บน ThinkPad → ดันขอบ → ต้องได้ {"type":"clipboard"} กลับมาที่ client
  3) IBus: set_lang แล้ว agent log ต้องรายงาน ibus=xkb:th::tha / xkb:us::eng (ไม่ใช่ ?)
"""
import json
import socket
import struct
import subprocess
import sys
import time

_CFG = json.load(open(r"D:\OmniLink\omnilink_config.json", encoding="utf-8"))
TOKEN = next((n.get("auth_token") or _CFG.get("auth_token") or "") for n in _CFG["nodes"] if n.get("ip") == "192.168.99.2")
AGENT = ("192.168.99.2", 24801)
UDP = ("192.168.99.2", 24802)
SSH = ["ssh", "-o", "BatchMode=yes", "psl440@192.168.99.2"]
UDP = ("192.168.99.2", 24802)


# ── v1.6 hardening live-test shim: TCP=TLS, UDP=HMAC trailer (ต้องตรงกับ agent) ──
import hashlib as _hashlib, hmac as _hmac, ssl as _ssl
_TLS_CTX = _ssl.create_default_context()
_TLS_CTX.check_hostname = False
_TLS_CTX.verify_mode = _ssl.CERT_NONE

def tls_wrap(sock):
    return _TLS_CTX.wrap_socket(sock)


def udp_sign(pkt):
    """v1.8 wire v2: [body][seq 8B BE][HMAC 16B] — seq = UnixMs*4096 + counter (โมโนโทนิก)"""
    seq = int(time.time() * 1000)
    body = pkt + seq.to_bytes(8, "big")
    key = _hashlib.sha256(("omnilink-udp-v2|" + TOKEN).encode("utf-8")).digest()
    return body + _hmac.new(key, body, _hashlib.sha256).digest()[:16]


def ssh(cmd):
    return subprocess.run(SSH + [cmd], capture_output=True, text=True, timeout=10).stdout.strip()


class Conn:
    def __init__(self):
        self.s = tls_wrap(socket.create_connection(AGENT, timeout=5))
        self.s.settimeout(6)
        self.buf = b""

    def send(self, o):
        raw = json.dumps(o).encode()
        self.s.sendall(struct.pack("!I", len(raw)) + raw)

    def recv_until(self, want, timeout=6):
        end = time.time() + timeout
        while time.time() < end:
            self.s.settimeout(max(0.2, end - time.time()))
            try:
                c = self.s.recv(65536)
            except socket.timeout:
                continue
            except OSError:
                return None
            if not c:
                return None
            self.buf += c
            while len(self.buf) >= 4:
                n = struct.unpack("!I", self.buf[:4])[0]
                if len(self.buf) < 4 + n:
                    break
                m = json.loads(self.buf[4:4 + n])
                self.buf = self.buf[4 + n:]
                if m.get("type") == want:
                    return m
        return None


def main():
    c = Conn()
    c.send({"type": "auth", "token": TOKEN})
    assert c.recv_until("auth_ok"), "auth fail"
    c.recv_until("hello", 3)
    c.send({"type": "enter_screen", "position": "right", "norm_y": 0.5, "lang_group": 1, "switch_delay_ms": 0, "double_tap_ms": 0, "corner_size": 0})
    time.sleep(0.5)

    # 1) host→linux clipboard
    marker = "OMNILINK_HOST_TO_LINUX_ญ720"
    c.send({"type": "clipboard", "text": marker})
    time.sleep(1.2)
    got = ssh("DISPLAY=:0 xclip -o -selection clipboard")
    r1 = got == marker
    print(("PASS" if r1 else "FAIL") + f" host→linux clipboard (got={got[:50]!r})")

    # 2) linux→host clipboard (push on leave)
    marker2 = "OMNILINK_LINUX_TO_HOST_12345"
    # xclip จะ fork daemon ถือ selection ค้าง → ต้อง setsid + ปิด stdio ทั้งคู่ ssh ถึงจะปิดได้
    ssh(f"DISPLAY=:0 setsid bash -c 'printf %s \"{marker2}\" | xclip -selection clipboard -i' >/dev/null 2>&1 </dev/null")
    time.sleep(0.4)
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    for _ in range(4):
        udp.sendto(udp_sign(struct.pack("!BhhB", 0x01, -30, 0, 0)), UDP)
        time.sleep(0.015)
    clip_msg = None
    edge_msg = None
    end = time.time() + 6
    while time.time() < end and (clip_msg is None or edge_msg is None):
        m = c.recv_until("clipboard", timeout=max(0.2, end - time.time()))
        if m:
            clip_msg = m
        # edge_return อาจมาก่อน/หลัง — ดึงแบบ drain
        m2 = c.recv_until("edge_return", timeout=0.3)
        if m2:
            edge_msg = m2
    r2 = clip_msg is not None and clip_msg.get("text") == marker2
    print(("PASS" if r2 else "FAIL") + f" linux→host clipboard (msg={clip_msg and clip_msg.get('text', '')[:40]!r}) edge={'yes' if edge_msg else 'no'}")

    # 3) IBus engine จริง (ตรวจสถานะ ibus ตรงๆ — รองรับทั้ง native-super-space และ direct path)
    c.send({"type": "set_lang", "group": 1})
    c.recv_until("lang_ack", 5)
    time.sleep(0.5)
    eng3 = ssh("DISPLAY=:0 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u)/bus ibus engine")
    r3 = "th::tha" in eng3
    print(("PASS" if r3 else "FAIL") + f" IBus TH engine (actual={eng3!r})")
    c.send({"type": "set_lang", "group": 0})
    c.recv_until("lang_ack", 5)
    time.sleep(0.5)
    eng = ssh("DISPLAY=:0 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u)/bus ibus engine")
    r4 = "us::eng" in eng
    print(("PASS" if r4 else "FAIL") + f" IBus EN engine (actual={eng!r})")

    c.send({"type": "leave_screen"})
    c.s.close()
    ok = r1 and r2 and r3 and r4
    print("ALL PASS" if ok else "SOME FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
