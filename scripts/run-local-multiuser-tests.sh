#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_PROJECT="$ROOT_DIR/XivIpc.Tests/XivIpc.Tests.csproj"
WINE_TEST_HOST_PROJECT="$ROOT_DIR/XivIpc.WineTestHost/XivIpc.WineTestHost.csproj"
NATIVE_HOST_PROJECT="$ROOT_DIR/XivIpc.NativeHost/XivIpc.NativeHost.csproj"
ARTIFACT_ROOT="${TINYIPC_RUNTIME_TEST_ARTIFACT_ROOT:-$ROOT_DIR/published/runtime-tests}"
WINDOWS_HOST_DIR="$ARTIFACT_ROOT/win-x64"
NATIVE_HOST_DIR="$ARTIFACT_ROOT/linux-x64"
WINDOWS_HOST_PATH="$WINDOWS_HOST_DIR/XivIpc.WineTestHost.exe"
NATIVE_HOST_PATH="$NATIVE_HOST_DIR/XivIpc.NativeHost"

find_secondary_user_with_prefix() {
  local kind="$1"
  local configured="${2:-}"

  if [[ -n "$configured" ]]; then
    if sudo -n -u "$configured" bash -lc "test -d \"\$HOME/.xlcore/${kind}prefix\"" >/dev/null 2>&1; then
      printf '%s\n' "$configured"
      return 0
    fi

    return 1
  fi

  local current_user
  current_user="$(id -un)"

  local home_dir
  for home_dir in /home/*; do
    [[ -d "$home_dir" ]] || continue
    local user
    user="$(basename "$home_dir")"
    [[ "$user" == "$current_user" ]] && continue

    if sudo -n -u "$user" bash -lc "test -d \"\$HOME/.xlcore/${kind}prefix\"" >/dev/null 2>&1; then
      printf '%s\n' "$user"
      return 0
    fi
  done

  return 1
}

find_secondary_user_in_group() {
  local group_name="$1"
  local configured="${2:-}"

  if [[ -n "$configured" ]]; then
    if id -nG "$configured" 2>/dev/null | tr ' ' '\n' | grep -Fx "$group_name" >/dev/null 2>&1; then
      printf '%s\n' "$configured"
      return 0
    fi
    return 1
  fi

  local current_user
  current_user="$(id -un)"

  local home_dir
  for home_dir in /home/*; do
    [[ -d "$home_dir" ]] || continue
    local user
    user="$(basename "$home_dir")"
    [[ "$user" == "$current_user" ]] && continue

    if id -nG "$user" 2>/dev/null | tr ' ' '\n' | grep -Fx "$group_name" >/dev/null 2>&1; then
      printf '%s\n' "$user"
      return 0
    fi
  done

  return 1
}

printf 'Building test project once...\n'
dotnet test "$TEST_PROJECT" -m:1 -v minimal --no-restore --filter "FullyQualifiedName~__build_smoke_sentinel_that_matches_nothing__" >/dev/null || true

BROKERED_SIDECAR_FILTER="FullyQualifiedName~XivIpc.Tests.FunctionalTests|FullyQualifiedName~XivIpc.Tests.ParallelFunctionalTests|FullyQualifiedName~XivIpc.Tests.SidecarLifecycleTests|FullyQualifiedName~XivIpc.Tests.SidecarRuntimeIntegrationTests"
MULTIUSER_FILTER="FullyQualifiedName~XivIpc.Tests.SidecarMultiUserTests|FullyQualifiedName~XivIpc.Tests.SidecarRingMultiUserTests|FullyQualifiedName~XivIpc.Tests.ProductionPathMultiUserTests|FullyQualifiedName~XivIpc.Tests.ProductionPathRingMultiUserTests"
SHARED_GROUP="${TINYIPC_SHARED_GROUP:-steam}"

printf 'Publishing runtime test artifacts...\n'
mkdir -p "$WINDOWS_HOST_DIR" "$NATIVE_HOST_DIR"
dotnet publish "$WINE_TEST_HOST_PROJECT" -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$WINDOWS_HOST_DIR"
dotnet publish "$NATIVE_HOST_PROJECT" -c Debug -f net9.0 -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$NATIVE_HOST_DIR"

WINE_SECONDARY_USER="$(find_secondary_user_with_prefix "wine" "${TINYIPC_WINE_SECONDARY_USER:-}" || true)"
PROTON_SECONDARY_USER="$(find_secondary_user_with_prefix "proton" "${TINYIPC_PROTON_SECONDARY_USER:-}" || true)"
MULTIUSER_SECONDARY_USER="$(find_secondary_user_in_group "$SHARED_GROUP" "${TINYIPC_MULTIUSER_SECONDARY_USER:-}" || true)"

printf '\nRunning native brokered-sidecar multi-user validation...\n'
if [[ -n "$MULTIUSER_SECONDARY_USER" ]]; then
  printf 'Using secondary multi-user account: %s (group %s)\n' "$MULTIUSER_SECONDARY_USER" "$SHARED_GROUP"
  env \
    TINYIPC_ENABLE_MULTIUSER_TESTS=1 \
    TINYIPC_MULTIUSER_SECONDARY_USER="$MULTIUSER_SECONDARY_USER" \
    TINYIPC_SHARED_GROUP="$SHARED_GROUP" \
    dotnet test "$TEST_PROJECT" --no-build --framework net9.0 -m:1 -v normal --filter \
      "$MULTIUSER_FILTER"
else
  printf 'No secondary user in shared group %s was found. Skipping native multi-user validation.\n' "$SHARED_GROUP"
fi

printf '\nRunning Wine brokered-sidecar validation...\n'
if [[ -n "$WINE_SECONDARY_USER" ]]; then
  printf 'Using secondary Wine user: %s\n' "$WINE_SECONDARY_USER"
  env \
    TINYIPC_ENABLE_WINE_TESTS=1 \
    TINYIPC_WINE_TEST_LEVEL=extended \
    TINYIPC_RUNTIME_TEST_HOST_PATH="$WINDOWS_HOST_PATH" \
    TINYIPC_RUNTIME_NATIVE_HOST_PATH="$NATIVE_HOST_PATH" \
    TINYIPC_WINE_SECONDARY_USER="$WINE_SECONDARY_USER" \
    dotnet test "$TEST_PROJECT" --no-build --framework net9.0 -m:1 -v normal --filter \
      "$BROKERED_SIDECAR_FILTER"
else
  printf 'No secondary Wine user with ~/.xlcore/wineprefix was found. Running brokered-sidecar validation under the current user only.\n'
  env \
    TINYIPC_ENABLE_WINE_TESTS=1 \
    TINYIPC_WINE_TEST_LEVEL=extended \
    TINYIPC_RUNTIME_TEST_HOST_PATH="$WINDOWS_HOST_PATH" \
    TINYIPC_RUNTIME_NATIVE_HOST_PATH="$NATIVE_HOST_PATH" \
    dotnet test "$TEST_PROJECT" --no-build --framework net9.0 -m:1 -v normal --filter \
      "$BROKERED_SIDECAR_FILTER"
fi

printf '\nRunning Proton brokered-sidecar validation...\n'
if [[ -n "$PROTON_SECONDARY_USER" ]]; then
  printf 'Using secondary Proton user: %s\n' "$PROTON_SECONDARY_USER"
  env \
    TINYIPC_ENABLE_PROTON_TESTS=1 \
    TINYIPC_PROTON_TEST_LEVEL=extended \
    TINYIPC_RUNTIME_TEST_HOST_PATH="$WINDOWS_HOST_PATH" \
    TINYIPC_RUNTIME_NATIVE_HOST_PATH="$NATIVE_HOST_PATH" \
    TINYIPC_PROTON_SECONDARY_USER="$PROTON_SECONDARY_USER" \
    dotnet test "$TEST_PROJECT" --no-build --framework net9.0 -m:1 -v normal --filter \
      "$BROKERED_SIDECAR_FILTER"
else
  printf 'No secondary Proton user with ~/.xlcore/protonprefix was found. Running brokered-sidecar validation under the current user only.\n'
  env \
    TINYIPC_ENABLE_PROTON_TESTS=1 \
    TINYIPC_PROTON_TEST_LEVEL=extended \
    TINYIPC_RUNTIME_TEST_HOST_PATH="$WINDOWS_HOST_PATH" \
    TINYIPC_RUNTIME_NATIVE_HOST_PATH="$NATIVE_HOST_PATH" \
    dotnet test "$TEST_PROJECT" --no-build --framework net9.0 -m:1 -v normal --filter \
      "$BROKERED_SIDECAR_FILTER"
fi
