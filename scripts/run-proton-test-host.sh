#!/usr/bin/env bash
set -euo pipefail

XLCORE_DIR="${TINYIPC_XLCORE_DIR:-$HOME/.xlcore}"
PREFIX="${TINYIPC_PROTON_PREFIX:-$XLCORE_DIR/protonprefix}"
LAUNCHER_INI="$XLCORE_DIR/launcher.ini"

read_launcher_toggle() {
  local key="$1"
  if [[ -f "$LAUNCHER_INI" ]]; then
    awk -F= -v target="$key" '$1 == target { print $2; exit }' "$LAUNCHER_INI"
  fi
}

read_launcher_value() {
  local key="$1"
  if [[ -f "$LAUNCHER_INI" ]]; then
    awk -F= -v target="$key" '$1 == target { print $2; exit }' "$LAUNCHER_INI"
  fi
}

discover_proton_path() {
  if [[ -n "${TINYIPC_PROTON_PATH:-}" ]]; then
    printf '%s\n' "$TINYIPC_PROTON_PATH"
    return 0
  fi

  local configured
  configured="$(read_launcher_value RB_ProtonVersion)"
  if [[ -n "$configured" && -d "/usr/share/steam/compatibilitytools.d/$configured" ]]; then
    printf '%s\n' "/usr/share/steam/compatibilitytools.d/$configured"
    return 0
  fi

  if [[ -d "/usr/share/steam/compatibilitytools.d/proton-cachyos-slr" ]]; then
    printf '%s\n' "/usr/share/steam/compatibilitytools.d/proton-cachyos-slr"
    return 0
  fi

  return 1
}

PROTON_PATH="$(discover_proton_path)"
RUNNER="${TINYIPC_PROTON_RUNNER:-}"
if [[ -z "$RUNNER" ]]; then
  if command -v umu-run >/dev/null 2>&1; then
    RUNNER="$(command -v umu-run)"
  else
    RUNNER="$PROTON_PATH/proton"
  fi
fi

export STEAM_COMPAT_DATA_PATH="$PREFIX"
export WINEPREFIX="${WINEPREFIX:-$PREFIX/pfx}"
export PROTONPATH="$PROTON_PATH"
export GAMEID="${GAMEID:-TinyIpcHarness}"
export XL_WINEONLINUX="${XL_WINEONLINUX:-true}"
export TINYIPC_WINDOWS_RUNTIME="${TINYIPC_WINDOWS_RUNTIME:-proton}"

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

if [[ -z "${PROTON_USE_NTSYNC:-}" ]]; then
  case "$(read_launcher_toggle NTSyncEnabled)" in
    true|True|1) export PROTON_USE_NTSYNC=1 ;;
    false|False|0) export PROTON_USE_NTSYNC=0 ;;
  esac
fi

printf 'RUNNER:%s\n' "$RUNNER" >&2
printf 'XLCORE:%s\n' "$XLCORE_DIR" >&2
printf 'PREFIX:%s\n' "$PREFIX" >&2
printf 'PROTONPATH:%s\n' "$PROTON_PATH" >&2
printf 'ESYNC:%s\n' "${WINEESYNC:-}" >&2
printf 'FSYNC:%s\n' "${WINEFSYNC:-}" >&2
printf 'NTSYNC:%s\n' "${PROTON_USE_NTSYNC:-}" >&2

if [[ $# -lt 1 ]]; then
  echo "Missing executable path." >&2
  exit 1
fi

EXE_PATH="$1"
shift

if [[ "$EXE_PATH" != /* ]]; then
  EXE_PATH="$(realpath "$EXE_PATH")"
fi

export STEAM_COMPAT_INSTALL_PATH="${STEAM_COMPAT_INSTALL_PATH:-$(dirname "$EXE_PATH")}"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$STEAM_COMPAT_INSTALL_PATH}"

if [[ "$(basename "$RUNNER")" == "proton" ]]; then
  exec "$RUNNER" run "$EXE_PATH" "$@"
fi

exec "$RUNNER" "$EXE_PATH" "$@"
