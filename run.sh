#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET_DIR="${DOTNET_DIR:-${ROOT_DIR}/.dotnet}"
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
    if check_web_health "${base_url}" && (( SECONDS - start >= min_seconds )); then
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

if [[ "${1:-}" == "--tui" ]]; then
  shift || true
  LOG_DIR="${LOG_DIR:-${ROOT_DIR}/logs}"
  LOG_FILE="${LOG_FILE:-${LOG_DIR}/zebra-web.log}"
  mkdir -p "${LOG_DIR}"

  "${DOTNET_BIN}" run --project "${ROOT_DIR}/src/ZebraBridge.Web/ZebraBridge.Web.csproj" \
    > "${LOG_FILE}" 2>&1 &

  SERVER_PID=$!
  BASE_URL="http://${WEB_HOST}:${WEB_PORT}"
  show_boot_animation "${BASE_URL}"

  cleanup() {
    if kill -0 "${SERVER_PID}" >/dev/null 2>&1; then
      kill "${SERVER_PID}" >/dev/null 2>&1 || true
    fi
  }

  trap cleanup EXIT INT TERM
  "${ROOT_DIR}/cli.sh" tui --setup "$@"
  exit 0
fi

exec "${DOTNET_BIN}" run --project "${ROOT_DIR}/src/ZebraBridge.Web/ZebraBridge.Web.csproj"
