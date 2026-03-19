#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_PROJECT="$ROOT_DIR/XivIpc.Tests/XivIpc.Tests.csproj"

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

printf 'Building test project once...\n'
dotnet test "$TEST_PROJECT" -m:1 -v minimal --no-restore --filter "FullyQualifiedName~__build_smoke_sentinel_that_matches_nothing__" >/dev/null || true

WINE_SECONDARY_USER="$(find_secondary_user_with_prefix "wine" "${TINYIPC_WINE_SECONDARY_USER:-}" || true)"
PROTON_SECONDARY_USER="$(find_secondary_user_with_prefix "proton" "${TINYIPC_PROTON_SECONDARY_USER:-}" || true)"

printf '\nRunning Wine multi-user validation...\n'
if [[ -n "$WINE_SECONDARY_USER" ]]; then
  printf 'Using secondary Wine user: %s\n' "$WINE_SECONDARY_USER"
  env \
    TINYIPC_ENABLE_WINE_TESTS=1 \
    TINYIPC_WINE_TEST_LEVEL=extended \
    TINYIPC_WINE_SECONDARY_USER="$WINE_SECONDARY_USER" \
    dotnet test "$TEST_PROJECT" --no-build -m:1 -v normal --filter \
      "FullyQualifiedName~Wine_FileTransport_CrossUser_Broadcast_Works_WhenEnabled|FullyQualifiedName~Wine_SidecarSharedMemory_CrossUser_Broadcast_Works_WhenEnabled|FullyQualifiedName~Wine_CrossUser_NamedEvent_Probes_AreObservational_WhenEnabled"
else
  printf 'No secondary Wine user with ~/.xlcore/wineprefix was found. Skipping Wine multi-user validation.\n'
fi

printf '\nRunning Proton multi-user validation...\n'
if [[ -n "$PROTON_SECONDARY_USER" ]]; then
  printf 'Using secondary Proton user: %s\n' "$PROTON_SECONDARY_USER"
  env \
    TINYIPC_ENABLE_PROTON_TESTS=1 \
    TINYIPC_PROTON_TEST_LEVEL=extended \
    TINYIPC_PROTON_SECONDARY_USER="$PROTON_SECONDARY_USER" \
    dotnet test "$TEST_PROJECT" --no-build -m:1 -v normal --filter \
      "FullyQualifiedName~Proton_FileTransport_CrossUser_Broadcast_Works_WhenEnabled|FullyQualifiedName~Proton_SidecarSharedMemory_CrossUser_Broadcast_Works_WhenEnabled|FullyQualifiedName~Proton_CrossUser_NamedEvent_Probes_AreObservational_WhenEnabled"
else
  printf 'No secondary Proton user with ~/.xlcore/protonprefix was found. Skipping Proton multi-user validation.\n'
fi
