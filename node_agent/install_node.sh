#!/usr/bin/env bash
# ==============================================================================
# OmniLink Node Agent Installer
# Works on Ubuntu / Debian / Linux Mint / Fedora / Arch Linux
# Zero configuration, high performance, two-way audio & KVM client
# ==============================================================================

set -e

GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BOLD='\033[1m'
NC='\033[0m'

echo -e "${CYAN}${BOLD}"
echo "  ___                 _ _     _       _     "
echo " / _ \ _ __ ___  _ __ (_) |   (_)_ __ | | __ "
echo "| | | | '_ \` _ \| '_ \| | |   | | '_ \| |/ / "
echo "| |_| | | | | | | | | | | |___| | | | |   <  "
echo " \___/|_| |_| |_|_| |_|_|_____|_|_| |_|_|\_\ "
echo "        Node Agent Installer     "
echo -e "${NC}"

INSTALL_DIR="$HOME/omnilink"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$INSTALL_DIR"
mkdir -p "$HOME/.config/autostart"

echo -e "${YELLOW}>> 1. Checking system dependencies...${NC}"

if command -v apt-get &> /dev/null; then
    echo -e "   Detected Debian/Ubuntu-based distribution."
    MISSING_PKGS=()
    for pkg in python3 python3-pip libx11-6 libxtst6 gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-plugins-good pulseaudio-utils x11-xkb-utils xclip; do
        if ! dpkg -s "$pkg" &> /dev/null; then
            MISSING_PKGS+=("$pkg")
        fi
    done
    if [ ${#MISSING_PKGS[@]} -gt 0 ]; then
        echo -e "   Installing missing packages: ${MISSING_PKGS[*]}"
        sudo apt-get update -qq
        sudo apt-get install -y -qq "${MISSING_PKGS[@]}"
    else
        echo -e "   All required packages are already installed."
    fi
elif command -v dnf &> /dev/null; then
    echo -e "   Detected Fedora/RHEL-based distribution."
    sudo dnf install -y python3 python3-pip libX11 libXtst gstreamer1 gstreamer1-plugins-base gstreamer1-plugins-good pulseaudio-utils xclip
elif command -v pacman &> /dev/null; then
    echo -e "   Detected Arch-based distribution."
    sudo pacman -S --noconfirm --needed python python-pip libx11 libxtst gst-plugins-base gst-plugins-good pulseaudio xorg-setxkbmap xclip
else
    echo -e "${YELLOW}   Notice: Unknown package manager. Please ensure Python 3, X11, and GStreamer are installed.${NC}"
fi

echo -e "\n${YELLOW}>> 2. Installing OmniLink Agent binary...${NC}"

if [ -f "$SCRIPT_DIR/omnilink_agent.py" ] && [ "$SCRIPT_DIR" != "$INSTALL_DIR" ]; then
    cp "$SCRIPT_DIR/omnilink_agent.py" "$INSTALL_DIR/omnilink_agent.py"
elif [ -f "./omnilink_agent.py" ] && [ "$(pwd)" != "$INSTALL_DIR" ]; then
    cp "./omnilink_agent.py" "$INSTALL_DIR/omnilink_agent.py"
elif [ ! -f "$INSTALL_DIR/omnilink_agent.py" ]; then
    echo -e "   Downloading latest canonical agent from repository..."
    curl -sSL "https://raw.githubusercontent.com/premi/OmniLink/main/node_agent/omnilink_agent.py" -o "$INSTALL_DIR/omnilink_agent.py" || {
        echo -e "${RED}Error: Failed to fetch omnilink_agent.py. Please copy it to $INSTALL_DIR/omnilink_agent.py manually.${NC}"
        exit 1
    }
fi

chmod +x "$INSTALL_DIR/omnilink_agent.py"
echo -e "   Agent installed at: ${GREEN}$INSTALL_DIR/omnilink_agent.py${NC}"

echo -e "\n${YELLOW}>> 3. Setting up Desktop Autostart...${NC}"

cat << 'EOF' > "$HOME/.config/autostart/omnilink-agent.desktop"
[Desktop Entry]
Type=Application
Exec=/usr/bin/env python3 %h/omnilink/omnilink_agent.py
Hidden=false
NoDisplay=false
X-GNOME-Autostart-enabled=true
Name=OmniLink Client Agent
Comment=Mesh KVM & Two-Way Low-Latency Audio Client Daemon
Icon=preferences-desktop-keyboard
Categories=Utility;System;
EOF

# Expand %h to $HOME for universal compatibility
sed -i "s|%h|$HOME|g" "$HOME/.config/autostart/omnilink-agent.desktop"
chmod +x "$HOME/.config/autostart/omnilink-agent.desktop"
echo -e "   Autostart configured at: ${GREEN}$HOME/.config/autostart/omnilink-agent.desktop${NC}"

echo -e "\n${YELLOW}>> 4. Generating Machine UUID and Security PIN...${NC}"

# Generate or load UUID
if [ ! -f "$INSTALL_DIR/machine_id" ]; then
    python3 -c "import uuid; print('node-' + str(uuid.uuid4())[:8])" > "$INSTALL_DIR/machine_id"
fi
MACHINE_ID=$(cat "$INSTALL_DIR/machine_id")

# Generate or load PIN
if [ ! -f "$INSTALL_DIR/pairing_pin" ]; then
    python3 -c "import secrets; print(f'{secrets.randbelow(900) + 100}-{secrets.randbelow(900) + 100}')" > "$INSTALL_DIR/pairing_pin"
fi
PAIRING_PIN=$(cat "$INSTALL_DIR/pairing_pin")

echo -e "\n${YELLOW}>> 5. Launching OmniLink Agent daemon...${NC}"

# Kill any existing agent instance
pkill -f "omnilink_agent.py" || true
sleep 0.5

# Start agent in background with active X11 display
env DISPLAY="${DISPLAY:-:0}" DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u)/bus" nohup python3 "$INSTALL_DIR/omnilink_agent.py" > /dev/null 2>&1 &
sleep 1

# Detect primary LAN IP
LAN_IP=$(hostname -I 2>/dev/null | awk '{print $1}' || echo "127.0.0.1")

echo -e "\n${GREEN}${BOLD}================================================================${NC}"
echo -e "${GREEN}${BOLD}       OmniLink Client Installed & Running Successfully!        ${NC}"
echo -e "${GREEN}${BOLD}================================================================${NC}"
echo -e "  • Device Name : ${BOLD}$(hostname)${NC}"
echo -e "  • Machine UUID: ${BOLD}${MACHINE_ID}${NC}"
echo -e "  • Local IP    : ${BOLD}${LAN_IP}${NC}"
echo -e "  • ${YELLOW}${BOLD}Pairing PIN : ${BOLD}${PAIRING_PIN}${NC}"
echo -e "----------------------------------------------------------------"
echo -e "  ${BOLD}Next steps on the Host machine:${NC}"
echo -e "  1. Open OmniLink Dashboard on Windows."
echo -e "  2. Go to 'Network & Discovery' tab (or click '🔍 Scan LAN')."
echo -e "  3. Select '${BOLD}$(hostname)${NC}' (${LAN_IP})."
echo -e "  4. Enter the 6-digit PIN: ${YELLOW}${BOLD}${PAIRING_PIN}${NC}"
echo -e "  5. Enjoy seamless mouse crossing and two-way audio!"
echo -e "${GREEN}${BOLD}================================================================${NC}\n"
