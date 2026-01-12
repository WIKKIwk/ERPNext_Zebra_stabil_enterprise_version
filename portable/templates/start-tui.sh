#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$ROOT_DIR/env.sh"

WEB_HOST_DEFAULT="127.0.0.1"
WEB_PORT_DEFAULT="18000"

WEB_HOST="${ZEBRA_WEB_HOST:-${WEB_HOST_DEFAULT}}"
WEB_PORT="${ZEBRA_WEB_PORT:-${WEB_PORT_DEFAULT}}"

export ZEBRA_WEB_HOST="$WEB_HOST"
export ZEBRA_WEB_PORT="$WEB_PORT"

if [[ "${ZEBRA_ENABLE_UI:-0}" != "1" ]]; then
  export ZEBRA_DISABLE_UI=1
fi

if [[ -z "${ASPNETCORE_URLS:-}" ]]; then
  export ASPNETCORE_URLS="http://${WEB_HOST}:${WEB_PORT}"
fi

LOG_DIR="${LOG_DIR:-${ROOT_DIR}/logs}"
LOG_FILE="${LOG_FILE:-${LOG_DIR}/zebra-web.log}"
mkdir -p "$LOG_DIR"

SETUP_ARGS=()
TUI_ARGS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --setup)
      ;;
    --online|--offline)
      SETUP_ARGS+=("$1")
      ;;
    --erp-url|--erp-token|--device|--mode)
      SETUP_ARGS+=("$1")
      shift || true
      if [[ -n "${1:-}" ]]; then
        SETUP_ARGS+=("$1")
      fi
      ;;
    --url)
      TUI_ARGS+=("$1")
      shift || true
      if [[ -n "${1:-}" ]]; then
        TUI_ARGS+=("$1")
      fi
      ;;
    *)
      TUI_ARGS+=("$1")
      ;;
  esac
  shift || true
done

if [[ "${ZEBRA_TUI_SETUP:-1}" != "0" ]]; then
  "$ZEBRA_PORTABLE_APP_DIR/cli/ZebraBridge.Cli" setup "${SETUP_ARGS[@]}"
fi

WEB_APP_DIR="$ZEBRA_PORTABLE_APP_DIR/web"
export ASPNETCORE_CONTENTROOT="$WEB_APP_DIR"
export ASPNETCORE_WEBROOT="$WEB_APP_DIR/wwwroot"

(
  cd "$WEB_APP_DIR"
  ./ZebraBridge.Web
) >"$LOG_FILE" 2>&1 &
SERVER_PID=$!

check_web_health() {
  local base_url="$1"
  local url="${base_url%/}/api/v1/health"
  if command -v curl >/dev/null 2>&1; then
    curl -fsS "$url" >/dev/null 2>&1 && return 0
  elif command -v wget >/dev/null 2>&1; then
    wget -qO /dev/null "$url" >/dev/null 2>&1 && return 0
  fi
  return 1
}

BASE_URL="http://${WEB_HOST}:${WEB_PORT}"
for _ in $(seq 1 40); do
  if check_web_health "$BASE_URL"; then
    break
  fi
  sleep 0.2
done

cleanup() {
  if kill -0 "$SERVER_PID" >/dev/null 2>&1; then
    kill "$SERVER_PID" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT INT TERM

"$ZEBRA_PORTABLE_APP_DIR/cli/ZebraBridge.Cli" tui "${TUI_ARGS[@]}"
