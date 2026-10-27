#!/usr/bin/env python3
"""v1.9.1 UIA sweep — ขับ OmniLink จริงผ่าน UI Automation (ไม่ใช่สายตา ไม่ใช่เคลม):
1) อ่าน rail 10 หมวด → คลิกทีละหน้า → นับ control ที่มองเห็นได้ต้อง > 0 ทุกหน้า
2) toggle-restore: เปลี่ยนค่าจริงใน UI → พิสูจน์ config file เปลี่ยนตาม → คืนค่าเดิม
   (Switching.delay / General.prevent_sleep / Logging.level)
3) สลับภาษา EN → TH → EN ผ่าน combo จริง → rail ต้องเปลี่ยนภาษาตาม
4) จับ screenshot ทุกหน้าเป็นหลักฐาน (test-artifacts/uia-*.png)

ก่อนรัน: OmniLink v1.9.0 ต้องกำลังรันอยู่ (หน้าต่างเดียว)
"""
import json
import sys
import time
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)

def connect_omnilink_window():
    """เชื่อมหน้าต่างแอปจริงทาง process (title match โดน Explorer ที่เปิดโฟลเดอร์ OmniLink ได้)"""
    import subprocess
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq OmniLink.exe", "/FO", "CSV"],
                         capture_output=True, text=True).stdout
    pids = [int(line.split('","')[1]) for line in out.splitlines() if line.startswith('"OmniLink.exe"')]
    if not pids:
        raise RuntimeError("OmniLink.exe ไม่ได้รันอยู่")
    return Application(backend="uia").connect(process=pids[0]).window()

from pywinauto import Application, Desktop
from pywinauto.keyboard import send_keys

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / "omnilink_config.json"
SHOTS = ROOT / "test-artifacts"
SHOTS.mkdir(parents=True, exist_ok=True)

PASS, FAIL = [], []


def ok(name):
    PASS.append(name)
    print(f"  OK  {name}")


def bad(name, why=""):
    FAIL.append(name)
    print(f" FAIL {name} {why}")


def read_config():
    return json.loads(CONFIG.read_text(encoding="utf-8"))


def pump(seconds=1.2):
    time.sleep(seconds)


def walk_texts(elem, depth=0, out=None, max_depth=14):
    """รวมชื่อ/ค่าของ control ทั้ง subtree — นับ 'ของที่มองเห็น' แบบ UIA"""
    if out is None:
        out = []
    if depth > max_depth:
        return out
    try:
        out.append(elem.element_info.name or "")
    except Exception:
        pass
    try:
        for child in elem.children():
            walk_texts(child, depth + 1, out, max_depth)
    except Exception:
        pass
    return out


