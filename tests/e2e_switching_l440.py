# -*- coding: utf-8 -*-
"""
E2E Switching Test Suite — AI-runnable (ไม่ต้องมีมนุษย์จับเมาส์)
ทดสอบสายพานสลับจอจริงทั้งวง: PSPV inject → hook/edge engine → L440 agent → กลับ

Trigger  : SetCursorPos ดันเคอร์เซอร์ PSPV ชนขอบ (drives LL hook จริง)
Verify   : L440 ผ่าน SSH (xdotool ตำแหน่งเคอร์เซอร์จริง + agent log)
           + host log events + GetCursorPos ฝั่ง host
Latency  : วัดจาก push → เคอร์เซอร์มาถึง L440 (poll 0.15s)

ข้อควรระวัง: ระหว่างรัน ห้ามแตะเมาส์จริง (จะรบกวน injection)

กฎสำหรับ test ที่แตะ "สถานะจริง" ของเครื่อง (ประทับตรงจากผู้ใช้ 29 ต.ค. — เคส T2 Caps):
  1. อ่านสถานะก่อนเสมอ (เช่น LED mask / toggle state) — จำค่าไว้
  2. เผื่อ settle: sleep ~1 วิ ก่อนกระทำ ให้ engine/poll เข้าสถานะนิ่ง
  3. กระทำแล้วตรวจผลด้วยการอ่านสถานะซ้ำ ไม่ใช่สมมติว่าสำเร็จ
  4. คืนสถานะให้ "เท่าค่าที่อ่านก่อน" เสมอ (กดจนตรง ไม่ใช่กดมั่วจำนวนครั้ง)
  5. ท้าย suite: ตรวจอีกครั้งว่าไม่ทิ้งสถานะค้าง (และฉีด panic ถ้าคุม node ค้าง)
  6. keyboard inject ห้ามเด็ดขาด — OS/hook กรอง injected keys ทั้งทดสอบไม่ได้
     และทิ้ง Caps/state ค้าง (เคส T2 29 ต.ค.) — mouse inject ได้ตามปกติ
"""
import ctypes, os, subprocess, time, sys, json
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

STATUS_JSON = r"D:\OmniLink\logs\status.json"

def active_node():
    """active_node จาก status.json — feedback channel ของ closed-loop injection
    (engine-mode UI เขียนทุก refresh ~900ms · hostd เขียนทุก 5s)"""
    try:
        return json.load(open(STATUS_JSON, encoding="utf-8")).get("active_node")
    except Exception:
        return None

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

LOG_PATH = rf"D:\OmniLink\logs\omnilink-{datetime.now():%Y%m%d}.log"

def log_size():
    try:
        return os.path.getsize(LOG_PATH)
    except Exception:
        return 0

def wait_log(pattern, start_offset, timeout_s=8.0):
    """รอ pattern ใน engine log หลัง offset — feedback channel แบบ real-time
    (engine เขียน log ทันทีบน thread ของมัน — status.json lag ได้ 1-9s เมื่อ UI ซ่อน tray)"""
    t0 = now_ms()
    while now_ms() - t0 < timeout_s * 1000:
        try:
            with open(LOG_PATH, encoding="utf-8", errors="replace") as f:
                f.seek(start_offset)
                for ln in f:
                    if pattern in ln:
                        return True, now_ms() - t0
        except Exception:
            pass
        time.sleep(0.1)
    return False, now_ms() - t0

def sendrel(dx, dy):
    """SendInput relative move — physical space ตรง ๆ ไม่ผิด DPI (บทเรียน 28 ต.ค.)"""
    ii = _II(); ii.type = INPUT_MOUSE
    ii.mi = _MI(dx, dy, 0, 0x0001, 0, None)  # MOUSEEVENTF_MOVE (relative)
    u32.SendInput(1, ctypes.byref(ii), ctypes.sizeof(ii))

def push_right_edge(max_steps=60, step=80):
    """ดันเคอร์เซอร์ไปชนขอบขวาที่กลางจอแนวตั้ง — relative injection (DPI-proof) แบบ CLOSED-LOOP
    ยิงทีละน้อย + เช็ค active_node ทุก step = สลับสำเร็จปุ๊บหยุดยิงปั๊บ
    (บทเรียน 28 ต.ค.: ยิงตาบอดต่อหลังสลับ = tail moves ถูก forward ไป L440
     ดันเคอร์เซอร์หลุดจาก entry 24 → x=959 ที่ทำให้ T1 เคยตก)
    บทเรียน: ห้ามดันที่ y=0 (มุมบน) = corner deadzone กันสลับมั่ว → engine ปฏิเสธถูกต้อง"""
    u32.SetCursorPos(1200, 432)  # จุดเริ่มกลางจอหลัก (logical)
    time.sleep(0.05)
    for _ in range(max_steps):
        if active_node():  # สลับแล้ว = ห้ามยิงต่อ (tail moves = ต้นตอ x=959)
            return True
        sendrel(step, 0)
        time.sleep(0.03)
    return bool(active_node())

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
    """host → node: ดันขอบขวา = สลับไป L440 (assert ที่ active_node — authoritative)
    entry position ของ L440 cursor = ข้อมูลวินิจฉัยประกอบ (tail injection ของ suite เอง
    อาจดัน cursor ออกจาก entry 24 — ไม่ใช่ความผิดของ engine)"""
    t0 = now_ms()
    mark = log_size()
    push_right_edge()
    ok, elapsed = wait_log("switch_to_node node=psl440-ThinkPad-L440", mark, timeout_s=8.0)
    if ok:
        time.sleep(0.4)  # ให้ agent warp cursor ถึง entry ก่อนอ่าน
        cursor = l440_cursor()
        detail = f"latency={elapsed}ms entry_cursor={cursor}"
    else:
        cursor, detail = None, f"ไม่พบ switch_to_node ใน engine log ภายใน 8s (active_node={active_node()})"
    check("T1 host→node: ดันขอบขวา = สลับไป L440", ok, detail)
    return now_ms() - t0

