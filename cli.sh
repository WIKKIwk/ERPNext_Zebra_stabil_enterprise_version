#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET_DIR="${DOTNET_DIR:-${ROOT_DIR}/.dotnet}"
DOTNET_BIN="${DOTNET_BIN:-${DOTNET_DIR}/dotnet}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-8.0}"
DOTNET_INSTALL_LOG="${DOTNET_INSTALL_LOG:-${DOTNET_DIR}/dotnet-install.log}"

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

ensure_dotnet

LOG_DIR="${LOG_DIR:-${ROOT_DIR}/logs}"
BUILD_LOG="${BUILD_LOG:-${LOG_DIR}/cli-build.log}"
mkdir -p "${LOG_DIR}"

if [[ "${CLI_NO_BUILD:-0}" != "1" ]]; then
  if ! "${DOTNET_BIN}" build "${ROOT_DIR}/src/ZebraBridge.Cli/ZebraBridge.Cli.csproj" \
    > "${BUILD_LOG}" 2>&1; then
    cat "${BUILD_LOG}" >&2
    exit 1
  fi
fi

exec "${DOTNET_BIN}" run --no-build --project "${ROOT_DIR}/src/ZebraBridge.Cli/ZebraBridge.Cli.csproj" -- "$@"
