#!/usr/bin/env python3
"""Agent edge state machine unit tests — รันได้บน Windows (ไม่ต้องมี X11)
ใช้เทคนิค object.__new__ ข้าม __init__ (ไม่โหลด libX11) + FakeX11 จำลองพิกัด
ครอบ: push threshold, hold delay, double-tap, corner dead-zone, lock, arming"""
import sys
import time
import os
from pathlib import Path

if sys.platform == "win32":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8")

# หาโฟลเดอร์ agent แบบทนทุก depth: เดินขึ้น parents หา node_agent → fallback ข้างไฟล์
_here = Path(__file__).resolve()
AGENT_DIR = _here.parent
for _p in _here.parents:
    if (_p / "node_agent" / "omnilink_agent.py").exists():
        AGENT_DIR = _p / "node_agent"
        break
sys.path.insert(0, str(AGENT_DIR))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import omnilink_agent as A  # noqa: E402

PASS = []
FAIL = []


def check(name, cond):
    (PASS if cond else FAIL).append(name)
    print(("PASS " if cond else "FAIL ") + name)


class FakeX11:
    """จำลอง X11Controller เฉพาะเมธอดที่ state machine ใช้"""

    def __init__(self, w=960, h=720):
        self.width = w
        self.height = h
        self.cursor = (480, 360)
        self.flushed = 0

    @property
    def cur_x(self):
        # PY-F11: check_edge_return อ่าน tracked position จาก x11.cur_x/cur_y
        return self.cursor[0]

    @property
    def cur_y(self):
        return self.cursor[1]

    def query_pointer(self):
        return self.cursor

    def release_all(self):
        pass

    def move_abs(self, x, y):
        self.cursor = (x, y)

    def get_lang_group(self):
        return 0

    def refresh_dimensions(self):
        pass


class FakeOsd:
    def set_active(self, active, lang):
        pass


def make_server(x11):
    s = object.__new__(A.OmniLinkAgentServer)  # ข้าม __init__ — ไม่แตะ X11 จริง
    s.x11 = x11
    s.osd = FakeOsd()
    s.active_focus = True
    s.node_position = "right"
    s.screen_locked = False
    s._edge_push = 0.0
    s._at_edge_since = 0.0
    s._last_leave_at = 0.0
    s._had_real_push = False
    s._last_udp_move = time.monotonic()  # armed
    s._switch_delay_ms = 0               # default: ไม่มี delay (เคสพื้นฐาน)
    s._double_tap_ms = 0                 # default: ปิด double-tap
    s._corner_size = 0                   # default: ไม่มีมุมตาย
    s._corners = {"tl": True, "tr": True, "bl": True, "br": True}
    s.sent = []

    def fake_send(payload):
        s.sent.append(payload)
        return True

    s.send_tcp_message = fake_send
    s._push_clipboard_async = lambda: None
    return s


def edge_at(s, y=360):
    s.x11.cursor = (0, y)  # ขอบซ้ายสุด (node ฝั่งขวา)


def away(s, x=100, y=360):
    s.x11.cursor = (x, y)


