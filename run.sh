#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET_DIR="${DOTNET_DIR:-${ROOT_DIR}/.dotnet}"
DOTNET_BIN="${DOTNET_BIN:-${DOTNET_DIR}/dotnet}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-8.0}"

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

  "${install_script}" --channel "${DOTNET_CHANNEL}" --install-dir "${DOTNET_DIR}" --no-path

  export DOTNET_ROOT="${DOTNET_DIR}"
  export PATH="${DOTNET_DIR}:${PATH}"
}

ensure_dotnet

if [[ "${1:-}" == "--tui" ]]; then
  shift || true
  LOG_DIR="${LOG_DIR:-${ROOT_DIR}/logs}"
  LOG_FILE="${LOG_FILE:-${LOG_DIR}/zebra-web.log}"
  mkdir -p "${LOG_DIR}"

  "${DOTNET_BIN}" run --project "${ROOT_DIR}/src/ZebraBridge.Web/ZebraBridge.Web.csproj" \
    > "${LOG_FILE}" 2>&1 &

  SERVER_PID=$!
  echo "ZebraBridge web started (pid: ${SERVER_PID}). Logs: ${LOG_FILE}"

  cleanup() {
    if kill -0 "${SERVER_PID}" >/dev/null 2>&1; then
      kill "${SERVER_PID}" >/dev/null 2>&1 || true
    fi
  }

  trap cleanup EXIT INT TERM
  "${ROOT_DIR}/cli.sh" tui "$@"
  exit 0
fi

exec "${DOTNET_BIN}" run --project "${ROOT_DIR}/src/ZebraBridge.Web/ZebraBridge.Web.csproj"
