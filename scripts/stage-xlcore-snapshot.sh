#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_DIR="${1:-$HOME/.xlcore}"
WINE_TARGET_DIR="$ROOT_DIR/docker/.xlcore-wine-snapshot"
PROTON_TARGET_DIR="$ROOT_DIR/docker/.xlcore-proton-snapshot"
LAUNCHER_INI="$SOURCE_DIR/launcher.ini"

if [[ ! -d "$SOURCE_DIR" ]]; then
  echo "XLCore source directory not found: $SOURCE_DIR" >&2
  exit 1
fi

read_launcher_value() {
  local key="$1"
  if [[ -f "$LAUNCHER_INI" ]]; then
    awk -F= -v target="$key" '$1 == target { print $2; exit }' "$LAUNCHER_INI"
  fi
}

stage_common_file() {
  local target_dir="$1"
  local source_path="$2"
  if [[ -f "$source_path" ]]; then
    mkdir -p "$target_dir"
    cp -a "$source_path" "$target_dir/"
  fi
}

copy_tree_or_fail() {
  local source_path="$1"
  local target_path="$2"
  local description="$3"
  if [[ ! -e "$source_path" ]]; then
    echo "Missing $description: $source_path" >&2
    exit 1
  fi

  mkdir -p "$(dirname "$target_path")"
  cp -a "$source_path" "$target_path"
}

discover_wine_runtime_path() {
  if [[ -n "${TINYIPC_WINE_SOURCE_PATH:-}" ]]; then
    printf '%s\n' "$TINYIPC_WINE_SOURCE_PATH"
    return 0
  fi

  local candidate
  candidate="$(find "$SOURCE_DIR/compatibilitytool/wine" -maxdepth 4 -type f -path '*/bin/wine' | head -n 1 || true)"
  if [[ -n "$candidate" ]]; then
    dirname "$(dirname "$candidate")"
    return 0
  fi

  return 1
}

rm -rf "$WINE_TARGET_DIR" "$PROTON_TARGET_DIR"
mkdir -p "$WINE_TARGET_DIR" "$PROTON_TARGET_DIR"

stage_common_file "$WINE_TARGET_DIR" "$LAUNCHER_INI"
stage_common_file "$PROTON_TARGET_DIR" "$LAUNCHER_INI"
WINE_RUNTIME_PATH="$(discover_wine_runtime_path)"
if [[ -z "$WINE_RUNTIME_PATH" || ! -d "$WINE_RUNTIME_PATH" ]]; then
  echo "Missing Wine runtime" >&2
  exit 1
fi
printf '%s\n' "$WINE_RUNTIME_PATH" > "$WINE_TARGET_DIR/wine-runtime-path.txt"

PROTON_VERSION="$(read_launcher_value RB_ProtonVersion)"
if [[ -z "$PROTON_VERSION" ]]; then
  echo "Missing RB_ProtonVersion in $LAUNCHER_INI" >&2
  exit 1
fi

PROTON_SOURCE_PATH="${TINYIPC_PROTON_SOURCE_PATH:-/usr/share/steam/compatibilitytools.d/$PROTON_VERSION}"
if [[ ! -d "$PROTON_SOURCE_PATH" ]]; then
  echo "Missing Proton runtime: $PROTON_SOURCE_PATH" >&2
  exit 1
fi

printf '%s\n' "$PROTON_SOURCE_PATH" > "$PROTON_TARGET_DIR/proton-runtime-path.txt"

chmod -R u+rwX "$WINE_TARGET_DIR" "$PROTON_TARGET_DIR"

printf 'WINE_SNAPSHOT:%s\n' "$WINE_TARGET_DIR"
printf 'PROTON_SNAPSHOT:%s\n' "$PROTON_TARGET_DIR"
