#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" == "--tui" ]]; then
  shift || true
  exec "$ROOT_DIR/start-tui.sh" "$@"
fi

exec "$ROOT_DIR/start-web.sh" "$@"
