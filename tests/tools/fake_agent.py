"""Fake OmniLink agent (load-test tool) — จำลองฝั่งรับให้ host เชื่อมต่อได้หลาย node
จากเครื่องเดียว (127.0.0.2-4 = loopback /8 ของ Windows) เพื่อวัดต้นทุนต่อ node ของ host
ใช้เฉพาะ loopback + policy=view ฝั่ง host เสมอ (ไม่มีทางรบกวนการใช้งานจริง)
ไม่ต้องมี HMAC/pairing จริง — รับ auth แล้วยอดทันที (loopback only)"""
import argparse
import json
import socket
import ssl
import struct
import threading
import time


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ip", default="127.0.0.2")
    ap.add_argument("--tcp", type=int, default=24801)
    ap.add_argument("--udp", type=int, default=24802)
    ap.add_argument("--cert", required=True)
    ap.add_argument("--key", required=True)
    ap.add_argument("--name", default="fake-node")
    args = ap.parse_args()

    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(args.cert, args.key)

    # UDP sink: รับทิ้ง (host จะยิง input/HMAC มาเมื่อ active — ไม่มีผล)
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        udp.bind((args.ip, args.udp))
        threading.Thread(target=lambda: [udp.recvfrom(2048) for _ in iter(int, 1)], daemon=True).start()
    except OSError:
        pass

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((args.ip, args.tcp))
    srv.listen(5)
    print(f"[fake] {args.name} on {args.ip}:{args.tcp}", flush=True)

    while True:
        try:
            conn, _ = srv.accept()
        except OSError:
            return
        try:
            tls = ctx.wrap_socket(conn, server_side=True)
        except (ssl.SSLError, OSError):
            try:
                conn.close()
            except Exception:
                pass
            continue
        threading.Thread(target=handle, args=(tls,), daemon=True).start()


def handle(s):
    buf = b""
    authed = False
    try:
        s.settimeout(5)
        while not authed:
            chunk = s.recv(4096)
            if not chunk:
                return
            buf += chunk
            while len(buf) >= 4 and not authed:
                n = struct.unpack("!I", buf[:4])[0]
                if len(buf) < 4 + n:
                    break
                msg = json.loads(buf[4:4 + n])
                buf = buf[4 + n:]
                if msg.get("type") == "auth":
                    authed = True
                    send(s, {"type": "auth_ok"})
                    send(s, hello())
                elif msg.get("type") == "pair_request":
                    import secrets
                    send(s, {"type": "pair_accept", "token": secrets.token_hex(32),
                             "uuid": "fake", "name": "fake", "width": 1920, "height": 1080,
                             "cert_fingerprint": ""})
                    return
        s.settimeout(None)
        while True:
            chunk = s.recv(4096)
            if not chunk:
                return
            buf += chunk
            while len(buf) >= 4:
                n = struct.unpack("!I", buf[:4])[0]
                if len(buf) < 4 + n:
                    break
                msg = json.loads(buf[4:4 + n])
                buf = buf[4 + n:]
                if msg.get("type") == "ping":
                    send(s, pong(msg.get("ts", 0)))
                elif msg.get("type") == "set_policy":
                    pass
    except Exception:
        pass
    finally:
        try:
            s.close()
        except Exception:
            pass


def hello():
    return {"type": "hello", "auth": True, "version": "1.7.0", "hostname": "fake-node",
            "width": 1920, "height": 1080, "lang_group": 0,
            "playback_running": False, "playback_active": False, "mic_running": False,
            "ram_pct": 40.0, "ram_used_gb": 0.8, "ram_total_gb": 2.0}


def pong(ts):
    return {"type": "pong", "ts": ts, "width": 1920, "height": 1080, "lang_group": 0,
            "ram_pct": 40.0, "ram_used_gb": 0.8, "ram_total_gb": 2.0,
            "playback_running": False, "playback_active": False, "mic_running": False,
            "focused": False}


def send(s, obj):
    raw = json.dumps(obj).encode()
    s.sendall(struct.pack("!I", len(raw)) + raw)


if __name__ == "__main__":
    main()
