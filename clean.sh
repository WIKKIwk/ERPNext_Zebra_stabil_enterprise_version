#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DRY_RUN=0

print_usage() {
  cat <<'EOF'
Usage: ./clean.sh [--dry-run]

Removes local build artifacts and caches to reduce disk usage.
EOF
}

for arg in "$@"; do
  case "${arg}" in
    --dry-run)
      DRY_RUN=1
      ;;
    -h|--help)
      print_usage
      exit 0
      ;;
    *)
      echo "Unknown argument: ${arg}" >&2
      print_usage >&2
      exit 1
      ;;
  esac
done

remove_path() {
  local path="$1"
  if [[ -e "${path}" ]]; then
    if [[ "${DRY_RUN}" == "1" ]]; then
      echo "Would remove: ${path}"
    else
      rm -rf "${path}"
    fi
  fi
}

remove_path "${ROOT_DIR}/.dotnet"
remove_path "${ROOT_DIR}/logs"
remove_path "${ROOT_DIR}/portable/dist"

if [[ -d "${ROOT_DIR}/src" ]]; then
  while IFS= read -r -d '' dir; do
    remove_path "${dir}"
  done < <(find "${ROOT_DIR}/src" -type d \( -name bin -o -name obj \) -prune -print0)
fi

if [[ -d "${ROOT_DIR}/tests" ]]; then
  while IFS= read -r -d '' dir; do
    remove_path "${dir}"
  done < <(find "${ROOT_DIR}/tests" -type d \( -name bin -o -name obj \) -prune -print0)
fi

if [[ "${DRY_RUN}" == "1" ]]; then
  echo "Dry run complete."
else
  echo "Cleanup complete."
fi
