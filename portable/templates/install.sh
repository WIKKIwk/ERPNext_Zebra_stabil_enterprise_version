#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${1:-/opt/zebra-bridge}"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Please run as root (sudo)." >&2
  exit 1
fi

copy_tree() {
  local src="$1"
  local dst="$2"
  if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete --exclude 'logs/*' --exclude 'downloads/*' --exclude 'dist/*' "$src/" "$dst/"
    return
  fi
  mkdir -p "$dst"
  (cd "$src" && tar -cf - .) | (cd "$dst" && tar -xf -)
}

mkdir -p "$PREFIX"
copy_tree "$ROOT_DIR" "$PREFIX"

chmod +x "$PREFIX"/*.sh

UDEV_RULES="/etc/udev/rules.d/99-zebra-bridge.rules"
cat > "$UDEV_RULES" <<'RULES'
KERNEL=="ttyUSB[0-9]*", MODE="0666"
KERNEL=="ttyACM[0-9]*", MODE="0666"
KERNEL=="lp[0-9]*", SUBSYSTEM=="usb", MODE="0666"
RULES

if command -v udevadm >/dev/null 2>&1; then
  udevadm control --reload-rules || true
  udevadm trigger || true
fi

if command -v systemctl >/dev/null 2>&1; then
  cat > /etc/systemd/system/zebra-bridge.service <<EOF_SERVICE
[Unit]
Description=ZebraBridge v1 (web service)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$PREFIX
ExecStart=$PREFIX/start-web.sh
Restart=always
RestartSec=2

[Install]
WantedBy=multi-user.target
EOF_SERVICE

  systemctl daemon-reload
  systemctl enable --now zebra-bridge.service
  echo "Service installed and started: zebra-bridge"
else
  echo "systemd not found. Start manually: $PREFIX/start-web.sh"
fi

