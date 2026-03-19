#!/usr/bin/env bash
set -euo pipefail

XLCORE_DIR="${TINYIPC_XLCORE_DIR:-$HOME/.xlcore}"
PREFIX="${TINYIPC_WINE_PREFIX:-$XLCORE_DIR/wineprefix}"
LAUNCHER_INI="$XLCORE_DIR/launcher.ini"

discover_wine_runner() {
  if [[ -n "${TINYIPC_WINE_RUNNER:-}" ]]; then
    printf '%s\n' "$TINYIPC_WINE_RUNNER"
    return 0
  fi

  local candidate
  candidate="$(find "$XLCORE_DIR/compatibilitytool/wine" -maxdepth 4 -type f -path '*/bin/wine' | head -n 1 || true)"
  if [[ -n "$candidate" ]]; then
    printf '%s\n' "$candidate"
    return 0
  fi

  command -v wine
}

read_launcher_toggle() {
  local key="$1"
  if [[ -f "$LAUNCHER_INI" ]]; then
    awk -F= -v target="$key" '$1 == target { print $2; exit }' "$LAUNCHER_INI"
  fi
}

RUNNER="$(discover_wine_runner)"
export WINEPREFIX="$PREFIX"
export XL_WINEONLINUX="${XL_WINEONLINUX:-true}"

if [[ -z "${WINEESYNC:-}" ]]; then
  case "$(read_launcher_toggle ESyncEnabled)" in
    true|True|1) export WINEESYNC=1 ;;
    false|False|0) export WINEESYNC=0 ;;
  esac
fi

if [[ -z "${WINEFSYNC:-}" ]]; then
  case "$(read_launcher_toggle FSyncEnabled)" in
    true|True|1) export WINEFSYNC=1 ;;
    false|False|0) export WINEFSYNC=0 ;;
  esac
fi

printf 'RUNNER:%s\n' "$RUNNER" >&2
printf 'XLCORE:%s\n' "$XLCORE_DIR" >&2
printf 'PREFIX:%s\n' "$PREFIX" >&2
printf 'ESYNC:%s\n' "${WINEESYNC:-}" >&2
printf 'FSYNC:%s\n' "${WINEFSYNC:-}" >&2

exec "$RUNNER" "$@"
echo $?
