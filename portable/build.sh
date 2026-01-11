#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd -- "$ROOT_DIR/.." && pwd)"
TEMPLATE_DIR="$ROOT_DIR/templates"
DIST_ROOT="$ROOT_DIR/dist"
DIST_DIR="$DIST_ROOT/zebra-bridge"

ARCHES="${ZEBRA_PORTABLE_ARCHES:-x64}"
ARCH_LIST=()
case "$ARCHES" in
  all)
    ARCH_LIST=(x64 arm64)
    ;;
  x64|amd64)
    ARCH_LIST=(x64)
    ;;
  arm64|aarch64)
    ARCH_LIST=(arm64)
    ;;
  *)
    echo "Unknown ZEBRA_PORTABLE_ARCHES: $ARCHES" >&2
    exit 1
    ;;
esac

DOTNET_BIN=""
if [[ -x "$APP_DIR/.dotnet/dotnet" ]]; then
  DOTNET_BIN="$APP_DIR/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
  DOTNET_BIN="$(command -v dotnet)"
else
  echo "dotnet SDK not found. Install .NET 8 SDK or run ./run.sh once." >&2
  exit 1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

mkdir -p "$DIST_ROOT"
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

if command -v rsync >/dev/null 2>&1; then
  rsync -a "$TEMPLATE_DIR/" "$DIST_DIR/"
else
  (cd "$TEMPLATE_DIR" && tar -cf - .) | (cd "$DIST_DIR" && tar -xf -)
fi

mkdir -p "$DIST_DIR/bin" "$DIST_DIR/logs"

WEB_PROJ="$APP_DIR/src/ZebraBridge.Web/ZebraBridge.Web.csproj"
CLI_PROJ="$APP_DIR/src/ZebraBridge.Cli/ZebraBridge.Cli.csproj"

publish_project() {
  local proj="$1"
  local rid="$2"
  local out_dir="$3"
  "$DOTNET_BIN" publish "$proj" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None \
    -o "$out_dir" \
    --nologo
}

for arch in "${ARCH_LIST[@]}"; do
  if [[ "$arch" == "x64" ]]; then
    RID="linux-x64"
  else
    RID="linux-arm64"
  fi

  OUT_BASE="$DIST_DIR/bin/$RID"
  mkdir -p "$OUT_BASE/web" "$OUT_BASE/cli"

  publish_project "$WEB_PROJ" "$RID" "$OUT_BASE/web"
  publish_project "$CLI_PROJ" "$RID" "$OUT_BASE/cli"

  chmod +x "$OUT_BASE/web/ZebraBridge.Web" "$OUT_BASE/cli/ZebraBridge.Cli"
done

echo "OK: $DIST_DIR"
