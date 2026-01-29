#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CACHE_BASE="${XDG_CACHE_HOME:-}"
if [[ -z "${CACHE_BASE}" && -n "${HOME:-}" ]]; then
  CACHE_BASE="${HOME}/.cache"
fi
DEFAULT_DOTNET_DIR="${ROOT_DIR}/.dotnet"
if [[ -n "${CACHE_BASE}" ]]; then
  DEFAULT_DOTNET_DIR="${CACHE_BASE}/zebra-bridge/dotnet"
fi
DOTNET_DIR="${DOTNET_DIR:-${DEFAULT_DOTNET_DIR}}"
DOTNET_BIN="${DOTNET_BIN:-${DOTNET_DIR}/dotnet}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-8.0}"
DOTNET_INSTALL_LOG="${DOTNET_INSTALL_LOG:-${DOTNET_DIR}/dotnet-install.log}"
WEB_HOST_DEFAULT="127.0.0.1"
WEB_PORT_DEFAULT="18000"

download_file() {
  local url="$1"
  local dest="$2"

  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "${url}" -o "${dest}"
    return
  fi

  if command -v wget >/dev/null 2>&1; then
    wget -qO "${dest}" "${url}"
    return
  fi

  echo "ERROR: curl or wget is required to download .NET." >&2
  exit 1
}

ensure_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    DOTNET_BIN="$(command -v dotnet)"
    return
  fi

  mkdir -p "${DOTNET_DIR}"

  local install_script="${DOTNET_DIR}/dotnet-install.sh"
  if [[ ! -f "${install_script}" ]]; then
    download_file "https://dot.net/v1/dotnet-install.sh" "${install_script}"
    chmod +x "${install_script}"
  fi

  if ! "${install_script}" --channel "${DOTNET_CHANNEL}" --install-dir "${DOTNET_DIR}" --no-path \
    > "${DOTNET_INSTALL_LOG}" 2>&1; then
    cat "${DOTNET_INSTALL_LOG}" >&2
    exit 1
  fi

  export DOTNET_ROOT="${DOTNET_DIR}"
  export PATH="${DOTNET_DIR}:${PATH}"
}

