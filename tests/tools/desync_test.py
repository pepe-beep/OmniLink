"""INCIDENT#2 desync test — จำลองเหตุการณ์ "conn ถูกแทนแล้วหล่นหาย" ที่ทำให้เมาส์ติดครั้งที่สอง

สถานการณ์จำลอง (ตรงกับที่เกิดจริง 04:49):
  conn1 = host-like connection (enter_screen แล้ว)
  conn2 = connection แปลกปลอมเข้ามาแทนที่ (เช่น client ทดสอบ/อินสแตนซ์ซ้ำ)
  conn2 ปิด → agent ไม่มี active conn
  conn3 = host reconnect → enter_screen ใหม่ → ดันขอบ
  PASS ต้องได้ edge_return กลับมาที่ conn3 (ต้องรอดจากการถูก kick ของ agent ด้วย)

รันตอน Host ปิดอยู่ (กัน kick-war กับ host จริง)
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
    def __init__(self, name):
        self.name = name
        self.s = tls_wrap(socket.create_connection(AGENT, timeout=5))
        self.s.settimeout(5)
        self.buf = b""

    def send(self, o):
        raw = json.dumps(o).encode()
        self.s.sendall(struct.pack("!I", len(raw)) + raw)

    def recv_until(self, want, timeout=6):
        end = time.time() + timeout
        seen = []
        while time.time() < end:
            self.s.settimeout(max(0.2, end - time.time()))
            try:
                c = self.s.recv(4096)
            except socket.timeout:
                continue  # ยังไม่หมดเวลารวม — รอต่อ
            except (ConnectionResetError, OSError) as ex:
                print(f"    [{self.name}] recv error: {type(ex).__name__}")
                return None
            if not c:
                print(f"    [{self.name}] EOF")
                return None
            self.buf += c
            while len(self.buf) >= 4:
                n = struct.unpack("!I", self.buf[:4])[0]
                if len(self.buf) < 4 + n:
                    break
                m = json.loads(self.buf[4:4 + n])
                self.buf = self.buf[4 + n:]
                seen.append((round(time.time() % 100, 2), m.get("type")))
                if m.get("type") == want:
                    if seen[:-1]:
                        print(f"    [{self.name}] ข้ามระหว่างทาง:", seen[:-1])
                    return m
        print(f"    [{self.name}] timeout {want} — เห็นมา: {seen}")
        return None

    def auth(self):
        self.send({"type": "auth", "token": TOKEN})
        r = self.recv_until("auth_ok")
        if r is None:
            # วินิจฉัย: ดูว่า socket รับอะไรมาบ้าง (รวมของค้างใน buffer)
            print(f"    [{self.name}] auth_ok ไม่มา — socket_error_check:", end=" ")
            try:
                self.s.settimeout(1)
                extra = self.s.recv(4096)
                print("recv เพิ่มเติมได้", len(extra), "bytes:", extra[:80])
            except Exception as ex:
                print("recv ไม่ได้เลย:", type(ex).__name__)
        return r is not None

    def close(self):
        try:
            self.s.close()
        except Exception:
            pass


def main():
    # ── ขั้น 1: conn1 = host จำลอง ──
    c1 = Conn("conn1")
    assert c1.auth(), "conn1 auth fail"
    c1.recv_until("hello", 3)
    c1.send({"type": "enter_screen", "position": "right", "norm_y": 0.5, "lang_group": 1})
    time.sleep(0.5)
    print("STEP1 conn1 auth+enter_screen OK")

    # ── ขั้น 2: conn2 แทนที่ → agent ต้อง KICK conn1 ──
    c2 = Conn("conn2")
    assert c2.auth(), "conn2 auth fail"
    r = c1.recv_until("auth_ok", 3)  # รออะไรก็ได้จาก conn1 — ถ้าถูก kick จะ error/EOF
    kicked = r is None
    print(("PASS" if kicked else "WARN") + " conn1 ถูก kick โดย agent (kick-on-new-connection) r=" + str(r))

    # ── ขั้น 3: conn2 ปิด (เหตุการณ์ที่ทำให้ active=None ตอนแรก) ──
    c2.close()
    time.sleep(0.5)

    # ── ขั้น 4: conn3 = host reconnect ──
    c3 = None
    for _ in range(6):
        attempt = Conn("conn3")
        try:
            if attempt.auth():
                c3 = attempt
                break
        except Exception:
            pass
        attempt.close()  # ห้าม leak socket ค้างใน agent
        time.sleep(0.5)
    assert c3 is not None, "conn3 reconnect fail"
    hello = c3.recv_until("hello", 6)
    assert hello, "conn3 ไม่ได้รับ hello — connection thread ค้างหลัง auth!"
    print("STEP4 conn3 reconnect+auth+hello OK (v" + str(hello.get("version")) + ")")

    # ── ขั้น 5: enter_screen + ดันขอบผ่าน UDP ──
    c3.send({"type": "enter_screen", "position": "right", "norm_y": 0.5, "lang_group": 1})
    time.sleep(0.8)
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    for _ in range(4):
        udp.sendto(udp_sign(struct.pack("!BhhB", 0x01, -30, 0, 0)), UDP)
        time.sleep(0.015)

    r = c3.recv_until("edge_return", timeout=5)
    if r and r.get("edge") == "left":
        print("PASS edge_return มาถึง conn3 หลังผ่านสงคราม connection — ไม่มี orphan!")
        c3.send({"type": "leave_screen"})
        c3.close()
        return 0
    print("FAIL edge_return หล่นหาย (orphan ยังอยู่?)")
    c3.close()
    return 1


if __name__ == "__main__":
    sys.exit(main())
