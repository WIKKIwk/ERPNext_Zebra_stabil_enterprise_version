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

WEB_APP_DIR="$ZEBRA_PORTABLE_APP_DIR/web"
export ASPNETCORE_CONTENTROOT="$WEB_APP_DIR"
export ASPNETCORE_WEBROOT="$WEB_APP_DIR/wwwroot"

cd "$WEB_APP_DIR"
exec ./ZebraBridge.Web
