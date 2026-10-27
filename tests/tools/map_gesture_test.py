#!/usr/bin/env python3
"""v1.9.1 gesture test จริงด้วยเมาส์ (แก้ false-pass รอบก่อน — ต้องยืนยันว่า pan เลื่อนจริง):
1) ลากเริ่มบน ghost ของ host → ภาพผังต้องเลื่อน (pixel-diff > threshold) + config ไม่เปลี่ยน (ไม่ place พลาด)
2) ลากพื้นว่าง → ภาพเลื่อน + config ไม่เปลี่ยน
3) ล้อเมาส์ซูม → ภาพต้องเปลี่ยน (ซูมจริง)
4) app รอดตลอด
ก่อนรัน: OmniLink v1.9.x ต้องกำลังรันอยู่"""
import json
import sys
import time
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)

from PIL import Image, ImageChops, ImageGrab
def connect_omnilink_window():
    """เชื่อมหน้าต่างแอปจริงทาง process (title match โดน Explorer ที่เปิดโฟลเดอร์ OmniLink ได้)"""
    import subprocess
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq OmniLink.exe", "/FO", "CSV"],
                         capture_output=True, text=True).stdout
    pids = [int(line.split('","')[1]) for line in out.splitlines() if line.startswith('"OmniLink.exe"')]
    if not pids:
        raise RuntimeError("OmniLink.exe ไม่ได้รันอยู่")
    return Application(backend="uia").connect(process=pids[0]).window()

from pywinauto import Application
from pywinauto import mouse

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


def cfg_signature(cfg):
    return json.dumps([[n["node_id"], n.get("anchor", ""), n["position"]] for n in cfg["nodes"]],
                      ensure_ascii=False)


def pump(s=0.8):
    time.sleep(s)


def alive(win):
    try:
        return win.exists(timeout=2)
    except Exception:
        return False


def main():
    win = connect_omnilink_window()
    win.set_focus()
    pump(1.0)

    def shot():
        r = win.rectangle()
        return ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))

    def diff_ratio(a, b):
        diff = ImageChops.difference(a.convert("RGB"), b.convert("RGB"))
        hist = diff.convert("L").histogram()
        changed = sum(hist[24:])  # พิกเซลที่ต่างเกิน 24 ระดับ
        return changed / max(1, a.size[0] * a.size[1])

    # ── จุดทดสอบ: กลางการ์ด host + ghost เหนือ host + พื้นที่ว่างขวาล่าง ──
    host = win.child_window(title_re=".*\\(Host\\)", control_type="Text")
    if not host.exists(timeout=3):
        bad("find host card")
        return 1
    hr = host.rectangle()
    cx, cy = (hr.left + hr.right) // 2, (hr.top + hr.bottom) // 2
    ghost_x, ghost_y = cx, hr.top - 55          # ghost เหนือ host
    empty_x, empty_y = hr.right + 150, hr.bottom + 90  # พื้นที่ว่างขวาล่าง

    cfg_before = cfg_signature(read_cfg())

    def reset_view():
        # ทาง 1: ปุ่ม Reset view (ระบุ Button กัน match ซ้ำ)
        try:
            btn = win.child_window(title_re="รีเซ็ตมุมมอง|Reset view", control_type="Button")
            if btn.exists(timeout=2):
                btn.click_input()
                pump(0.8)
                return
        except Exception as ex:
            print("  (reset via button failed:", ex, ")")
        # ทาง 2: double-click พื้นที่ว่าง = fit
        mouse.double_click(coords=(empty_x, empty_y))
        pump(0.8)

    reset_view()  # baseline ก่อนเทสต์แรก
    reset_view()  # ครั้งสองยืนยันความพร้อม

    # ── 1) ลากเริ่มบน ghost → pan จริง (ภาพเลื่อน) + config ไม่เปลี่ยน — retry 2 รอบ ──
    def case_ghost_drag():
        reset_view()
        ghost_el = win.child_window(title_re="วางด้านบน|Place Above", control_type="Text", found_index=0)
        if not ghost_el.exists(timeout=2):
            raise RuntimeError("ไม่เจอ ghost Place Above")
        gr = ghost_el.rectangle()
        gx, gy = (gr.left + gr.right) // 2, (gr.top + gr.bottom) // 2
        before = shot()
        mouse.press(coords=(gx, gy), button="left")
        for step in range(1, 11):
            mouse.move(coords=(gx - 25 * step, gy + 10 * step))
            time.sleep(0.03)
        mouse.release(coords=(gx - 250, gy + 100), button="left")
        pump(1.0)
        return diff_ratio(before, shot())

    for attempt in (1, 2, 3):
        ratio = case_ghost_drag()
        cfg_mid = cfg_signature(read_cfg())
        if alive(win) and ratio > 0.015 and cfg_mid == cfg_before:
            ok(f"ghost drag = pan (pixels moved {ratio:.0%}, config unchanged)")
            break
        if attempt == 3:
            bad("ghost drag = pan", f"diff {ratio:.0%} config_changed={cfg_mid != cfg_before}")
        pump(1.0)

    # ── 2) ลากพื้นว่าง → pan — retry 2 รอบ ──
    def case_empty_drag():
        reset_view()
        before = shot()
        mouse.press(coords=(empty_x, empty_y), button="left")
        for step in range(1, 11):
            mouse.move(coords=(empty_x - 25 * step, empty_y - 8 * step))
            time.sleep(0.03)
        mouse.release(coords=(empty_x - 250, empty_y - 80), button="left")
        pump(1.0)
        return diff_ratio(before, shot())

    for attempt in (1, 2, 3):
        ratio = case_empty_drag()
        if alive(win) and ratio > 0.01 and cfg_signature(read_cfg()) == cfg_before:
            ok(f"empty drag = pan (pixels moved {ratio:.0%})")
            break
        if attempt == 3:
            bad("empty drag = pan", f"diff {ratio:.0%}")
        pump(1.0)

    # ── 3) ล้อเมาส์ซูม 4 จังหวะ (จับ after ก่อนซูมกลับ) ──
    def case_wheel_zoom():
        reset_view()
        before = shot()
        mouse.move(coords=(cx, cy))
        pump(0.3)
        for _ in range(4):
            mouse.scroll(coords=(cx, cy), wheel_dist=600)
            pump(0.3)
        after = shot()  # จับขณะซูมเข้าอยู่ (ซูม 1.15^4 ≈ 1.75 เท่า)
        ratio = diff_ratio(before, after)
        if ratio <= 0.05:
            art = ROOT / "test-artifacts"
            art.mkdir(exist_ok=True)
            before.save(art / "wheel-before.png")
            after.save(art / "wheel-after.png")
        mouse.scroll(coords=(cx, cy), wheel_dist=-600)
        pump(0.5)
        return ratio

    for attempt in (1, 2, 3):
        ratio = case_wheel_zoom()
        if alive(win) and ratio > 0.015 and cfg_signature(read_cfg()) == cfg_before:
            ok(f"wheel zoom = real zoom (pixels changed {ratio:.0%})")
            break
        if attempt == 3:
            bad("wheel zoom = real zoom", f"diff {ratio:.0%}")
        pump(1.0)

    # ── สรุป ──
    print(f"\nสรุป: {len(PASS)} passed, {len(FAIL)} failed")
    if FAIL:
        for f_ in FAIL:
            print("  FAILED:", f_)
        return 1
    print("GESTURE TEST ALL GREEN")
    return 0


if __name__ == "__main__":
    sys.exit(main())
