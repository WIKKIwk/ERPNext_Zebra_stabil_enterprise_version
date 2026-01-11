#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

ARCH="$(uname -m)"
APP_DIR=""
case "$ARCH" in
  x86_64|amd64)
    APP_DIR="$ROOT_DIR/bin/linux-x64"
    ;;
  aarch64|arm64)
    APP_DIR="$ROOT_DIR/bin/linux-arm64"
    ;;
  *)
    echo "Unsupported architecture: $ARCH" >&2
    exit 1
    ;;
esac

if [[ ! -d "$APP_DIR" ]]; then
  echo "Missing binaries for: $ARCH" >&2
  exit 1
fi

export ZEBRA_PORTABLE_APP_DIR="$APP_DIR"
