#!/usr/bin/env bash
# ==============================================================================
# v2.0 เฟส 6: สร้าง .deb สองตัว (host daemon + node agent) — รันบนเครื่อง Linux
# ที่มี dpkg-deb (Ubuntu/Mint/Lubuntu ใดๆ) จากไฟล์ที่คัดลอกมาวางข้างสคริปต์:
#   omnilink-hostd        (binary self-contained จาก publish-linux/omnilink-hostd/)
#   omnilink_agent.py     (จาก node_agent/)
# ผลลัพธ์: ~/omnilink-dist/*.deb — ติดตั้งด้วย: sudo dpkg -i <deb>
#   host  : /usr/lib/omnilink-host/omnilink-hostd + systemd --user unit + omnilink-hostd ใน PATH
#   node  : /usr/lib/omnilink/node-agent/omnilink_agent.py + omnilink-node-agent ใน PATH
# ==============================================================================
set -euo pipefail

SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST="${HOME}/omnilink-dist"
VER="2.0.0"

mkdir -p "$DIST"
rm -rf "$DIST/pkg-root-host" "$DIST/pkg-root-node"

# ── deb 1: omnilink-host (host daemon headless) ──
H="$DIST/pkg-root-host"
mkdir -p "$H/usr/lib/omnilink-host" "$H/usr/bin" "$H/usr/share/systemd/user" "$H/DEBIAN"
cp -f "$SRC_DIR/omnilink-hostd" "$H/usr/lib/omnilink-host/omnilink-hostd"
chmod 755 "$H/usr/lib/omnilink-host/omnilink-hostd"

cat > "$H/usr/bin/omnilink-hostd" <<'EOF'
#!/bin/sh
# launcher — config/รันstate อยู่ที่ ~/.omnilink-host (แยกจากการติดตั้งใน /usr)
OMNILINK_HOST_HOME="${OMNILINK_HOST_HOME:-$HOME/.omnilink-host}"
mkdir -p "$OMNILINK_HOST_HOME"
[ -f "$OMNILINK_HOST_HOME/omnilink_config.json" ] || echo '{"host_name":"linux-host","nodes":[]}' > "$OMNILINK_HOST_HOME/omnilink_config.json"
OMNILINK_HOME="$OMNILINK_HOST_HOME" DISPLAY="${DISPLAY:-:0}" exec /usr/lib/omnilink-host/omnilink-hostd "$@"
EOF
chmod 755 "$H/usr/bin/omnilink-hostd"

# systemd --user unit (ผู้ใช้เรียกเอง: systemctl --user enable --now omnilink-hostd)
cat > "$H/usr/share/systemd/user/omnilink-hostd.service" <<'EOF'
[Unit]
Description=OmniLink Host Daemon (headless mesh KVM host)
After=graphical-session.target

[Service]
Type=simple
ExecStart=/usr/bin/omnilink-hostd run
Environment=DISPLAY=%I
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
EOF

cat > "$H/DEBIAN/control" <<EOF
Package: omnilink-host
Version: $VER
Section: utils
Priority: optional
Architecture: amd64
Depends:
Maintainer: OmniLink Project
Description: OmniLink host daemon (headless) — mesh KVM host for Linux
 Self-contained .NET daemon: connects to/control nodes over TLS+HMAC,
 topology/chain, embedded sub mode (host-to-host), status.json, X11 input
 gateway. Config at ~/.omnilink-host/omnilink_config.json.
 Enables with: systemctl --user enable --now omnilink-hostd
EOF

dpkg-deb --build "$H" "$DIST/omnilink-host_${VER}_amd64.deb"

# ── deb 2: omnilink-node (node agent = เครื่องลูก) ──
N="$DIST/pkg-root-node"
mkdir -p "$N/usr/lib/omnilink/node-agent" "$N/usr/bin" "$N/DEBIAN"
cp -f "$SRC_DIR/omnilink_agent.py" "$N/usr/lib/omnilink/node-agent/omnilink_agent.py"
chmod 644 "$N/usr/lib/omnilink/node-agent/omnilink_agent.py"

cat > "$N/usr/bin/omnilink-node-agent" <<'EOF'
#!/bin/sh
OMNILINK_NODE_HOME="${OMNILINK_NODE_HOME:-$HOME/.omnilink}"
mkdir -p "$OMNILINK_NODE_HOME"
cd "$OMNILINK_NODE_HOME" || exit 1
exec python3 -u /usr/lib/omnilink/node-agent/omnilink_agent.py "$@"
EOF
chmod 755 "$N/usr/bin/omnilink-node-agent"

cat > "$N/DEBIAN/control" <<EOF
Package: omnilink-node
Version: $VER
Section: utils
Priority: optional
Architecture: all
Depends: python3 (>= 3.6), libx11-6, libxtst6, x11-xkb-utils
Recommends: gstreamer1.0-tools, pulseaudio-utils, xclip
Maintainer: OmniLink Project
Description: OmniLink node agent — the controlled node (sub) for Linux
 Single-file Python agent: TLS+TOFU pairing, HMAC input injection (XTest),
 clipboard (text), edge machine, escape hotkey. State at ~/.omnilink.
 Run: omnilink-node-agent  (หรือใช้ install_node.sh สำหรับติดตั้งแบบไม่ต้อง sudo)
EOF

dpkg-deb --build "$N" "$DIST/omnilink-node_${VER}_all.deb"

echo "=================================================="
ls -la "$DIST"/*.deb
echo "ติดตั้ง (ต้อง sudo ครั้งเดียว): sudo dpkg -i $DIST/omnilink-host_${VER}_amd64.deb $DIST/omnilink-node_${VER}_all.deb"
