#!/usr/bin/env python3
"""Phase 4b — UIA "bar" sweep กับ OmniLink จริง: พิมพ์ของประหลาดใส่ทุกกล่อง
ทุกขั้นตอนต้อง: app ยังมีชีวิต + config อยู่ในขอบเขตปลอดภัย (clamp/คงค่าเดิม)
ก่อนรัน: OmniLink v1.9.0 ต้องกำลังรันอยู่"""
import json
import sys
import time
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)

from pywinauto import Application

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / "omnilink_config.json"
PASS, FAIL = [], []


def ok(name):
    PASS.append(name)
    print(f"  OK  {name}")


def bad(name, why=""):
    FAIL.append(name)
    print(f" FAIL {name} {why}")


def read_cfg():
    return json.loads(CONFIG.read_text(encoding="utf-8"))


def pump(s=1.2):
    time.sleep(s)


def alive(win):
    try:
        return win.exists(timeout=2) and win.is_visible()
    except Exception:
        return False


def type_garbage(box, cfg_key, lo, hi, restore=None):
    """พิมพ์ของประหลาดเข้ากล่อง — config ต้องไม่หลุดช่วง [lo,hi] และ app ต้องไม่ตาย"""
    cfg = read_cfg()
    original = cfg.get(cfg_key)
    restore = restore if restore is not None else original
    for junk in ["-1", "abc", "99999999", "🦎", ""]:
        box.set_edit_text(junk)
        pump(1.4)
        value = read_cfg().get(cfg_key)
        name = junk if junk else "<empty>"
        if value is None or not (lo <= value <= hi):
            bad(f"{cfg_key} survives '{name}'", f"config={value}")
            box.set_edit_text(str(restore))
            pump(1.2)
            return
    ok(f"{cfg_key} survives -1/abc/99999999/🦎/empty (final={read_cfg().get(cfg_key)})")
    box.set_edit_text(str(restore))
    pump(1.2)


def main():
    win = Application(backend="uia").connect(title_re="OmniLink.*").window()
    win.set_focus()
    pump(1.0)

    tab = win.child_window(title="Settings", control_type="TabItem")
    tab.select()
    pump(1.0)
    rail = win.child_window(control_type="List", found_index=0)
    items = rail.children(control_type="ListItem")

    # ── Switching: กล่อง delay กับของประหลาด ──
    items[1].select()
    pump(0.9)
    type_garbage(win.child_window(auto_id="SwitchDelayBox", control_type="Edit"),
                 "switch_delay_ms", 0, 2000, restore=250)

    # ── Network: allowlist ขยะ (valid entries เท่านั้นที่บันทึก) ──
    items[3].select()
    pump(0.9)
    before = read_cfg().get("allowed_subnets")
    box = win.child_window(auto_id="AllowlistBox", control_type="Edit")
    box.set_edit_text("banana, 300.300.300.300/99, 🦎")
    pump(1.5)
    mid = read_cfg().get("allowed_subnets")
    if alive(win) and mid == before:
        ok("allowlist rejects garbage (config unchanged)")
    else:
        bad("allowlist rejects garbage", f"{before} -> {mid}")
    box.set_edit_text("")
    pump(1.0)

    # ── Logging: ขนาดไฟล์ประหลาด ──
    items[7].select()
    pump(0.9)
    type_garbage(win.child_window(auto_id="LogMaxKbBox", control_type="Edit"),
                 "log_max_bytes", 500_000, 50_000_000, restore=5_000_000)

    # ── Rename ด้วยอิโมจิ แล้ว reset ──
    win.MainTabs = None
    tab0 = win.child_window(title_re="Topology.*", control_type="TabItem")
    tab0.select()
    pump(1.0)
    rename = win.child_window(auto_id="BtnRenameNode")
    rename.click_input()
    pump(1.2)
    dlg = win.top_level_control if False else None
    # dialog เป็น window ลูกแยก — จับด้วย title
    try:
        dlgwin = Application(backend="uia").connect(title_re="Rename Device|เปลี่ยนชื่อเครื่อง").window()
        alias_box = dlgwin.child_window(control_type="Edit", found_index=0)
        for junk in ["🦎 ทดสอบ 🚀", ""]:
            alias_box.set_edit_text(junk)
            pump(0.6)
        alias_box.set_edit_text("🦎 UIA Bar 🚀")
        pump(0.5)
        save = dlgwin.child_window(title_re="Save Alias|บันทึกชื่อ")
        save.click_input()
        pump(1.5)
        name = read_cfg()["nodes"][0].get("custom_name")
        if alive(win) and name == "🦎 UIA Bar 🚀":
            ok(f"rename with emoji saved ({name})")
        elif alive(win):
            bad("rename with emoji saved", f"custom_name={name}")
        # คืนชื่อ: กด Reset to hostname ใน dialog อีกรอบ
        rename.click_input()
        pump(1.2)
        dlgwin2 = Application(backend="uia").connect(title_re="Rename Device|เปลี่ยนชื่อเครื่อง").window()
        dlgwin2.child_window(title_re="Reset to Hostname|รีเซ็ต").click_input()
        pump(1.0)
        dlgwin2.child_window(title_re="Save Alias|บันทึกชื่อ").click_input()
        pump(1.5)
        name2 = read_cfg()["nodes"][0].get("custom_name")
        if alive(win) and not name2:
            ok("rename reset clears custom_name")
        else:
            bad("rename reset clears custom_name", f"custom_name={name2}")
    except Exception as ex:
        bad("rename dialog flow", str(ex)[:120])

    # ── กดรัว: สลับแท็บ 20 รอบ + หน้า settings 20 รอบ + สลับภาษา 6 รอบ ──
    tabs = win.child_window(control_type="Tab", found_index=0)
    for i in range(20):
        tabs.select(i % 4)
    pump(0.5)
    tabs.select(3)
    pump(0.8)
    rail2 = win.child_window(control_type="List", found_index=0)
    secs = rail2.children(control_type="ListItem")
    for i in range(20):
        secs[i % len(secs)].select()
    pump(0.5)
    ok("rapid switching x40 survived")

    items0 = rail2.children(control_type="ListItem")[0].select()
    pump(0.9)
    lang = win.child_window(auto_id="LangCombo", control_type="ComboBox")
    for idx in [1, 2, 1, 2, 1, 2]:
        lang.expand()
        pump(0.4)
        lang.select(idx)
        pump(0.8)
    lang.select(2)
    pump(1.2)
    if alive(win):
        ok("language spam x6 survived")
    else:
        bad("language spam x6")

    # ── สรุป ──
    final_alive = alive(win)
    if final_alive:
        ok("app alive at end of bar sweep")
    else:
        bad("app alive at end of bar sweep")

    print(f"\nสรุป: {len(PASS)} passed, {len(FAIL)} failed")
    if FAIL or not final_alive:
        for f_ in FAIL:
            print("  FAILED:", f_)
        return 1
    print("BAR UIA SWEEP ALL GREEN")
    return 0


if __name__ == "__main__":
    sys.exit(main())
