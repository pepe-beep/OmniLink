"""v1.7 C3: unit test ของ capability matrix ฝั่ง agent (_packet_allowed)
รันได้บน Windows ไม่ต้องมี X11 (import ตรงๆ จาก module โดยไม่ instantiate agent)"""
import importlib.util
import os
import sys
import unittest

# หาโฟลเดอร์ agent แบบทน path ตื้น: repo ปกติ → ข้างไฟล์ → cwd
AGENT_DIR = os.path.join(os.path.dirname(__file__), "..", "..", "node_agent")
if not os.path.isdir(AGENT_DIR):
    AGENT_DIR = os.path.dirname(__file__)
sys.path.insert(0, os.path.abspath(AGENT_DIR))

spec = importlib.util.spec_from_file_location(
    "omnilink_agent", os.path.join(os.path.abspath(AGENT_DIR), "omnilink_agent.py"))
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class PolicyMatrixTests(unittest.TestCase):
    def test_full_allows_everything(self):
        for pkt in (mod.PKT_MOUSE_MOVE_REL, mod.PKT_MOUSE_MOVE_ABS, mod.PKT_MOUSE_BUTTON,
                    mod.PKT_MOUSE_WHEEL, mod.PKT_KEY_EVENT, mod.PKT_SET_LANG, mod.PKT_RELEASE_ALL):
            self.assertTrue(mod._packet_allowed("full", pkt), f"full ต้องอนุญาต 0x{pkt:02x}")

    def test_clipboard_blocks_input_but_allows_release(self):
        for pkt in mod._INPUT_PKTS:
            self.assertFalse(mod._packet_allowed("clipboard", pkt), f"clipboard ห้าม input 0x{pkt:02x}")
        self.assertFalse(mod._packet_allowed("clipboard", mod.PKT_SET_LANG))
        self.assertTrue(mod._packet_allowed("clipboard", mod.PKT_RELEASE_ALL))

    def test_view_allows_only_release_all(self):
        for pkt in (mod.PKT_MOUSE_MOVE_REL, mod.PKT_MOUSE_BUTTON, mod.PKT_KEY_EVENT,
                    mod.PKT_SET_LANG, mod.PKT_MOUSE_WHEEL):
            self.assertFalse(mod._packet_allowed("view", pkt), f"view ห้าม 0x{pkt:02x}")
        self.assertTrue(mod._packet_allowed("view", mod.PKT_RELEASE_ALL))

    def test_unknown_policy_treated_as_view(self):
        self.assertFalse(mod._packet_allowed("nonsense", mod.PKT_MOUSE_MOVE_REL))
        self.assertTrue(mod._packet_allowed("nonsense", mod.PKT_RELEASE_ALL))

    def test_input_packet_group_is_exact(self):
        self.assertEqual(set(mod._INPUT_PKTS),
                         {mod.PKT_MOUSE_MOVE_REL, mod.PKT_MOUSE_MOVE_ABS, mod.PKT_MOUSE_BUTTON,
                          mod.PKT_MOUSE_WHEEL, mod.PKT_KEY_EVENT})


if __name__ == "__main__":
    unittest.main(verbosity=2)
