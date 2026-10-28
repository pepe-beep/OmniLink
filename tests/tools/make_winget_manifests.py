#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""B10 (distribution hardening): สร้าง winget manifests สำหรับยื่นเข้า microsoft/winget-pkgs

ใช้หลัง release จริง:
  python tests/tools/make_winget_manifests.py --version 2.1.0 \
      --installer-url https://github.com/pepe-beep/OmniLink/releases/download/v2.1.0/OmniLink-Node-Win-Portable.zip

จะสร้าง manifests/PepeBeep/OmniLink/<version>/{version,installer,locale}.yaml
โดยคำนวณ SHA256 จากไฟล์ zip ในเครื่อง (publish/installers/) เพื่อให้ตรงกับไฟล์ที่อัปโหลด release
"""
import argparse
import hashlib
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_app_version() -> str:
    csproj = ROOT / "src" / "OmniLink" / "OmniLink.csproj"
    for line in csproj.read_text(encoding="utf-8").splitlines():
        if "<Version>" in line:
            return line.split("<Version>")[1].split("</Version>")[0]
    raise RuntimeError("หา <Version> ใน OmniLink.csproj ไม่เจอ")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True, help="เวอร์ชัน release (เช่น 2.1.0)")
    ap.add_argument("--installer-url", required=True, help="URL ของ zip ใน GitHub release")
    ap.add_argument("--zip", default=str(ROOT / "publish" / "installers" / "OmniLink-Node-Win-Portable.zip"))
    ap.add_argument("--out", default=str(ROOT / "manifests" / "PepeBeep" / "OmniLink"))
    args = ap.parse_args()

    zip_path = Path(args.zip)
    if not zip_path.exists():
        print(f"ไม่พบ zip: {zip_path} — publish.ps1 ก่อนแล้วรันอีกครั้ง")
        return 1
    sha = sha256_of(zip_path)
    size = zip_path.stat().st_size
    app_ver = read_app_version()
    if app_ver != args.version:
        print(f"⚠️ csproj Version={app_ver} ต่างจาก --version={args.version} — แก้ให้ตรงก่อนยื่น winget")

    out_dir = Path(args.out) / args.version
    out_dir.mkdir(parents=True, exist_ok=True)
    pkg = "PepeBeep.OmniLink"

    (out_dir / f"{pkg}.yaml").write_text(
        f"""# winget version manifest — สร้างโดย make_winget_manifests.py
PackageIdentifier: {pkg}
PackageVersion: {args.version}
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
""", encoding="utf-8")

    (out_dir / f"{pkg}.installer.yaml").write_text(
        f"""# winget installer manifest — portable zip (แตกไฟล์เอง ไม่มี installer)
# SHA256 คำนวณจาก {zip_path.name} ({size:,} bytes) ในเครื่อง — ต้องตรงกับไฟล์ใน release
PackageIdentifier: {pkg}
PackageVersion: {args.version}
Installers:
  - Architecture: x64
    InstallerType: zip
    InstallerUrl: {args.installer_url}
    InstallerSha256: {sha}
    NestedInstallerType: portable
    NestedInstallerFiles:
      - RelativeFilePath: OmniLink.exe
        PortableCommandAlias: omnilink
ManifestType: installer
ManifestVersion: 1.6.0
""", encoding="utf-8")

    (out_dir / f"{pkg}.locale.yaml").write_text(
        f"""# winget locale manifest
PackageIdentifier: {pkg}
PackageVersion: {args.version}
PackageLocale: en-US
Publisher: pepe-beep
PackageName: OmniLink
License: MIT
ShortDescription: LAN KVM — control mouse/keyboard across machines, two-way clipboard and audio streaming
Description: |-
  OmniLink คือ KVM ระหว่างเครื่องผ่าน LAN: ควบคุมเมาส์/คีย์บอร์ดข้ามเครื่อง, clipboard สองทิศ
  (ข้อความ/รูป/ไฟล์) และสตรีมเสียงสองทาง (GStreamer RTP) — จับคู่แบบ TLS+TOFU+PIN
  ตัวติดตั้งนี้คือ Node Agent (ฝั่งเครื่องลูก) แบบพกพา ฝัง Python มาให้
Tags:
  - kvm
  - remote-control
  - clipboard
  - audio-streaming
  - lan
ManifestType: defaultLocale
ManifestVersion: 1.6.0
""", encoding="utf-8")

    print(f"winget manifests → {out_dir}")
    print(f"  installer sha256 = {sha}")
    print("  ขั้นต่อไป: อัปโหลด zip เข้า GitHub release ตาม version ที่ระบุ แล้ว PR เข้า microsoft/winget-pkgs")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
