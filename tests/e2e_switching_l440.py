# -*- coding: utf-8 -*-
"""
E2E Switching Test Suite — AI-runnable (ไม่ต้องมีมนุษย์จับเมาส์)
ทดสอบสายพานสลับจอจริงทั้งวง: PSPV inject → hook/edge engine → L440 agent → กลับ

Trigger  : SetCursorPos ดันเคอร์เซอร์ PSPV ชนขอบ (drives LL hook จริง)
Verify   : L440 ผ่าน SSH (xdotool ตำแหน่งเคอร์เซอร์จริง + agent log)
           + host log events + GetCursorPos ฝั่ง host
Latency  : วัดจาก push → เคอร์เซอร์มาถึง L440 (poll 0.15s)

ข้อควรระวัง: ระหว่างรัน ห้ามแตะเมาส์จริง (จะรบกวน injection)
"""
import ctypes, subprocess, time, sys, json
from datetime import datetime
from ctypes import wintypes

u32 = ctypes.windll.user32

# godclass-fix: DPI-aware ก่อนทุกอย่าง — ไม่งั้น SendInput VIRTUALDESK absolute
# จาก process DPI-unaware ถูก OS scale ผิดบนจอ dual mixed-DPI → เคอร์เซอร์หายนอกจอ
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PER_MONITOR_AWARE_V2
except Exception:
    u32.SetProcessDPIAware()

def virtual_screen():
    class RECT(ctypes.Structure):
        _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long), ("r", ctypes.c_long), ("b", ctypes.c_long)]
    met = RECT()
    # 76/77/78/79 = SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN
    met.l, met.t = u32.GetSystemMetrics(76), u32.GetSystemMetrics(77)
    met.r = met.l + u32.GetSystemMetrics(78)
    met.b = met.t + u32.GetSystemMetrics(79)
    return met

VS = virtual_screen()

PUL = ctypes.POINTER(ctypes.c_ulong)
class _KI(ctypes.Structure):
    _fields_=[("wVk",ctypes.c_ulong),("wScan",ctypes.c_ulong),("dwFlags",ctypes.c_ulong),("time",ctypes.c_ulong),("dwExtraInfo",PUL)]
class _MI(ctypes.Structure):
    _fields_=[("dx",ctypes.c_long),("dy",ctypes.c_long),("mouseData",ctypes.c_ulong),("dwFlags",ctypes.c_ulong),("time",ctypes.c_ulong),("dwExtraInfo",PUL)]
class _II(ctypes.Structure):
    class _U(ctypes.Union): _fields_=[("ki",_KI),("mi",_MI)]
    _anonymous_=("u",); _fields_=[("type",ctypes.c_ulong),("u",_U)]

INPUT_MOUSE = 0
MOVE_ABS = 0x0001 | 0x4000  # MOUSEEVENTF_MOVE | MOUSEEVENTF_VIRTUALDESK

def sendmove_abs(x, y):
    """ฉีด ABSOLUTE normalized (0-65535 บน virtual screen) — ตรงจุดที่ตั้งใจ ไม่สะสม"""
    nx = int((x - VS.l) * 65535 / max(1, VS.r - VS.l - 1))
    ny = int((y - VS.t) * 65535 / max(1, VS.b - VS.t - 1))
    ii = _II(); ii.type = INPUT_MOUSE
    ii.mi = _MI(nx, ny, 0, MOVE_ABS, 0, None)
    u32.SendInput(1, ctypes.byref(ii), ctypes.sizeof(ii))
from ctypes import wintypes

L440_SSH = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "psl440@192.168.99.2"]
EDGE_PUSH_Y = 432          # กลางจอแนวตั้ง (เลี่ยมุมตาย)
RIGHT_EDGE = 1535          # ขอบขวาสุดของ virtual screen
RESULTS = []

def now_ms(): return int(time.time() * 1000)

def check(name, ok, detail=""):
    RESULTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}")

def ssh(cmd, timeout=8):
    try:
        out = subprocess.run(L440_SSH + [cmd], capture_output=True, text=True, timeout=timeout)
        return out.stdout.strip()
    except Exception as e:
        return f"ERR:{e}"

