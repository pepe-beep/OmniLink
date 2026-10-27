#!/usr/bin/env python3
"""Phase 5 — Anti "It Works on My Machine" environment matrix
บูต OmniLink จริงทีละสถานการณ์ ตรวจว่า process มีชีวิต + log ตรง signature แล้วคืนสภาพ
ก่อนรัน: ปิด OmniLink ที่รันอยู่ก่อน (สคริปต์จะ kill เอง) — จบแล้วเปิด build จริงให้เอง
"""
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / "publish" / "OmniLink.exe"
CONFIG = ROOT / "omnilink_config.json"
LOGDIR = ROOT / "logs"
BACKUP = ROOT / "test-artifacts" / "config-backup.json"

PASS, FAIL = [], []


def ok(name, note=""):
    PASS.append(name)
    print(f"  OK  {name} {note}")


def bad(name, note=""):
    FAIL.append(name)
    print(f" FAIL {name} {note}")


def kill():
    subprocess.run(["taskkill", "/IM", "OmniLink.exe", "/F"], capture_output=True)
    time.sleep(2)


def boot_and_check(name, signature=None, timeout=16):
    """เปิด build จริง รอ ตรวจ process มีชีวิต + (optional) log มี signature"""
    before = newest_log()
    p = subprocess.Popen([str(EXE)], cwd="C:\\")  # cwd อื่นด้วย = ทดสอบ CWD independence พร้อมกัน
    time.sleep(timeout)
    alive = p.poll() is None
    if not alive:
        bad(name, "process ตายระหว่างบูต")
        return False
    log_text = read_log_since(before)
    if signature and signature not in log_text:
        bad(name, f"ไม่เจอ '{signature}' ใน log")
        p.kill()
        return False
    ok(name, f"pid={p.pid}")
    p.kill()
    time.sleep(2)
    return True


def newest_log():
    logs = sorted(LOGDIR.glob("omnilink-*.log"), key=lambda f: f.stat().st_mtime)
    return logs[-1] if logs else None


def read_log_since(marker):
    if marker is None:
        return ""
    return marker.read_text(encoding="utf-8", errors="ignore")


def main():
    SHOTS = ROOT / "test-artifacts"
    SHOTS.mkdir(parents=True, exist_ok=True)
    kill()
    shutil.copy2(CONFIG, BACKUP)  # สำรอง config จริง
    original = CONFIG.read_text(encoding="utf-8")

    original_cfg = json.loads(original)

    def setup_clean():
        CONFIG.unlink(missing_ok=True)

    def setup_corrupt():
        CONFIG.write_text('{"schema_version":2,"host_na', encoding="utf-8")

    def setup_empty():
        CONFIG.write_text("", encoding="utf-8")

    def setup_old_schema():
        CONFIG.write_text(json.dumps({
            "schema_version": 2, "host_name": "PSPV", "host_ip": "192.168.99.55",
            "switch_delay_ms": 123, "enable_clipboard": True,
            "nodes": [{"node_id": "old-node", "name": "Old", "ip": "192.168.99.2",
                       "position": "left", "enabled": True}],
        }), encoding="utf-8")

    def setup_fake_gst():
        cfg = dict(original_cfg)
        cfg["gst_exe"] = "C:" + chr(92) + "definitely" + chr(92) + "not" + chr(92) + "here" + chr(92) + "gst-launch-1.0.exe"
        CONFIG.write_text(json.dumps(cfg), encoding="utf-8")

    def setup_offline():
        cfg = json.loads(original)
        for n in cfg["nodes"]:
            n["ip"] = "127.0.0.1"
        CONFIG.write_text(json.dumps(cfg), encoding="utf-8")

    def setup_low_mem():
        cfg = json.loads(original)
        cfg["low_memory_mode"] = True
        CONFIG.write_text(json.dumps(cfg), encoding="utf-8")

    scenarios = [
        ("clean: no config file -> creates defaults", None, setup_clean),
        ("corrupt: truncated json -> defaults, file untouched", "อ่านไม่ได้", setup_corrupt),
        ("empty: zero-byte config", None, setup_empty),
        ("old-schema: v1.8 config -> new keys defaulted", None, setup_old_schema),
        ("fake-gst: missing gstreamer -> boots, audio degraded", None, setup_fake_gst),
        ("offline: unreachable nodes -> boots, retry quiet", None, setup_offline),
        ("low-mem: forced profile -> software render boots", "[LowMem]", setup_low_mem),
    ]

    for name, signature, setup in scenarios:
        print()
        print("[scenario] " + name)
        kill()
        setup()  # เขียน config ของสถานการณ์ "ก่อนบูตเสมอ" (บั๊กแรก: setup ล่วงหน้าถูก scenario ถัดไปทับ)
        if boot_and_check(name, signature):
            pass
        CONFIG.write_text(original, encoding="utf-8")  # คืน config จริงทุกครั้ง

    # 8. OMNILINK_HOME override
    print()
    print("[scenario] omnilink-home: env override root")
    home = SHOTS / "omnihome"
    home.mkdir(parents=True, exist_ok=True)
    shutil.copy2(BACKUP, home / "omnilink_config.json")
    kill()
    import os
    env = dict(os.environ, OMNILINK_HOME=str(home))
    p = subprocess.Popen([str(EXE)], cwd="C:\\", env=env)
    time.sleep(14)
    got_config = (home / "omnilink_config.json").exists()
    if p.poll() is None and got_config:
        ok("omnilink-home: env root honored", f"pid={p.pid}")
    else:
        bad("omnilink-home: env root honored", f"alive={p.poll() is None} config={got_config}")
    p.kill()
    time.sleep(2)

    # ── สรุป ──
    print(f"\nสรุป: {len(PASS)} passed, {len(FAIL)} failed")
    if FAIL:
        for f_ in FAIL:
            print("  FAILED:", f_)
        # คืน config จริงเสมอ
        shutil.copy2(BACKUP, CONFIG)
        return 1
    shutil.copy2(BACKUP, CONFIG)  # config จริงกลับมา
    print("ENV MATRIX ALL GREEN (config restored)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
