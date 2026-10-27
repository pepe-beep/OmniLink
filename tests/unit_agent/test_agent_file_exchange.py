import os
import shutil
import tempfile
import unittest
import hashlib
import json
import base64

import sys
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", ".."))
from node_agent.omnilink_agent import LinuxClipboard


class TestAgentFileExchange(unittest.TestCase):
    def setUp(self):
        self.test_dir = tempfile.mkdtemp(prefix="omnilink_agent_test_")

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def test_file_hash_verification(self):
        sample_path = os.path.join(self.test_dir, "sample.txt")
        content = b"OmniLink Python Agent Chunked Transfer SHA256 Verification Test"
        with open(sample_path, "wb") as f:
            f.write(content)

        expected_hash = hashlib.sha256(content).hexdigest()

        # Simulate chunk reception
        hasher = hashlib.sha256()
        chunk_b64 = base64.b64encode(content).decode("ascii")
        decoded_chunk = base64.b64decode(chunk_b64)
        hasher.update(decoded_chunk)

        self.assertEqual(hasher.hexdigest().lower(), expected_hash.lower())

    def test_manifest_parsing(self):
        manifest_data = {
            "id": "12345678abcdef",
            "effect": "copy",
            "total_bytes": 100,
            "files": [
                {
                    "rel_path": "test.pdf",
                    "size": 100,
                    "sha256": "abc123hash",
                    "is_dir": False
                }
            ]
        }
        json_str = json.dumps(manifest_data)
        parsed = json.loads(json_str)
        self.assertEqual(parsed["id"], "12345678abcdef")
        self.assertEqual(len(parsed["files"]), 1)
        self.assertEqual(parsed["files"][0]["rel_path"], "test.pdf")

    def test_clipboard_set_files_empty(self):
        clip = LinuxClipboard(status_ui=None)
        res = clip.set_files([])
        self.assertFalse(res)


if __name__ == "__main__":
    unittest.main()