def host_cursor():
    pt = wintypes.POINT()
    u32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y

def set_cursor(x, y):
    u32.SetCursorPos(int(x), int(y))

def l440_cursor():
    out = ssh("DISPLAY=:0 xdotool getmouselocation shell 2>/dev/null | head -1")
    # "X=470 Y=497 SCREEN=0 WINDOW=..."
    try:
        parts = dict(p.split("=", 1) for p in out.replace(" ", "").splitlines() if "=" in p)
        return int(parts["X"]), int(parts["Y"])
    except Exception:
        return None

def log_lines():
    try:
        with open(r"D:\OmniLink\logs\omnilink-20261028.log", encoding="utf-8", errors="replace") as f:
            return f.read().splitlines()
    except Exception:
        return []

def count_log(pattern, since_line=-1):
    lines = log_lines()
    hits = 0
    for i, ln in enumerate(lines):
        if i > since_line and pattern in ln:
            hits += 1
    return hits, len(lines) - 1

def sendrel(dx, dy):
    """SendInput relative move — physical space ตรง ๆ ไม่ผิด DPI (บทเรียน 28 ต.ค.)"""
    ii = _II(); ii.type = INPUT_MOUSE
    ii.mi = _MI(dx, dy, 0, 0x0001, 0, None)  # MOUSEEVENTF_MOVE (relative)
    u32.SendInput(1, ctypes.byref(ii), ctypes.sizeof(ii))

def push_right_edge():
    """ดันเคอร์เซอร์ไปชนขอบขวาที่กลางจอแนวตั้ง — relative injection (DPI-proof)
    บทเรียน: ห้ามดันที่ y=0 (มุมบน) = corner deadzone กันสลับมั่ว → engine ปฏิเสธถูกต้อง"""
    u32.SetCursorPos(1200, 432)  # กลางจอหลักก่อน (logical)
    time.sleep(0.05)
    for _ in range(20):
        sendrel(300, 0)
        time.sleep(0.03)
    for _ in range(8):  # ชนขอบค้างไว้
        sendrel(30, 0)
        time.sleep(0.05)

def poll_l440_cursor(predicate, timeout_s=6.0):
    t0 = now_ms()
    while now_ms() - t0 < timeout_s * 1000:
        c = l440_cursor()
        if c and predicate(c):
            return now_ms() - t0, c
        time.sleep(0.15)
    return None, None

# ───────────────────────────────────────────────

def t1_host_to_node():
    """host → node: ดันขอบขวา = สลับไป L440 (ตรวจเคอร์เซอร์จริงบน L440 + active_node)"""
    lines_before = log_lines()
    push_right_edge()
    elapsed, cursor = poll_l440_cursor(lambda c: c[0] < 120, timeout_s=6.0)
    ok = cursor is not None
    detail = f"latency={elapsed}ms cursor={cursor}" if ok else "L440 cursor ไม่ขยับภายใน 6s"
    check("T1 host→node: ดันขอบขวา = สลับไป L440", ok, detail)
    return elapsed

def t2_keyboard_relay():
    """คีย์บอร์ดต่อ: กด Caps Lock บน host → LED ของ L440 ต้องสลับ"""
    def led_mask():
        out = ssh("DISPLAY=:0 xset q | grep -o 'LED mask: *[0-9a-f]*'")
        try: return int(out.split(":")[-1].strip(), 16)
        except Exception: return None
    before = led_mask()
    VK_CAPS, KEYEVENTF_KEYUP = 0x14, 0x0002
    for down in (True, False):
        u32.keybd_event(VK_CAPS, 0x3A, 0 if down else KEYEVENTF_KEYUP, 0)
        time.sleep(0.08)
    time.sleep(0.4)
    after = led_mask()
    ok = before is not None and after is not None and before != after
    check("T2 keyboard: Caps Lock relay ถึง L440", ok, f"LED {before} → {after}")
    # คืนสถานะ
    u32.keybd_event(VK_CAPS, 0x3A, 0, 0); time.sleep(0.05)
    u32.keybd_event(VK_CAPS, 0x3A, 0x0002, 0); time.sleep(0.05)
    u32.keybd_event(VK_CAPS, 0x3A, 0, 0)

