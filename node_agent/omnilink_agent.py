#!/usr/bin/env python3
"""
OmniLink Node Agent (omnilink_agent.py)
Single-file, zero-external-dependency node agent — runs on Linux (X11) and Windows (Vista+/Win7+).

Features:
1. Ultra-low latency X11 input injection via ctypes (libX11.so.6 + libXtst.so.6)
2. Instant Hardware ScanCode -> Local Layout translation + XkbLockGroup(0=EN, 1=TH) calibration
3. Stateless Modifier Bitmask Synchronization + Auto-Release on focus leave (zero stuck Ctrl/Alt/Shift/Super)
4. Edge Detection: smoothly returns cursor control back to the Host when cursor hits configured screen edge
5. Integrated GStreamer RTP/RTCP Audio Bridge Manager (GhostSink playback & VirtualMic receiver) with zero disk-bloat logging
6. Live Telemetry (CPU%, RAM%, Active XKB Language Group, Audio Stream PIDs)
"""

import argparse
import ctypes
import hashlib
import hmac
import ipaddress
import json
import os
import signal
import socket
import ssl
import struct
import subprocess
import sys
import threading
import time

# v2.0 เฟส 5: node agent รันได้ทั้ง Linux (X11) และ Windows (Vista+/Win7+ ผ่าน ctypes)
IS_WINDOWS = sys.platform.startswith("win")

# B2: allowlist subnet (โหลดก่อน log เพราะ log ต้องการ? ไม่ — โหลดหลัง log ได้ แต่ต้องก่อน start server)
_ALLOWED_SUBNETS: list = []

# ─── P2: identity + event logging (บทเรียนจากเหตุการณ์ 2026-10-17: ไม่มี log = วินิจฉัยตาบอด) ───
AGENT_VERSION = "2.0.0"
_LOG_PATH = os.path.expanduser("~/omnilink/agent.log")
_log_lock = threading.Lock()


def log(msg: str):
    line = f"{time.strftime('%Y-%m-%d %H:%M:%S')} [agent {AGENT_VERSION}] {msg}"
    with _log_lock:
        try:
            os.makedirs(os.path.dirname(_LOG_PATH), exist_ok=True)
            # A4: rotate ที่ 2MB เหลือสำรอง 1 ไฟล์ -- กัน log โตไม่จำกัด
            try:
                if os.path.getsize(_LOG_PATH) > 2_000_000:
                    os.replace(_LOG_PATH, _LOG_PATH + ".1")
            except OSError:
                pass
            with open(_LOG_PATH, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except Exception:
            pass
    print(line)


# X11 modifier/event masks (สำหรับ hotkey passive grab)
X_KEYPRESS = 2
KEYPRESS_MASK = 1 << 0
SHIFT_MASK = 1 << 0
LOCK_MASK = 1 << 1
CONTROL_MASK = 1 << 2
MOD1_MASK = 1 << 3  # Alt
MOD2_MASK = 1 << 4  # NumLock
MOD5_MASK = 1 << 7

# P0: ต้องดันเมาส์ชนขอบสะสมเกินกี่ px จึงสลับกลับ (กันสลับเองตอนเมาส์แปะขอบเฉยๆ)
EDGE_PUSH_THRESHOLD_PX = 60.0


class _XEvent(ctypes.Structure):
    _fields_ = [("type", ctypes.c_int), ("pad", ctypes.c_ubyte * 512)]


def _load_token() -> str:
    """BOOT-ORDER FIX: token จาก env (ตอน deploy) หรือจากไฟล์ (ตอน autostart บนเครื่องลูก)
    ทำให้ agent เปิดเองตอนบูตเครื่องลูกได้แม้ host ยังไม่เปิด — ไม่ต้องพึ่ง SSH push"""
    tok = os.environ.get("OMNILINK_TOKEN", "")
    if tok:
        return tok
    try:
        p = os.path.expanduser("~/omnilink/auth_token")
        if os.path.exists(p):
            with open(p, "r", encoding="utf-8") as f:
                t = f.read().strip()
                if t:
                    return t
    except Exception:
        pass
    return ""


def _load_or_create_uuid() -> str:
    """อ่านหรือสร้าง Machine ID เฉพาะของเครื่องลูกข่าย (คงทนถาวร)"""
    p = os.path.expanduser("~/omnilink/machine_id")
    try:
        if os.path.exists(p):
            with open(p, "r", encoding="utf-8") as f:
                val = f.read().strip()
                if val:
                    return val
    except Exception:
        pass
    import uuid
    new_id = f"node-{str(uuid.uuid4())[:8]}"
    try:
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "w", encoding="utf-8") as f:
            f.write(new_id)
    except Exception:
        pass
    return new_id


def _load_allowlist() -> list:
    """B2: allowlist subnet ที่เชื่อใจ (CIDR คั่น comma) — อ่านจากไฟล์ ~/omnilink/allowed_subnets
    ก่อน (ทน reboot) แล้ว fallback เป็น env OMNILINK_ALLOW_SUBNETS · ว่าง = อนุญาตทุกที่ (พฤติกรรมเดิม)
    ตั้งแล้ว = TCP/UDP/discovery จากนอก subnet ถูกปฏิเสธก่อนแม้แต่ handshake"""
    raw = ""
    try:
        p = os.path.expanduser("~/omnilink/allowed_subnets")
        if os.path.exists(p):
            with open(p, "r", encoding="utf-8") as f:
                raw = f.read().strip()
    except Exception:
        pass
    if not raw:
        raw = os.environ.get("OMNILINK_ALLOW_SUBNETS", "")
    nets = []
    for part in raw.split(","):
        part = part.strip()
        if not part:
            continue
        try:
            nets.append(ipaddress.ip_network(part, strict=False))
        except ValueError:
            log(f"[allowlist] subnet ไม่ถูกต้อง ข้าม: {part}")
    if nets:
        log(f"[allowlist] active — อนุญาตเฉพาะ {', '.join(str(n) for n in nets)}")
    return nets


def _ip_allowed(ip: str) -> bool:
    if not _ALLOWED_SUBNETS:
        return True
    try:
        addr = ipaddress.ip_address(ip)
        return any(addr in net for net in _ALLOWED_SUBNETS)
    except ValueError:
        return False


def _write_secret(path: str, content: str):
    """v1.6 hardening (PY-F13): เขียนไฟล์ลับด้วยสิทธิ์ 0600 เสมอ — ห้ามให้ user อื่นอ่าน"""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    try:
        os.write(fd, content.encode("utf-8"))
    finally:
        os.close(fd)
    try:
        os.chmod(path, 0o600)
    except OSError:
        pass


def _generate_pin() -> str:
    """v1.6 hardening (PY-F1): PIN ต้องมาจาก CSPRNG — เลิกใช้ random.randint (Mersenne Twister ทำนายได้)"""
    import secrets
    return f"{secrets.randbelow(900) + 100}-{secrets.randbelow(900) + 100}"