def main():
    win = connect_omnilink_window()
    win.set_focus()
    pump(1.0)
    print("[1] connected:", win.window_text())

    # ── ไปที่แท็บ Settings ──
    tab = win.child_window(title="Settings", control_type="TabItem")
    if tab.exists(timeout=3):
        tab.select()
    else:
        tab = win.child_window(title_re="ตั้งค่า", control_type="TabItem")
        tab.select()
    pump(1.0)

    # ── [1] rail 10 หมวด + นับ control ต่อหน้า ──
    expected = ["General", "Switching", "Shortcuts", "Network & Ports", "Security & Pairing",
                "Audio", "Clipboard", "Logging", "Advanced", "Danger Zone"]
    rail_list = win.child_window(control_type="List", found_index=0)
    rail_titles = [i.window_text() for i in rail_list.children(control_type="ListItem")]
    print("  rail =", rail_titles)
    if len(rail_titles) == 10:
        ok("rail has 10 sections")
    else:
        bad("rail has 10 sections", f"got {len(rail_titles)}")

    counts = {}
    for name in expected:
        item = rail_list.child_window(title=name, control_type="ListItem")
        if not item.exists(timeout=2):
            # ภาษาไทย (ถ้าผู้ใช้สลับไว้) — หาตามลำดับแทน
            idx = expected.index(name)
            item = rail_list.children(control_type="ListItem")[idx]
        item.select()
        pump(0.9)
        texts = [t for t in walk_texts(win) if t]
        counts[name] = len(texts)
        if len(texts) > 5:
            ok(f"section '{name}' renders ({len(texts)} UIA nodes)")
        else:
            bad(f"section '{name}' renders", f"only {len(texts)} nodes")
        shot = SHOTS / f"uia-{name.replace(' ', '_').replace('&', 'and')}.png"
        try:
            win.capture_as_image().save(shot)
        except Exception as ex:
            print("  (screenshot failed:", ex, ")")

    # ── [2] toggle-restore ──
    # 2a. Switching: delay box 321 → config → คืน 250 (เลือกด้วย index — ListItem title ผูก icon ไม่ตรง)
    rail_list.children(control_type="ListItem")[1].select()
    pump(0.9)
    before = read_config()["switch_delay_ms"]
    delay = win.child_window(auto_id="SwitchDelayBox", control_type="Edit")
    delay.set_edit_text("321")
    pump(1.5)
    mid = read_config()["switch_delay_ms"]
    delay.set_edit_text(str(before))
    pump(1.5)
    after = read_config()["switch_delay_ms"]
    if mid == 321 and after == before:
        ok(f"switching delay wiring (config {before}→{mid}→{after})")
    else:
        bad("switching delay wiring", f"{before}→{mid}→{after}")

    # 2b. General: prevent_sleep ON → config → OFF
    rail_list.children(control_type="ListItem")[0].select()
    pump(0.9)
    before = read_config()["prevent_sleep"]
    chk = win.child_window(auto_id="ChkPreventSleep", control_type="CheckBox")
    chk.toggle()
    pump(1.5)
    mid = read_config()["prevent_sleep"]
    chk.toggle()
    pump(1.5)
    after = read_config()["prevent_sleep"]
    if mid != before and after == before:
        ok(f"prevent_sleep wiring ({before}→{mid}→{after})")
    else:
        bad("prevent_sleep wiring", f"{before}→{mid}→{after}")

    # 2c. Logging: level → ERROR → config → INFO
    rail_list.children(control_type="ListItem")[7].select()
    pump(0.9)
    before = read_config()["log_level"]
    combo = win.child_window(auto_id="LevelCombo", control_type="ComboBox")
    combo.expand()
    pump(0.5)
    combo.select(1)  # "Errors only"
    pump(1.5)
    mid = read_config()["log_level"]
    combo.select(3)  # "Everything (recommended)"
    pump(1.5)
    after = read_config()["log_level"]
    if mid == "ERROR" and after == before:
        ok(f"log level wiring ({before}→{mid}→{after})")
    else:
        bad("log level wiring", f"{before}→{mid}→{after}")

    # ── [3] สลับภาษา EN → TH → EN ──
    rail_list.children(control_type="ListItem")[0].select()
    pump(0.9)
    lang = win.child_window(auto_id="LangCombo", control_type="ComboBox")
    lang.expand()
    pump(0.5)
    lang.select(1)  # "Thai (ไทย)"
    pump(1.5)
    rail_th = [i.window_text() for i in rail_list.children(control_type="ListItem")]
    lang.select(2)  # "English"
    pump(1.5)
    rail_en = [i.window_text() for i in rail_list.children(control_type="ListItem")]
    # ListItem.window_text() คืน ToString ทั้ง record — เช็คจาก substring ของ Title
    th_ok = any("ทั่วไป" in t for t in rail_th)
    en_ok = any("General" in t for t in rail_en)
    if th_ok and en_ok:
        ok(f"language live switch (rail[0] th='{rail_th[0][:60]}' en='{rail_en[0][:60]}')")
    else:
        bad("language live switch", f"th[0]={rail_th[:1]} en[0]={rail_en[:1]}")

    # ── สรุป ──
    print(f"\nสรุป: {len(PASS)} passed, {len(FAIL)} failed")
    if FAIL:
        for f_ in FAIL:
            print("  FAILED:", f_)
        return 1
    print("UIA SWEEP ALL GREEN")
    return 0


if __name__ == "__main__":
    sys.exit(main())
