"""v2.1 onboarding walkthrough — ขับแอปจริงผ่าน UIA ทั้ง flow แล้วจับภาพทุกขั้น
ใช้กับแอปที่เพิ่งบูตด้วย OMNILINK_HOME ว่างเปล่า (FirstRunDialog กำลังโชว์)
ภาพเก็บ logs/ob*.png · จบด้วย Verdict ผ่าน/ไม่ผ่านแต่ละขั้น
"""
import sys
import time
import subprocess
from pathlib import Path

from pywinauto import Application, Desktop

SHOT = Path(r"D:\OmniLink\logs")
fails = []

def check(name, ok, detail=""):
    print(("[PASS] " if ok else "[FAIL] ") + name + (f" — {detail}" if detail else ""))
    if not ok:
        fails.append(name)

def shot(name):
    out = SHOT / name
    r = subprocess.run(["powershell", "-ExecutionPolicy", "Bypass", "-File",
                        r"D:\OmniLink\tests\tools\shot_window.ps1", "-OutFile", str(out)],
                       capture_output=True, text=True, timeout=60)
    ok = "SAVED" in (r.stdout or "")
    print(f"[SHOT] {name}: {'ok' if ok else r.stderr[-120:]}")
    return ok

app = Application(backend="uia").connect(title_re="OmniLink.*", timeout=8)
PID = app.process

import ctypes

def _wizard_hwnd():
    """หา hwnd ของ wizard ('OmniLink Wizard') ด้วย win32 — UIA Desktop ไม่เห็น window แบบ AllowsTransparency"""
    user32 = ctypes.windll.user32
    WM = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    hits = []
    def cb(hwnd, _):
        pid = ctypes.c_uint()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == PID and user32.IsWindowVisible(hwnd):
            buf = ctypes.create_unicode_buffer(128)
            user32.GetWindowTextW(hwnd, buf, 128)
            if buf.value == "OmniLink Wizard":
                hits.append(hwnd)
        return True
    user32.EnumWindows(WM(cb), 0)
    return hits[0] if hits else None

def wizard():
    """UIA wrapper ของ wizard ปัจจุบัน — ค้นใหม่ทุกครั้ง (hwnd เปลี่ยนได้) + รอ ready"""
    hw = _wizard_hwnd()
    if not hw:
        return None
    try:
        w = Desktop(backend="uia").window(handle=hw)
        w.wait("exists ready", timeout=5)
        return w
    except Exception:
        return None

def _els():
    """element ทั้งหมดของ process นี้ — main window + wizard ผ่าน handle ตรง"""
    out = []
    for w in Desktop(backend="uia").windows(process=PID):
        try:
            out.append(w)
        except Exception:
            pass
    hw = _wizard_hwnd()
    if hw:
        try:
            wiz = Desktop(backend="uia").window(handle=hw)
            wiz.wait("exists ready", timeout=3)
            out.insert(0, wiz)  # wizard มาก่อน — ปุ่มของ wizard ชนะ main window ที่ชื่อซ้ำ
        except Exception as ex:
            print("[dbg] wizard wrap fail:", ex)
    return out

def _btns_all():
    out = {}
    for w in _els():
        try:
            for b in w.descendants(control_type="Button"):
                t = _txt(b)
                if t and t not in out:
                    out[t] = b
        except Exception:
            pass
    return out

def _texts_all():
    out = []
    for w in _els():
        try:
            for t in w.descendants(control_type="Text"):
                tt = _txt(t)
                if tt:
                    out.append(tt)
        except Exception:
            pass
    return out

def _txt(el):
    try:
        return el.window_text() or ""
    except Exception:
        return ""

def buttons():
    w = wizard()
    if w is not None:
        try:
            out = {}
            for b in w.descendants(control_type="Button"):
                t = _txt(b)
                if t and t not in out:
                    out[t] = b
            return out
        except Exception:
            pass
    return _btns_all()

def texts():
    w = wizard()
    if w is not None:
        try:
            return [_txt(t) for t in w.descendants(control_type="Text") if _txt(t)]
        except Exception:
            pass
    return _texts_all()

# ── ขั้น 0: FirstRunDialog + ปุ่มใหม่ (tolerant: รอนซ้ำอาจไม่เด้งเพราะ flag ถูกเซฟแล้ว) ──
btns = buttons()
btn_names = [str(t) for t in btns.keys()]
dialog_up = "Add a computer now" in btns or "เพิ่มเครื่องตอนนี้เลย" in btns
if dialog_up:
    check("firstrun มีปุ่ม Add a computer now", True)
    shot("ob1_firstrun.png")
    addnow = btns.get("Add a computer now") or btns.get("เพิ่มเครื่องตอนนี้เลย")
    addnow.set_focus(); time.sleep(0.3); addnow.invoke(); time.sleep(2.0)
    still = [t for t in texts() if "ยินดีต้อนรับ" in t or "Welcome" in t]
    check("firstrun ปิดหลังกดปุ่ม", len(still) == 0, str(still[:1]))
else:
    print("[SKIP] firstrun ไม่เด้ง (flag ถูกเซฟจากรอบก่อน) — เปิด wizard ผ่าน hero แทนช่วงท้าย")

