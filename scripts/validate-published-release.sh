#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

# shellcheck source=./release-common.sh
source "${SCRIPT_DIR}/release-common.sh"

MODE=""
TAG=""
REPOSITORY=""
METADATA_FILE=""
EXPECTED_PLUGINMASTER_FILE=""
DEFAULT_BRANCH=""

usage() {
  cat <<'TXT'
Usage:
  scripts/validate-published-release.sh --mode plugin|semver --tag <tag> --repo <owner/repo> --metadata-file <path> [options]

Options:
  --mode <plugin|semver>
  --tag <tag>
  --repo <owner/repo>
  --metadata-file <path>
  --expected-pluginmaster-file <path>
  --default-branch <branch>
TXT
}

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --mode)
      MODE="${2:?missing value for --mode}"
      shift 2
      ;;
    --tag)
      TAG="${2:?missing value for --tag}"
      shift 2
      ;;
    --repo)
      REPOSITORY="${2:?missing value for --repo}"
      shift 2
      ;;
    --metadata-file)
      METADATA_FILE="${2:?missing value for --metadata-file}"
      shift 2
      ;;
    --expected-pluginmaster-file)
      EXPECTED_PLUGINMASTER_FILE="${2:?missing value for --expected-pluginmaster-file}"
      shift 2
      ;;
    --default-branch)
      DEFAULT_BRANCH="${2:?missing value for --default-branch}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      die "Unknown argument: $1"
      ;;
  esac
done

[[ -n "${MODE}" ]] || die "--mode is required"
[[ -n "${TAG}" ]] || die "--tag is required"
[[ -n "${REPOSITORY}" ]] || die "--repo is required"
[[ -n "${METADATA_FILE}" ]] || die "--metadata-file is required"

case "${MODE}" in
  plugin|semver) ;;
  *) die "Unsupported mode: ${MODE}" ;;
esac

assert_json_file() {
  local file="$1"
  [[ -f "${file}" ]] || die "Missing file: ${file}"
  jq -e '.' "${file}" >/dev/null || die "Invalid JSON file: ${file}"
}

sorted_lines() {
  LC_ALL=C sort
}

assert_same_file_contents() {
  local label="$1"
  local expected_file="$2"
  local actual_file="$3"

  if ! diff -u "${expected_file}" "${actual_file}" >/dev/null; then
    printf '[validate] expected %s:\n' "${label}" >&2
    cat "${expected_file}" >&2
    printf '[validate] actual %s:\n' "${label}" >&2
    cat "${actual_file}" >&2
    die "Mismatch for ${label}"
  fi
}

assert_json_file "${METADATA_FILE}"

release_json_file="$(mktemp "${TMPDIR:-/tmp}/validate-published-release.release.XXXXXX")"
expected_assets_file="$(mktemp "${TMPDIR:-/tmp}/validate-published-release.expected-assets.XXXXXX")"
actual_assets_file="$(mktemp "${TMPDIR:-/tmp}/validate-published-release.actual-assets.XXXXXX")"

gh_api_json "repos/${REPOSITORY}/releases/tags/${TAG}" "${REPOSITORY}" > "${release_json_file}"

jq -e --arg tag "${TAG}" '.tag_name == $tag and .draft == false' "${release_json_file}" >/dev/null || die "Published release does not match expected tag/draft state"

case "${MODE}" in
  plugin)
    jq -e '.prerelease == true' "${release_json_file}" >/dev/null || die "Plugin release must be a prerelease"
    latest_release_file="$(mktemp "${TMPDIR:-/tmp}/validate-published-release.latest.XXXXXX")"
    if gh_api_json "repos/${REPOSITORY}/releases/latest" "${REPOSITORY}" > "${latest_release_file}" 2>/dev/null; then
      jq -e --arg tag "${TAG}" '.tag_name != $tag' "${latest_release_file}" >/dev/null || die "Plugin prerelease unexpectedly became latest"
    fi
    rm -f "${latest_release_file}"
    ;;
  semver)
    jq -e '.prerelease == false' "${release_json_file}" >/dev/null || die "Semver release must not be a prerelease"
    gh_api_json "repos/${REPOSITORY}/releases/latest" "${REPOSITORY}" | jq -e --arg tag "${TAG}" '.tag_name == $tag' >/dev/null || die "Semver release is not the latest release"
    ;;
esac

jq -r --arg tag "${TAG}" '.releases[] | select(.tag == $tag) | .assets[] | split("/")[-1]' "${METADATA_FILE}" | sorted_lines > "${expected_assets_file}"
jq -r '.assets[].name' "${release_json_file}" | sorted_lines > "${actual_assets_file}"
assert_same_file_contents "published assets" "${expected_assets_file}" "${actual_assets_file}"

if [[ -n "${EXPECTED_PLUGINMASTER_FILE}" ]]; then
  [[ -n "${DEFAULT_BRANCH}" ]] || die "--default-branch is required when validating pluginmaster"
  assert_json_file "${EXPECTED_PLUGINMASTER_FILE}"

  remote_pluginmaster_file="$(mktemp "${TMPDIR:-/tmp}/validate-published-release.pluginmaster.XXXXXX")"
  gh_api_json "repos/${REPOSITORY}/contents/pluginmaster.json?ref=${DEFAULT_BRANCH}" "${REPOSITORY}" \
    | jq -r '.content // empty' \
    | tr -d '\n' \
    | base64 -d > "${remote_pluginmaster_file}"

  expected_pluginmaster_sorted="$(mktemp "${TMPDIR:-/tmp}/validate-published-release.expected-pluginmaster.XXXXXX")"
  remote_pluginmaster_sorted="$(mktemp "${TMPDIR:-/tmp}/validate-published-release.remote-pluginmaster.XXXXXX")"
  jq -S . "${EXPECTED_PLUGINMASTER_FILE}" > "${expected_pluginmaster_sorted}"
  jq -S . "${remote_pluginmaster_file}" > "${remote_pluginmaster_sorted}"
  assert_same_file_contents "pluginmaster.json" "${expected_pluginmaster_sorted}" "${remote_pluginmaster_sorted}"
  rm -f "${remote_pluginmaster_file}" "${expected_pluginmaster_sorted}" "${remote_pluginmaster_sorted}"
fi

rm -f "${release_json_file}" "${expected_assets_file}" "${actual_assets_file}"
log "Validated published ${MODE} release ${TAG}"