def push_packets(s, total_px, y=360, dx=-10, pause=0.0):
    """ส่ง packet ดันทีละ 10px ตามที่ hook จริงทำ"""
    remaining = total_px
    while remaining > 0:
        step = min(10, remaining)
        edge_at(s, y)
        s.check_edge_return(s.x11.cursor[0], y, dx * (step // 10), 0)
        remaining -= step
        if pause:
            time.sleep(pause)


def test_basic_push_switch():
    """ดัน 60px ที่ขอบ (ไม่มี delay/corner) → ต้องสลับ + park กลางจอ"""
    s = make_server(FakeX11())
    push_packets(s, 60)
    m = s.sent[-1] if s.sent else None
    check("basic: edge_return ถูกส่ง", m and m.get("type") == "edge_return" and m.get("edge") == "left")
    check("basic: focus ปิดหลังสลับ", s.active_focus is False)


def test_no_switch_when_away():
    s = make_server(FakeX11())
    away(s)
    s.check_edge_return(100, 360, -10, 0)
    check("away: ไม่สลับเมื่อไม่ถึงขอบ", not s.sent)


def test_push_below_threshold():
    s = make_server(FakeX11())
    push_packets(s, 30)  # ครึ่งเดียวของ threshold 60
    check("threshold: ดันไม่ครบ 60px → ไม่สลับ", not s.sent)


def test_hold_delay_blocks_early():
    """delay=600ms: ดันครบ 60px แต่ hold ยังไม่ครบ → ไม่สลับ"""
    s = make_server(FakeX11())
    s._switch_delay_ms = 600
    edge_at(s, 360)
    s.check_edge_return(0, 360, -10, 0)  # arrival
    push_packets(s, 60)
    check("delay: ดันครบแต่ hold ไม่ครบ → ยังไม่สลับ", not s.sent)


def test_hold_delay_triggers_after_wait():
    s = make_server(FakeX11())
    s._switch_delay_ms = 200
    edge_at(s, 360)
    s.check_edge_return(0, 360, -10, 0)
    push_packets(s, 60)
    time.sleep(0.25)
    # มา packet เล็กๆ ต่อ (จิ้มๆ ที่ขอบ) — ประเมิน hold อีกครั้ง
    edge_at(s, 360)
    s.check_edge_return(0, 360, -2, 0)
    check("delay: ครบกำหนดแล้ว → สลับ", bool(s.sent))


def test_double_tap():
    """tap1 ดันหนัก → ออกจากขอบ → กลับมาใน double_tap_ms → สลับทันทีแม้ delay สูง"""
    s = make_server(FakeX11())
    s._switch_delay_ms = 5000  # ปิดทาง hold
    s._double_tap_ms = 400
    # tap 1
    edge_at(s, 360)
    s.check_edge_return(0, 360, -10, 0)
    push_packets(s, 50)  # ดัน 50px (≥ ครึ่ง threshold)
    # ออกจากขอบ
    away(s, 40)
    s.check_edge_return(40, 360, 10, 0)
    time.sleep(0.05)
    # tap 2: กลับมาถึงขอบ
    edge_at(s, 360)
    s.check_edge_return(0, 360, -10, 0)
    m = s.sent[-1] if s.sent else None
    check("double-tap: สลับทันทีเมื่อกลับมาใน 400ms", m and m.get("type") == "edge_return")


def test_corner_deadzone():
    """corner=60px, มุมซ้ายบนเปิด: ดันขอบที่ y=10 (ใกล้มุม) → ไม่สลับ"""
    s = make_server(FakeX11())
    s._corner_size = 60
    edge_at(s, 10)
    s.check_edge_return(0, 10, -10, 0)
    push_packets(s, 90, y=10)
    check("corner: ดันในโซนมุมตาย → ไม่สลับ", not s.sent)
    # กลางจอ → สลับได้
    push_packets(s, 90, y=360)
    check("corner: นอกโซนมุม → สลับปกติ", bool(s.sent))


def test_corner_disabled():
    s = make_server(FakeX11())
    s._corner_size = 60
    s._corners = {"tl": False, "tr": True, "bl": True, "br": True}
    edge_at(s, 10)
    s.check_edge_return(0, 10, -10, 0)
    push_packets(s, 90, y=10)
    check("corner-off: ปิดรายมุมแล้วสลับได้", bool(s.sent))


def test_lock_blocks():
    s = make_server(FakeX11())
    s.screen_locked = True
    push_packets(s, 90)
    check("lock: ล็อกแล้วไม่สลับ", not s.sent)


def test_not_armed_without_focus_or_udp():
    s = make_server(FakeX11())
    s.active_focus = False
    s._last_udp_move = time.monotonic() - 5  # เงียบเกิน 2 วิ
    push_packets(s, 90)
    check("arming: ไม่ focus + UDP เงียบ → ไม่ประมวลผล", not s.sent)


def test_left_position_node():
    """เครื่องวางซ้าย → หนีออกขอบขวา"""
    s = make_server(FakeX11())
    s.node_position = "left"
    s.x11.cursor = (959, 360)  # ขอบขวาสุด (w-1)
    s.check_edge_return(959, 360, 10, 0)
    s.x11.cursor = (959, 360)
    s.check_edge_return(959, 360, 10, 0)
    s.x11.cursor = (959, 360)
    s.check_edge_return(959, 360, 10, 0)
    s.x11.cursor = (959, 360)
    s.check_edge_return(959, 360, 10, 0)
    s.x11.cursor = (959, 360)
    s.check_edge_return(959, 360, 10, 0)
    s.x11.cursor = (959, 360)
    s.check_edge_return(959, 360, 10, 0)
    m = s.sent[-1] if s.sent else None
    check("left-node: หนีออกขอบขวา ได้ edge=right", m and m.get("edge") == "right")


def test_top_position_node():
    """เครื่องวางบน → หนีออกขอบล่าง (edge=bottom)"""
    s = make_server(FakeX11())
    s.node_position = "top"
    for _ in range(6):
        s.x11.cursor = (480, 719)  # ขอบล่างสุด (h-1)
        s.check_edge_return(480, 719, 0, 10)
    m = s.sent[-1] if s.sent else None
    check("top-node: หนีออกขอบล่าง ได้ edge=bottom", m and m.get("edge") == "bottom")


def test_bottom_position_node():
    """เครื่องวางล่าง → หนีออกขอบบน (edge=top)"""
    s = make_server(FakeX11())
    s.node_position = "bottom"
    for _ in range(6):
        s.x11.cursor = (480, 0)  # ขอบบนสุด (y=0)
        s.check_edge_return(480, 0, 0, -10)
    m = s.sent[-1] if s.sent else None
    check("bottom-node: หนีออกขอบบน ได้ edge=top", m and m.get("edge") == "top")


def test_park_position():
    s = make_server(FakeX11())
    push_packets(s, 60)  # พอดี threshold — packet สุดท้ายคือตัว trigger (ไม่มี packet ทับหลังจอด)
    cx, cy = s.x11.cursor
    check("park: เคอร์เซอร์ถูกจอดกลางจอ", abs(cx - 480) <= 1 and abs(cy - 360) <= 1)


def run_all():
    test_basic_push_switch()
    test_no_switch_when_away()
    test_push_below_threshold()
    test_hold_delay_blocks_early()
    test_hold_delay_triggers_after_wait()
    test_double_tap()
    test_corner_deadzone()
    test_corner_disabled()
    test_lock_blocks()
    test_not_armed_without_focus_or_udp()
    test_left_position_node()
    test_top_position_node()
    test_bottom_position_node()
    test_park_position()
    print(f"\n=== {len(PASS)} passed, {len(FAIL)} failed ===")
    if FAIL:
        print("FAILED:", FAIL)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(run_all())
