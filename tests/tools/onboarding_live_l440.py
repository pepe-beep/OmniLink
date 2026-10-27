"""Gate live onboarding — wizard Linux path กับ L440 จริงทั้ง flow:
1) boot แอป (config 0 node, firstrun เด้ง)
2) ปิด firstrun → wizard ขั้น 1 โผล่ (hero ก็ได้แต่ firstrun เร็วกว่า)
3) เลือก Linux → กรอก 192.168.99.2 + psl440 → กดติดตั้ง
4) deploy สำเร็จจริง (ผ่าน SSH ของจริง) → wizard ไปขั้นสแกนเอง
5) สแกนเจอ L440 จริง → ใส่ PIN จริงจากเครื่อง L440 (อ่านผ่าน SSH = ผู้ใช้อ่านบนจอ)
6) ยืนยัน fingerprint (กด Yes ให้อัตโนมัติ) → จับคู่สำเร็จ → wizard ปิดเอง
7) node L440 โผล่บนแผนที่/Devices จริง (UIA + status.json)
ผ่าน = ONBOARDING-LIVE-L440-PASS
"""
import json
import re
import subprocess
import sys
import time
import ctypes

from pywinauto import Desktop

user32 = ctypes.windll.user32
fails = []

def check(name, ok, detail=""):
    print(("[PASS] " if ok else "[FAIL] ") + name + (f" — {detail}" if detail else ""))
    if not ok:
        fails.append(name)

def ssh(cmd, timeout=15):
    r = subprocess.run(["ssh", "-o", "BatchMode=yes", "psl440@192.168.99.2", cmd],
                       capture_output=True, text=True, timeout=timeout)
    return r.stdout.strip()

def wizard_hwnd():
    WM = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    hits = []
    def cb(hwnd, _):
        buf = ctypes.create_unicode_buffer(128)
        user32.GetWindowTextW(hwnd, buf, 128)
        if buf.value == "OmniLink Wizard" and user32.IsWindowVisible(hwnd):
            hits.append(hwnd)
        return True
    user32.EnumWindows(WM(cb), 0)
    return hits[0] if hits else None

def wiz():
    hw = wizard_hwnd()
    if not hw:
        return None
    try:
        w = Desktop(backend="uia").window(handle=hw)
        w.wait("exists ready", timeout=5)
        return w
    except Exception:
        return None

def wiz_buttons():
    w = wiz()
    if w is None:
        return {}
    out = {}
    try:
        for b in w.descendants(control_type="Button"):
            try:
                t = b.window_text() or ""
            except Exception:
                t = ""
            if t and t not in out:
                out[t] = b
    except Exception:
        pass
    return out

def main_btns():
    wins = [x for x in Desktop(backend="uia").windows() if "OmniLink — Mesh" in (x.window_text() or "")]
    if not wins:
        return {}
    out = {}
    for b in wins[0].descendants(control_type="Button"):
        try:
            t = b.window_text() or ""
        except Exception:
            t = ""
        if t and t not in out:
            out[t] = b
    return out

def main_texts():
    wins = [x for x in Desktop(backend="uia").windows() if "OmniLink — Mesh" in (x.window_text() or "")]
    if not wins:
        return []
    out = []
    for t in wins[0].descendants(control_type="Text"):
        try:
            tt = t.window_text() or ""
        except Exception:
            tt = ""
        if tt:
            out.append(tt)
    return out

# ── 1) firstrun → wizard ──
b = main_btns()
addnow = b.get("Add a computer now")
if addnow:
    addnow.set_focus(); time.sleep(0.3); addnow.invoke(); time.sleep(2.5)
deadline = time.time() + 8
while time.time() < deadline and wizard_hwnd() is None:
    time.sleep(0.4)
check("wizard เปิดจาก firstrun", wizard_hwnd() is not None)

# ── 2) เลือก Linux ──
wb = wiz_buttons()
check("ขั้น 1 มีการ์ด Linux", "Choose Linux SSH" in wb)
wb["Choose Linux SSH"].invoke()
time.sleep(1.2)

w = wiz()
ips = w.descendants(control_type="Edit")
check("ขั้น 2 Linux มีช่อง IP+user", len(ips) >= 2, f"edits={len(ips)}")
ips[0].set_focus()
# พิมพ์ผ่าน send_keys
from pywinauto.keyboard import send_keys
ips[0].set_edit_text("192.168.99.2")
ips[1].set_edit_text("psl440")

wb = wiz_buttons()
check("มีปุ่ม Install it for me", "Install it for me" in wb, ",".join(list(wb)[:5]))
wb["Install it for me"].invoke()

