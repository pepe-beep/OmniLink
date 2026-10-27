"""R&D integration test — ยิง event จริงผ่าน protocol ไปที่ agent บน ThinkPad
ทดสอบ: (1) แปะขอบเบาๆ ต้องไม่สลับ (2) ดันขอบเกิน threshold ต้องสลับ (3) set_lang verify ต้อง True
ก่อนรัน: agent ต้องออนไลน์ (host กำลังรันอยู่ก็ได้ — protocol เดียวกัน)"""
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


def pack_msg(o):
    raw = json.dumps(o).encode()
    return struct.pack("!I", len(raw)) + raw


def main():
    s = tls_wrap(socket.create_connection(AGENT, timeout=5))
    s.settimeout(6)
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    def send(o):
        s.sendall(pack_msg(o))

    def recv_until(want_type, timeout=6):
        buf = b""
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                chunk = s.recv(4096)
            except socket.timeout:
                break
            if not chunk:
                break
            buf += chunk
            while len(buf) >= 4:
                n = struct.unpack("!I", buf[:4])[0]
                if len(buf) < 4 + n:
                    break
                msg = json.loads(buf[4:4 + n])
                buf = buf[4 + n:]
                if msg.get("type") == want_type:
                    return msg
        return None

    def udp_rel(dx, dy):
        udp.sendto(udp_sign(struct.pack("!BhhB", 0x01, dx, dy, 0)), UDP)
        time.sleep(0.012)

    # ── auth ──
    send({"type": "auth", "token": TOKEN})
    r = recv_until("auth_ok")
    assert r, "auth ล้มเหลว"
    print("PASS auth_ok")
    recv_until("hello", timeout=3)

    # ── enter screen ──
    send({"type": "enter_screen", "position": "right", "norm_y": 0.5, "lang_group": 1, "switch_delay_ms": 0, "double_tap_ms": 0, "corner_size": 0})
    time.sleep(0.8)

    # ── test 1: แปะขอบเบาๆ (15px) ต้องไม่สลับ ──
    for _ in range(3):
        udp_rel(-5, 0)
    time.sleep(1.2)  # พ้น grace window ให้ push reset
    r = recv_until("edge_return", timeout=1)
    if r is None:
        print("PASS แปะขอบเบาๆ ไม่สลับ (ตาม design)")
    else:
        print("FAIL แปะขอบเบาๆ แต่สลับแล้ว!")
        return 1

    # ── test 2: ดันขอบหนักๆ ต้องสลับ ──
    for _ in range(4):
        udp_rel(-30, 0)  # รวม 120px > threshold 60px
    r = recv_until("edge_return", timeout=4)
    if r and r.get("edge") == "left":
        print(f"PASS ดันขอบสลับกลับ (edge={r['edge']}, norm_y={r.get('norm_y', 0):.2f})")
    else:
        print("FAIL ดันขอบหนักแล้วแต่ไม่สลับ!")
        return 1

    # ── test 3: set_lang EN → verify ต้อง True (บั๊ก gsettings-order เดิม) ──
    send({"type": "enter_screen", "position": "right", "norm_y": 0.5, "lang_group": 0, "switch_delay_ms": 0, "double_tap_ms": 0, "corner_size": 0})
    time.sleep(1.2)
    send({"type": "set_lang", "group": 0})
    r = recv_until("lang_ack", timeout=5)
    en_ok = r is not None and r.get("lang_group") == 0
    print(("PASS" if en_ok else "FAIL") + f" set_lang EN → ack={r and r.get('lang_group')}")
    send({"type": "set_lang", "group": 1})
    r = recv_until("lang_ack", timeout=5)
    th_ok = r is not None and r.get("lang_group") == 1
    print(("PASS" if th_ok else "FAIL") + f" set_lang TH → ack={r and r.get('lang_group')}")

    # คืนสถานะ: leave screen + TH
    send({"type": "leave_screen"})
    s.close()
    if en_ok and th_ok:
        print("ALL PASS")
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main())