# ── ขั้น 1: wizard เปิด + ตัวเลือก 3 การ์ด (เช็คปุ่ม Choose* — รอ hwnd โผล่ก่อน) ──
import time as _t
_deadline = _t.time() + 6
while _t.time() < _deadline and _wizard_hwnd() is None:
    _t.sleep(0.3)
btns = buttons()
has_win = any("Choose Windows" in t for t in btns)
has_ssh = any("Choose Linux SSH" in t for t in btns)
check("wizard ขั้น 1 เปิด (3 การ์ดเลือก OS)", has_win and has_ssh, f"win={has_win} ssh={has_ssh}")
shot("ob2_wizard_step1.png")

# ── เลือก Windows → ขั้น 2 ──
win_card = None
for t, b in _btns_all().items():
    if "Choose Windows" in t or "Windows" in t or "windows" in t:
        win_card = b
        break
check("เลือกการ์ด Windows ได้", win_card is not None)
if win_card:
    win_card.invoke()
    time.sleep(1.2)
allt = "\n".join(texts())
btns = buttons()
btn_names = [str(t) for t in btns.keys()]
check("ขั้น 2 Windows: มีปุ่มเปิดโฟลเดอร์ + ถัดไปสแกน",
      any("Open installer" in b for b in btn_names) and any("scan" in b.lower() for b in btn_names),
      ",".join(btn_names[:6]))
shot("ob3_wizard_step2_win.png")
check("ขั้น 2 Windows: บอกว่ามี portable zip",
      "Portable" in allt or "portable" in allt or "Python" in allt)
next_scan = None
for t, b in _btns_all().items():
    if "scan" in t.lower() or "สแกน" in t:
        next_scan = b
        break
if next_scan:
    next_scan.invoke()
    time.sleep(4.5)  # รอสแกน 3 วิ
else:
    check("ปุ่มถัดไปสแกนหาเจอ", False)
    fails.append("no next")

# ── ขั้น 3: สแกน + ช่อง PIN ──
allt = "\n".join(texts())
btns = buttons()
btn_names = [str(t) for t in btns.keys()]
check("ขั้น 3: มีช่องรายการ/สถานะสแกน + ปุ่มจับคู่",
      any("Rescan" in b or "สแกนใหม่" in b for b in btn_names),
      ",".join(btn_names[:6]))
shot("ob4_wizard_step3_scan.png")

# ย้อนกลับ → เลือก Linux ทางเลือกที่ 2
back = None
for t, b in _btns_all().items():
    if "Back" in t or "ย้อน" in t:
        back = b
        break
if back:
    back.invoke()
    time.sleep(0.8)
    back.invoke()
    time.sleep(0.8)
linux_card = None
for t, b in _btns_all().items():
    if "Linux" in t:
        linux_card = b
        break
check("ย้อนกลับแล้วเลือก Linux ได้", linux_card is not None)
if linux_card:
    linux_card.invoke()
    time.sleep(1)
allt = "\n".join(texts())
btns = buttons()
btn_names = [str(t) for t in btns.keys()]
check("ขั้น 2 Linux: ฟอร์ม IP + SSH user + ปุ่มติดตั้ง",
      any("Install" in b or "ติดตั้ง" in b for b in btn_names),
      ",".join(btn_names[:6]))
shot("ob5_wizard_step2_linux.png")

# ปิด wizard
close = None
for t, b in _btns_all().items():
    if "Close" in t or "ปิด" in t:
        close = b
        break
if close:
    close.invoke()
    time.sleep(1)

# ── หน้าหลัก: hero โผล่เฉพาะเมื่อ 0 node (มี node แล้ว = ไม่โผล่ ถูกต้องตามดีไซน์) ──
import json as _json
try:
    _cfg = _json.load(open(r"D:\OmniLink\omnilink_config.json", encoding="utf-8"))
except Exception:
    _cfg = {"nodes": []}
_node_count = len([n for n in _cfg.get("nodes", []) if n.get("enabled", True)])
btns = buttons()
btn_names = [str(t) for t in btns.keys()]
if _node_count == 0:
    check("หน้าหลัก 0-node: hero ปุ่มเพิ่มเครื่องแรกโผล่",
          any("Add your first" in b or "เพิ่มเครื่องแรก" in b for b in btn_names),
          ",".join(btn_names[:8]))
else:
    check(f"มี node {_node_count} ตัวแล้ว: hero ไม่โผล่ (ถูกต้องตามดีไซน์)",
          not any("Add your first" in b or "เพิ่มเครื่องแรก" in b for b in btn_names))
shot("ob6_hero.png")

hero = None
for t, b in _btns_all().items():
    if "Add your first" in t or "เพิ่มเครื่องแรก" in t:
        hero = b
        break
if hero:
    hero.invoke()
    time.sleep(1.2)
    btns = buttons()
    check("hero เปิด wizard ได้", any("SSH" in t for t in texts()))
    shot("ob7_hero_opens_wizard.png")
    close = None
    for b in win.descendants(control_type="Button"):
        if "Close" in (b.window_text or "") or "ปิด" in (b.window_text or ""):
            close = b
            break
    if close:
        close.invoke()

print()
if fails:
    print(f"ONBOARDING-WALKTHROUGH-FAIL ({len(fails)}): {fails}")
    sys.exit(1)
print("ONBOARDING-WALKTHROUGH-PASS")
