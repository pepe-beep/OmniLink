"""รอ edge_return จาก hotkey ฉุกเฉิน (ใช้คู่กับ XTEST ฝั่ง ThinkPad)"""
import json
import socket
import struct
import sys
import time

TOKEN = open(r"D:\OmniLink\omnilink_config.json", encoding="utf-8").read().split('"auth_token": "')[1].split('"')[0]

s = socket.create_connection(("192.168.99.2", 24801), timeout=5)
s.settimeout(10)


def send(o):
    raw = json.dumps(o).encode()
    s.sendall(struct.pack("!I", len(raw)) + raw)


def recv_until(want, timeout):
    buf = b""
    end = time.time() + timeout
    while time.time() < end:
        try:
            c = s.recv(4096)
        except socket.timeout:
            return None
        if not c:
            return None
        buf += c
        while len(buf) >= 4:
            n = struct.unpack("!I", buf[:4])[0]
            if len(buf) < 4 + n:
                break
            m = json.loads(buf[4:4 + n])
            buf = buf[4 + n:]
            if m.get("type") == want:
                return m
    return None


send({"type": "auth", "token": TOKEN})
assert recv_until("auth_ok", 6), "auth fail"
recv_until("hello", 3)
send({"type": "enter_screen", "position": "right", "norm_y": 0.5, "lang_group": 1})
print("entered screen — กำลังรอ hotkey (มีเวลา 8 วิ: ยิง XTEST เลย)", flush=True)
r = recv_until("edge_return", 8)
if r:
    print(f"HOTKEY PASS — edge_return edge={r.get('edge')}")
    sys.exit(0)
print("HOTKEY FAIL — ไม่ได้รับ edge_return")
sys.exit(1)