# ── 3) รอ deploy จริง (SSH ของจริง ~5-15 วิ) ──
deploy_ok = False
deadline = time.time() + 40
while time.time() < deadline:
    w = wiz()
    if w is None:
        break  # wizard ปิดไปแล้ว? ผิดปกติ
    try:
        texts = [t.window_text() or "" for t in w.descendants(control_type="Text")]
    except Exception:
        texts = []
    joined = " ".join(texts)
    if "✅" in joined:
        deploy_ok = True
        break
    if "❌" in joined:
        check("deploy ผ่าน SSH จริง", False, joined[:120])
        sys.exit(1)
    time.sleep(1)
check("deploy ผ่าน SSH จริง (✅ จาก DeployRemoteAgent)", deploy_ok)

# รอ wizard เด้งไปขั้นสแกนเอง (Task.Delay 900ms → GoScanNow)
time.sleep(3)
wb = wiz_buttons()
check("ไปขั้น 3 อัตโนมัติหลัง deploy", "Rescan" in wb, ",".join(list(wb)[:5]))

# ── 4) รอสแกนเจอ L440 จริง ──
found = False
deadline = time.time() + 12
while time.time() < deadline:
    w = wiz()
    if w is None:
        break
    try:
        items = [t.window_text() or "" for t in w.descendants(control_type="ListItem")]
    except Exception:
        items = []
    if any("192.168.99.2" in i or "L440" in i for i in items):
        found = True
        break
    time.sleep(1)
check("สแกนเจอ L440 จริง", found)

# เลือก item L440
w = wiz()
for li in w.descendants(control_type="ListItem"):
    if "192.168.99.2" in (li.window_text() or "") or "L440" in (li.window_text() or ""):
        li.select()
        break

# ── 5) PIN จริงจาก L440 ──
pin_raw = ssh("cat /home/psl440/omnilink/pairing_pin")
pin = pin_raw.strip().replace("-", "").replace(" ", "")
check("อ่าน PIN จริงจาก L440", len(pin) == 6, pin_raw)

# ช่อง PIN ของ wizard = edit ที่ยังว่างอยู่
w = wiz()
edits = w.descendants(control_type="Edit")
pinbox = edits[0] if edits else None
for e in edits:
    try:
        if not (e.window_text() or "").strip():
            pinbox = e
            break
    except Exception:
        pass
pinbox.set_edit_text(pin)

# ── 6) กดจับคู่ + ยืนยัน fingerprint อัตโนมัติ ──
# fingerprint MessageBox = Yes/No — spy เป็น background: กด Yes เมื่อเจอ dialog ยืนยัน
import threading

def auto_yes():
    """ยืนยัน fingerprint: กด Yes ใน MessageBox ที่ผูกกับ process OmniLink (title ไทย/อังกฤษก็ได้)
    เผื่อ UIA window ของ MessageBox ไม่ถูก enumerate ตามปกติ — ไล่ทุก dialog ที่มีปุ่ม Yes"""
    import subprocess as sp
    deadline = time.time() + 30
    pressed = False
    while time.time() < deadline and not pressed:
        time.sleep(0.4)
        try:
            pid_line = sp.run(["powershell", "-NoProfile", "-Command",
                               "(Get-Process OmniLink -ErrorAction SilentlyContinue).Id"],
                              capture_output=True, text=True, timeout=10).stdout.strip()
        except Exception:
            pid_line = ""
        for x in Desktop(backend="uia").windows():
            try:
                t = x.window_text() or ""
            except Exception:
                continue
            if "Fingerprint" not in t:
                continue
            for b in x.descendants(control_type="Button"):
                bt = ""
                try:
                    bt = b.window_text() or ""
                except Exception:
                    pass
                if bt in ("Yes", "ใช่", "ตกลง", "OK"):
                    b.invoke()
                    pressed = True
                    return

t = threading.Thread(target=auto_yes, daemon=True)
t.start()

wb = wiz_buttons()
wb["Pair & Connect Device"].invoke()

# ── 7) wizard ปิดเองเมื่อจับคู่สำเร็จ ──
closed = False
deadline = time.time() + 25
while time.time() < deadline:
    if wizard_hwnd() is None:
        closed = True
        break
    time.sleep(0.5)
check("wizard ปิดเองหลังจับคู่สำเร็จ", closed)

# ── 8) node โผล่จริงบนแอป ──
time.sleep(2.5)
node_visible = any("L440" in t or "192.168.99.2" in t for t in main_texts())
check("node L440 โผล่บนหน้าหลักจริง", node_visible)

status = json.load(open(r"D:\OmniLink\logs\status.json", encoding="utf-8"))
l440 = [n for n in status.get("nodes", []) if "99.2" in n.get("ip", "")]
check("status.json เห็น L440 connected", l440 and l440[0].get("connected") is True,
      str(l440[:1]))

print()
if fails:
    print(f"ONBOARDING-LIVE-L440-FAIL ({len(fails)}): {fails}")
    sys.exit(1)
print("ONBOARDING-LIVE-L440-PASS")
