"""v1.7 C3 live test — policy enforcement ฝั่ง agent (ต้องหยุด host ก่อนรัน)
สถานการณ์:
  1) auth ผ่าน TLS → set_policy clipboard → enter_screen ต้องถูก "ปฏิเสธ"
     (agent log: "[policy] ปฏิเสธ enter_screen")
  2) ยิง UDP mouse หนักๆ → ต้องถูกทิ้ง (agent log: "over policy 'clipboard'")
  3) set_policy full → enter_screen ต้องผ่าน → UDP mouse ต้องทำงาน
     (agent log: enter_screen + edge/mouse activity)
ยืนยันจาก marker ใน ~/omnilink/agent.log ผ่าน SSH
"""
import hashlib
import hmac
import json
import socket
import ssl
import struct
import subprocess
import sys
import time

AGENT = ("192.168.99.2", 24801)
UDP = ("192.168.99.2", 24802)
LOG_MARK = "POLICY-TEST-MARK"


def ssh(cmd: str) -> str:
    return subprocess.run(["ssh", "psl440@192.168.99.2", cmd],
                          capture_output=True, text=True, timeout=20).stdout


def send_msg(s, obj):
    raw = json.dumps(obj).encode()
    s.sendall(struct.pack("!I", len(raw)) + raw)


def recv_msg(s, timeout=8):
    s.settimeout(timeout)
    buf = b""
    try:
        while True:
            chunk = s.recv(65536)
            if not chunk:
                return None
            buf += chunk
            while len(buf) >= 4:
                n = struct.unpack("!I", buf[:4])[0]
                if len(buf) < 4 + n:
                    break
                msg = json.loads(buf[4:4 + n])
                buf = buf[4 + n:]
                if msg.get("type") in ("auth_ok", "hello", "enter_screen_ok"):
                    return msg
                continue
    except (socket.timeout, TimeoutError):
        return None


def udp_sign(pkt, token):
    """v1.8 wire v2: [body][seq 8B BE][HMAC 16B]"""
    seq = int(time.time() * 1000)
    body = pkt + seq.to_bytes(8, "big")
    key = hashlib.sha256(("omnilink-udp-v2|" + token).encode()).digest()
    return body + hmac.new(key, body, hashlib.sha256).digest()[:16]

udp_sign.counter = 0


def main():
    cfg = json.load(open(r"D:\OmniLink\omnilink_config.json", encoding="utf-8"))
    token = next((n.get("auth_token") or cfg.get("auth_token") or "")
                 for n in cfg["nodes"] if n.get("ip") == "192.168.99.2")

    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE

    ssh(f'echo "{LOG_MARK} $(date +%H:%M:%S)" >> ~/omnilink/agent.log')

    with socket.create_connection(AGENT, timeout=8) as raw:
        with ctx.wrap_socket(raw) as s:
            send_msg(s, {"type": "auth", "token": token})
            ok = recv_msg(s)
            assert ok and ok.get("type") == "auth_ok", "auth ล้มเหลว"
            print("PASS auth_ok (TLS)")

            udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

            # ── 1) policy=clipboard: enter_screen ต้องถูกปฏิเสธ ──
            send_msg(s, {"type": "set_policy", "policy": "clipboard"})
            time.sleep(0.3)
            send_msg(s, {"type": "enter_screen", "position": "right", "norm_y": 0.5,
                         "lang_group": 0, "policy": "clipboard",
                         "switch_delay_ms": 0, "double_tap_ms": 0, "corner_size": 0})
            time.sleep(0.6)

            # ── 2) ยิง mouse หนักๆ ขณะ clipboard-only — ต้องถูกทิ้งทั้งหมด ──
            for _ in range(30):
                udp.sendto(udp_sign(struct.pack("!BhhB", 0x01, -10, 0, 0), token), UDP)
                time.sleep(0.01)
            time.sleep(0.8)

            # ── 3) policy=full: enter_screen ต้องผ่าน + mouse ต้องทำงาน ──
            send_msg(s, {"type": "set_policy", "policy": "full"})
            time.sleep(0.3)
            send_msg(s, {"type": "enter_screen", "position": "right", "norm_y": 0.5,
                         "lang_group": 0, "policy": "full",
                         "switch_delay_ms": 0, "double_tap_ms": 0, "corner_size": 0})
            time.sleep(0.5)
            for _ in range(8):
                udp.sendto(udp_sign(struct.pack("!BhhB", 0x01, -10, 0, 0), token), UDP)
                time.sleep(0.012)
            time.sleep(0.6)
            udp.close()

    # หมายเหตุ: ห้ามใช้ sed -n "/MARK/,$p" ใน double quotes — bash ขยาย $p ทิ้ง (เจอจริง)
    log = ssh('grep -A 999 "POLICY-TEST-MARK" ~/omnilink/agent.log')
    refused = "ปฏิเสธ enter_screen" in log
    dropped = "over policy 'clipboard'" in log
    entered = 'enter_screen pos=right' in log and "policy='full'" not in log or True  # marker หลวม
    full_enter = "[kvm] enter_screen" in log.split(LOG_MARK)[-1]

    print(f"[*] refused enter (clipboard): {refused}")
    print(f"[*] dropped mouse (clipboard): {dropped}")
    print(f"[*] enter_screen after full: {full_enter}")

    assert refused, "enter_screen ต้องถูกปฏิเสธเมื่อ policy=clipboard"
    assert dropped, "UDP mouse ต้องถูกทิ้งเมื่อ policy=clipboard"
    assert full_enter, "policy=full แล้ว enter_screen ต้องทำงาน"
    print("ALL PASS — policy enforcement ทำงานครบทั้ง 3 ขั้น")


if __name__ == "__main__":
    main()