check_web_health() {
  local base_url="$1"
  local url="${base_url%/}/api/v1/health"

  if command -v curl >/dev/null 2>&1; then
    curl -fsS "${url}" >/dev/null 2>&1 && return 0
  elif command -v wget >/dev/null 2>&1; then
    wget -qO /dev/null "${url}" >/dev/null 2>&1 && return 0
  fi

  return 1
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

is_zebra_bridge_process() {
  local pid="$1"
  if [[ -z "${pid}" ]]; then
    return 1
  fi
  ps -p "${pid}" -o comm= 2>/dev/null | tr -d '[:space:]' | grep -qi "ZebraBridge.Web"
}

start_detached_server() {
  local log_file="$1"
  local -a cmd
  cmd=("${DOTNET_BIN}" run --project "${ROOT_DIR}/src/ZebraBridge.Web/ZebraBridge.Web.csproj")

  if command -v setsid >/dev/null 2>&1; then
    setsid "${cmd[@]}" > "${log_file}" 2>&1 < /dev/null &
  else
    nohup "${cmd[@]}" > "${log_file}" 2>&1 < /dev/null &
  fi

  echo "$!"
}

stop_existing_server() {
  local base_url="$1"
  local port="$2"

  local pid
  pid="$(find_listening_pid "${port}")"
  if [[ -z "${pid}" ]]; then
    return 0
  fi

  if is_zebra_bridge_health "${base_url}" || is_zebra_bridge_process "${pid}"; then
    kill "${pid}" >/dev/null 2>&1 || true
    for _ in $(seq 1 40); do
      if [[ -z "$(find_listening_pid "${port}")" ]]; then
        return 0
      fi
      sleep 0.1
    done
    kill -9 "${pid}" >/dev/null 2>&1 || true
    sleep 0.2
    return 0
  fi

  echo "ERROR: Port ${port} is already in use and does not look like zebra-bridge." >&2
  exit 1
}

show_boot_animation() {
  local base_url="$1"
  if [[ ! -t 1 ]]; then
    return
  fi

  local frames=(
$'ZEBRA BRIDGE\n[>----------]\nInitializing...'
$'ZEBRA BRIDGE\n[=>---------]\nInitializing...'
$'ZEBRA BRIDGE\n[==>--------]\nInitializing...'
$'ZEBRA BRIDGE\n[===>-------]\nInitializing...'
$'ZEBRA BRIDGE\n[====>------]\nInitializing...'
$'ZEBRA BRIDGE\n[=====>-----]\nInitializing...'
$'ZEBRA BRIDGE\n[======>----]\nInitializing...'
$'ZEBRA BRIDGE\n[=======>---]\nInitializing...'
$'ZEBRA BRIDGE\n[========>--]\nInitializing...'
$'ZEBRA BRIDGE\n[=========>-]\nInitializing...'
$'ZEBRA BRIDGE\n[==========>]\nInitializing...'
  )

  local start=$SECONDS
  local min_seconds=1
  local max_seconds=8
  local i=0

  while (( SECONDS - start < max_seconds )); do
    printf "\033[2J\033[H%s" "${frames[i]}"
    i=$(( (i + 1) % ${#frames[@]} ))
    if is_zebra_bridge_health "${base_url}" && (( SECONDS - start >= min_seconds )); then
      break
    fi
    sleep 0.08
  done

  printf "\033[2J\033[H"
}

ensure_dotnet

WEB_HOST="${ZEBRA_WEB_HOST:-${WEB_HOST_DEFAULT}}"
WEB_PORT="${ZEBRA_WEB_PORT:-${WEB_PORT_DEFAULT}}"

export ZEBRA_WEB_HOST="${WEB_HOST}"
export ZEBRA_WEB_PORT="${WEB_PORT}"

if [[ -z "${ASPNETCORE_URLS:-}" ]]; then
  export ASPNETCORE_URLS="http://${WEB_HOST}:${WEB_PORT}"
fi

if [[ "${1:-}" == "--daemon" || "${1:-}" == "--service" ]]; then
  shift || true
  LOG_DIR="${LOG_DIR:-${ROOT_DIR}/logs}"
  LOG_FILE="${ZEBRA_DAEMON_LOG:-${LOG_DIR}/zebra-web.log}"
  PID_FILE="${ZEBRA_DAEMON_PID:-${LOG_DIR}/zebra-web.pid}"
  mkdir -p "${LOG_DIR}"

  if [[ -f "${PID_FILE}" ]]; then
    pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
    if [[ -n "${pid}" ]] && kill -0 "${pid}" >/dev/null 2>&1; then
      echo "zebra-bridge already running (pid ${pid})."
      exit 0
    fi
    rm -f "${PID_FILE}"
  fi

  BASE_URL="http://${WEB_HOST}:${WEB_PORT}"
  stop_existing_server "${BASE_URL}" "${WEB_PORT}"

  pid="$(start_detached_server "${LOG_FILE}")"
  echo "${pid}" > "${PID_FILE}"
  echo "zebra-bridge started (pid ${pid}). Logs: ${LOG_FILE}"
  exit 0
fi

if [[ "${1:-}" == "--tui" ]]; then
  use_screen="${ZEBRA_TUI_SCREEN:-0}"
  for arg in "$@"; do
    case "${arg}" in
      --screen)
        use_screen=1
        ;;
      --no-screen)
        use_screen=0
        ;;
    esac
  done
  if [[ "${use_screen}" != "0" && -z "${STY:-}" && -z "${TMUX:-}" && -t 0 && -t 1 && -t 2 ]]; then
    if command -v screen >/dev/null 2>&1; then
      session="${ZEBRA_TUI_SESSION:-zebra-tui}"
      if ! screen -ls 2>/dev/null | grep -q "[.]${session}[[:space:]]"; then
        screen -S "${session}" -d -m "${ROOT_DIR}/run.sh" --tui --no-screen "$@"
      fi
      exec screen -S "${session}" -x
    fi
  fi

  shift || true
  LOG_DIR="${LOG_DIR:-${ROOT_DIR}/logs}"
  LOG_FILE="${ZEBRA_DAEMON_LOG:-${LOG_DIR}/zebra-web.log}"
  PID_FILE="${ZEBRA_DAEMON_PID:-${LOG_DIR}/zebra-web.pid}"
  mkdir -p "${LOG_DIR}"

  if [[ -z "${ZEBRA_DISABLE_UI:-}" ]]; then
    export ZEBRA_DISABLE_UI=1
  fi

  expand_home() {
    local path="$1"
    if [[ "${path}" == "~"* ]]; then
      echo "${path/#\~/${HOME}}"
      return
    fi
    echo "${path}"
  }

  resolve_state_dir() {
    if [[ -n "${ZEBRA_STATE_DIR:-}" ]]; then
      expand_home "${ZEBRA_STATE_DIR}"
      return
    fi
    if [[ -n "${XDG_STATE_HOME:-}" ]]; then
      echo "$(expand_home "${XDG_STATE_HOME}")/zebra-bridge"
      return
    fi
    echo "${HOME}/.local/state/zebra-bridge"
  }

  resolve_erp_config_path() {
    if [[ -n "${ZEBRA_ERP_CONFIG_PATH:-}" ]]; then
      expand_home "${ZEBRA_ERP_CONFIG_PATH}"
      return
    fi
    echo "$(resolve_state_dir)/erp-config.json"
  }

  SETUP_ARGS=()
  TUI_ARGS=()
  FORCE_SETUP=0

  while [[ $# -gt 0 ]]; do
    case "$1" in
      --no-screen|--screen)
        ;;
      --setup)
        FORCE_SETUP=1
        ;;
      --online|--offline)
        SETUP_ARGS+=("$1")
        FORCE_SETUP=1
        ;;
      --erp-url|--erp-token|--device|--mode)
        SETUP_ARGS+=("$1")
        shift || true
        if [[ -n "${1:-}" ]]; then
          SETUP_ARGS+=("$1")
        fi
        FORCE_SETUP=1
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
    "${ROOT_DIR}/cli.sh" setup "${SETUP_ARGS[@]}"
  fi

  BASE_URL="http://${WEB_HOST}:${WEB_PORT}"
  server_started=0

  if [[ -f "${PID_FILE}" ]]; then
    pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
    if [[ -n "${pid}" ]] && kill -0 "${pid}" >/dev/null 2>&1; then
      echo "zebra-bridge already running (pid ${pid})."
    else
      rm -f "${PID_FILE}"
    fi
  fi

  if [[ ! -f "${PID_FILE}" ]]; then
    pid="$(find_listening_pid "${WEB_PORT}")"
    if [[ -n "${pid}" ]]; then
      if is_zebra_bridge_health "${BASE_URL}" || is_zebra_bridge_process "${pid}"; then
        echo "zebra-bridge already running (pid ${pid})."
      else
        echo "ERROR: Port ${WEB_PORT} is already in use and does not look like zebra-bridge." >&2
        exit 1
      fi
    else
      pid="$(start_detached_server "${LOG_FILE}")"
      echo "${pid}" > "${PID_FILE}"
      echo "zebra-bridge started (pid ${pid}). Logs: ${LOG_FILE}"
      server_started=1
    fi
  fi

  if [[ "${server_started}" -eq 1 ]]; then
    show_boot_animation "${BASE_URL}"
  fi
  "${ROOT_DIR}/cli.sh" tui "${TUI_ARGS[@]}"
  exit 0
fi

BASE_URL="http://${WEB_HOST}:${WEB_PORT}"
stop_existing_server "${BASE_URL}" "${WEB_PORT}"

exec "${DOTNET_BIN}" run --project "${ROOT_DIR}/src/ZebraBridge.Web/ZebraBridge.Web.csproj"
