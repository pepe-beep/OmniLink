# -*- coding: utf-8 -*-
"""เฟส 5: Windows platform layer ของ node agent — ทดสอบจริงบนเครื่อง Windows
(Win32Controller ขยับเมาส์จริง / keycode map / ภาษา / Win32Clipboard roundtrip /
 server __init__ smoke test แบบ isolate โฮม)
รันเฉพาะบน Windows — บน Linux ให้ข้าม (exit 0)
"""
import ctypes
import os
import sys
import tempfile
import unittest
from pathlib import Path

if not sys.platform.startswith("win"):
    print("skip: not Windows")
    raise SystemExit(0)

_here = Path(__file__).resolve()
_agent_dir = _here.parent  # fallback: agent วางข้างไฟล์
for _p in _here.parents:
    if (_p / "node_agent" / "omnilink_agent.py").exists():
        _agent_dir = _p / "node_agent"
        break
sys.path.insert(0, str(_agent_dir))
import omnilink_agent as A  # noqa: E402


class TestWin32Controller(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.ctl = A.Win32Controller()

    def test_dimensions_match_system(self):
        self.assertEqual(self.ctl.width, ctypes.windll.user32.GetSystemMetrics(0))
        self.assertEqual(self.ctl.height, ctypes.windll.user32.GetSystemMetrics(1))
        self.assertGreater(self.ctl.width, 0)

    def test_query_pointer_sane(self):
        x, y = self.ctl.query_pointer()
        self.assertTrue(0 <= x <= self.ctl.width)
        self.assertTrue(0 <= y <= self.ctl.height)

    def test_move_abs_moves_real_cursor(self):
        target_x = self.ctl.width // 4
        target_y = self.ctl.height // 2
        self.ctl.move_abs(target_x, target_y)
        x, y = self.ctl.query_pointer()
        self.assertLessEqual(abs(x - target_x), 3, f"warp จริงไม่ตรง: {x} vs {target_x}")
        self.assertLessEqual(abs(y - target_y), 3, f"warp จริงไม่ตรง: {y} vs {target_y}")

    def test_keycode_map_core_keys(self):
        # modifier พิเศษจากตาราง
        self.assertEqual(self.ctl.keycode_to_vk(50), (0xA0, False))   # LShift
        self.assertEqual(self.ctl.keycode_to_vk(133), (0x5B, False))  # Super_L → VK_LWIN
        self.assertEqual(self.ctl.keycode_to_vk(105), (0xA3, True))   # RCtrl extended
        # คีย์ทั่วไปผ่าน scan: X keycode 65 = space (scan 57) → VK_SPACE
        vk, _ = self.ctl.keycode_to_vk(65)
        self.assertEqual(vk, 0x20)

    def test_lang_group_shape(self):
        self.assertIn(self.ctl.get_lang_group(), (0, 1))

    def test_release_all_no_throw(self):
        self.ctl.release_all()  # ต้องไม่โยนแม้ไม่มีปุ่มกดค้าง


class TestWin32Clipboard(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.clip = A.Win32Clipboard()

    def test_text_roundtrip(self):
        ok = self.clip.set_text("omnilink-win-test-ทดสอบ-123")
        self.assertTrue(ok)
        self.assertEqual(self.clip.get_text(), "omnilink-win-test-ทดสอบ-123")


class TestServerWindowsInit(unittest.TestCase):
    """smoke test: __init__ ของ server บน Windows ต้องสร้าง platform trio ครบโดยไม่แตะ X11"""

    def test_init_creates_windows_platform(self):
        with tempfile.TemporaryDirectory() as home:
            old = {k: os.environ.get(k) for k in ("HOME", "USERPROFILE")}
            try:
                os.environ["HOME"] = home
                os.environ["USERPROFILE"] = home
                os.environ["OMNILINK_TOKEN"] = "t" * 64  # มี token = ไม่เด้ง PIN ตอน init
                srv = A.OmniLinkAgentServer(bind_ip="127.0.0.1")
                self.assertIsInstance(srv.x11, A.Win32Controller)
                self.assertIsInstance(srv.clip, A.Win32Clipboard)
                self.assertIsInstance(srv.osd, A.NullStatusUi)
                self.assertIsInstance(srv.audio, A.NullAudioPipeline)
                self.assertTrue(A.IS_WINDOWS)
            finally:
                for k, v in old.items():
                    if v is None:
                        os.environ.pop(k, None)
                    else:
                        os.environ[k] = v
                os.environ.pop("OMNILINK_TOKEN", None)


if __name__ == "__main__":
    unittest.main(verbosity=2)
