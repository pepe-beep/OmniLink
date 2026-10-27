#!/usr/bin/env bash
# ==============================================================================
# OmniLink Host Installer for Linux (Ubuntu / Mint / Lubuntu 22.04+, .NET 10)
# ติดตั้ง omnilink-hostd (headless host daemon) แบบ systemd --user — ไม่ต้อง sudo
# ใช้คู่กับโฟลเดอร์ publish-linux/omnilink-hostd (self-contained — ไม่ต้องลง .NET)
# ==============================================================================
set -e

INSTALL_DIR="$HOME/omnilink-host"
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo ">> ติดตั้ง OmniLink host daemon ที่ $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
if [ "$SRC_DIR" != "$INSTALL_DIR" ]; then
  cp -f "$SRC_DIR/omnilink-hostd" "$INSTALL_DIR/omnilink-hostd" 2>/dev/null || {
    echo "ไม่พบ omnilink-hostd ข้างสคริปต์ — วาง binary (จาก publish-linux/omnilink-hostd/) ข้างสคริปต์ก่อน"; exit 1; }
fi
[ -x "$INSTALL_DIR/omnilink-hostd" ] || {
  echo "ไม่พบ $INSTALL_DIR/omnilink-hostd ที่รันได้ — คัดลอกจาก publish-linux/omnilink-hostd/ ก่อน"; exit 1; }
chmod +x "$INSTALL_DIR/omnilink-hostd"

# config ตัวอย่าง (ถ้ายังไม่มี) — แก้ nodes ต่อจากนี้หรือใช้ 'omnilink-hostd run' ครั้งแรกให้สร้างเอง
if [ ! -f "$INSTALL_DIR/omnilink_config.json" ]; then
  cat > "$INSTALL_DIR/omnilink_config.json" <<'EOF'
{
  "host_name": "linux-host",
  "nodes": []
}
EOF
  echo ">> สร้าง omnilink_config.json ตั้งต้นแล้ว (ยังไม่มี node — เพิ่มได้ที่ไฟล์นี้)"
fi

# systemd --user (ไม่ต้อง sudo — รันใน session ของผู้ใช้จึงเห็น X display)
mkdir -p "$HOME/.config/systemd/user"
cp -f "$SRC_DIR/omnilink-hostd.service" "$HOME/.config/systemd/user/omnilink-hostd.service"
systemctl --user daemon-reload
systemctl --user enable omnilink-hostd.service
systemctl --user restart omnilink-hostd.service
sleep 2
systemctl --user --no-pager --lines=5 status omnilink-hostd.service || true

echo ""
echo ">> เสร็จแล้ว:"
echo "   ดูสถานะ   : systemctl --user status omnilink-hostd"
echo "   ดู log     : journalctl --user -u omnilink-hostd -f"
echo "   เพิ่ม node : แก้ $INSTALL_DIR/omnilink_config.json แล้ว systemctl --user restart omnilink-hostd"
