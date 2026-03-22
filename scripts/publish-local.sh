#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

: "${PUBLISH_SCHEME:=plugins}"
: "${GITHUB_REPOSITORY:=local/test}"
: "${RELEASE_VERSION:=0.0.0-local}"
: "${TOOL_SELECTION:=all}"

exec bash "${SCRIPT_DIR}/publish-release.sh"