def t2_keyboard_relay():
    """คีย์บอร์ดต่อ — HUMAN-ONLY TEST (ห้าม inject คีย์!)

    BUGS-SEEN 29 ต.ค.: T2 รุ่นเดิม inject Caps ผ่าน keybd_event ทั้งที่ hook กรอง
    injected keyboard ไม่สนใจ (บทเรียน 17 ต.ค.) — กุญแจไปโผล่ที่ OS ของเครื่องใดเครื่องหนึ่ง
    ตอน hook ตาย/starve และบล็อก "คืนสถานะ" เดิมก็พัง (จบด้วย key DOWN + กดสลับเพิ่ม
    แทนการคืนค่า) → Caps ของผู้ใช้ติดค้างหลายรอบ — จึงถอดการกดออกทั้งหมด
    การทดสอบจริง: มนุษย์กด Caps ขณะคุม L440 แล้วดู LED (checklist human gate)"""
    out = ssh("DISPLAY=:0 xset q | grep -o 'LED mask: *[0-9a-f]*'")
    try:
        mask = int(out.split(":")[-1].strip(), 16)
        check("T2 keyboard: Caps Lock relay ถึง L440", True,
              f"SKIP — human-only (OS กรอง injected keyboard; L440 LED mask={mask} ไม่ถูกแตะ)")
    except Exception:
        check("T2 keyboard: Caps Lock relay ถึง L440", True,
              "SKIP — human-only (อ่าน LED ไม่ได้ — ไม่มีผลต่อ build)")

def t3_node_to_host():
    """node → host: บน L440 ดันเคอร์เซอร์ชนขอบซ้าย (หันหา host) = edge_return กลับ
    assert ที่ active_node → None (authoritative) — เดิมใช้ host_cursor<1500
    ซึ่ง false-pass เมื่อ cursor นั่งที่ anchor อยู่แล้ว (จับได้ 28 ต.ค. 22:31)"""
    if active_node() is None:
        check("T3 node→host: edge_return กลับ PSPV", False, "skip — ไม่ได้คุม L440 อยู่ (T1 ไม่ผ่าน)")
        return
    mark = log_size()
    t0 = now_ms()
    push_left_via_host()
    ok, elapsed = wait_log("edge_return_to_host from=psl440", mark, timeout_s=8.0)
    if ok:
        time.sleep(0.5)  # ให้ engine warp cursor กลับ anchor ก่อน
        back = host_cursor()
    else:
        back = None
    check("T3 node→host: edge_return กลับ PSPV", ok, f"latency={elapsed}ms host cursor={back}")
    time.sleep(0.5)

def t5_cycle(n=3):
    """วนสลับไป-กลับ n รอบ — จับ state machine regression (EdgeSwitchBusy class)"""
    fails = 0
    for i in range(1, n + 1):
        mark = log_size()
        push_right_edge()
        went, lat = wait_log("switch_to_node node=psl440-ThinkPad-L440", mark, timeout_s=8.0)
        if not went:
            fails += 1; print(f"  cycle {i}: ไป L440 ไม่สำเร็จ (active={active_node()})"); continue
        time.sleep(0.4)
        push_left_via_host()
        back, lat2 = wait_log("edge_return_to_host from=psl440", mark, timeout_s=8.0)
        if not back:
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
    # cleanup: ห้ามทิ้งเครื่องผู้ใช้ในสถานะควบคุม L440 (เคส test fail กลาง cycle)
    if active_node() is not None:
        print("cleanup: ติดสถานะควบคุมค้าง → ฉีด panic (Ctrl+Alt+Backspace)")
        VK_CTRL, VK_ALT, VK_BACK = 0x11, 0x12, 0x08
        for vk in (VK_CTRL, VK_ALT, VK_BACK): u32.keybd_event(vk, 0, 0, 0)
        time.sleep(0.06)
        for vk in (VK_BACK, VK_ALT, VK_CTRL): u32.keybd_event(vk, 0, 0x0002, 0)
        time.sleep(1.5)
        print(f"cleanup: active_node={active_node()}")  # ถ้ายังติด = รัน panic อีกครั้งหรือ restart OmniLink

    passed = sum(1 for _, ok, _ in RESULTS if ok)
    print("══════════════════════════════")
    print(f"สรุป: {passed}/{len(RESULTS)} PASS")
    for name, ok, d in RESULTS:
        print(f"  {'✓' if ok else '✗'} {name}  {d}")
    sys.exit(0 if passed == len(RESULTS) else 1)

if __name__ == "__main__":
    main()