def _load_or_create_tls():
    """v1.6 hardening: สร้าง/โหลด self-signed certificate สำหรับ TLS บน TCP 24801
    คืน (SSLContext|None, fingerprint_sha256_hex|None) — fail-closed: สร้างไม่ได้ = ไม่เปิดรับแบบ TLS"""
    cert_path = os.path.expanduser("~/omnilink/tls_cert.pem")
    key_path = os.path.expanduser("~/omnilink/tls_key.pem")
    try:
        if not (os.path.exists(cert_path) and os.path.exists(key_path)):
            res = subprocess.run(
                ["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                 "-keyout", key_path, "-out", cert_path, "-days", "3650",
                 "-subj", f"/CN={socket.gethostname()}"],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
            if res.returncode != 0:
                log("[tls] openssl สร้าง certificate ไม่สำเร็จ — agent จะไม่เปิดรับ TCP (fail-closed)")
                return None, None
        for p in (key_path, cert_path):
            try:
                os.chmod(p, 0o600)
            except OSError:
                pass
        # fingerprint = SHA-256 ของ DER — decode base64 จาก PEM เอง (ssl.PEM_cert_to_DER_cert
        # รับ str แต่คืน bytes กลับกันกับ intuition — เจอตอน live test 00:37)
        import base64
        with open(cert_path, "r", encoding="utf-8") as f:
            pem_lines = [ln for ln in f.read().splitlines() if "-----" not in ln]
        cert_der = base64.b64decode("".join(pem_lines))
        fingerprint = hashlib.sha256(cert_der).hexdigest()
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        ctx.load_cert_chain(cert_path, key_path)
        log(f"[tls] ready — cert fingerprint (SHA-256) = {fingerprint[:8].upper()}-{fingerprint[8:12].upper()}... โชว์คู่กับ PIN ตอนจับคู่")
        return ctx, fingerprint
    except Exception as ex:
        log(f"[tls] init failed: {ex} — agent จะไม่เปิดรับ TCP (fail-closed)")
        return None, None


def _load_or_create_pin() -> str:
    """อ่านหรือสร้างรหัสยืนยันตัวตน 6 หลัก (Pairing PIN: XXX-XXX)"""
    p = os.path.expanduser("~/omnilink/pairing_pin")
    try:
        if os.path.exists(p):
            with open(p, "r", encoding="utf-8") as f:
                val = f.read().strip()
                if val:
                    return val
    except Exception:
        pass
    new_pin = _generate_pin()
    try:
        _write_secret(p, new_pin)
    except Exception:
        pass
    return new_pin


# Protocol Constants
DISCOVERY_PORT = 24800
KVM_TCP_PORT = 24801
KVM_UDP_PORT = 24802

PKT_MOUSE_MOVE_REL = 0x01
PKT_MOUSE_MOVE_ABS = 0x02
PKT_MOUSE_BUTTON   = 0x03
PKT_MOUSE_WHEEL    = 0x04
PKT_KEY_EVENT      = 0x05
PKT_SET_LANG       = 0x06
PKT_RELEASE_ALL    = 0x07

# v1.7 C3: packet กลุ่ม "input" — สิ่งเดียวที่ policy ต่ำกว่า full ต้องห้าม
_INPUT_PKTS = (PKT_MOUSE_MOVE_REL, PKT_MOUSE_MOVE_ABS, PKT_MOUSE_BUTTON, PKT_MOUSE_WHEEL, PKT_KEY_EVENT)


def _packet_allowed(policy: str, pkt_type: int) -> bool:
    """C3 capability matrix ฝั่งรับ:
    full     = ทุกอย่าง
    clipboard = clipboard/ไฟล์ได้ แต่ห้าม input (mouse/key/set_lang)
    view      = ห้ามหมด ยกเว้น PKT_RELEASE_ALL (กันปุ่มค้าง — safety override เสมอ)
    (extracted เป็นฟังก์ชันเพื่อ unit test ได้ตรงๆ)"""
    if policy == "full":
        return True
    if pkt_type == PKT_RELEASE_ALL:
        return True
    if policy == "clipboard":
        return pkt_type not in _INPUT_PKTS and pkt_type != PKT_SET_LANG
    return False

MOD_LSHIFT = 1 << 0
MOD_RSHIFT = 1 << 1
MOD_LCTRL  = 1 << 2
MOD_RCTRL  = 1 << 3
MOD_LALT   = 1 << 4
MOD_RALT   = 1 << 5
MOD_LSUPER = 1 << 6
MOD_RSUPER = 1 << 7

MOD_BIT_TO_X11_KEYCODE = [
    (MOD_LSHIFT, 50),
    (MOD_RSHIFT, 62),
    (MOD_LCTRL,  37),
    (MOD_RCTRL,  105),
    (MOD_LALT,   64),
    (MOD_RALT,   108),
    (MOD_LSUPER, 133),
    (MOD_RSUPER, 134),
]


class XkbStateRec(ctypes.Structure):
    _fields_ = [
        ("group", ctypes.c_ubyte),
        ("locked_group", ctypes.c_ubyte),
        ("base_group", ctypes.c_ushort),
        ("latched_group", ctypes.c_ushort),
        ("mods", ctypes.c_ubyte),
        ("base_mods", ctypes.c_ubyte),
        ("latched_mods", ctypes.c_ubyte),
        ("locked_mods", ctypes.c_ubyte),
        ("compat_state", ctypes.c_ubyte),
        ("grab_mods", ctypes.c_ubyte),
        ("compat_grab_mods", ctypes.c_ubyte),
        ("lookup_mods", ctypes.c_ubyte),
        ("compat_lookup_mods", ctypes.c_ubyte),
        ("ptr_buttons", ctypes.c_ushort),
    ]


class X11Controller:
    """Direct ctypes wrapper around libX11.so.6 and libXtst.so.6 for sub-millisecond input & XKB control."""

    def __init__(self, display_name: str = ":0"):
        self.lock = threading.Lock()
        self.display_name = display_name.encode("utf-8")
        os.environ.setdefault("DISPLAY", display_name)
        if hasattr(os, "getuid"):
            os.environ.setdefault("DBUS_SESSION_BUS_ADDRESS", f"unix:path=/run/user/{os.getuid()}/bus")
        self.x11 = ctypes.cdll.LoadLibrary("libX11.so.6")
        self.xtst = ctypes.cdll.LoadLibrary("libXtst.so.6")

        self.x11.XInitThreads()
        self.x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
        self.x11.XOpenDisplay.restype = ctypes.c_void_p
        self.x11.XCloseDisplay.argtypes = [ctypes.c_void_p]

        self.dpy = self.x11.XOpenDisplay(self.display_name)
        if not self.dpy:
            self.dpy = self.x11.XOpenDisplay(None)
        if not self.dpy:
            raise RuntimeError(f"Unable to open X11 Display {display_name}")

        self.x11.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
        self.x11.XDefaultRootWindow.restype = ctypes.c_ulong
        self.root = self.x11.XDefaultRootWindow(self.dpy)

        self.x11.XDisplayWidth.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.x11.XDisplayWidth.restype = ctypes.c_int
        self.x11.XDisplayHeight.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.x11.XDisplayHeight.restype = ctypes.c_int

        self.x11.XFlush.argtypes = [ctypes.c_void_p]
        self.x11.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]

        # XTest functions
        self.xtst.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
        self.xtst.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
        self.xtst.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
        self.xtst.XTestFakeRelativeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]

        # XKB functions
        self.x11.XkbUseExtension.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int)]
        self.x11.XkbUseExtension.restype = ctypes.c_int
        self.x11.XkbLockGroup.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint]
        self.x11.XkbGetState.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.POINTER(XkbStateRec)]
        self.x11.XkbKeycodeToKeysym.argtypes = [ctypes.c_void_p, ctypes.c_ubyte, ctypes.c_int, ctypes.c_int]
        self.x11.XkbKeycodeToKeysym.restype = ctypes.c_ulong

        # P0: passive-grab functions สำหรับ emergency hotkey
        self.x11.XGrabKey.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_uint, ctypes.c_ulong, ctypes.c_int, ctypes.c_int, ctypes.c_int]
        self.x11.XSelectInput.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_long]
        self.x11.XNextEvent.argtypes = [ctypes.c_void_p, ctypes.POINTER(_XEvent)]
        self.x11.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]

        maj = ctypes.c_int(1)
        min_v = ctypes.c_int(0)
        self.x11.XkbUseExtension(self.dpy, ctypes.byref(maj), ctypes.byref(min_v))

        self.ibus_bus = None
        try:
            import gi
            gi.require_version("IBus", "1.0")
            from gi.repository import IBus
            self.ibus_bus = IBus.Bus()
        except Exception:
            self.ibus_bus = None

        # XQueryPointer
        self.x11.XQueryPointer.argtypes = [
            ctypes.c_void_p, ctypes.c_ulong,
            ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_ulong),
            ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_uint),
        ]

        self.width = self.x11.XDisplayWidth(self.dpy, 0)
        self.height = self.x11.XDisplayHeight(self.dpy, 0)
        self.cur_x = self.width // 2
        self.cur_y = self.height // 2
        self.held_keys = set()
        self.held_buttons = set()

    def refresh_dimensions(self):
        with self.lock:
            self.width = self.x11.XDisplayWidth(self.dpy, 0)
            self.height = self.x11.XDisplayHeight(self.dpy, 0)

    def query_pointer(self):
        with self.lock:
            ret_root = ctypes.c_ulong()
            ret_child = ctypes.c_ulong()
            rx = ctypes.c_int()
            ry = ctypes.c_int()
            wx = ctypes.c_int()
            wy = ctypes.c_int()
            mask = ctypes.c_uint()
            self.x11.XQueryPointer(
                self.dpy, self.root,
                ctypes.byref(ret_root), ctypes.byref(ret_child),
                ctypes.byref(rx), ctypes.byref(ry),
                ctypes.byref(wx), ctypes.byref(wy),
                ctypes.byref(mask),
            )
            self.cur_x = rx.value
            self.cur_y = ry.value
            return rx.value, ry.value

    def move_abs(self, x: int, y: int):
        with self.lock:
            x = max(0, min(self.width - 1, int(x)))
            y = max(0, min(self.height - 1, int(y)))
            self.cur_x = x
            self.cur_y = y
            self.xtst.XTestFakeMotionEvent(self.dpy, -1, x, y, 0)
            self.x11.XFlush(self.dpy)

    def move_rel(self, dx: int, dy: int):
        with self.lock:
            new_x = self.cur_x + int(dx)
            new_y = self.cur_y + int(dy)
            clamped_x = max(0, min(self.width - 1, new_x))
            clamped_y = max(0, min(self.height - 1, new_y))
            self.cur_x = clamped_x
            self.cur_y = clamped_y
            self.xtst.XTestFakeMotionEvent(self.dpy, -1, clamped_x, clamped_y, 0)
            self.x11.XFlush(self.dpy)
            return new_x, new_y

    def mouse_button(self, button: int, down: bool):
        with self.lock:
            if down:
                self.held_buttons.add(button)
            else:
                self.held_buttons.discard(button)
            self.xtst.XTestFakeButtonEvent(self.dpy, button, 1 if down else 0, 0)
            self.x11.XFlush(self.dpy)

    def mouse_wheel(self, wx: int, wy: int):
        with self.lock:
            # Vertical scroll: button 4 (up), button 5 (down)
            if wy != 0:
                btn = 4 if wy > 0 else 5
                steps = max(1, min(10, abs(wy) // 120 or 1))
                for _ in range(steps):
                    self.xtst.XTestFakeButtonEvent(self.dpy, btn, 1, 0)
                    self.xtst.XTestFakeButtonEvent(self.dpy, btn, 0, 0)
            # Horizontal scroll: button 6 (left), button 7 (right)
            if wx != 0:
                btn = 7 if wx > 0 else 6
                steps = max(1, min(10, abs(wx) // 120 or 1))
                for _ in range(steps):
                    self.xtst.XTestFakeButtonEvent(self.dpy, btn, 1, 0)
                    self.xtst.XTestFakeButtonEvent(self.dpy, btn, 0, 0)
            self.x11.XFlush(self.dpy)

    def key_event(self, keycode: int, down: bool):
        if keycode < 8 or keycode > 255:
            return
        with self.lock:
            if down:
                self.held_keys.add(keycode)
            else:
                self.held_keys.discard(keycode)
            self.xtst.XTestFakeKeyEvent(self.dpy, keycode, 1 if down else 0, 0)
            self.x11.XFlush(self.dpy)

    def sync_modifiers(self, mod_mask: int):
        """Ensures modifier keys never get stuck even if a packet was dropped."""
        with self.lock:
            flushed = False
            for bit, keycode in MOD_BIT_TO_X11_KEYCODE:
                should_be_down = bool(mod_mask & bit)
                is_down = keycode in self.held_keys
                if not should_be_down and is_down:
                    self.xtst.XTestFakeKeyEvent(self.dpy, keycode, 0, 0)
                    self.held_keys.discard(keycode)
                    flushed = True
            if flushed:
                self.x11.XFlush(self.dpy)

    def release_all(self):
        with self.lock:
            for kc in list(self.held_keys):
                self.xtst.XTestFakeKeyEvent(self.dpy, kc, 0, 0)
            self.held_keys.clear()
            for _, kc in MOD_BIT_TO_X11_KEYCODE:
                self.xtst.XTestFakeKeyEvent(self.dpy, kc, 0, 0)
            for btn in list(self.held_buttons):
                self.xtst.XTestFakeButtonEvent(self.dpy, btn, 0, 0)
            self.held_buttons.clear()
            self.x11.XFlush(self.dpy)

    def get_lang_group(self) -> int:
        """Hardware-verified check: queries X11 server keymap for physical key 38 ('a' vs Thai 'ฟ' 0xdbf).
        F4: ใช้ display connection ที่เปิดค้างไว้ (XInitThreads + lock แล้ว) — ไม่เปิดใหม่ทุกครั้งอีก"""
        with self.lock:
            st = XkbStateRec()
            self.x11.XkbGetState(self.dpy, 0x0100, ctypes.byref(st))
            ks = self.x11.XkbKeycodeToKeysym(self.dpy, 38, st.group, 0)
            return 1 if ks == 0x0DBF else 0

    def lock_group(self, group: int) -> bool:
        """F5/P1: สลับ group แบบ in-place ผ่าน XkbLockGroup (เทียบเท่า xkb-switch)
        ไม่ไปทำลาย options/variant ที่ผู้ใช้ตั้งไว้ เหมาะกับการเรียกบ่อยๆ"""
        with self.lock:
            target = 1 if group == 1 else 0
            self.x11.XkbLockGroup(self.dpy, 0x0100, target)
            self.x11.XSync(self.dpy, 0)
        return self.get_lang_group() == (1 if group == 1 else 0)

    def _ensure_two_groups(self):
        """ตรวจว่า keymap มี 2 group (us,th) — ถ้าไม่มีค่อยตั้งด้วย setxkbmap ครั้งเดียว"""
        try:
            q = subprocess.run(["setxkbmap", "-query"], capture_output=True, text=True, timeout=2)
            layouts = ""
            for line in q.stdout.splitlines():
                if line.startswith("layout:"):
                    layouts = line.split(":", 1)[1].strip()
            if layouts not in ("us,th", "th,us"):
                subprocess.run(["setxkbmap", "-layout", "us,th"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=2, check=False)
                log(f"[lang] ตั้ง keymap us,th (เดิม: '{layouts or '?'}')")
        except Exception as ex:
            log(f"[lang] ensure groups failed: {ex}")

    def _sync_desktop_lang(self, target: int):
        """จัดเรียง GNOME indicator ให้ตรงกับ group จริง (gsettings input-sources current)"""
        try:
            r = subprocess.run(["gsettings", "get", "org.gnome.desktop.input-sources", "sources"],
                               capture_output=True, text=True, timeout=2)
            srcs = r.stdout.strip()
            want = "('xkb', 'th')" if target == 1 else "('xkb', 'us')"
            idx = srcs.find(want)
            if idx >= 0:
                pos = srcs[:idx].count("('xkb'")
                subprocess.run(["gsettings", "set", "org.gnome.desktop.input-sources", "current", str(pos)],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=2, check=False)
        except Exception:
            pass

    def _native_toggle_lang(self):
        """INCIDENT lang-final-mile: กด Super+Space "จริง" ผ่าน XTest — ให้ GNOME Shell จัดการเองทั้งหมด
        (indicator, per-window source, IBus engine, XKB group) เหมือนผู้ใช้กดเองบนเครื่องนี้"""
        try:
            with self.lock:
                self.xtst.XTestFakeKeyEvent(self.dpy, 133, 1, 0)  # Super_L down
                self.xtst.XTestFakeKeyEvent(self.dpy, 65, 1, 0)   # Space down
                self.xtst.XTestFakeKeyEvent(self.dpy, 65, 0, 0)   # Space up
                self.xtst.XTestFakeKeyEvent(self.dpy, 133, 0, 0)  # Super_L up
                self.x11.XFlush(self.dpy)
            return True
        except Exception as ex:
            log("[lang] native toggle ล้มเหลว: " + str(ex))
            return False

    def set_lang_group(self, group: int) -> int:
        """เส้นทางหลักใหม่: ถ้าสถานะปัจจุบันไม่ตรง target → กด Super+Space จริง (GNOME จัดการเอง)
        แล้วรอยืนยัน ≤0.8s — ถ้าไม่ถึงค่อยใช้ชุดตรง (IBus+XkbLockGroup+gsettings) ตามเดิม
        เหตุผล: ชุดตรงเปลี่ยน global state แต่ GNOME อาจมี per-window source ดันกลับ = ผู้ใช้พิมพ์ภาษาเดิม"""
        target = 1 if group == 1 else 0
        now = time.monotonic()
        if getattr(self, "_last_set_group", -1) == target and (now - getattr(self, "_last_set_time", 0.0)) < 0.15:
            return target
        self._last_set_group = target
        self._last_set_time = now

        if self.get_lang_group() != target:
            method = "native-super-space"
            for attempt in range(2):  # 2 sources = 1 toggle พอ; กันเผื่อพลาด
                self._native_toggle_lang()
                deadline = time.monotonic() + 0.45
                while time.monotonic() < deadline:
                    time.sleep(0.05)
                    if self.get_lang_group() == target:
                        break
                if self.get_lang_group() == target:
                    break
            ok = self.get_lang_group() == target
            log(f"[lang] set → {'TH' if target else 'EN'} via {method} ok={ok}")
            if ok:
                return target
            # ไม่ถึง → ไปชุดตรงด้านล่าง

        # ── ชุดตรง (เดิม) ──
        engine_name = "xkb:th::tha" if target == 1 else "xkb:us::eng"
        method = "ibus+xkb-lockgroup"

        # ชั้น 1: IBus engine — สิ่งที่แอป GNOME ใช้พิมพ์จริง (ต้องมาก่อน!)
        if self.ibus_bus is not None:
            try:
                self.ibus_bus.set_global_engine(engine_name)
            except Exception:
                pass

        # ชั้น 2+3: raw XKB group + GNOME indicator
        self.lock_group(target)
        self._sync_desktop_lang(target)
        ok = self.get_lang_group() == target

        if not ok:
            method = "prepare+lockgroup"
            self._ensure_two_groups()
            if self.ibus_bus is not None:
                try:
                    self.ibus_bus.set_global_engine(engine_name)
                except Exception:
                    pass
            self.lock_group(target)
            self._sync_desktop_lang(target)
            ok = self.get_lang_group() == target

        if not ok:
            # ทางสุดท้าย (เดิม) — หนักและรีเซ็ต options แต่ยังใช้ได้เสมอ
            method = "ibus+setxkbmap"
            try:
                if self.ibus_bus is not None:
                    self.ibus_bus.set_global_engine(engine_name)
            except Exception:
                pass
            subprocess.run(["setxkbmap", "-layout", "th" if target == 1 else "us,th"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=2, check=False)
            self._sync_desktop_lang(target)

        actual = self.get_lang_group()
        # รายงาน engine จริงกลับด้วย — ทำให้ log พอสรุปได้ว่า "พิมพิ์ได้จริง" ไม่ใช่แค่ XKB สวย
        eng_now = "?"
        if self.ibus_bus is not None:
            try:
                eng_now = self.ibus_bus.get_global_engine().get_name()
            except Exception:
                eng_now = "(query fail)"
        log(f"[lang] set → {'TH' if target else 'EN'} via {method} xkb={actual == target} ibus={eng_now}")
        return actual

    def start_escape_hotkey(self, on_trigger):
        """P0: hotkey ฉุกเฉินบนคีย์บอร์ด "จริง" ของเครื่องนี้ (Ctrl+Alt+Shift+Q)
        บทเรียนจาก input-leap/barrier: เครื่องที่ถูกกักเมาส์ต้องหนีได้ด้วยอินพุตของตัวเองเสมอ"""
        t = threading.Thread(target=self._hotkey_loop, args=(on_trigger,), daemon=True, name="escape-hotkey")
        t.start()
        return t

    def _hotkey_loop(self, on_trigger):
        d = None
        try:
            d = self.x11.XOpenDisplay(self.display_name) or self.x11.XOpenDisplay(None)
            if not d:
                log("[hotkey] เปิด display ไม่ได้ — hotkey ปิดใช้งาน")
                return
            root = self.x11.XDefaultRootWindow(d)
            mods = CONTROL_MASK | MOD1_MASK | SHIFT_MASK  # Ctrl+Alt+Shift
            grabbed = 0
            for extra in (0, LOCK_MASK, MOD2_MASK, LOCK_MASK | MOD2_MASK,
                          MOD5_MASK, MOD5_MASK | LOCK_MASK,
                          MOD5_MASK | MOD2_MASK, MOD5_MASK | LOCK_MASK | MOD2_MASK):
                self.x11.XGrabKey(d, 24, mods | extra, root, 0, 1, 1)  # keycode 24 = Q (evdev)
                grabbed += 1
            self.x11.XSelectInput(d, root, KEYPRESS_MASK)
            self.x11.XSync(d, 0)
            log(f"[hotkey] Ctrl+Alt+Shift+Q พร้อมทำงาน ({grabbed} modifier combos, keycode 24)")
            ev = _XEvent()
            while True:
                self.x11.XNextEvent(d, ctypes.byref(ev))
                if ev.type == X_KEYPRESS:
                    log("[hotkey] ฉุกเฉิน! สั่งคืนเมาส์ไปเครื่องหลัก")
                    try:
                        on_trigger()
                    except Exception as ex:
                        log(f"[hotkey] trigger error: {ex}")
        except Exception as ex:
            log(f"[hotkey] loop หยุด: {ex}")
        finally:
            if d:
                try:
                    self.x11.XCloseDisplay(d)
                except Exception:
                    pass


# ═══════════════════════════════════════════════════════════════════════════
# v2.0 เฟส 5: Windows platform layer (ctypes ล้วน — ทำงานบน Vista/Win7+,
# ไม่ต้องลงอะไรเพิ่มนอกจาก Python 3.8+) — surface ตรงกับ X11Controller ทุกเมธอด
# ที่ OmniLinkAgentServer ใช้ (แยกที่ __init__ ของ server ด้วย IS_WINDOWS)
# ═══════════════════════════════════════════════════════════════════════════

# X11 keycode → (VK, extended) — inverse ของ Win32ToX11Keycode ฝั่ง host (แก้ sync พร้อมกันเสมอ)
_X11KC_TO_VK = {
    50: (0xA0, False), 62: (0xA1, False), 37: (0xA2, False), 105: (0xA3, True),
    64: (0xA4, False), 108: (0xA5, True), 133: (0x5B, False), 134: (0x5C, False),
    127: (0x13, False), 77: (0x90, False), 78: (0x91, False),
    121: (0xAD, False), 122: (0xAE, False), 123: (0xAF, False),
    104: (0x0D, True), 106: (0x6F, True), 107: (0x2A, True),
    110: (0x24, True), 111: (0x26, True), 112: (0x21, True), 113: (0x25, True),
    114: (0x27, True), 115: (0x23, True), 116: (0x28, True), 117: (0x22, True),
    118: (0x2D, True), 119: (0x2E, True), 135: (0x5D, False),
}

# (bit ของ OmniProtocol.Mod*, VK ปล่อยตอน sync) — MOD_* ของไฟล์นี้ใช้ค่า bit เดียวกับ host
_MOD_BIT_TO_WIN_VK = [
    (MOD_LSHIFT, 0xA0), (MOD_RSHIFT, 0xA1), (MOD_LCTRL, 0xA2), (MOD_RCTRL, 0xA3),
    (MOD_LALT, 0xA4), (MOD_RALT, 0xA5), (MOD_LSUPER, 0x5B), (MOD_RSUPER, 0x5C),
]


class Win32Controller:
    """Direct ctypes wrapper around user32 for the node agent on Windows —
    surface เทียบเท่า X11Controller: move_abs/move_rel/mouse_button/mouse_wheel/
    key_event/sync_modifiers/release_all/get_lang_group/set_lang_group/query_pointer/
    refresh_dimensions/start_escape_hotkey + width/height/cur_x/cur_y"""

    def __init__(self, display_name=None):  # display_name ส่งมาจาก server — ฝั่ง Windows ไม่ใช้
        self.user32 = ctypes.windll.user32
        self.width = self.user32.GetSystemMetrics(0)
        self.height = self.user32.GetSystemMetrics(1)
        self.cur_x = self.width // 2
        self.cur_y = self.height // 2
        self.held_keys = set()
        self.held_buttons = set()

    def refresh_dimensions(self):
        self.width = self.user32.GetSystemMetrics(0)
        self.height = self.user32.GetSystemMetrics(1)

    def query_pointer(self):
        class P(ctypes.Structure):
            _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]
        pt = P()
        if self.user32.GetCursorPos(ctypes.byref(pt)):
            self.cur_x, self.cur_y = pt.x, pt.y
        return self.cur_x, self.cur_y

    def _move_abs_px(self, x, y):
        # mouse_event absolute: พิกัด 0..65535 เทียบจอหลัก (กลไกเดียวกับ host ฝั่ง Windows)
        w = max(1, self.width - 1)
        h = max(1, self.height - 1)
        nx = int(max(0, min(x, self.width - 1)) * 65535 / w)
        ny = int(max(0, min(y, self.height - 1)) * 65535 / h)
        self.user32.mouse_event(0x8001, nx, ny, 0, 0)  # ABSOLUTE|MOVE

    def move_abs(self, x, y):
        x = max(0, min(self.width - 1, int(x)))
        y = max(0, min(self.height - 1, int(y)))
        self.cur_x, self.cur_y = x, y
        self._move_abs_px(x, y)

    def move_rel(self, dx, dy):
        new_x = self.cur_x + int(dx)
        new_y = self.cur_y + int(dy)
        cx = max(0, min(self.width - 1, new_x))
        cy = max(0, min(self.height - 1, new_y))
        self.cur_x, self.cur_y = cx, cy
        self._move_abs_px(cx, cy)
        return new_x, new_y

    def mouse_button(self, button, down):
        flags, data = {
            1: (0x0002 if down else 0x0004, 0),
            2: (0x0020 if down else 0x0040, 0),
            3: (0x0008 if down else 0x0010, 0),
            8: (0x0080 if down else 0x0100, 1),
            9: (0x0080 if down else 0x0100, 2),
        }.get(button, (0, 0))
        if flags == 0:
            return
        if down:
            self.held_buttons.add(button)
        else:
            self.held_buttons.discard(button)
        self.user32.mouse_event(flags, 0, 0, data, 0)

    def mouse_wheel(self, wx, wy):
        if wy != 0:
            self.user32.mouse_event(0x0800, 0, 0, wy & 0xFFFFFFFF, 0)   # WHEEL
        if wx != 0:
            self.user32.mouse_event(0x1000, 0, 0, wx & 0xFFFFFFFF, 0)   # HWHEEL

    def keycode_to_vk(self, keycode):
        if keycode in _X11KC_TO_VK:
            return _X11KC_TO_VK[keycode]
        scan = keycode - 8
        if 1 <= scan <= 88:
            vk = self.user32.MapVirtualKeyW(scan, 1)  # MAPVK_VSC_TO_VK
            return (vk, False) if vk else (0, False)
        return (0, False)

    def key_event(self, keycode, down):
        if keycode < 8 or keycode > 255:
            return
        vk, extended = self.keycode_to_vk(keycode)
        if not vk:
            return
        if down:
            self.held_keys.add(keycode)
        else:
            self.held_keys.discard(keycode)
        flags = 0x0001 if extended else 0     # KEYEVENTF_EXTENDEDKEY
        if not down:
            flags |= 0x0002                    # KEYEVENTF_KEYUP
        self.user32.keybd_event(vk, 0, flags, 0)

    def sync_modifiers(self, mod_mask):
        held_vks = {self.keycode_to_vk(kc)[0] for kc in self.held_keys}
        for bit, vk in _MOD_BIT_TO_WIN_VK:
            should = bool(mod_mask & bit)
            if not should and vk in held_vks:
                self.user32.keybd_event(vk, 0, 0x0002, 0)  # KEYEVENTF_KEYUP

    def release_all(self):
        for kc in list(self.held_keys):
            vk, ext = self.keycode_to_vk(kc)
            if vk:
                self.user32.keybd_event(vk, 0, (0x0001 if ext else 0) | 0x0002, 0)
        self.held_keys.clear()
        for _, vk in _MOD_BIT_TO_WIN_VK:
            self.user32.keybd_event(vk, 0, 0x0002, 0)
        for btn in list(self.held_buttons):
            self.mouse_button(btn, False)
        self.held_buttons.clear()

    def get_lang_group(self):
        hkl = self.user32.GetKeyboardLayout(0)
        return 1 if (hkl & 0xFFFF) == 0x041E else 0

    def set_lang_group(self, group):
        target = 0x041E041E if group == 1 else 0x04090409
        hwnd = self.user32.GetForegroundWindow()
        if hwnd:
            self.user32.PostMessageW(hwnd, 0x0050, 0, target)  # WM_INPUTLANGCHANGEREQUEST
        return self.get_lang_group()

    def start_escape_hotkey(self, on_trigger):
        t = threading.Thread(target=self._hotkey_loop, args=(on_trigger,), daemon=True, name="escape-hotkey-win")
        t.start()
        return t

    def _hotkey_loop(self, on_trigger):
        MOD_ALT, MOD_CONTROL, MOD_SHIFT = 0x1, 0x2, 0x4
        WM_HOTKEY = 0x0312
        HOTKEY_ID = 0x4F4D
        if not self.user32.RegisterHotKey(None, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_SHIFT, 0x51):
            log("[hotkey] RegisterHotKey(Ctrl+Alt+Shift+Q) ไม่สำเร็จ")
            return
        log("[hotkey] Ctrl+Alt+Shift+Q พร้อม (RegisterHotKey)")

        class MSG(ctypes.Structure):
            _fields_ = [("hwnd", ctypes.c_void_p), ("message", ctypes.c_uint), ("wParam", ctypes.c_void_p),
                        ("lParam", ctypes.c_void_p), ("time", ctypes.c_uint), ("ptx", ctypes.c_long), ("pty", ctypes.c_long)]

        msg = MSG()
        while True:
            r = self.user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
            if r <= 0:
                break
            if msg.message == WM_HOTKEY and msg.wParam == HOTKEY_ID:
                log("[hotkey] ฉุกเฉิน! สั่งคืนเมาส์ไปเครื่องหลัก")
                try:
                    on_trigger()
                except Exception as ex:
                    log(f"[hotkey] trigger error: {ex}")
        self.user32.UnregisterHotKey(None, HOTKEY_ID)


class Win32Clipboard:
    """Clipboard ผ่าน ctypes (CF_UNICODETEXT) — v1: text เท่านั้น (png/files เฟสถัดไป)"""

    CF_UNICODETEXT = 13
    GMEM_MOVEABLE = 0x0002

    def __init__(self, osd=None):
        self.osd = osd
        self.user32 = ctypes.windll.user32
        self.kernel32 = ctypes.windll.kernel32
        # กับดัก ctypes บน x64: ไม่ประกาศ restype = c_void_p ค่า pointer จะโดนตัดเป็น c_int
        self.kernel32.GlobalAlloc.restype = ctypes.c_void_p
        self.kernel32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
        self.kernel32.GlobalLock.restype = ctypes.c_void_p
        self.kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
        self.kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
        self.user32.GetClipboardData.restype = ctypes.c_void_p
        self.user32.GetClipboardData.argtypes = [ctypes.c_uint]
        self.user32.SetClipboardData.restype = ctypes.c_void_p
        self.user32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]

    def get_text(self):
        u = self.user32
        if not u.OpenClipboard(None):
            return None
        try:
            handle = u.GetClipboardData(self.CF_UNICODETEXT)
            if not handle:
                return None
            ptr = self.kernel32.GlobalLock(handle)
            if not ptr:
                return None
            try:
                return ctypes.c_wchar_p(ptr).value
            finally:
                self.kernel32.GlobalUnlock(handle)
        finally:
            u.CloseClipboard()

    def set_text(self, text):
        u = self.user32
        k = self.kernel32
        if not u.OpenClipboard(None):
            return False
        try:
            u.EmptyClipboard()
            data = ctypes.create_unicode_buffer(text)
            nbytes = ctypes.sizeof(data)
            handle = k.GlobalAlloc(self.GMEM_MOVEABLE, nbytes)
            if not handle:
                return False
            ptr = k.GlobalLock(handle)
            if not ptr:
                return False
            try:
                ctypes.memmove(ptr, ctypes.addressof(data), nbytes)
            finally:
                k.GlobalUnlock(handle)
            u.SetClipboardData(self.CF_UNICODETEXT, handle)
            return True
        finally:
            u.CloseClipboard()

    def get_png(self):
        return None  # เฟสถัดไปของ node agent บน Windows

    def set_png(self, raw_bytes):
        return False

    def get_files(self):
        return None

    def set_files(self, paths, is_cut=False):
        return False


class NullStatusUi:
    """OSD เปล่าสำหรับ Windows (ไม่มี GNOME indicator) — รักษา surface เดิมของ ClientStatusUi"""

    def __init__(self, width=0, height=0):
        pass

    def start(self):
        pass

    def set_active(self, active, lang):
        pass

    def notify_pin(self, pin_text):
        log(f"[pair] PIN จับคู่: {pin_text}")

    def notify_connect(self, host_ip):
        pass


class NullAudioPipeline:
    """เสียงยังไม่รองรับบน node agent ฝั่ง Windows (v1) — surface ครบแต่เป็น no-op"""

    def get_status(self):
        return (False, False)

    def is_playback_active(self):
        return False

    def start_playback_sender(self, host_ip=None):
        pass

    def stop_playback_sender(self):
        pass

    def start_mic_receiver(self, host_ip=None):
        pass

    def stop_mic_receiver(self):
        pass

    def ensure_pipelines(self, host_ip=None):
        pass

class AudioPipelineManager:
    """Manages PulseAudio virtual devices (GhostSink, MicSink, VirtualMic) and GStreamer RTP/RTCP pipelines."""

    def __init__(self, host_ip: str = "192.168.99.55"):
        self.host_ip = host_ip
        self.playback_proc = None
        self.mic_proc = None
        self.lock = threading.Lock()
        self._prev_default_sink = None  # D2: sink จริงของผู้ใช้ก่อนถูกสลับไป GhostSink
        self._status_cache = (0.0, (False, False))  # F4: cache ผล pgrep 1.2 วินาที (ping ถามถี่)
        self._activity_cache = (0.0, False)

    def _run_cmd(self, cmd: list):
        try:
            return subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=5)
        except Exception:
            return None

    def ensure_virtual_devices(self):
        # 1. GhostSink for playback capture
        res = self._run_cmd(["pactl", "list", "sinks", "short"])
        sinks_out = res.stdout if res else ""
        sink_names = [line.split()[1] for line in sinks_out.splitlines() if len(line.split()) >= 2]

        if "GhostSink" not in sink_names:
            self._run_cmd([
                "pactl", "load-module", "module-null-sink",
                "sink_name=GhostSink",
                "sink_properties=device.description=Ghost_Speaker",
            ])

        if "MicSink" not in sink_names:
            self._run_cmd([
                "pactl", "load-module", "module-null-sink",
                "sink_name=MicSink",
                "sink_properties=device.description=Virtual_Mic_Sink",
            ])

        res_src = self._run_cmd(["pactl", "list", "sources", "short"])
        src_out = res_src.stdout if res_src else ""
        src_names = [line.split()[1] for line in src_out.splitlines() if len(line.split()) >= 2]

        if "VirtualMic" not in src_names:
            self._run_cmd([
                "pactl", "load-module", "module-remap-source",
                "master=MicSink.monitor",
                "source_name=VirtualMic",
                "source_properties=device.description=Virtual_Microphone",
            ])

    def _find_hardware_sink(self):
        """หา sink จริง (ไม่ใช่ null-sink ของระบบ) เพื่อใช้คืนค่า — กันกรณี prev หายจากการ restart agent"""
        res = self._run_cmd(["pactl", "list", "short", "sinks"])
        if not res or res.returncode != 0:
            return None
        for line in res.stdout.splitlines():
            parts = line.split()
            # คอลัมน์: index | name | module | spec | properties
            if len(parts) >= 3 and parts[2] != "module-null-sink.c":
                return parts[1]
        return None

    def start_playback_sender(self, host_ip: str = None, buffer_time: int = 25000, latency_time: int = 5000):
        with self.lock:
            if host_ip:
                self.host_ip = host_ip
            self.stop_playback_sender_unlocked()
            self.ensure_virtual_devices()
            # D2: จำ sink จริงของผู้ใช้ไว้เพื่อคืนค่าตอนหยุดสตรีม
            res = self._run_cmd(["pactl", "get-default-sink"])
            cur = res.stdout.strip() if res and res.returncode == 0 else ""
            if cur and cur != "GhostSink":
                self._prev_default_sink = cur
            elif not self._prev_default_sink:
                # ค่า default ถูก agent รุ่นเก่าทับไปแล้ว — หา hardware sink เป็นค่าคืนแทน
                hw = self._find_hardware_sink()
                if hw and hw != "GhostSink":
                    self._prev_default_sink = hw
            self._run_cmd(["pactl", "set-default-sink", "GhostSink"])

            env = os.environ.copy()
            env["GST_DEBUG"] = "1"  # Only critical errors, no 80MB disk bloat!

            cmd = [
                "gst-launch-1.0", "-q", "rtpbin", "name=rtpbin",
                "pulsesrc", "device=GhostSink.monitor", f"buffer-time={buffer_time}", f"latency-time={latency_time}",
                "!", "audioconvert", "!", "audioresample",
                "!", "audio/x-raw,format=S16BE,channels=2,rate=48000",
                "!", "rtpL16pay", "pt=96", "!", "rtpbin.send_rtp_sink_0",
                "rtpbin.send_rtp_src_0", "!", "udpsink", f"host={self.host_ip}", "port=50044", "sync=false", "async=false", "buffer-size=262144",
                "rtpbin.send_rtcp_src_0", "!", "udpsink", f"host={self.host_ip}", "port=50045", "sync=false", "async=false",
                "udpsrc", "port=50046", "!", "rtpbin.recv_rtcp_sink_0",
            ]
            self.playback_proc = subprocess.Popen(cmd, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def stop_playback_sender_unlocked(self):
        if self.playback_proc and self.playback_proc.poll() is None:
            self.playback_proc.terminate()
            try:
                self.playback_proc.wait(timeout=1.5)
            except Exception:
                self.playback_proc.kill()
        self.playback_proc = None
        subprocess.run(["pkill", "-f", "gst-launch-1.0.*port=50044"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        # D2: คืน default sink ให้ผู้ใช้ — ไม่งั้นเครื่อง Linux จะเงียบสนิทหลัง Host ปิด
        prev = self._prev_default_sink or self._find_hardware_sink()
        if prev and prev != "GhostSink":
            cur_res = self._run_cmd(["pactl", "get-default-sink"])
            cur = cur_res.stdout.strip() if cur_res and cur_res.returncode == 0 else ""
            if cur == "GhostSink":
                self._run_cmd(["pactl", "set-default-sink", prev])

    def stop_playback_sender(self):
        with self.lock:
            self.stop_playback_sender_unlocked()

    def start_mic_receiver(self, host_ip: str = None, buffer_time: int = 30000, latency_time: int = 8000):
        with self.lock:
            if host_ip:
                self.host_ip = host_ip
            self.stop_mic_receiver_unlocked()
            self.ensure_virtual_devices()

            env = os.environ.copy()
            env["GST_DEBUG"] = "1"

            cmd = [
                "gst-launch-1.0", "-q", "rtpbin", "name=rtpbin", "latency=20", "drop-on-latency=true",
                "udpsrc", "port=50047", "buffer-size=262144",
                "caps=application/x-rtp,media=audio,clock-rate=48000,encoding-name=L16,channels=2,payload=97",
                "!", "rtpbin.recv_rtp_sink_0",
                "rtpbin.", "!", "rtpL16depay", "!", "audioconvert", "!", "audioresample",
                "!", "audio/x-raw,format=S16LE,channels=2,rate=48000",
                "!", "pulsesink", "device=MicSink", f"buffer-time={buffer_time}", f"latency-time={latency_time}", "sync=false",
                "udpsrc", "port=50048", "!", "rtpbin.recv_rtcp_sink_0",
                "rtpbin.send_rtcp_src_0", "!", "udpsink", f"host={self.host_ip}", "port=50049", "sync=false", "async=false",
            ]
            self.mic_proc = subprocess.Popen(cmd, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def stop_mic_receiver_unlocked(self):
        if self.mic_proc and self.mic_proc.poll() is None:
            self.mic_proc.terminate()
            try:
                self.mic_proc.wait(timeout=1.5)
            except Exception:
                self.mic_proc.kill()
        self.mic_proc = None
        subprocess.run(["pkill", "-f", "gst-launch-1.0.*port=50047"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def stop_mic_receiver(self):
        with self.lock:
            self.stop_mic_receiver_unlocked()

    def get_status(self):
        # F4: cache 1.2 วินาที — แต่ละ ping เดิม spawn pgrep 2 ตัว ทิ้งทุก 1.5 วินาที
        now = time.monotonic()
        if now - self._status_cache[0] < 1.2:
            return self._status_cache[1]
        pb_alive = bool(self.playback_proc and self.playback_proc.poll() is None)
        mic_alive = bool(self.mic_proc and self.mic_proc.poll() is None)
        if not pb_alive:
            # Check if an external gst-launch is running on 50044
            r = subprocess.run(["pgrep", "-f", "gst-launch-1.0.*port=50044"], stdout=subprocess.PIPE)
            pb_alive = (r.returncode == 0)
        if not mic_alive:
            r = subprocess.run(["pgrep", "-f", "gst-launch-1.0.*port=50047"], stdout=subprocess.PIPE)
            mic_alive = (r.returncode == 0)
        self._status_cache = (now, (pb_alive, mic_alive))
        return pb_alive, mic_alive

    def is_playback_active(self) -> bool:
        """Check if any application is actively playing audio into GhostSink (cached 2s)."""
        now = time.monotonic()
        if (now - self._activity_cache[0]) < 2.0:
            return self._activity_cache[1]
        active = False
        try:
            res = self._run_cmd(["pactl", "list", "sink-inputs", "short"])
            if res and res.returncode == 0 and res.stdout.strip():
                active = True
            else:
                res_sinks = self._run_cmd(["pactl", "list", "sinks", "short"])
                if res_sinks and res_sinks.returncode == 0:
                    for line in res_sinks.stdout.splitlines():
                        parts = line.split()
                        if len(parts) >= 2 and "GhostSink" in parts[1]:
                            if "RUNNING" in line:
                                active = True
                                break
        except Exception:
            pass
        self._activity_cache = (now, active)
        return active


_system_metrics_cache = (0.0, None)
_metrics_lock = threading.Lock()


def get_system_metrics():
    """Lightweight /proc reader for CPU & RAM telemetry with 3-second in-memory cache to save Celeron CPU/disk I/O."""
    global _system_metrics_cache
    now = time.monotonic()
    if _system_metrics_cache[1] is not None and (now - _system_metrics_cache[0]) < 3.0:
        return _system_metrics_cache[1]

    ram_pct = 0.0
    ram_used_gb = 0.0
    ram_total_gb = 0.0
    try:
        if IS_WINDOWS:
            class _MSE(ctypes.Structure):
                _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                            ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                            ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                            ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                            ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
            mse = _MSE()
            mse.dwLength = ctypes.sizeof(_MSE)
            if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(mse)):
                total = mse.ullTotalPhys // 1024
                avail = mse.ullAvailPhys // 1024
                ram_pct = round((total - avail) * 100.0 / total, 1) if total else 0.0
                ram_used_gb = round((total - avail) / 1048576.0, 2)
                ram_total_gb = round(total / 1048576.0, 2)
            metrics = {"ram_pct": ram_pct, "ram_used_gb": ram_used_gb, "ram_total_gb": ram_total_gb}
            with _metrics_lock:
                _system_metrics_cache = (now, metrics)
            return metrics
        mem = {}
        with open("/proc/meminfo", "r") as f:
            for line in f:
                parts = line.split(":")
                if len(parts) == 2:
                    mem[parts[0].strip()] = int(parts[1].strip().split()[0])
        total = mem.get("MemTotal", 1)
        avail = mem.get("MemAvailable", total)
        used = total - avail
        ram_pct = round((used / total) * 100.0, 1)
        ram_used_gb = round(used / 1048576.0, 2)
        ram_total_gb = round(total / 1048576.0, 2)
    except Exception:
        pass
    metrics = {
        "ram_pct": ram_pct,
        "ram_used_gb": ram_used_gb,
        "ram_total_gb": ram_total_gb,
    }
    with _metrics_lock:
        _system_metrics_cache = (now, metrics)
    return metrics


class ClientStatusUi:
    """สถานะ OmniLink บนเครื่องลูก — ตามที่ผู้ใช้ขอ: แสดงบนแถบบนสุด "บรรทัดเดียวกับ wifi/เสียง/ไมค์"
    (เทียบเท่า Hidden icons ของ Windows) ผ่าน AppIndicator มาตรฐานของ Ubuntu/GNOME
    - ไม่มี extension → fallback เป็น pill บางๆ ชิดขอบบน (config ตำแหน่งได้)
    - ตั้งค่าได้ที่ ~/omnilink/osd_config.json: mode=indicator|pill|off, show_lang_label,
      pill_position=top_right|top_center|top_left, hotkey_hint
    """

    ICON_COLORS = {
        ("active", 0): (0.05, 0.62, 0.42),
        ("active", 1): (0.80, 0.48, 0.02),
        ("on", 0): (0.15, 0.42, 0.90),
        ("on", 1): (0.72, 0.50, 0.05),
        ("off", 0): (0.30, 0.33, 0.38),
        ("off", 1): (0.34, 0.30, 0.26),
    }

    def __init__(self, screen_w: int, screen_h: int):
        self.screen_w = screen_w
        self.screen_h = screen_h
        self.cfg = self._load_config()
        self.mode = self.cfg.get("mode", "indicator")
        self.active = False
        self.lang_group = 0
        self.ready = False
        self.indicator = None
        self.pill_win = None
        self._icons = {}

    @staticmethod
    def _load_config():
        cfg = {
            "mode": "indicator",
            "show_lang_label": True,
            "pill_position": "top_right",
            "hotkey_hint": True,
        }
        path = os.path.expanduser("~/omnilink/osd_config.json")
        try:
            if os.path.exists(path):
                with open(path, "r", encoding="utf-8") as f:
                    cfg.update({k: v for k, v in json.load(f).items() if k in cfg})
            else:
                with open(path, "w", encoding="utf-8") as f:
                    json.dump(cfg, f, ensure_ascii=False, indent=2)
                log("[ui] สร้างไฟล์ตั้งค่าเริ่มต้น: " + path)
        except Exception as ex:
            log("[ui] config load failed: " + str(ex))
        return cfg

    def start(self):
        if self.mode == "off":
            log("[ui] status UI ปิดตาม config")
            return
        if self.mode in ("indicator", "auto"):
            try:
                self._start_indicator()
                return
            except Exception as ex:
                log("[ui] top-bar indicator ใช้ไม่ได้ (" + str(ex) + ") → fallback pill")
        try:
            self._start_pill()
            log("[ui] โหมด pill (fallback, ชิดขอบบน)")
            return
        except Exception as ex:
            log("[ui] pill ล้มเหลว: " + str(ex))
        self.mode = "off"

    def _ensure_gtk(self):
        import gi
        gi.require_version("Gtk", "3.0")
        from gi.repository import Gtk, GLib
        if not Gtk.init_check()[0]:
            raise RuntimeError("no display")
        return Gtk, GLib

    def _icon_path(self, key):
        if key in self._icons:
            return self._icons[key]
        import cairo
        color = self.ICON_COLORS.get(key, self.ICON_COLORS[("off", 0)])
        surf = cairo.ImageSurface(cairo.FORMAT_ARGB32, 22, 22)
        cr = cairo.Context(surf)
        r = 5.0
        cr.new_sub_path()
        cr.arc(22 - r - 1, r + 1, r, -1.5707963, 0)
        cr.arc(22 - r - 1, 22 - r - 1, r, 0, 1.5707963)
        cr.arc(r + 1, 22 - r - 1, r, 1.5707963, 3.1415926)
        cr.arc(r + 1, r + 1, r, 3.1415926, 4.7123889)
        cr.close_path()
        cr.set_source_rgb(*color)
        cr.fill()
        if key[1] == 1:  # วงแหวนทอง = ภาษาไทย (แยกจากไอคอนได้ทันที)
            cr.set_source_rgba(1.0, 0.85, 0.35, 0.95)
            cr.set_line_width(1.8)
            cr.arc(11, 11, 4.5, 0, 6.2831853)
            cr.stroke()
        # PY-F13 fix: อย่าเขียน icon ลง /tmp (follow symlink = ใครก็ปล่อยพาธกับดักได้) — ใช้ runtime dir ของ user
        icon_base = os.environ.get("XDG_RUNTIME_DIR") or os.path.expanduser("~/.cache/omnilink")
        try:
            os.makedirs(icon_base, exist_ok=True)
        except Exception:
            icon_base = "/tmp"
        path = os.path.join(icon_base, "omnilink-" + key[0] + "-" + str(key[1]) + ".png")
        surf.write_to_png(path)
        self._icons[key] = path
        return path

    def _start_indicator(self):
        """top-bar indicator 3 ชั้น: AppIndicator (ถ้ามี gir) → SNI ตรงผ่าน DBus/Gio → โยนให้ pill"""
        try:
            self._start_appindicator()
            log("[ui] โหมด top-bar indicator (AppIndicator) — แถวเดียวกับ wifi/เสียง")
        except Exception as ex1:
            log("[ui] AppIndicator binding ไม่มี (" + str(ex1)[:80] + ") → ลอง SNI ตรงผ่าน D-Bus")
            self._start_sni()
            log("[ui] โหมด top-bar indicator (SNI/D-Bus) — แถวเดียวกับ wifi/เสียง")

    def _start_appindicator(self):
        Gtk, GLib = self._ensure_gtk()
        import gi
        try:
            gi.require_version("AppIndicator3", "0.1")
            from gi.repository import AppIndicator3 as AppInd
        except (ValueError, ImportError):
            gi.require_version("AyatanaAppIndicator3", "0.1")
            from gi.repository import AyatanaAppIndicator3 as AppInd

        ind = AppInd.Indicator.new("omnilink-client", self._icon_path(("off", 0)),
                                   AppInd.IndicatorCategory.APPLICATION_STATUS)
        ind.set_status(AppInd.IndicatorStatus.ACTIVE)
        if self.cfg.get("show_lang_label", True):
            ind.set_label("EN", "TH ไทย")

        menu = Gtk.Menu()
        hint = Gtk.MenuItem()
        hint.set_label(self._menu_label())
        hint.set_sensitive(False)
        menu.append(hint)
        menu.show_all()
        ind.set_menu(menu)

        self.indicator = ind
        self.ready = True
        threading.Thread(target=Gtk.main, daemon=True, name="status-ui").start()

    def _menu_label(self):
        base = "OmniLink v" + AGENT_VERSION
        state = "Active" if self.active else "Connected"
        lang = "TH ไทย" if self.lang_group == 1 else "EN"
        if self.cfg.get("hotkey_hint", True):
            return base + " — " + state + " [" + lang + "] — หนีฉุกเฉิน: Ctrl+Alt+Shift+Q"
        return base + " — " + state + " [" + lang + "]"

    # ─── SNI (StatusNotifierItem) ตรงผ่าน D-Bus ด้วย Gio — ไม่ต้องพึ่ง gir binding ของ appindicator ───
    SNI_ITEM_XML = """<node>
      <interface name="org.kde.StatusNotifierItem">
        <property name="Id" type="s" access="read"/>
        <property name="Category" type="s" access="read"/>
        <property name="Status" type="s" access="read"/>
        <property name="Title" type="s" access="read"/>
        <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
        <property name="IconName" type="s" access="read"/>
        <property name="IconPixmap" type="a(iiay)" access="read"/>
        <property name="AttentionIconName" type="s" access="read"/>
        <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
        <property name="Menu" type="o" access="read"/>
        <property name="ItemIsMenu" type="b" access="read"/>
        <property name="WindowId" type="i" access="read"/>
      </interface>
    </node>"""

    DBUSMENU_XML = """<node>
      <interface name="com.canonical.dbusmenu">
        <method name="GetLayout">
          <arg name="parentId" type="i" direction="in"/>
          <arg name="recursionDepth" type="i" direction="in"/>
          <arg name="propertyNames" type="as" direction="in"/>
          <arg name="revision" type="u" direction="out"/>
          <arg name="layout" type="ia(iav)" direction="out"/>
        </method>
        <method name="GetGroupProperties">
          <arg name="ids" type="ai" direction="in"/>
          <arg name="propertyNames" type="as" direction="in"/>
          <arg name="properties" type="a(ia{sv})" direction="out"/>
        </method>
        <method name="AboutToShow">
          <arg name="id" type="i" direction="in"/>
          <arg name="needUpdate" type="b" direction="out"/>
        </method>
        <method name="Event">
          <arg name="id" type="i" direction="in"/>
          <arg name="eventId" type="s" direction="in"/>
          <arg name="data" type="v" direction="in"/>
          <arg name="timestamp" type="u" direction="in"/>
        </method>
        <property name="Version" type="u" access="read"/>
        <property name="Status" type="s" access="read"/>
        <property name="IconThemePath" type="as" access="read"/>
        <property name="ItemPropertyNames" type="as" access="read"/>
      </interface>
    </node>"""

    def _cairo_to_sni_argb(self, surf):
        """cairo ARGB32 (premultiplied BGRA LE) → SNI ARGB big-endian แบบไม่ pre-multiply"""
        import cairo
        assert surf.get_format() == cairo.FORMAT_ARGB32
        raw = bytes(surf.get_data())
        out = bytearray(len(raw))
        for i in range(0, len(raw), 4):
            b, g, r, a = raw[i], raw[i + 1], raw[i + 2], raw[i + 3]
            if a:
                r2, g2, b2 = (r * 255) // a, (g * 255) // a, (b * 255) // a
            else:
                r2 = g2 = b2 = 0
            out[i], out[i + 1], out[i + 2], out[i + 3] = a, r2, g2, b2
        return bytes(out)

    def _sni_pixmap_variant(self, key):
        import cairo
        # วาด 22x22 ลง surface (ใช้เส้นทางเดียวกับ _icon_path แต่คง surface ไว้แปลงสด)
        color = self.ICON_COLORS.get(key, self.ICON_COLORS[("off", 0)])
        surf = cairo.ImageSurface(cairo.FORMAT_ARGB32, 22, 22)
        cr = cairo.Context(surf)
        r = 5.0
        cr.new_sub_path()
        cr.arc(22 - r - 1, r + 1, r, -1.5707963, 0)
        cr.arc(22 - r - 1, 22 - r - 1, r, 0, 1.5707963)
        cr.arc(r + 1, 22 - r - 1, r, 1.5707963, 3.1415926)
        cr.arc(r + 1, r + 1, r, 3.1415926, 4.7123889)
        cr.close_path()
        cr.set_source_rgb(*color)
        cr.fill()
        if key[1] == 1:
            cr.set_source_rgba(1.0, 0.85, 0.35, 0.95)
            cr.set_line_width(1.8)
            cr.arc(11, 11, 4.5, 0, 6.2831853)
            cr.stroke()
        surf.flush()
        pix = self._cairo_to_sni_argb(surf)
        return GLib.Variant("(iiay)", (22, 22, pix))

    def _start_sni(self):
        Gtk, GLib = self._ensure_gtk()
        from gi.repository import Gio
        self._glib = GLib
        self._sni_rev = 1
        self._sni_conn = Gio.bus_get_sync(Gio.BusType.SESSION, None)

        item_node = Gio.DBusNodeInfo.new_for_xml(self.SNI_ITEM_XML)
        menu_node = Gio.DBusNodeInfo.new_for_xml(self.DBUSMENU_XML)

        self._sni_conn.register_object(
            "/StatusNotifierItem", item_node.interfaces[0],
            self._sni_method, self._sni_get_prop, None)
        self._sni_conn.register_object(
            "/StatusNotifierMenu", menu_node.interfaces[0],
            self._sni_menu_method, self._sni_menu_get_prop, None)

        # แจ้ง GNOME Shell (ubuntu-appindicators extension = StatusNotifierWatcher)
        self._sni_conn.call_sync(
            "org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
            "org.kde.StatusNotifierWatcher", "RegisterStatusNotifierItem",
            GLib.Variant("(s)", ("/StatusNotifierItem",)),
            None, Gio.DBusCallFlags.NONE, 3000, None)

        self.indicator = "sni"
        self.ready = True
        loop = GLib.MainLoop()
        threading.Thread(target=loop.run, daemon=True, name="status-ui").start()

    def _sni_state_key(self):
        return ("active" if self.active else "on", self.lang_group)

    def _sni_title(self):
        state = "Active" if self.active else "Connected"
        lang = "TH ไทย" if self.lang_group == 1 else "EN"
        return "OmniLink " + state + " [" + lang + "]"

    def _sni_get_prop(self, conn, sender, path, iface, name):
        from gi.repository import GLib, Gio
        if name == "Id":
            return GLib.Variant("s", "omnilink-client")
        if name == "Category":
            return GLib.Variant("s", "Hardware")
        if name == "Status":
            return GLib.Variant("s", "Active")
        if name == "Title":
            return GLib.Variant("s", self._sni_title())
        if name == "ToolTip":
            hint = "หนีฉุกเฉิน: Ctrl+Alt+Shift+Q" if self.cfg.get("hotkey_hint", True) else ""
            return GLib.Variant("(sa(iiay)ss)", ("OmniLink", [], self._sni_title(), hint))
        if name == "IconName":
            return GLib.Variant("s", "")
        if name == "IconPixmap":
            return GLib.Variant("a(iiay)", [self._sni_pixmap_variant(self._sni_state_key())])
        if name == "AttentionIconName":
            return GLib.Variant("s", "")
        if name == "AttentionIconPixmap":
            return GLib.Variant("a(iiay)", [])
        if name == "Menu":
            return GLib.Variant("o", "/StatusNotifierMenu")
        if name == "ItemIsMenu":
            return GLib.Variant("b", True)
        if name == "WindowId":
            return GLib.Variant("i", 0)
        raise GLib.Error(Gio.DBusError.UNKNOWN_PROPERTY)

    def _sni_method(self, conn, sender, path, iface, method, params, invocation):
        invocation.return_value(None)

    def _sni_menu_get_prop(self, conn, sender, path, iface, name):
        from gi.repository import GLib, Gio
        if name == "Version":
            return GLib.Variant("u", 3)
        if name == "Status":
            return GLib.Variant("s", "normal")
        if name == "IconThemePath":
            return GLib.Variant("as", [])
        if name == "ItemPropertyNames":
            return GLib.Variant("as", ["label"])
        raise GLib.Error(Gio.DBusError.UNKNOWN_PROPERTY)

    def _sni_menu_method(self, conn, sender, path, iface, method, params, invocation):
        from gi.repository import GLib
        if method == "GetLayout":
            text = self._menu_label()
            layout = GLib.Variant("(iav)", (1, {"label": GLib.Variant("s", text)}))
            invocation.return_value(GLib.Variant("(u(ia(iav)))", (self._sni_rev, (0, [layout]))))
        elif method == "AboutToShow":
            invocation.return_value(GLib.Variant("(b)", (False,)))
        elif method == "GetGroupProperties":
            invocation.return_value(GLib.Variant("(a(ia{sv}))", ([],)))
        else:  # Event
            invocation.return_value(None)

    def _start_pill(self):
        Gtk, GLib = self._ensure_gtk()
        import cairo
        win = Gtk.Window(type=Gtk.WindowType.POPUP)
        win.set_app_paintable(True)
        screen = win.get_screen()
        visual = screen.get_rgba_visual()
        if visual:
            win.set_visual(visual)

        pill_w, pill_h = 168, 22
        pos = self.cfg.get("pill_position", "top_right")
        if pos == "top_left":
            x = 8
        elif pos == "top_center":
            x = max(8, (self.screen_w - pill_w) // 2)
        else:
            x = max(8, self.screen_w - pill_w - 8)
        win.move(x, 30)  # 30px = ใต้แถบ GNOME พอดี (แถบสูง ~28px)
        win.set_size_request(pill_w, pill_h)
        win.set_keep_above(True)
        win.set_accept_focus(False)
        win.input_shape_combine_region(cairo.Region())

        def on_draw(widget, cr):
            cr.set_operator(cairo.OPERATOR_SOURCE)
            cr.set_source_rgba(0, 0, 0, 0)
            cr.paint()
            cr.set_operator(cairo.OPERATOR_OVER)
            r = 8
            cr.new_sub_path()
            cr.arc(pill_w - r, r, r, -1.5707963, 0)
            cr.arc(pill_w - r, pill_h - r, r, 0, 1.5707963)
            cr.arc(r, pill_h - r, r, 1.5707963, 3.1415926)
            cr.arc(r, r, r, 3.1415926, 4.7123889)
            cr.close_path()
            if self.active:
                cr.set_source_rgba(0.05, 0.14, 0.10, 0.92)
                cr.fill_preserve()
                cr.set_source_rgba(0.3, 0.6, 0.5, 0.9)
            else:
                cr.set_source_rgba(0.07, 0.09, 0.14, 0.80)
                cr.fill_preserve()
                cr.set_source_rgba(0.25, 0.4, 0.7, 0.6)
            cr.set_line_width(1.2)
            cr.stroke()
            cr.select_font_face("Sans", cairo.FONT_SLANT_NORMAL, cairo.FONT_WEIGHT_BOLD)
            cr.set_font_size(11.0)
            cr.set_source_rgba(1, 1, 1, 0.95)
            is_th = self.lang_group == 1
            cr.move_to(10, pill_h / 2 + 4)
            cr.show_text(("● " if self.active else "○ ") + ("TH ไทย" if is_th else "EN"))
            return False

        win.connect("draw", on_draw)
        self.pill_win = win
        self.ready = True
        win.show_all()
        threading.Thread(target=Gtk.main, daemon=True, name="status-ui").start()

    def set_active(self, active: bool, lang_group: int):
        self.active = active
        self.lang_group = 1 if lang_group == 1 else 0
        if not self.ready:
            return
        try:
            from gi.repository import GLib

            def _do():
                if self.indicator == "sni":
                    # SNI: emit PropertiesChanged ให้ GNOME Shell วาดไอคอน/ชื่อใหม่
                    try:
                        self._sni_rev += 1
                        props = {
                            "IconPixmap": self._sni_pixmap_variant(self._sni_state_key()),
                            "Title": GLib.Variant("s", self._sni_title()),
                            "ToolTip": GLib.Variant("(sa(iiay)ss)", (
                                "OmniLink", [], self._sni_title(),
                                "หนีฉุกเฉิน: Ctrl+Alt+Shift+Q" if self.cfg.get("hotkey_hint", True) else "")),
                        }
                        self._sni_conn.emit_signal(
                            None, "/StatusNotifierItem",
                            "org.freedesktop.DBus.Properties", "PropertiesChanged",
                            GLib.Variant("(sa{sv}as)", ("org.kde.StatusNotifierItem", props, [])))
                        self._sni_conn.emit_signal(
                            None, "/StatusNotifierMenu", "com.canonical.dbusmenu", "LayoutUpdated",
                            GLib.Variant("(ui)", (self._sni_rev, 0)))
                    except Exception:
                        pass
                elif self.indicator is not None:
                    key = ("active" if self.active else "on", self.lang_group)
                    path = self._icon_path(key)
                    try:
                        self.indicator.set_icon_full(path, "")
                    except Exception:
                        try:
                            self.indicator.set_icon(path)
                        except Exception:
                            pass
                    if self.cfg.get("show_lang_label", True):
                        self.indicator.set_label("TH ไทย" if self.lang_group else "EN", "TH ไทย")
                elif self.pill_win is not None:
                    self.pill_win.queue_draw()
                return False

            GLib.idle_add(_do)
        except Exception:
            pass

    def notify_connect(self, host_ip: str):
        try:
            import gi
            gi.require_version("Notify", "0.7")
            from gi.repository import Notify
            Notify.init("OmniLink")
            n = Notify.Notification.new(
                "OmniLink Hub",
                "เชื่อมต่อกับเครื่องหลัก " + host_ip + " สำเร็จ (ฉุกเฉิน: Ctrl+Alt+Shift+Q)",
                "input-keyboard")
            n.set_urgency(1)
            n.show()
        except Exception:
            pass

    def notify_pin(self, pin: str):
        try:
            import gi
            gi.require_version("Notify", "0.7")
            from gi.repository import Notify
            Notify.init("OmniLink")
            n = Notify.Notification.new(
                "OmniLink Pairing (PIN + Fingerprint)",
                f"{pin}\nกรอก PIN บนเครื่องหลัก แล้วเทียบ Fingerprint ให้ตรงก่อนกดยืนยัน",
                "dialog-password")
            n.set_urgency(2)
            n.show()
        except Exception:
            pass


class LinuxClipboard:
    """คัดลอกข้ามเครื่อง (แบบ Deskflow): หลัก = xclip, สำรอง = GTK (ผ่าน main loop ของ status UI)"""

    def __init__(self, status_ui):
        self.ui = status_ui

    @staticmethod
    def _xclip():
        import shutil
        return shutil.which("xclip")

    def set_text(self, text: str) -> bool:
        try:
            xc = self._xclip()
            if xc:
                p = subprocess.Popen([xc, "-selection", "clipboard"],
                                     stdin=subprocess.PIPE,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                p.communicate(text.encode("utf-8"), timeout=1.0)
                return True
        except Exception:
            pass
        # fallback: GTK ownership (ต้องมี GLib main loop ของ status UI)
        try:
            if self.ui is not None and self.ui.ready:
                from gi.repository import Gtk, Gdk, GLib

                def _do():
                    try:
                        cb = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
                        cb.set_text(text, -1)
                        cb.store()
                    except Exception:
                        pass
                    return False

                GLib.idle_add(_do)
                return True
        except Exception:
            pass
        return False

    def get_text(self, timeout=0.5) -> str:
        try:
            xc = self._xclip()
            if xc:
                r = subprocess.run([xc, "-o", "-selection", "clipboard"],
                                   capture_output=True, timeout=timeout)
                return r.stdout.decode("utf-8", errors="replace")
        except Exception:
            pass
        return ""

    def set_png(self, png_bytes: bytes) -> bool:
        try:
            xc = self._xclip()
            if xc:
                p = subprocess.Popen([xc, "-selection", "clipboard", "-t", "image/png"],
                                     stdin=subprocess.PIPE,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                p.communicate(png_bytes, timeout=3.0)
                return True
        except Exception as ex:
            log(f"[clip] xclip set_png error: {ex}")
        # fallback: GTK
        try:
            if self.ui is not None and self.ui.ready:
                from gi.repository import Gtk, Gdk, GLib, GdkPixbuf
                loader = GdkPixbuf.PixbufLoader.new_with_type("png")
                loader.write(png_bytes)
                loader.close()
                pixbuf = loader.get_pixbuf()

                def _do():
                    try:
                        cb = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
                        cb.set_image(pixbuf)
                        cb.store()
                    except Exception:
                        pass
                    return False

                GLib.idle_add(_do)
                return True
        except Exception:
            pass
        return False

    def get_png(self, timeout=0.5) -> bytes:
        try:
            xc = self._xclip()
            if xc:
                r = subprocess.run([xc, "-o", "-selection", "clipboard", "-t", "image/png"],
                                   capture_output=True, timeout=timeout)
                if r.returncode == 0 and len(r.stdout) > 8 and r.stdout.startswith(b"\x89PNG\r\n\x1a\n"):
                    return r.stdout
        except Exception:
            pass
        return b""

    def get_files(self, timeout=0.5):
        """อ่านรายการไฟล์จาก Clipboard (GNOME x-special/gnome-copied-files หรือ text/uri-list)"""
        xc = self._xclip()
        if not xc:
            return [], False
        # 1. ลองอ่าน x-special/gnome-copied-files ก่อน
        try:
            r = subprocess.run([xc, "-o", "-selection", "clipboard", "-t", "x-special/gnome-copied-files"],
                               capture_output=True, timeout=timeout)
            if r.returncode == 0 and r.stdout:
                lines = r.stdout.decode("utf-8", errors="replace").strip().splitlines()
                if lines:
                    action = lines[0].strip().lower()
                    is_cut = (action == "cut")
                    files = []
                    for line in lines[1:]:
                        line = line.strip()
                        if line.startswith("file://"):
                            import urllib.parse
                            path = urllib.parse.unquote(urllib.parse.urlparse(line).path)
                            if os.path.exists(path):
                                files.append(path)
                    if files:
                        return files, is_cut
        except Exception:
            pass

        # 2. Fallback: text/uri-list
        try:
            r = subprocess.run([xc, "-o", "-selection", "clipboard", "-t", "text/uri-list"],
                               capture_output=True, timeout=timeout)
            if r.returncode == 0 and r.stdout:
                lines = r.stdout.decode("utf-8", errors="replace").strip().splitlines()
                files = []
                for line in lines:
                    line = line.strip()
                    if line.startswith("file://"):
                        import urllib.parse
                        path = urllib.parse.unquote(urllib.parse.urlparse(line).path)
                        if os.path.exists(path):
                            files.append(path)
                if files:
                    return files, False
        except Exception:
            pass
        return [], False

    def set_files(self, paths: list, is_cut: bool = False) -> bool:
        """ตั้งค่ารายการไฟล์ลง Clipboard (x-special/gnome-copied-files + text/uri-list)"""
        if not paths:
            return False
        import pathlib
        uris = []
        for p in paths:
            try:
                uri = pathlib.Path(p).resolve().as_uri()
                uris.append(uri)
            except Exception:
                pass
        if not uris:
            return False

        action = "cut" if is_cut else "copy"
        gnome_payload = (action + "\n" + "\n".join(uris) + "\n").encode("utf-8")
        uri_payload = ("\r\n".join(uris) + "\r\n").encode("utf-8")

        xc = self._xclip()
        if xc:
            try:
                p1 = subprocess.Popen([xc, "-selection", "clipboard", "-t", "x-special/gnome-copied-files"],
                                      stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                p1.communicate(gnome_payload, timeout=1.5)

                p2 = subprocess.Popen([xc, "-selection", "clipboard", "-t", "text/uri-list"],
                                      stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                p2.communicate(uri_payload, timeout=1.5)
                return True
            except Exception as ex:
                log(f"[clip] xclip set_files error: {ex}")
        return False


class OmniLinkAgentServer:
    def __init__(self, bind_ip: str = "0.0.0.0", auth_token: str = ""):
        self.bind_ip = bind_ip
        self.auth_token = auth_token or ""
        # v2.0 เฟส 5: เลือก platform layer — Windows = ctypes, Linux = X11 เดิม
        if IS_WINDOWS:
            self.x11 = Win32Controller()
            self.audio = NullAudioPipeline()
        else:
            self.x11 = X11Controller(":0")
            self.audio = AudioPipelineManager()
        self.running = True
        self.active_focus = False
        self.node_position = "right"  # Relative to the Host ("right" means left edge returns to Host)
        self.active_tcp_conn = None
        self.tcp_lock = threading.Lock()
        self._authed_hosts = set()  # F2: IP ของ host ที่ยืนยัน token แล้วเท่านั้น — UDP อื่นถูกทิ้ง

        # สถานะบนแถบบนสุด (indicator แถวเดียวกับ wifi/เสียง, fallback pill, ปิดได้)
        if IS_WINDOWS:
            self.osd = NullStatusUi(self.x11.width, self.x11.height)
            self.osd.start()
            self.clip = Win32Clipboard(self.osd)
        else:
            self.osd = ClientStatusUi(self.x11.width, self.x11.height)
            self.osd.start()
            self.clip = LinuxClipboard(self.osd)
        self._pending_clips = {}
        self._inbound_files = {}
        self._outbound_files = {}
        self.lifecycle_state = "hot"
        self._last_host_contact = time.time()

        # P0: edge-push state machine — กันสลับเองเมื่อเมาส์แปะขอบ (ต้องดันสะสมเกิน threshold)
        self._edge_push = 0.0
        self._edge_push_at = 0.0
        # A1 Switching UX (ค่าที่ Host ส่งมาพร้อม enter_screen — default ตาม Deskflow)
        self._switch_delay_ms = 250   # แปะขอบค้างอย่างน้อยเท่านี้จึงสลับ
        self._double_tap_ms = 400     # หรือแตะขอบสองรอบภายในเท่านี้ → สลับทันที
        self._corner_size = 32        # มุมจอตาย (px)
        self._corners = {"tl": True, "tr": True, "bl": True, "br": True}
        self._at_edge_since = 0.0
        self._last_leave_at = 0.0
        self._had_real_push = False
        # A2: ล็อกเมาส์ไว้จอนี้ (Scroll Lock จาก Host) — edge return ถูกพักจนกว่าจะปลด
        self.screen_locked = False
        # INCIDENT#2 fix: UDP-activity arming — ถ้า Host กำลังส่งเมาส์อยู่ ให้ edge detection
        # ทำงานแม้ active_focus หลุด (กัน state สองฝั่งไม่ตรงกันจนเมาส์ติด)
        # v1.9.1 chain topology: edge -> target (จาก set_layout)
        self.neighbors = {}
        self._neighbor_state = {}
        self._last_udp_move = 0.0
        self._udp_idle_logged = True
        self._tcp_drop_logged = 0.0

        # P0: hotkey ฉุกเฉินบนคีย์บอร์ดจริงของเครื่องนี้ — หนีได้เสมอแม้ระบบเมาส์พัง
        self.x11.start_escape_hotkey(self.emergency_return)

        # Stop any legacy Flatpak Deskflow client so it doesn't fight with OmniLink
        if not IS_WINDOWS:
            subprocess.run(["pkill", "-f", "deskflow"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.machine_uuid = _load_or_create_uuid()
        self.pairing_pin = _load_or_create_pin()

        # v1.6 hardening: TLS context + fingerprint (TOFU) + UDP HMAC key + pair rate-limit state
        self.tls_ctx, self.cert_fingerprint = _load_or_create_tls()
        self._udp_key = self._derive_udp_key()
        self._pair_attempts = {}   # ip -> [timestamps ของ PIN ผิด]
        self._pair_blocked_until = {}  # ip -> monotonic ที่หมดช่วงถูกบล็อก
        self._authed_conns = {}    # ip -> จำนวน connection ที่ auth แล้วยังเปิดอยู่ (PY-F5: ผูก allowlist กับ session)
        self._authed_lock = threading.Lock()
        # A1 fix: clipboard echo — จำ hash เนื้อหาล่าสุดที่ "แลกเปลี่ยนแล้ว" กับ host
        # (set จาก host หรือ push ของเราเอง = แลกแล้วทั้งคู่ → เนื้อหาเดิมไม่ส่งซ้ำ)
        self._last_clip_hash = ""
        # v1.7 Zone Model: capability ที่ host กำหนดให้ node นี้ — full/clipboard/view
        # (C3: enforce ฝั่งรับ — security จริงไม่พึ่งความสุจริตของ host)
        self.remote_policy = "full"

        # PY-F2: ห้ามพิมพ์ PIN ลง log เด็ดขาด (log 0644 = ใครอ่านได้คือได้กุญแจ) — ให้ดูบนจอแทน
        log(f"[start] agent ready — uuid={self.machine_uuid}, auth=" + ("on" if self.auth_token else "PAIRING-READY") +
            ", tls=" + ("on" if self.tls_ctx else "FAIL-CLOSED") +
            ", edge_threshold=" + str(int(EDGE_PUSH_THRESHOLD_PX)) + "px, hotkey=Ctrl+Alt+Shift+Q")
        if not self.auth_token:
            # ยังไม่มีคู่จับคู่ — เด้ง PIN + fingerprint บนจอเครื่องลูกให้ผู้ใช้อ่าน (TOFU แบบ Deskflow)
            fp = self.cert_fingerprint or ""
            fp_short = (fp[:4] + "-" + fp[4:8] + "-" + fp[8:12] + "-" + fp[12:16]).upper() if len(fp) >= 16 else "(n/a)"
            threading.Thread(target=self.osd.notify_pin,
                             args=(f"{self.pairing_pin}   ·   FP: {fp_short}",), daemon=True).start()

    def _record_clip_exchange(self, content_hash: str):
        """A1 fix: บันทึกว่าเนื้อหานี้ "แลกเปลี่ยนกับ host แล้ว" — ทั้งกรณีรับจาก host
        และกรณี push ของเราเอง ผลคือ watcher จะไม่ผลักเนื้อหาเดิมกลับไปมา (echo loop)"""
        self._last_clip_hash = content_hash

    def _derive_udp_key(self):
        """v1.6: กุญแจ HMAC ของ UDP — derive จาก auth token (สองฝั่งได้เหมือนกันจาก token เดียวกัน)"""
        if not self.auth_token:
            return None
        return hashlib.sha256(("omnilink-udp-v2|" + self.auth_token).encode("utf-8")).digest()

    def _pair_rate_limited(self, ip: str):
        """v1.6 hardening (PY-F1): จำกัดการเดา PIN — 10 ครั้ง/10 นาที แล้วบล็อก IP 5 นาที
        คืน True เมื่อ 'อนุญาตให้ลอง' / False เมื่อโดนบล็อก"""
        now = time.monotonic()
        blocked_until = self._pair_blocked_until.get(ip, 0.0)
        if now < blocked_until:
            return False
        stamps = [t for t in self._pair_attempts.get(ip, []) if now - t < 600.0]
        self._pair_attempts[ip] = stamps
        return len(stamps) < 10

    def _record_pair_failure(self, ip: str):
        now = time.monotonic()
        self._pair_attempts.setdefault(ip, []).append(now)
        if len([t for t in self._pair_attempts[ip] if now - t < 600.0]) >= 10:
            self._pair_blocked_until[ip] = now + 300.0
            log(f"[pair] {ip} เดา PIN เกิน 10 ครั้ง/10 นาที — บล็อก 5 นาที")
        return len(self._pair_attempts[ip])

    def send_reverse_knock(self, host_ip=None):
        """ส่งสัญญาณ Reverse Knock (UDP 24800) ปลุก Host ที่หลับอยู่ใน Freeze state"""
        payload = json.dumps({
            "cmd": "KNOCK_KNOCK",
            "node_id": self.machine_uuid,
            "port": KVM_TCP_PORT,
            "status": "ready",
            "name": socket.gethostname(),
            "os": "Windows" if IS_WINDOWS else "Linux"
        }).encode("utf-8")
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            if host_ip:
                s.sendto(payload, (host_ip, DISCOVERY_PORT))
            s.sendto(payload, ("255.255.255.255", DISCOVERY_PORT))
            s.close()
            log(f"[knock] Sent KNOCK_KNOCK reverse broadcast to port {DISCOVERY_PORT}")
        except Exception as ex:
            log(f"[knock] error sending knock: {ex}")

    def emergency_return(self, reason="hotkey"):
        """P0: ดึงการควบคุมกลับเครื่องหลักทันที — ใช้ได้แม้ edge detection พัง"""
        if not self.active_focus:
            return
        self.active_focus = False
        self._edge_push = 0.0
        self.screen_locked = False  # ฉุกเฉิน override ล็อกเสมอ
        try:
            self.x11.release_all()
            w, h = self.x11.width, self.x11.height
            self.x11.move_abs(w // 2, h // 2)
        except Exception:
            pass
        self.osd.set_active(False, self.x11.get_lang_group())
        edge = "left" if self.node_position == "right" else "right"
        self.send_tcp_message({"type": "edge_return", "edge": edge, "norm_y": 0.5, "norm_x": 0.5})
        self._push_clipboard_async()
        log("[emergency] คืนการควบคุมไปเครื่องหลักแล้ว (" + str(reason) + ")")

    def _push_clipboard_async(self):
        """คัดลอกข้ามเครื่อง: ส่ง clipboard ของเครื่องนี้กลับ Host เมื่อผู้ใช้ออกจอ (แบบ Deskflow)
        ทำบนเธรดแยก — ห้ามบล็อกเส้นทางเมาส์/edge"""
        threading.Thread(target=self._push_clipboard, daemon=True).start()

    def _push_clipboard(self):
        try:
            # 1. First check for copied files (BATCH 5 / SPEC-FX)
            files, is_cut = self.clip.get_files()
            if files:
                # ห้าม import hashlib แบบ local ที่นี่ — Python scoping จะทำให้ hashlib กลายเป็น
                # local ทั้งฟังก์ชัน แล้ว branch text (ใช้ hashlib ก่อนบรรทัดนี้) พังแบบเจอจริง 23 ต.ค.
                import uuid
                manifest_files = []
                total_bytes = 0
                for fpath in files:
                    if os.path.isfile(fpath):
                        fsize = os.path.getsize(fpath)
                        h = hashlib.sha256()
                        with open(fpath, "rb") as f:
                            while chunk := f.read(65536):
                                h.update(chunk)
                        manifest_files.append({
                            "rel_path": os.path.basename(fpath),
                            "size": fsize,
                            "sha256": h.hexdigest(),
                            "is_dir": False
                        })
                        total_bytes += fsize
                if manifest_files:
                    # A1 fix: dedup — ชุดไฟล์เดิมที่เพิ่งแลกกับ host ไปแล้ว ไม่ offer ซ้ำ
                    files_hash = hashlib.sha256(("files|" + "|".join(files) + "|" + ("cut" if is_cut else "copy")).encode("utf-8")).hexdigest()
                    if files_hash == self._last_clip_hash:
                        log("[file] skip file_offer (เนื้อหาเดิมที่เพิ่งแลกไป)")
                        return
                    transfer_id = uuid.uuid4().hex
                    manifest = {
                        "id": transfer_id,
                        "effect": "cut" if is_cut else "copy",
                        "total_bytes": total_bytes,
                        "files": manifest_files
                    }
                    self._outbound_files[transfer_id] = {
                        "manifest": manifest,
                        "file_map": {idx: f for idx, f in enumerate(files)}
                    }
                    self._record_clip_exchange(files_hash)
                    self.send_tcp_message({
                        "type": "file_offer",
                        "manifest": json.dumps(manifest)
                    })
                    log(f"[file] push file_offer {len(manifest_files)} files ({total_bytes}B) id={transfer_id}")
                    return

            # 2. Check for image PNG (TASK-008 & TASK-009)
            png = self.clip.get_png(timeout=0.5)
            if png and len(png) > 8:
                import base64, uuid
                # A1 fix: dedup ภาพเดิม
                png_hash = hashlib.sha256(b"image|" + png).hexdigest()
                if png_hash == self._last_clip_hash:
                    return
                if len(png) >= 65536:
                    token = uuid.uuid4().hex
                    self._pending_clips[token] = (png, "image/png")
                    self.send_tcp_message({
                        "type": "clip_meta",
                        "mime": "image/png",
                        "size": len(png),
                        "token": token
                    })
                    log(f"[clipboard] push clip_meta image/png len={len(png)} token={token}")
                else:
                    b64 = base64.b64encode(png).decode("ascii")
                    self.send_tcp_message({
                        "type": "clipboard",
                        "mime": "image/png",
                        "data": b64
                    })
                    self._record_clip_exchange(png_hash)
                    log(f"[clipboard] push image/png direct len={len(png)}")
                return

            # 2. Text clipboard
            text = self.clip.get_text()
            if text and len(text.encode("utf-8")) <= 5 * 1024 * 1024:
                text_bytes = text.encode("utf-8")
                # A1 fix: dedup ข้อความเดิม — watcher จะไม่ผลักสิ่งที่ host มีอยู่แล้ว
                text_hash = hashlib.sha256(("text|" + text).encode("utf-8")).hexdigest()
                if text_hash == self._last_clip_hash:
                    return
                if len(text_bytes) >= 65536:
                    import uuid
                    token = uuid.uuid4().hex
                    self._pending_clips[token] = (text_bytes, "text/plain")
                    self.send_tcp_message({
                        "type": "clip_meta",
                        "mime": "text/plain",
                        "size": len(text_bytes),
                        "token": token
                    })
                    self._record_clip_exchange(text_hash)
                    log(f"[clipboard] push clip_meta text len={len(text)} token={token}")
                else:
                    self.send_tcp_message({"type": "clipboard", "text": text})
                    self._record_clip_exchange(text_hash)
                    log(f"[clipboard] push ไป Host len={len(text)}")
        except Exception as ex:
            log("[clipboard] push failed: " + str(ex))

    def _send_direct(self, conn, payload: dict):
        """INCIDENT#2: ส่งข้อความตรงไปยัง socket ที่กำหนด — ห้ามผ่าน active_tcp_conn
        (active เปลี่ยนได้จาก thread อื่นทุกเมื่อ → ข้อความไผ่คนละซองแบบที่พังครั้งที่สอง)
        PY-F4 fix: ครอบ tcp_lock เดียวกับ send_tcp_message — กันสอง thread sendall
        แทรกกลางเฟรมเดียวกันจนโครงสร้าง length-prefixed พังทั้งสองฝั่ง"""
        raw = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        with self.tcp_lock:
            conn.sendall(struct.pack("!I", len(raw)) + raw)

    def send_tcp_message(self, payload: dict):
        with self.tcp_lock:
            if not self.active_tcp_conn:
                # INCIDENT#2: เดิมหล่นหายเงียบๆ — edge_return หายไปทั้งข้อความโดยไม่มีใครรู้
                now = time.monotonic()
                if now - self._tcp_drop_logged > 2.0:
                    self._tcp_drop_logged = now
                    log("[tcp] !! DROP ข้อความ type=" + str(payload.get("type")) +
                        " (active_tcp_conn=None — Host conn ถูก orphan หรือยังไม่ reconnect)")
                return
            try:
                raw = json.dumps(payload, ensure_ascii=False).encode("utf-8")
                self.active_tcp_conn.sendall(struct.pack("!I", len(raw)) + raw)
            except Exception as ex:
                log("[tcp] ส่งข้อความล้มเหลว type=" + str(payload.get("type")) + ": " + str(ex))
                self.active_tcp_conn = None

    def _host_edge(self):
        # ขอบที่หันหา host (ตรงข้าม node_position)
        return {"right": "left", "left": "right", "top": "bottom", "bottom": "top"}.get(
            self.node_position, "left")

    def _armed_neighbor_edges(self):
        # ขอบที่มีเพื่อนบ้าน (ไม่รวมขอบ host) - จาก set_layout
        return {e: t for e, t in (getattr(self, "neighbors", None) or {}).items()
                if e in ("left", "right", "top", "bottom") and t}

    def _check_neighbor_edges(self, rx, ry, w, h, dx, dy, now, delay_s):
        # v1.9.1 chain topology: state machine ต่อขอบที่มีเพื่อนบ้าน (เฉพาะขอบที่ไม่ใช่ host-facing)
        # เกณฑ์เดียวกับ host-facing (push 60px + delay) - ไม่มี double-tap ใน v1
        # trigger = ส่ง switch_request (Host จะ SwitchToNode + วางเคอร์เซอร์ที่ขอบตรงข้ามให้เอง)
        # คืน True ถ้าเพิ่งส่ง request (caller ต้อง return ทันที)
        for edge, target in self._armed_neighbor_edges().items():
            if edge == self._host_edge():
                continue
            states = getattr(self, "_neighbor_state", None)
            if states is None:
                self._neighbor_state = states = {}
            st = states.setdefault(edge, {"since": 0.0, "push": 0.0})
            if edge == "left":
                at_edge, pushing = rx <= 1, dx < 0
            elif edge == "right":
                at_edge, pushing = rx >= w - 2, dx > 0
            elif edge == "top":
                at_edge, pushing = ry <= 1, dy < 0
            else:
                at_edge, pushing = ry >= h - 2, dy > 0
            if at_edge:
                if st["since"] == 0.0:
                    st["since"] = now
                    st["push"] = 0.0
                if pushing:
                    st["push"] += abs(dy) if edge in ("top", "bottom") else abs(dx)
                if st["push"] >= EDGE_PUSH_THRESHOLD_PX and (now - st["since"]) >= delay_s:
                    if now - getattr(self, "_last_edge_request_sent", 0.0) < 0.05:
                        return False
                    self._last_edge_request_sent = now
                    fx = max(0.0, min(1.0, rx / max(1, w - 1)))
                    fy = max(0.0, min(1.0, ry / max(1, h - 1)))
                    log("[edge] switch_request target=" + str(target) + " (edge=" + edge + ")")
                    self.send_tcp_message({"type": "switch_request", "target": target,
                                           "edge": edge, "norm_x": fx, "norm_y": fy})
                    st["since"] = 0.0
                    st["push"] = 0.0
                    return True
            else:
                st["since"] = 0.0
                st["push"] = 0.0
        return False

    def check_edge_return(self, new_x: int, new_y: int, dx: int = 0, dy: int = 0):
        """A1 Switching UX state machine:
        - สลับเมื่อ (ดันสะสม >=60px และอยู่ที่ขอบมาแล้ว >= switch_delay_ms) หรือ (double-tap)
        - มุมจอที่เปิด "ตาย" = เข้าไปแล้วไม่สลับ (รีเซ็ตสถานะ)
        - ล็อกหน้าจอ (screen_locked) = พักการสลับทั้งหมด
        - armed ด้วย active_focus หรือ UDP activity <2 วิ (INCIDENT#2)"""
        now = time.monotonic()
        armed_by_focus = self.active_focus
        armed_by_udp = (now - self._last_udp_move) < 2.0
        if not (armed_by_focus or armed_by_udp) or self.screen_locked:
            return
        w, h = self.x11.width, self.x11.height
        # PY-F11 fix: ใช้พิกัดที่ track ไว้ต่อ "ทุก packet" — เดิม XQueryPointer ทุก move =
        # X server round-trip ~2000 ครั้ง/วินาทีที่เมาส์ 1000Hz (serialized บน lock เดียวกับ injection)
        # query จริงจะเกิดเฉพาะตอนกำลังจะ trigger (ยืนยันตำแหน่ง 1 ครั้งก่อนสลับ)
        rx, ry = self.x11.cur_x, self.x11.cur_y

        if self.node_position == "right":
            exit_left = True
            at_edge = rx <= 1
            pushing = dx < 0
        elif self.node_position == "left":
            exit_left = False
            at_edge = rx >= w - 2
            pushing = dx > 0
        elif self.node_position == "top":
            exit_left = False
            at_edge = ry >= h - 2
            pushing = dy > 0
        elif self.node_position == "bottom":
            exit_left = False
            at_edge = ry <= 1
            pushing = dy < 0
        else:
            exit_left = True
            at_edge = rx <= 1
            pushing = dx < 0

        # -- มุมจอตาย --
        if at_edge and self._corner_size > 0:
            if self.node_position in ("right", "left"):
                near_top = ry <= self._corner_size
                near_bottom = ry >= h - self._corner_size - 1
                if exit_left:
                    in_corner = (near_top and self._corners.get("tl", True)) or (near_bottom and self._corners.get("bl", True))
                else:
                    in_corner = (near_top and self._corners.get("tr", True)) or (near_bottom and self._corners.get("br", True))
            else:
                near_left = rx <= self._corner_size
                near_right = rx >= w - self._corner_size - 1
                if self.node_position == "top":
                    in_corner = (near_left and self._corners.get("bl", True)) or (near_right and self._corners.get("br", True))
                else:
                    in_corner = (near_left and self._corners.get("tl", True)) or (near_right and self._corners.get("tr", True))
            if in_corner:
                self._edge_push = 0.0
                self._at_edge_since = 0.0
                self._had_real_push = False
                return

        if at_edge:
            if self._at_edge_since == 0.0:
                # เพิ่งมาถึงขอบ -- เช็ค double-tap ก่อน
                since_leave_ms = (now - self._last_leave_at) * 1000.0 if self._last_leave_at > 0 else 999999.0
                if self._had_real_push and self._double_tap_ms > 0 and since_leave_ms <= self._double_tap_ms:
                    try:
                        rx, ry = self.x11.query_pointer()  # PY-F11: ยืนยันกับ X จริงก่อนสลับ
                        # tracked position อาจเพี้ยนจาก pointer acceleration ของ XTest — ต้องยังอยู่ขอบจริง
                        if self.node_position == "right":
                            at_edge = rx <= 1
                        elif self.node_position == "left":
                            at_edge = rx >= w - 2
                        elif self.node_position == "top":
                            at_edge = ry >= h - 2
                        else:
                            at_edge = ry <= 1
                    except Exception:
                        pass
                    if not at_edge:
                        return
                    log("[edge] double-tap (" + f"{since_leave_ms:.0f}" + "ms หลังออกจากขอบ) -> สลับทันที")
                    self._trigger_return(rx, ry, "double-tap")
                    return
                self._at_edge_since = now
                self._edge_push = 0.0
            if pushing:
                self._edge_push += abs(dy) if self.node_position in ("top", "bottom") else abs(dx)
            delay_s = max(0, self._switch_delay_ms) / 1000.0
            if self._edge_push >= EDGE_PUSH_THRESHOLD_PX and (now - self._at_edge_since) >= delay_s:
                try:
                    rx, ry = self.x11.query_pointer()  # PY-F11: ยืนยันกับ X จริงก่อนสลับ
                    if self.node_position == "right":
                        at_edge = rx <= 1
                    elif self.node_position == "left":
                        at_edge = rx >= w - 2
                    elif self.node_position == "top":
                        at_edge = ry >= h - 2
                    else:
                        at_edge = ry <= 1
                except Exception:
                    pass
                if not at_edge:
                    return
                self._trigger_return(rx, ry, "push" + str(int(self._edge_push)) + "px+hold" +
                                     f"{(now - self._at_edge_since) * 1000:.0f}" + "ms")
                return
        else:
            if self._at_edge_since != 0.0:
                # เพิ่งออกจากขอบ -- จำไว้เป็น "tap" ถ้าเคยดันจริงจัง
                self._last_leave_at = now
                self._had_real_push = self._edge_push >= EDGE_PUSH_THRESHOLD_PX * 0.5
                self._edge_push = 0.0
                self._at_edge_since = 0.0


        # v1.9.1: ขอบอื่นที่มีเพื่อนบ้าน (chain) - delay เดียวกัน, ไม่มี double-tap (v1)
        if self._check_neighbor_edges(rx, ry, w, h, dx, dy, now,
                                      max(0, self._switch_delay_ms) / 1000.0):
            return
    def _trigger_return(self, rx: int, ry: int, reason: str):
        # dedup burst (เห็นจริงคืน 2026-10-22: edge_return ยิง 7 ครั้งใน 4ms — packet ค้างในคิว
        # ยังเห็นพิกัดเก่าก่อน park มีผล) — หน้าต่าง 50ms พอสำหรับ burst เดียวกัน แต่ไม่แตะ
        # double-tap ที่สลับถี่กว่านั้นตามดีไซน์ (เคยลอง 250ms แล้วกลืนสลับที่ถูกต้อง — live test)
        now = time.monotonic()
        if now - getattr(self, "_last_edge_return_sent", 0.0) < 0.05:
            return
        self._last_edge_return_sent = now
        w, h = self.x11.width, self.x11.height
        norm_y = max(0.0, min(1.0, ry / max(1, h - 1)))
        norm_x = max(0.0, min(1.0, rx / max(1, w - 1)))
        if self.node_position == "right":
            edge = "left"
        elif self.node_position == "left":
            edge = "right"
        elif self.node_position == "top":
            edge = "bottom"
        elif self.node_position == "bottom":
            edge = "top"
        else:
            edge = "left"
        log(f"[edge] return to host (edge={edge}, reason={reason}, cursor=({rx},{ry}))")
        self.active_focus = False
        self._edge_push = 0.0
        self._at_edge_since = 0.0
        self._had_real_push = False
        self._last_leave_at = 0.0
        self.osd.set_active(False, self.x11.get_lang_group())
        self.x11.release_all()
        # Park cursor cleanly inside screen
        self.x11.move_abs(w // 2, int(norm_y * (h - 1)))
        self.send_tcp_message({"type": "edge_return", "edge": edge, "norm_y": norm_y, "norm_x": norm_x})
        self._push_clipboard_async()

    def udp_loop(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 262144)
        # F3: รอพอร์ตว่าง (โปรเซสเก่าอาจยังถืออยู่ชั่วครู่หลัง redeploy)
        for _ in range(10):
            try:
                sock.bind((self.bind_ip, KVM_UDP_PORT))
                break
            except OSError:
                time.sleep(0.4)
        else:
            print("[agent] cannot bind UDP port — udp loop exiting")
            return
        drop_count = 0
        policy_drop = 0
        replay_drop = 0
        while self.running:
            try:
                data, addr = sock.recvfrom(1024)
                # F2: ทิ้ง packet จาก IP ที่ไม่เคยยืนยัน token (กันใครในแลนยิงเมาส์แทน)
                # B2: + ต้องอยู่ใน allowlist subnet ด้วย (ถ้าตั้งไว้)
                if addr[0] not in self._authed_hosts or not _ip_allowed(addr[0]):
                    drop_count += 1
                    if drop_count == 1 or drop_count % 200 == 0:
                        print(f"[agent] dropped {drop_count} UDP packet(s) from unauthed {addr[0]}")
                    continue
                if not data:
                    continue
                # v1.6 hardening: ตรวจ HMAC-SHA256 trailer 16 ไบต์ท้าย packet — ปลอมไม่ได้ถ้าไม่รู้ token
                key = self._udp_key
                if key is not None:
                    if len(data) <= 16:
                        continue
                    if not hmac.compare_digest(
                            hmac.new(key, data[:-16], hashlib.sha256).digest()[:16], data[-16:]):
                        drop_count += 1
                        if drop_count == 1 or drop_count % 200 == 0:
                            print(f"[agent] dropped {drop_count} UDP packet(s) with BAD HMAC from {addr[0]}")
                        continue
                    data = data[:-16]
                    # v1.8 SV-L1: freshness window — seq = UnixMs ตอน host ส่ง (±5s)
                    # replay packet เก่า = ts ห่างจากปัจจุบันเกิน window → ทิ้ง
                    # (ทน multi-sender ต่อ IP + clock skew ±5s + host reboot โดยไม่ต้อง track ต่อ IP)
                    if len(data) < 9:
                        continue
                    seq = struct.unpack("!Q", data[-8:])[0]
                    now_ms = time.time() * 1000
                    if abs(now_ms - seq) > 5000:
                        replay_drop += 1
                        if replay_drop == 1 or replay_drop % 200 == 0:
                            print(f"[agent] dropped {replay_drop} STALE/REPLAYED packet(s) from {addr[0]} (seq={seq}, drift={now_ms - seq:.0f}ms)")
                        continue
                    data = data[:-8]
                # v1.7 C3: policy enforcement ฝั่งรับ — packet เกินสิทธิ์ถูกทิ้ง (นับแยก)
                if not _packet_allowed(self.remote_policy, data[0]):
                    policy_drop += 1
                    if policy_drop == 1 or policy_drop % 200 == 0:
                        print(f"[agent] dropped {policy_drop} UDP packet(s) over policy "
                              f"'{self.remote_policy}' (type=0x{data[0]:02x}) from {addr[0]}")
                    continue
                # INCIDENT#2: ติดตาม UDP activity — ใช้ arm edge detection แม้ active_focus หลุด
                if data[0] in (PKT_MOUSE_MOVE_REL, PKT_MOUSE_MOVE_ABS):
                    nowm = time.monotonic()
                    if nowm - self._last_udp_move > 2.0:
                        log("[udp] mouse activity resumed (edge detection armed)")
                    self._last_udp_move = nowm
                pkt_type = data[0]
                if pkt_type == PKT_MOUSE_MOVE_REL and len(data) >= 6:
                    _, dx, dy, mod_mask = struct.unpack("!BhhB", data[:6])
                    self.x11.sync_modifiers(mod_mask)
                    nx, ny = self.x11.move_rel(dx, dy)
                    self.check_edge_return(nx, ny, dx, dy)
                elif pkt_type == PKT_MOUSE_MOVE_ABS and len(data) >= 6:
                    _, ix, iy, mod_mask = struct.unpack("!BHHB", data[:6])
                    self.x11.sync_modifiers(mod_mask)
                    x = int((ix / 65535.0) * (self.x11.width - 1))
                    y = int((iy / 65535.0) * (self.x11.height - 1))
                    self.x11.move_abs(x, y)
                elif pkt_type == PKT_MOUSE_BUTTON and len(data) >= 4:
                    _, btn, down, mod_mask = struct.unpack("!BBBB", data[:4])
                    self.x11.sync_modifiers(mod_mask)
                    self.x11.mouse_button(btn, bool(down))
                elif pkt_type == PKT_MOUSE_WHEEL and len(data) >= 6:
                    _, wx, wy, mod_mask = struct.unpack("!BhhB", data[:6])
                    self.x11.sync_modifiers(mod_mask)
                    self.x11.mouse_wheel(wx, wy)
                elif pkt_type == PKT_KEY_EVENT and len(data) >= 5:
                    _, kc, down, mod_mask = struct.unpack("!BHBB", data[:5])
                    self.x11.key_event(kc, bool(down))
                elif pkt_type == PKT_SET_LANG and len(data) >= 3:
                    _, group, _ = struct.unpack("!BBB", data[:3])
                    threading.Thread(target=self._apply_lang_async, args=(group,), daemon=True).start()
                elif pkt_type == PKT_RELEASE_ALL:
                    self.x11.release_all()
            except Exception:
                pass

    def discovery_loop(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        except Exception:
            pass
        for _ in range(5):
            try:
                sock.bind((self.bind_ip, DISCOVERY_PORT))
                break
            except OSError:
                time.sleep(0.5)
        else:
            log(f"[discovery] cannot bind UDP port {DISCOVERY_PORT}")
            return
        log(f"[discovery] listening on UDP port {DISCOVERY_PORT} for LAN discovery beacons")
        while self.running:
            try:
                data, addr = sock.recvfrom(2048)
                if not data:
                    continue
                # B2: ตอบเฉพาะผู้ถามที่อยู่ใน allowlist (ถ้าตั้งไว้) — ไม่เปิดเผยข้อมูลเครื่องให้คนแปลกหน้า
                if not _ip_allowed(addr[0]):
                    continue
                try:
                    msg = json.loads(data.decode("utf-8"))
                except Exception:
                    continue
                if isinstance(msg, dict) and msg.get("cmd") == "DISCOVER_OMNILINK":
                    announce = {
                        "cmd": "ANNOUNCE",
                        "uuid": self.machine_uuid,
                        "name": socket.gethostname(),
                        "os": "Windows" if IS_WINDOWS else "Linux",
                        "port": KVM_TCP_PORT,
                        "width": self.x11.width,
                        "height": self.x11.height,
                        "paired": bool(self.auth_token),
                        "version": AGENT_VERSION,
                    }
                    resp_bytes = json.dumps(announce, ensure_ascii=False).encode("utf-8")
                    sock.sendto(resp_bytes, addr)
            except Exception:
                time.sleep(0.5)

    def _apply_lang_async(self, group: int):
        actual = self.x11.set_lang_group(group)
        self.osd.set_active(self.active_focus, actual)
        self.send_tcp_message({
            "type": "lang_ack",
            "lang_group": actual,
        })

    def handle_tcp_client(self, conn: socket.socket, addr):
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

        # B2: allowlist — ปฏิเสธก่อน TLS handshake เลย (กันแม้แต่การสอบถาม)
        if not _ip_allowed(addr[0]):
            log(f"[allowlist] ปฏิเสธ connection จาก {addr[0]} (นอก subnet ที่อนุญาต)")
            try:
                conn.close()
            except Exception:
                pass
            return

        # v1.6 hardening: ทุก connection ต้องผ่าน TLS handshake ก่อน (fail-closed เมื่อไม่มี cert)
        if self.tls_ctx is not None:
            try:
                conn = self.tls_ctx.wrap_socket(conn, server_side=True)
            except (ssl.SSLError, OSError) as ex:
                log(f"[tls] handshake failed from {addr[0]}: {ex}")
                try:
                    conn.close()
                except Exception:
                    pass
                return
        else:
            log(f"[tls] ไม่มี TLS context — ปฏิเสธ connection จาก {addr[0]} (fail-closed)")
            try:
                conn.close()
            except Exception:
                pass
            return

        # F2: ข้อความแรกสุดต้องเป็น auth ที่ถูกต้อง หรือ pair_request
        buf = b""
        authed = False
        try:
            conn.settimeout(5.0)
            while self.running and not authed:
                chunk = conn.recv(4096)
                if not chunk:
                    return
                buf += chunk
                while len(buf) >= 4 and not authed:
                    msg_len = struct.unpack("!I", buf[:4])[0]
                    if msg_len > 65536:  # F1: cap เท่าฝั่ง Host
                        print(f"[agent] oversized auth msg_len={msg_len} — drop")
                        conn.close()
                        return
                    if len(buf) < 4 + msg_len:
                        break
                    first = json.loads(buf[4:4 + msg_len].decode("utf-8"))
                    buf = buf[4 + msg_len:]

                    if isinstance(first, dict) and first.get("type") == "pair_request":
                        # v1.6 hardening (PY-F1): จำกัดการเดา PIN ก่อนเทียบทุกครั้ง
                        if not self._pair_rate_limited(addr[0]):
                            log(f"[pair] {addr[0]} ถูกบล็อกชั่วคราว (เดา PIN เกินงบ) — ปฏิเสธโดยไม่เทียบ")
                            self._send_direct(conn, {"type": "pair_reject", "reason": "Too many attempts, try later"})
                            conn.close()
                            return
                        req_pin = str(first.get("pin", "")).strip().replace(" ", "").replace("-", "")
                        my_pin_clean = self.pairing_pin.replace(" ", "").replace("-", "")
                        if hmac.compare_digest(req_pin, my_pin_clean):
                            import secrets
                            new_token = secrets.token_hex(32)
                            try:
                                _write_secret(os.path.expanduser("~/omnilink/auth_token"), new_token)
                            except Exception as ex:
                                log(f"[pair] Error saving auth_token: {ex}")
                            self.auth_token = new_token
                            self._udp_key = self._derive_udp_key()  # v1.6: คีย์ HMAC ต้องตาม token ใหม่
                            self._authed_hosts.add(addr[0])
                            # สุ่ม PIN ใหม่สำหรับครั้งถัดไป (CSPRNG — PY-F1)
                            self.pairing_pin = _generate_pin()
                            try:
                                _write_secret(os.path.expanduser("~/omnilink/pairing_pin"), self.pairing_pin)
                            except Exception:
                                pass
                            log(f"[pair] Pair accepted from {addr[0]} ({first.get('host_name', 'Host')})!")
                            self._send_direct(conn, {
                                "type": "pair_accept",
                                "token": new_token,
                                "uuid": self.machine_uuid,
                                "name": socket.gethostname(),
                                "width": self.x11.width,
                                "height": self.x11.height,
                                # v1.6 TLS TOFU: fingerprint ที่ host ต้อง pin — ต้องตรงกับ cert ที่ใช้บนสายนี้
                                "cert_fingerprint": self.cert_fingerprint or "",
                            })
                            threading.Thread(target=self.osd.notify_connect, args=(addr[0],), daemon=True).start()
                        else:
                            attempts = self._record_pair_failure(addr[0])
                            # PY-F2: ห้ามเปิดเผย PIN (ทั้งของผู้เดาและของจริง) ลง log — บอกแค่สถิติ
                            log(f"[pair] Pair rejected from {addr[0]}: Invalid PIN (attempt {attempts}/10 in window)")
                            self._send_direct(conn, {
                                "type": "pair_reject",
                                "reason": "Invalid PIN"
                            })
                        conn.close()
                        return

                    if (first.get("type") == "auth" and self.auth_token
                            and hmac.compare_digest(str(first.get("token", "")), self.auth_token)):
                        authed = True
                        log("[auth] " + addr[0] + " ยืนยันสำเร็จ")
                    else:
                        log("[auth] ปฏิเสธการเชื่อมต่อจาก " + addr[0])
                        conn.close()
                        return
            if not authed:
                conn.close()
                return
            conn.settimeout(None)
        except Exception:
            try:
                conn.close()
            except Exception:
                pass
            return

        self._authed_hosts.add(addr[0])  # ★ อนุญาต UDP จาก host นี้
        # PY-F5 fix: ผูก allowlist กับอายุ connection — ตัดแล้วหลุดทันที (refcount กัน conn ใหม่โดน conn เก่าปิดพาลุกฮือ)
        with self._authed_lock:
            self._authed_conns[addr[0]] = self._authed_conns.get(addr[0], 0) + 1

        # INCIDENT#2 (รากของเมาส์ติดครั้งที่ 2): การเชื่อมต่อใหม่ต้อง "ปิดการเชื่อมต่อเก่าทิ้ง"
        # เดิม: conn ใหม่แทนที่ active_tcp_conn → conn เก่าค้างเป็นเบี้ยหนู → เมื่อ conn ทดสอบปิด
        # active_tcp_conn กลายเป็น None → conn จริงของ Host กลายเป็นท้าม → edge_return หล่นหายเงียบ
        # ใหม่: conn ใหม่ชนะเสมอ + บังคับปิด conn เก่า (ฝั่ง Host จะรู้ตัวและ reconnect มาเป็น active เอง)
        with self.tcp_lock:
            prev = self.active_tcp_conn
            self.active_tcp_conn = conn
        if prev is not None and prev is not conn:
            try:
                prev.shutdown(socket.SHUT_RDWR)
            except Exception:
                pass
            try:
                prev.close()
            except Exception:
                pass
            log("[tcp] การเชื่อมต่อใหม่เข้ามาแทน — ปิด conn เดิมทิ้ง (ป้องกัน orphaned conn)")

        # INCIDENT#2 ส่วนที่สอง: auth_ok/hello ต้องส่ง "ตรง" ไปที่ socket นี้เสมอ
        # เดิมส่งผ่าน active_tcp_conn ซึ่ง thread อื่นเปลี่ยนได้ระหว่างนั้น → ข้อความไผ่คนละซอง
        try:
            self._send_direct(conn, {"type": "auth_ok"})
        except Exception as ex:
            log("[tcp] ส่ง auth_ok ไม่สำเร็จ: " + str(ex))
            return

        host_ip = addr[0]
        # INCIDENT#2: notification ไม่ควรบล็อก connection thread — ยิงคนละเธรด
        threading.Thread(target=self.osd.notify_connect, args=(host_ip,), daemon=True).start()
        self.osd.set_active(False, self.x11.get_lang_group())

        # Send immediate hello with screen resolution and current language
        self.x11.refresh_dimensions()
        pb_alive, mic_alive = self.audio.get_status()
        try:
            self._send_direct(conn, {
                "type": "hello",
                "auth": True,  # F2c: บอกฝั่ง Host ว่าเป็น agent รุ่นใหม่แล้ว
                "version": AGENT_VERSION,  # version handshake — host เทียบแล้ว deploy ทับถ้าไม่ตรง
                "hostname": socket.gethostname(),
                "width": self.x11.width,
                "height": self.x11.height,
                "lang_group": self.x11.get_lang_group(),
                "playback_running": pb_alive,
                "playback_active": self.audio.is_playback_active(),
                "mic_running": mic_alive,
                **get_system_metrics(),
            })
        except Exception as ex:
            log("[tcp] ส่ง hello ไม่สำเร็จ: " + str(ex))
            return
        log("[tcp] conn ready (hello sent) — เข้าสู่ read loop")

        # PY-F7 fix: ใช้ buf ต่อจาก auth loop — bytes ที่มาพร้อมกับ auth (เช่น set_lang ที่ host
        # ยิงตามมาทันที) ห้ามทิ้ง (เดิม `buf = b""` ทำให้คำสั่งแรกหลัง connect หล่นเงียบๆ)
        try:
            while self.running:
                chunk = conn.recv(4096)
                if not chunk:
                    break
                buf += chunk
                while len(buf) >= 4:
                    msg_len = struct.unpack("!I", buf[:4])[0]
                    if msg_len > 1024 * 128:  # 128KB max message limit
                        raise ValueError(f"oversized msg_len={msg_len}")
                    if len(buf) < 4 + msg_len:
                        break
                    raw_msg = buf[4:4 + msg_len]
                    buf = buf[4 + msg_len:]
                    try:
                        self.process_control_msg(json.loads(raw_msg.decode("utf-8")), host_ip)
                    except Exception as ex:
                        # PY-F9 (ครึ่งแรก): ข้อความเดียวพัง = ทิ้งข้อความนั้น ไม่ตัดทั้ง connection
                        # + log เสมอ (เดิม exception แทรกจาก process_control_msg ตัด conn เงียบๆ
                        # จนวินิจฉัยไม่ได้ — เจอจริง 23 ต.ค. ตอน debug clipboard echo)
                        log(f"[tcp] process_control_msg failed: {ex.__class__.__name__}: {ex}")
        except Exception as ex:
            if not isinstance(ex, (ConnectionResetError, BrokenPipeError, socket.timeout, ssl.SSLError, OSError)):
                log(f"[tcp] read loop ended: {ex.__class__.__name__}: {ex}")
        finally:
            self.x11.release_all()
            self.active_focus = False
            self.osd.set_active(False, self.x11.get_lang_group())
            # PY-F5 fix: หมด connection นี้ → ถอนสิทธิ์ UDP ของ IP นี้ (token ต้องยืนยันใหม่ทุก session)
            with self._authed_lock:
                remaining = self._authed_conns.get(addr[0], 0) - 1
                if remaining <= 0:
                    self._authed_conns.pop(addr[0], None)
                    self._authed_hosts.discard(addr[0])
                else:
                    self._authed_conns[addr[0]] = remaining
            with self.tcp_lock:
                if self.active_tcp_conn is conn:
                    self.active_tcp_conn = None
                    log("[tcp] active host conn ปิดลง — รอ Host reconnect (edge_return จะไม่ถูกส่งจนกว่าจะ reconnect)")
            try:
                conn.close()
            except Exception:
                pass

    def process_control_msg(self, msg: dict, host_ip: str):
        mtype = msg.get("type")
        if mtype == "set_layout":
            # v1.9.1 chain topology: edge -> target ("__host" = edge_return เดิม, อื่นๆ = switch_request)
            nb = msg.get("neighbors")
            if isinstance(nb, dict):
                self.neighbors = {str(k): str(v) for k, v in nb.items() if v}
                self._neighbor_state = {}
                log("[layout] neighbors=" + json.dumps(self.neighbors))
        elif mtype == "set_policy":
            # v1.7 C3: host กำหนด capability ของ node นี้ — enforce ทันที (ทั้ง UDP และ control)
            p = msg.get("policy", "full")
            self.remote_policy = p if p in ("full", "clipboard", "view") else "full"
            log(f"[policy] host set policy='{self.remote_policy}'")
            if self.remote_policy != "full" and self.active_focus:
                # กันค้าง: สิทธิ์ลดระดับขณะถูกควบคุม — ปล่อยปุ่ม + คืนสถานะทันที
                self.active_focus = False
                self.x11.release_all()
                self.osd.set_active(False, self.x11.get_lang_group())
                self._push_clipboard_async()
            return

        if mtype == "enter_screen":
            # v1.7 C3: enter_screen = เริ่มถูกควบคุม — node ที่ไม่ใช่ full ปฏิเสธเสมอ
            pol = msg.get("policy")
            if pol in ("full", "clipboard", "view"):
                self.remote_policy = pol
            if self.remote_policy != "full":
                log(f"[policy] ปฏิเสธ enter_screen — policy='{self.remote_policy}' ไม่อนุญาต input")
                return
            self.node_position = msg.get("position", "right")
            norm_y = float(msg.get("norm_y", 0.5))
            norm_x = float(msg.get("norm_x", 0.5))
            # PY-F15 fix: รับสถานะล็อกจาก host ด้วย (agent restart ขณะ host ล็อกอยู่ = ต้องรู้ทันที)
            locked = msg.get("locked")
            if locked is not None:
                self.screen_locked = bool(locked)
            lang_group = msg.get("lang_group")
            if lang_group is not None:
                self.x11.set_lang_group(int(lang_group))
            self.x11.refresh_dimensions()
            if self.node_position == "right":
                entry_x = 24
                entry_y = int(norm_y * (self.x11.height - 1))
            elif self.node_position == "left":
                entry_x = max(0, self.x11.width - 25)
                entry_y = int(norm_y * (self.x11.height - 1))
            elif self.node_position == "top":
                entry_x = int(norm_x * (self.x11.width - 1))
                entry_y = max(0, self.x11.height - 25)
            elif self.node_position == "bottom":
                entry_x = int(norm_x * (self.x11.width - 1))
                entry_y = 24
            else:
                entry_x = 24
                entry_y = int(norm_y * (self.x11.height - 1))
            self.x11.move_abs(entry_x, entry_y)
            self._edge_push = 0.0  # เริ่มจอใหม่ — ล้างสถานะดันขอบ
            self._at_edge_since = 0.0
            self._had_real_push = False
            self._last_leave_at = 0.0
            self._last_edge_return_sent = 0.0  # session ใหม่ = สิทธิ์สลับใหม่ (ไม่โดน dedup เงาเก่า)
            # A1: Host ส่งพารามิเตอร์การสลับจอมาพร้อม enter_screen
            try:
                self._switch_delay_ms = max(0, int(msg.get("switch_delay_ms", self._switch_delay_ms)))
                self._double_tap_ms = max(0, int(msg.get("double_tap_ms", self._double_tap_ms)))
                self._corner_size = max(0, int(msg.get("corner_size", self._corner_size)))
                cn = msg.get("corners")
                if isinstance(cn, dict):
                    self._corners = {k: bool(v) for k, v in cn.items()}
            except Exception:
                pass
            self.active_focus = True
            self.osd.set_active(True, self.x11.get_lang_group())
            log(f"[kvm] enter_screen pos={self.node_position} y={entry_y} "
                f"(delay={self._switch_delay_ms}ms tap={self._double_tap_ms}ms corner={self._corner_size}px)")

        elif mtype == "leave_screen":
            self.active_focus = False
            self.x11.release_all()
            self.osd.set_active(False, self.x11.get_lang_group())
            self._push_clipboard_async()

        elif mtype == "clipboard":
            if self.remote_policy == "view":
                # v1.7 C3: view-only = ไม่รับ clipboard
                return
            mime = msg.get("mime", "text/plain")
            if mime == "image/png" or "data" in msg:
                import base64
                raw_bytes = base64.b64decode(msg.get("data", ""))
                ok = self.clip.set_png(raw_bytes)
                log(f"[clipboard] ตั้งค่าภาพ PNG จาก Host len={len(raw_bytes)} ok={ok}")
                if ok:
                    self._record_clip_exchange(hashlib.sha256(b"image|" + raw_bytes).hexdigest())
            else:
                text = msg.get("text", "")
                if text:
                    ok = self.clip.set_text(text)
                    log(f"[clipboard] ตั้งค่าจาก Host len={len(text)} ok={ok}")
                    if ok:
                        self._record_clip_exchange(hashlib.sha256(("text|" + text).encode("utf-8")).hexdigest())

        elif mtype == "clip_meta":
            token = msg.get("token")
            mime = msg.get("mime", "text/plain")
            size = msg.get("size", 0)
            log(f"[clipboard] clip_meta received token={token} mime={mime} size={size}")
            self.send_tcp_message({"type": "clip_fetch", "token": token})

        elif mtype == "clip_fetch":
            token = msg.get("token")
            if token and token in self._pending_clips:
                data_bytes, mime = self._pending_clips[token]
                import base64
                self.send_tcp_message({
                    "type": "clip_data",
                    "token": token,
                    "mime": mime,
                    "data": base64.b64encode(data_bytes).decode("ascii")
                })

        elif mtype == "clip_data":
            token = msg.get("token")
            mime = msg.get("mime", "text/plain")
            import base64
            data_bytes = base64.b64decode(msg.get("data", ""))
            if mime == "image/png":
                ok = self.clip.set_png(data_bytes)
                log(f"[clipboard] ตั้งค่าภาพ PNG จาก Host (on-demand) len={len(data_bytes)} ok={ok}")
            else:
                text = data_bytes.decode("utf-8", errors="replace")
                ok = self.clip.set_text(text)
                log(f"[clipboard] ตั้งค่าข้อความจาก Host (on-demand) len={len(text)} ok={ok}")

        elif mtype == "file_offer":
            if self.remote_policy == "view":
                # v1.7 C3: view-only = ไม่รับไฟล์
                log("[policy] ปฏิเสธ file_offer (view-only)")
                return
            import pathlib  # json+hashlib เป็น module-level แล้ว — local import ทำให้ hashlib เป็น local ทั้งฟังก์ชัน (UnboundLocalError x2)
            manifest_json = msg.get("manifest")
            if manifest_json:
                manifest = json.loads(manifest_json)
                transfer_id = manifest["id"]
                target_dir = os.path.expanduser("~/Downloads/OmniLink")
                staging_dir = os.path.join(target_dir, ".omni_staging", transfer_id)
                os.makedirs(staging_dir, exist_ok=True)
                os.makedirs(target_dir, exist_ok=True)

                self._inbound_files[transfer_id] = {
                    "manifest": manifest,
                    "target_dir": target_dir,
                    "staging_dir": staging_dir,
                    "states": {},
                    "completed": []
                }
                self.send_tcp_message({"type": "file_accept", "id": transfer_id})
                log(f"[file] Accepted file_offer id={transfer_id} files={len(manifest['files'])}")

        elif mtype == "file_accept":
            transfer_id = msg.get("id")
            if transfer_id in self._outbound_files:
                def _stream():
                    import base64
                    sess = self._outbound_files[transfer_id]
                    manifest = sess["manifest"]
                    for idx, entry in enumerate(manifest["files"]):
                        local_path = sess["file_map"][idx]
                        if entry.get("is_dir"):
                            self.send_tcp_message({"type": "file_done", "id": transfer_id, "idx": idx})
                            continue
                        with open(local_path, "rb") as f:
                            offset = 0
                            while chunk := f.read(32768):
                                b64 = base64.b64encode(chunk).decode("ascii")
                                self.send_tcp_message({
                                    "type": "file_chunk",
                                    "id": transfer_id,
                                    "idx": idx,
                                    "offset": offset,
                                    "data": b64
                                })
                                offset += len(chunk)
                                time.sleep(0.001)
                        self.send_tcp_message({"type": "file_done", "id": transfer_id, "idx": idx})
                    self.send_tcp_message({"type": "file_complete", "id": transfer_id, "success": True})
                    log(f"[file] Outbound stream finished id={transfer_id}")
                threading.Thread(target=_stream, daemon=True).start()

        elif mtype == "file_chunk":
            transfer_id = msg.get("id")
            idx = int(msg.get("idx", 0))
            offset = int(msg.get("offset", 0))
            data_b64 = msg.get("data", "")
            if transfer_id in self._inbound_files:
                import base64  # hashlib เป็น module-level แล้ว (เดิม local import ทำ UnboundLocalError)
                sess = self._inbound_files[transfer_id]
                entry = sess["manifest"]["files"][idx]
                if idx not in sess["states"]:
                    safe_name = os.path.basename(entry["rel_path"])
                    part_path = os.path.join(sess["staging_dir"], f"{safe_name}.part")
                    final_path = os.path.join(sess["target_dir"], safe_name)
                    mode = "r+b" if os.path.exists(part_path) and offset > 0 else "wb"
                    f = open(part_path, mode)
                    if offset > 0:
                        f.seek(offset)
                    hasher = hashlib.sha256()
                    sess["states"][idx] = {
                        "file": f,
                        "hasher": hasher,
                        "part_path": part_path,
                        "final_path": final_path
                    }
                st = sess["states"][idx]
                raw_data = base64.b64decode(data_b64)
                st["file"].write(raw_data)
                st["hasher"].update(raw_data)

        elif mtype == "file_done":
            transfer_id = msg.get("id")
            idx = int(msg.get("idx", 0))
            if transfer_id in self._inbound_files:
                import shutil
                sess = self._inbound_files[transfer_id]
                entry = sess["manifest"]["files"][idx]
                if entry.get("is_dir"):
                    status = "ok"
                elif idx in sess["states"]:
                    st = sess["states"][idx]
                    st["file"].close()
                    calculated_sha = st["hasher"].hexdigest().lower()
                    expected_sha = entry["sha256"].lower()
                    if calculated_sha == expected_sha:
                        shutil.move(st["part_path"], st["final_path"])
                        sess["completed"].append(st["final_path"])
                        status = "ok"
                        log(f"[file] File verified: {st['final_path']}")
                    else:
                        status = "hash_mismatch"
                        log(f"[file] Hash mismatch for {st['final_path']}")
                else:
                    status = "missing"
                self.send_tcp_message({"type": "file_result", "id": transfer_id, "idx": idx, "status": status})

        elif mtype == "file_result":
            transfer_id = msg.get("id")
            idx = msg.get("idx")
            status = msg.get("status")
            log(f"[file] file_result id={transfer_id} idx={idx} status={status}")

        elif mtype == "file_complete":
            transfer_id = msg.get("id")
            if transfer_id in self._inbound_files:
                import shutil, subprocess
                sess = self._inbound_files.pop(transfer_id)
                shutil.rmtree(sess["staging_dir"], ignore_errors=True)
                completed = sess["completed"]
                is_cut = (sess["manifest"].get("effect") == "cut")
                if completed:
                    self.clip.set_files(completed, is_cut=is_cut)
                    log(f"[file] Transfer complete! Saved {len(completed)} files to {sess['target_dir']}")
                    try:
                        subprocess.Popen(["notify-send", "OmniLink", f"ได้รับ {len(completed)} ไฟล์เรียบร้อยแล้ว (วางด้วย Ctrl+V)"])
                    except Exception:
                        pass

        elif mtype == "lock":
            self.screen_locked = bool(msg.get("on", False))
            if self.screen_locked:
                self._edge_push = 0.0
                self._at_edge_since = 0.0
            log("[kvm] screen lock = " + str(self.screen_locked))

        elif mtype == "set_lang":
            group = int(msg.get("group", 0))
            self.x11.set_lang_group(group)
            current_lang = self.x11.get_lang_group()
            self.osd.set_active(self.active_focus, current_lang)
            self.send_tcp_message({
                "type": "lang_ack",
                "lang_group": current_lang,
            })

        elif mtype == "toggle_lang":
            cur = self.x11.get_lang_group()
            nxt = 0 if cur >= 1 else 1
            self.x11.set_lang_group(nxt)
            current_lang = self.x11.get_lang_group()
            self.osd.set_active(self.active_focus, current_lang)
            self.send_tcp_message({
                "type": "lang_ack",
                "lang_group": current_lang,
            })

        elif mtype == "key_event":
            kc = int(msg.get("keycode", 0))
            down = bool(msg.get("down", False))
            self.x11.key_event(kc, down)

        elif mtype == "audio_control":
            action = msg.get("action")
            target_host = msg.get("host_ip", host_ip)
            if action == "start_stream":
                self.audio.start_playback_sender(target_host)
            elif action == "stop_stream":
                self.audio.stop_playback_sender()
            elif action == "start_mic":
                self.audio.start_mic_receiver(target_host)
            elif action == "stop_mic":
                self.audio.stop_mic_receiver()
            elif action == "restart_all":
                self.audio.start_playback_sender(target_host)
                self.audio.start_mic_receiver(target_host)

            pb_alive, mic_alive = self.audio.get_status()
            self.send_tcp_message({
                "type": "telemetry",
                "lang_group": self.x11.get_lang_group(),
                "playback_running": pb_alive,
                "playback_active": self.audio.is_playback_active(),
                "mic_running": mic_alive,
                **get_system_metrics(),
            })

        elif mtype == "ping":
            pb_alive, mic_alive = self.audio.get_status()
            self.send_tcp_message({
                "type": "pong",
                "ts": msg.get("ts", time.time()),
                "lang_group": self.x11.get_lang_group(),
                # INCIDENT#2: รายงานสถานะ focus กลับไปให้ Host ตรวจ desync
                # (Host คิดว่าอยู่ remote แต่ agent ไม่ focused = ต้อง re-assert enter_screen)
                "focused": self.active_focus,
                "focused_by_udp": (time.monotonic() - self._last_udp_move) < 2.0,
                "playback_running": pb_alive,
                "playback_active": self.audio.is_playback_active(),
                "mic_running": mic_alive,
                "width": self.x11.width,
                "height": self.x11.height,
                **get_system_metrics(),
            })

    def run(self):
        t_udp = threading.Thread(target=self.udp_loop, daemon=True, name="OmniLink_Udp")
        t_udp.start()
        t_disc = threading.Thread(target=self.discovery_loop, daemon=True, name="OmniLink_Discovery")
        t_disc.start()

        # ปลุก Host ทันทีที่สตาร์ต (Reverse Knock UDP 24800)
        self.send_reverse_knock()

        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        # F3: รอ/แย่งพอร์ตคืนจาก agent ตัวเก่า (ฆ่าเฉพาะตัวอื่น ไม่แตะตัวเอง) — กัน deploy ชนกัน
        me = os.getpid()
        for _ in range(15):
            try:
                srv.bind((self.bind_ip, KVM_TCP_PORT))
                break
            except OSError:
                if not IS_WINDOWS:
                    res = subprocess.run(["pgrep", "-f", "omnilink_agent.py"], stdout=subprocess.PIPE, text=True)
                    for line in res.stdout.splitlines():
                        try:
                            pid = int(line.strip())
                            if pid != me:
                                os.kill(pid, signal.SIGKILL)
                        except Exception:
                            pass
                time.sleep(0.4)
        else:
            print("[agent] cannot bind TCP port — exiting")
            return
        srv.listen(5)
        import gc
        gc.collect()
        print(f"[OmniLink Agent] Listening on TCP:{KVM_TCP_PORT} & UDP:{KVM_UDP_PORT} (Screen {self.x11.width}x{self.x11.height}, auth={'on' if self.auth_token else 'FAIL-CLOSED'})")

        while self.running:
            try:
                conn, addr = srv.accept()
                t_client = threading.Thread(target=self.handle_tcp_client, args=(conn, addr), daemon=True)
                t_client.start()
            except Exception:
                time.sleep(0.5)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="OmniLink Node Agent (Linux X11 + Windows)")
    parser.add_argument("--bind", default="0.0.0.0", help="IP address to bind")
    args = parser.parse_args()

    agent = OmniLinkAgentServer(bind_ip=args.bind, auth_token=_load_token())
    _ALLOWED_SUBNETS.extend(_load_allowlist())
    log("[start] OmniLink agent v" + AGENT_VERSION + " pid=" + str(os.getpid()))

    # D2: ตายแบบสุภาพ — หยุด pipeline เสียง (คืน default sink) และคืนปุ่มที่ค้างทั้งหมด
    def _graceful_term(signum, frame):
        try:
            agent.audio.stop_playback_sender()
            agent.audio.stop_mic_receiver()
            agent.x11.release_all()
        except Exception:
            pass
        os._exit(0)

    signal.signal(signal.SIGTERM, _graceful_term)
    signal.signal(signal.SIGINT, _graceful_term)

    agent.run()
