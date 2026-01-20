#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WEB_HOST="${ZEBRA_WEB_HOST:-127.0.0.1}"
WEB_PORT="${ZEBRA_WEB_PORT:-18000}"
LOG_DIR="${LOG_DIR:-${ROOT_DIR}/logs}"
PID_FILE="${ZEBRA_DAEMON_PID:-${LOG_DIR}/zebra-web.pid}"

find_listening_pid() {
  local port="$1"

  if command -v ss >/dev/null 2>&1; then
    ss -lptn "( sport = :${port} )" 2>/dev/null \
      | awk -F'pid=' 'NR>1 && $2 { split($2, a, ","); print a[1]; exit }'
    return
  fi

  if command -v lsof >/dev/null 2>&1; then
    lsof -tiTCP:"${port}" -sTCP:LISTEN 2>/dev/null | head -n 1 || true
    return
  fi

  echo ""
}

is_zebra_bridge_health() {
  local base_url="$1"
  local url="${base_url%/}/api/v1/health"
  local body=""

  if command -v curl >/dev/null 2>&1; then
    body="$(curl -fsS "${url}" 2>/dev/null || true)"
  elif command -v wget >/dev/null 2>&1; then
    body="$(wget -qO - "${url}" 2>/dev/null || true)"
  else
    return 1
  fi

  echo "${body}" | grep -q '"service":"zebra-bridge-v1"' || return 1
  return 0
}

is_zebra_bridge_process() {
  local pid="$1"
  if [[ -z "${pid}" ]]; then
    return 1
  fi
  ps -p "${pid}" -o comm= 2>/dev/null | tr -d '[:space:]' | grep -qi "ZebraBridge.Web"
}

stop_pid() {
  local pid="$1"
  if [[ -z "${pid}" ]]; then
    return 1
  fi

  if ! kill -0 "${pid}" >/dev/null 2>&1; then
    return 0
  fi

  kill "${pid}" >/dev/null 2>&1 || true
  for _ in $(seq 1 40); do
    if ! kill -0 "${pid}" >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.1
  done

  kill -9 "${pid}" >/dev/null 2>&1 || true
  return 0
}

if [[ -f "${PID_FILE}" ]]; then
  pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
  stop_pid "${pid}"
  rm -f "${PID_FILE}"
  echo "zebra-bridge stopped."
  exit 0
fi

pid="$(find_listening_pid "${WEB_PORT}")"
if [[ -z "${pid}" ]]; then
  echo "No running zebra-bridge found."
  exit 0
fi

BASE_URL="http://${WEB_HOST}:${WEB_PORT}"
if is_zebra_bridge_health "${BASE_URL}" || is_zebra_bridge_process "${pid}"; then
  stop_pid "${pid}"
  echo "zebra-bridge stopped."
  exit 0
fi

echo "ERROR: Port ${WEB_PORT} is in use and does not look like zebra-bridge." >&2
exit 1
