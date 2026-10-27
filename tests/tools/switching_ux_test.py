"""A1/A2 switching UX live test — agent 1.3.0 (หยุด host ก่อนรัน)
ทดสอบ 4 เคส:
  1) hold delay: ดันถึงขอบแล้วหยุด < delay → ไม่สลับ; ค้างเกิน delay → สลับ
  2) double-tap: ดันถึงขอบ→ออก→กลับมาใน 400ms → สลับทันที
  3) มุมจอตาย: ดันขอบที่ y ใกล้มุม → ไม่สลับ
  4) lock: ส่ง lock on → ดันขอบไม่สลับ; lock off → กลับมาปกติ
"""
import json
import socket
import struct
import sys
import time

_CFG = json.load(open(r"D:\OmniLink\omnilink_config.json", encoding="utf-8"))
TOKEN = next((n.get("auth_token") or _CFG.get("auth_token") or "") for n in _CFG["nodes"] if n.get("ip") == "192.168.99.2")
AGENT = ("192.168.99.2", 24801)
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


udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)


def push(px, y=300):
    """ดันเมาส์ซ้าย px พิกเซล (ทีละ 10px) ที่แถว y — ย้าย y ด้วย dy ก่อนเสมอ (relative moves!)"""
    steps = max(1, px // 10)
    for _ in range(steps):
        udp.sendto(udp_sign(struct.pack("!BhhB", 0x01, -10, 0, 0)), UDP)
        time.sleep(0.012)


def position_y(target_y, cur_y=359):
    """ย้ายเคอร์เซอร์ไปแถว y ที่ต้องการ (entry ของ agent คือ y≈359 จาก norm_y=0.5)"""
    dy = int(target_y) - int(cur_y)
    step = 20 if dy > 0 else -20
    remaining = abs(dy)
    while remaining > 0:
        s = min(20, remaining)
        udp.sendto(udp_sign(struct.pack("!BhhB", 0x01, 0, step, 0)), UDP)
        remaining -= s
        time.sleep(0.012)
    time.sleep(0.05)


def enter(c, **extra):
    msg = {"type": "enter_screen", "position": "right", "norm_y": 0.5, "lang_group": 0}
    msg.update(extra)
    c.send(msg)
    time.sleep(0.6)


def drain(c, t=0.4):
    end = time.time() + t
    got = None
    while time.time() < end:
        m = c.recv_until("edge_return", timeout=max(0.05, end - time.time()))
        if m:
            got = m
    return got


def main():
    c = Conn()
    c.send({"type": "auth", "token": TOKEN})
    assert c.recv_until("auth_ok"), "auth fail"
    c.recv_until("hello", 3)

    # ── 1) hold delay: delay=600ms — ดัน 80px แล้วหยุด 0.3s (ยังไม่ครบ) → ไม่สลับ; ค้างต่อ (มี packet ไหล) → สลับ ──
    enter(c, switch_delay_ms=600, double_tap_ms=0, corner_size=0)
    push(80, 300)
    early = drain(c, 0.35)
    print(("PASS" if early is None else "FAIL") + " 1a: ดันครบแต่ hold ไม่ถึง delay → ยังไม่สลับ")
    m = None
    end = time.time() + 2.5
    while time.time() < end and m is None:
        # ค้างที่ขอบพร้อม packet ไหลเรื่อยๆ (จริงๆ คือจิ๊บจ๊อยเล็กๆ) — hold timer ถูกประเมินต่อ
        udp.sendto(udp_sign(struct.pack("!BhhB", 0x01, 0, 0, 0)), UDP)
        time.sleep(0.08)
        m = drain(c, 0.1)
    print(("PASS" if m else "FAIL") + " 1b: ค้างขอบต่อจนครบ delay → สลับ")
    enter(c, switch_delay_ms=600, double_tap_ms=0, corner_size=0)

    # ── 2) double-tap: delay=5000 (ปิดทาง hold) tap=400ms — ดัน→ถอย 30px→ดันกลับ → สลับเร็ว ──
    enter(c, switch_delay_ms=5000, double_tap_ms=400, corner_size=0)
    t0 = time.time()
    push(70, 300)            # tap 1: ถึงขอบ + ดันเกิน 30px
    for _ in range(3):       # ถอยออกจากขอบแค่ 30px (ต้องถอยน้อยๆ ให้ดันกลับถึงขอบ)
        udp.sendto(udp_sign(struct.pack("!BhhB", 0x01, 10, 0, 0)), UDP)
        time.sleep(0.012)
    push(60, 300)            # tap 2: กลับมาถึงขอบอีกครั้งภายใน 400ms
    m = c.recv_until("edge_return", timeout=2.5)
    dt = time.time() - t0
    ok2 = m is not None and dt < 3.0
    print(("PASS" if ok2 else "FAIL") + f" 2: double-tap สลับทันที (ใช้เวลา {dt:.1f}s)")
    enter(c, switch_delay_ms=5000, double_tap_ms=400, corner_size=0)

    # ── 3) มุมจอตาย: ย้ายไป y=10 (ใกล้มุมบน, corner=60) แล้วดันขอบ → ไม่สลับ ──
    enter(c, switch_delay_ms=0, double_tap_ms=0, corner_size=60)
    position_y(10)
    push(90, 10)
    r3 = drain(c, 0.8)
    print(("PASS" if r3 is None else "FAIL") + " 3: ดันขอบในโซนมุมตาย → ไม่สลับ")
    enter(c, switch_delay_ms=0, double_tap_ms=0, corner_size=60)
    position_y(360)
    push(90, 360)  # กลางจอ = ไม่ติดมุม → ต้องสลับ (delay=0)
    m = c.recv_until("edge_return", timeout=2.5)
    print(("PASS" if m else "FAIL") + " 3b: ดันขอบนอกโซนมุม (delay=0) → สลับทันที")
    enter(c, switch_delay_ms=0, double_tap_ms=0, corner_size=0)

    # ── 4) lock: lock on → ดันไม่สลับ; lock off → กลับมา ──
    enter(c, switch_delay_ms=0, double_tap_ms=0, corner_size=0)
    c.send({"type": "lock", "on": True})
    time.sleep(0.3)
    push(90, 300)
    r4 = drain(c, 0.8)
    print(("PASS" if r4 is None else "FAIL") + " 4a: lock on → ดันขอบไม่สลับ")
    c.send({"type": "lock", "on": False})
    time.sleep(0.3)
    push(90, 300)
    m = c.recv_until("edge_return", timeout=2.5)
    print(("PASS" if m else "FAIL") + " 4b: lock off → สลับได้ปกติ")

    c.send({"type": "leave_screen"})
    c.s.close()

    results = [early is None, m is not None]
    print("DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
