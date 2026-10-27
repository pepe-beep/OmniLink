#!/usr/bin/env python3
"""OmniLink Test Runner — รันทุกชั้นของการทดสอบตามลำดับ

Usage:
  python tests/run_all_tests.py            # ชั้น 1-2 (unit: C# + Python) — ปลอดภัย รันได้ตลอด
  python tests/run_all_tests.py --live     # + ชั้น 3 (live protocol tests — ต้องหยุด host ชั่วคราว)
  python tests/run_all_tests.py --live --e2e  # + ชั้น 4 (E2E บนหน้าจอจริง)

ชั้นการทดสอบ:
  1. C# unit tests (dotnet test)           — protocol/strategy/config/geometry ทุกอย่าง
  2. Python agent unit tests               — edge state machine ด้วย FakeX11 (ไม่ต้องมี Linux)
  3. Live protocol tests (ต้องมี ThinkPad) — หยุด host → รัน integration/desync/switching/clipboard → เปิด host คืน
  4. E2E (หน้าจอจริง)                      — fullscreen block ฯลฯ
"""
import subprocess
import sys
import time

if sys.platform == "win32":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8")
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PASS, FAIL = [], []


def run(name, cmd, timeout=600):
    print(f"\n{'=' * 60}\n[STAGE] {name}\n{'=' * 60}")
    try:
        r = subprocess.run(cmd, cwd=str(ROOT), timeout=timeout,
                           shell=isinstance(cmd, str))
        ok = r.returncode == 0
    except subprocess.TimeoutExpired:
        print(f"TIMEOUT after {timeout}s")
        ok = False
    (PASS if ok else FAIL).append(name)
    print(f"--> {'OK' if ok else 'FAILED'}: {name}")
    return ok


def stop_host():
    subprocess.run(["powershell.exe", "-NoProfile", "-Command",
                    "Stop-Process -Name OmniLink -Force -ErrorAction SilentlyContinue"], timeout=30)
    time.sleep(2)


def start_host():
    subprocess.run(["cmd", "/c", str(ROOT / "Start_OmniLink.bat")], timeout=30)
    time.sleep(18)


def main():
    args = sys.argv[1:]
    live = "--live" in args
    e2e = "--e2e" in args

    # ── ชั้น 1: C# ──
    run("C# build", "dotnet build src/OmniLink/OmniLink.csproj")
    run("C# unit tests", "dotnet test tests/OmniLink.Tests/OmniLink.Tests.csproj")

    # ── ชั้น 2: Python agent unit ──
    run("Python agent edge machine", [sys.executable, "tests/unit_agent/test_agent_edge_machine.py"])
    run("Python agent file exchange", [sys.executable, "-m", "unittest", "tests/unit_agent/test_agent_file_exchange.py"])

    # ── ชั้น 3: live protocol ──
    if live:
        print("\n[!] หยุด OmniLink host ชั่วคราวสำหรับ live tests...")
        was_stopped = True
        stop_host()
        try:
            run("Live: integration", [sys.executable, "tests/tools/integration_test.py"], timeout=120)
            run("Live: switching UX", [sys.executable, "tests/tools/switching_ux_test.py"], timeout=150)
            run("Live: clipboard + language", [sys.executable, "tests/tools/clipboard_lang_test.py"], timeout=120)
            run("Live: file exchange", [sys.executable, "tests/tools/live_file_exchange_test.py"], timeout=120)
            run("Live: desync (conn war)", [sys.executable, "tests/tools/desync_test.py"], timeout=120)
        finally:
            print("\n[!] เปิด OmniLink host คืน...")
            start_host()

    # ── ชั้น 4: E2E ──
    if e2e:
        run("E2E: fullscreen block", "powershell -NoProfile -ExecutionPolicy Bypass "
            "-File tests/tools/fullscreen_block_e2e.ps1", timeout=180)

    # ── สรุป ──
    print(f"\n{'=' * 60}\nสรุป: {len(PASS)} passed, {len(FAIL)} failed")
    if FAIL:
        print("FAILED STAGES:")
        for f in FAIL:
            print(f"  - {f}")
        return 1
    print("ALL GREEN")
    return 0


if __name__ == "__main__":
    sys.exit(main())