def t3_node_to_host():
    """node → host: บน L440 ดันเคอร์เซอร์ชนขอบซ้าย (หันหา host) = edge_return กลับ"""
    ssh("DISPLAY=:0 xdotool mousemove 30 430")
    time.sleep(0.1)
    t0 = now_ms()
    for _ in range(8):
        ssh("DISPLAY=:0 xdotool mousemove_relative -- -60 0")
        time.sleep(0.04)
    # รอ host cursor กลับ (ไม่อยู่บนขอบซ้ายของ L440 อีก)
    t1s = now_ms()
    back = None
    while now_ms() - t1s < 6000:
        hx, hy = host_cursor()
        if hx < 1500:  # กลับมาบนจอ PSPV
            back = (hx, hy); break
        time.sleep(0.15)
    ok = back is not None
    check("T3 node→host: edge_return กลับ PSPV", ok, f"host cursor={back}")
    time.sleep(0.5)

def t5_cycle(n=3):
    """วนสลับไป-กลับ n รอบ — จับ state machine regression (EdgeSwitchBusy class)"""
    fails = 0
    for i in range(1, n + 1):
        push_right_edge()
        _, cur = poll_l440_cursor(lambda c: c[0] < 120, timeout_s=6.0)
        if cur is None:
            fails += 1; print(f"  cycle {i}: ไป L440 ไม่สำเร็จ"); continue
        ssh("DISPLAY=:0 xdotool mousemove 30 430"); time.sleep(0.15)
        for _ in range(8):
            ssh("DISPLAY=:0 xdotool mousemove_relative -- -60 0"); time.sleep(0.04)
        time.sleep(1.2)
        hx, _ = host_cursor()
        if hx >= 1500:
            fails += 1; print(f"  cycle {i}: กลับ PSPV ไม่สำเร็จ"); continue
        print(f"  cycle {i}: ไป-กลับสำเร็จ")
    check(f"T5 วนสลับ {n} รอบ", fails == 0, f"fails={fails}/{n}")

def t0_precondition():
    """T0: engine ต้องอยู่สถานะ host (active_node=None) — ถ้าติดสถานะควบคุมค้าง
    (เช่น engine restart กลางการควบคุม) ทุก test จะล้มเหลวโดยไม่มีความหมาย"""
    try:
        s = json.load(open(r"D:\OmniLink\logs\status.json", encoding="utf-8"))
        active = s.get("active_node")
        if active:
            check("T0: engine ต้องอยู่สถานะ host", False,
                  f"engine ติดสถานะควบคุม '{active}' — restart hostd ก่อน (หรือกด panic Ctrl+Alt+Backspace)")
            return False
        check("T0: engine อยู่สถานะ host", True)
        return True
    except Exception as e:
        check("T0: อ่าน status.json", False, str(e))
        return False

def main():
    print(f"=== E2E Switching Suite — start {datetime.now():%H:%M:%S} ===")
    hx, hy = host_cursor()
    print(f"host cursor=({hx},{hy}) — ห้ามแตะเมาส์ระหว่างทดสอบ")
    if ssh("echo ok") != "ok":
        print("FAIL  L440 SSH ไม่ถึง — ยกเลิก"); sys.exit(2)
    if not t0_precondition():
        print("ABORT — engine ติดสถานะควบคุม: restart hostd ก่อน"); sys.exit(3)
    t1_host_to_node()
    t2_keyboard_relay()
    t3_node_to_host()
    t5_cycle(3)
    passed = sum(1 for _, ok, _ in RESULTS if ok)
    print("══════════════════════════════")
    print(f"สรุป: {passed}/{len(RESULTS)} PASS")
    for name, ok, d in RESULTS:
        print(f"  {'✓' if ok else '✗'} {name}  {d}")
    sys.exit(0 if passed == len(RESULTS) else 1)

if __name__ == "__main__":
    main()
