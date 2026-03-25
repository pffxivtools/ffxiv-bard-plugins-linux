#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

# shellcheck source=./release-common.sh
source "${SCRIPT_DIR}/release-common.sh"

MODE=""
PLUGIN_SLUG=""
RELEASE_TAG=""
OUTPUT_ROOT=""
GITHUB_REPOSITORY_INPUT="${GITHUB_REPOSITORY:-}"
BASELINE_PLUGINMASTER_FILE=""
SHARED_DIR="${TINYIPC_SHARED_DIR:-}"
BASE_URL_INPUT="${BASE_URL:-}"

usage() {
  cat <<'TXT'
Usage:
  scripts/validate-publish-output.sh --mode plugin|semver|local [options]

Options:
  --mode <plugin|semver|local>
  --plugin <asset slug>
  --release-tag <tag>
  --output-root <path>
  --github-repository <owner/repo>
  --baseline-pluginmaster-file <path>
  --shared-dir <path>
  --base-url <url>
TXT
}

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --mode)
      MODE="${2:?missing value for --mode}"
      shift 2
      ;;
    --plugin)
      PLUGIN_SLUG="${2:?missing value for --plugin}"
      shift 2
      ;;
    --release-tag)
      RELEASE_TAG="${2:?missing value for --release-tag}"
      shift 2
      ;;
    --output-root)
      OUTPUT_ROOT="${2:?missing value for --output-root}"
      shift 2
      ;;
    --github-repository)
      GITHUB_REPOSITORY_INPUT="${2:?missing value for --github-repository}"
      shift 2
      ;;
    --baseline-pluginmaster-file)
      BASELINE_PLUGINMASTER_FILE="${2:?missing value for --baseline-pluginmaster-file}"
      shift 2
      ;;
    --shared-dir)
      SHARED_DIR="${2:?missing value for --shared-dir}"
      shift 2
      ;;
    --base-url)
      BASE_URL_INPUT="${2:?missing value for --base-url}"
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

case "${MODE}" in
  plugin|semver|local) ;;
  *) die "Unsupported mode: ${MODE}" ;;
esac

if [[ -z "${OUTPUT_ROOT}" ]]; then
  case "${MODE}" in
    local) OUTPUT_ROOT="${REPO_ROOT}/dist" ;;
    *) OUTPUT_ROOT="${REPO_ROOT}/dist/github" ;;
  esac
fi

OUTPUT_ROOT="$(realpath -m "${OUTPUT_ROOT}")"

make_temp_file() {
  mktemp "${TMPDIR:-/tmp}/validate-publish-output.XXXXXX"
}

target_record_for_slug() {
  local asset_slug="$1"
  local target target_asset_slug
  for target in "${TARGETS[@]}"; do
    IFS='|' read -r target_asset_slug _ <<<"${target}"
    if [[ "${target_asset_slug}" == "${asset_slug}" ]]; then
      printf '%s\n' "${target}"
      return 0
    fi
  done

  return 1
}

plugin_name_for_slug() {
  local asset_slug="$1"
  local target plugin_name
  target="$(target_record_for_slug "${asset_slug}")" || die "Unknown plugin slug: ${asset_slug}"
  IFS='|' read -r _ plugin_name _ <<<"${target}"
  printf '%s\n' "${plugin_name}"
}

release_asset_basenames() {
  local metadata_file="$1"
  jq -r '.releases[0].assets[] | split("/")[-1]' "${metadata_file}"
}

sorted_lines() {
  LC_ALL=C sort
}

assert_sorted_sets_equal() {
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

zip_contains_basename() {
  local zip_file="$1"
  local basename="$2"
  unzip -Z1 "${zip_file}" | grep -Eq "(^|/)${basename//./\\.}$"
}

assert_zip_contains_basename() {
  local zip_file="$1"
  local basename="$2"
  zip_contains_basename "${zip_file}" "${basename}" || die "${zip_file} is missing ${basename}"
}

assert_json_file() {
  local file="$1"
  [[ -f "${file}" ]] || die "Missing file: ${file}"
  jq -e '.' "${file}" >/dev/null || die "Invalid JSON file: ${file}"
}

extract_entry_to_file() {
  local pluginmaster_file="$1"
  local plugin_name="$2"
  local output_file="$3"

  extract_plugin_entry "${pluginmaster_file}" "${plugin_name}" > "${output_file}"
  jq -e '.' "${output_file}" >/dev/null || die "Could not extract entry for ${plugin_name} from ${pluginmaster_file}"
}

entry_version() {
  local entry_file="$1"
  jq -r '.AssemblyVersion // .Version // .TestingAssemblyVersion // empty' "${entry_file}"
}

assert_download_links() {
  local entry_file="$1"
  local expected_url="$2"

  jq -e --arg url "${expected_url}" '
    .DownloadLinkInstall == $url and
    .DownloadLinkUpdate == $url and
    .DownloadLinkTesting == $url
  ' "${entry_file}" >/dev/null || die "Download links do not match ${expected_url}"
}

assert_plugin_entry_matches_release() {
  local pluginmaster_file="$1"
  local asset_slug="$2"
  local expected_url="$3"
  local expected_version="$4"
  local entry_file

  entry_file="$(make_temp_file)"
  extract_entry_to_file "${pluginmaster_file}" "$(plugin_name_for_slug "${asset_slug}")" "${entry_file}"
  [[ "$(entry_version "${entry_file}")" == "${expected_version}" ]] || die "Unexpected version for ${asset_slug} in ${pluginmaster_file}"
  assert_download_links "${entry_file}" "${expected_url}"
  rm -f "${entry_file}"
}

validate_plugin_zip() {
  local asset_slug="$1"
  local zip_file="$2"
  local upstream_zip="$3"

  [[ -f "${zip_file}" ]] || die "Missing plugin artifact: ${zip_file}"
  [[ -f "${upstream_zip}" ]] || die "Missing upstream payload: ${upstream_zip}"

  assert_zip_contains_basename "${zip_file}" "${asset_slug}.dll"
  assert_zip_contains_basename "${zip_file}" "TinyIpc.dll"
  assert_zip_contains_basename "${zip_file}" "XivIpc.dll"

  if zip_contains_basename "${upstream_zip}" "${asset_slug}.json"; then
    assert_zip_contains_basename "${zip_file}" "${asset_slug}.json"
  fi
}

validate_plugin_mode() {
  local metadata_file="${OUTPUT_ROOT}/metadata/releases.json"
  local pluginmaster_file="${OUTPUT_ROOT}/pluginmaster.json"
  local assets_dir="${OUTPUT_ROOT}/assets"
  local work_dir="${OUTPUT_ROOT}/work"
  local release_count target plugin_name original_entry_file upstream_version expected_tag expected_asset artifact_zip upstream_zip expected_url
  local expected_file actual_file baseline_entry_file current_entry_file

  [[ -n "${PLUGIN_SLUG}" ]] || die "--plugin is required for plugin mode"
  assert_json_file "${metadata_file}"
  assert_json_file "${pluginmaster_file}"

  release_count="$(jq -r '.releases | length' "${metadata_file}")"
  plugin_name="$(plugin_name_for_slug "${PLUGIN_SLUG}")"

  if [[ "${release_count}" == "0" ]]; then
    find "${assets_dir}" -maxdepth 1 -type f | grep -q . && die "Plugin mode produced assets despite no release metadata"
    [[ -n "${BASELINE_PLUGINMASTER_FILE}" ]] || die "--baseline-pluginmaster-file is required to validate unchanged plugin runs"

    baseline_entry_file="$(make_temp_file)"
    current_entry_file="$(make_temp_file)"
    extract_entry_to_file "${BASELINE_PLUGINMASTER_FILE}" "${plugin_name}" "${baseline_entry_file}"
    extract_entry_to_file "${REPO_ROOT}/pluginmaster.json" "${plugin_name}" "${current_entry_file}"

    jq -S . "${baseline_entry_file}" > "${baseline_entry_file}.sorted"
    jq -S . "${current_entry_file}" > "${current_entry_file}.sorted"
    cmp -s "${baseline_entry_file}.sorted" "${current_entry_file}.sorted" || die "Plugin entry changed despite unchanged upstream version"
    rm -f "${baseline_entry_file}" "${current_entry_file}" "${baseline_entry_file}.sorted" "${current_entry_file}.sorted"
    log "Validated unchanged plugin flow for ${PLUGIN_SLUG}"
    return 0
  fi

  [[ "${release_count}" == "1" ]] || die "Plugin mode expected exactly one release item"

  original_entry_file="${work_dir}/${PLUGIN_SLUG}.original-entry.json"
  assert_json_file "${original_entry_file}"
  upstream_version="$(get_upstream_version "${original_entry_file}")"
  expected_tag="${PLUGIN_SLUG}-${upstream_version}"
  expected_asset="${PLUGIN_SLUG}-${upstream_version}.zip"

  [[ -n "${RELEASE_TAG}" ]] || RELEASE_TAG="${expected_tag}"
  [[ "${RELEASE_TAG}" == "${expected_tag}" ]] || die "Expected tag ${expected_tag}, got ${RELEASE_TAG}"

  jq -e --arg tag "${expected_tag}" --arg title "${plugin_name} ${upstream_version}" '
    .releases[0].tag == $tag and .releases[0].title == $title
  ' "${metadata_file}" >/dev/null || die "Plugin release metadata does not match expected tag/title"

  expected_file="$(make_temp_file)"
  actual_file="$(make_temp_file)"
  printf '%s\n' "${expected_asset}" | sorted_lines > "${expected_file}"
  release_asset_basenames "${metadata_file}" | sorted_lines > "${actual_file}"
  assert_sorted_sets_equal "plugin assets" "${expected_file}" "${actual_file}"
  rm -f "${expected_file}" "${actual_file}"

  artifact_zip="${assets_dir}/${expected_asset}"
  upstream_zip="${work_dir}/${PLUGIN_SLUG}.upstream.zip"
  validate_plugin_zip "${PLUGIN_SLUG}" "${artifact_zip}" "${upstream_zip}"

  [[ -n "${GITHUB_REPOSITORY_INPUT}" ]] || die "--github-repository is required for plugin mode"
  expected_url="$(github_release_asset_url "${GITHUB_REPOSITORY_INPUT}" "${expected_tag}" "${expected_asset}")"
  assert_plugin_entry_matches_release "${pluginmaster_file}" "${PLUGIN_SLUG}" "${expected_url}" "${upstream_version}"
  log "Validated changed plugin flow for ${PLUGIN_SLUG}"
}

validate_semver_mode() {
  local metadata_file="${OUTPUT_ROOT}/metadata/releases.json"
  local pluginmaster_file="${OUTPUT_ROOT}/pluginmaster.json"
  local assets_dir="${OUTPUT_ROOT}/assets"
  local work_dir="${OUTPUT_ROOT}/work"
  local expected_file actual_file
  local version target asset_slug plugin_name original_entry_file upstream_version artifact_zip upstream_zip expected_url
  local -a expected_assets=()

  [[ -n "${RELEASE_TAG}" ]] || die "--release-tag is required for semver mode"
  [[ -n "${GITHUB_REPOSITORY_INPUT}" ]] || die "--github-repository is required for semver mode"
  assert_json_file "${metadata_file}"
  assert_json_file "${pluginmaster_file}"

  jq -e --arg tag "${RELEASE_TAG}" '
    .releases | length == 1 and .[0].tag == $tag and .[0].title == $tag
  ' "${metadata_file}" >/dev/null || die "Semver metadata does not contain the expected release"

  while IFS= read -r target; do
    IFS='|' read -r asset_slug plugin_name _ <<<"${target}"
    expected_assets+=("${asset_slug}.zip")

    original_entry_file="${work_dir}/${asset_slug}.original-entry.json"
    assert_json_file "${original_entry_file}"
    upstream_version="$(get_upstream_version "${original_entry_file}")"
    artifact_zip="${assets_dir}/${asset_slug}.zip"
    upstream_zip="${work_dir}/${asset_slug}.upstream.zip"
    validate_plugin_zip "${asset_slug}" "${artifact_zip}" "${upstream_zip}"

    expected_url="$(github_release_asset_url "${GITHUB_REPOSITORY_INPUT}" "${RELEASE_TAG}" "${asset_slug}.zip")"
    assert_plugin_entry_matches_release "${pluginmaster_file}" "${asset_slug}" "${expected_url}" "${upstream_version}"
  done < <(selected_targets)

  expected_assets+=(
    "TinyIpc.Shim.${RELEASE_TAG}.nupkg"
    "XivIpc.${RELEASE_TAG}.nupkg"
    "XivIpc.NativeHost-linux-x64.tar.gz"
  )

  expected_file="$(make_temp_file)"
  actual_file="$(make_temp_file)"
  printf '%s\n' "${expected_assets[@]}" | sorted_lines > "${expected_file}"
  release_asset_basenames "${metadata_file}" | sorted_lines > "${actual_file}"
  assert_sorted_sets_equal "semver assets" "${expected_file}" "${actual_file}"
  rm -f "${expected_file}" "${actual_file}"

  tar -tzf "${assets_dir}/XivIpc.NativeHost-linux-x64.tar.gz" | grep -Eq '(^|/)XivIpc\.NativeHost$' || die "Native host archive is missing XivIpc.NativeHost"
  log "Validated semver publish output for ${RELEASE_TAG}"
}

validate_local_mode() {
  local artifacts_dir="${OUTPUT_ROOT}/artifacts"
  local manifests_dir="${OUTPUT_ROOT}/manifests"
  local pluginmaster_file="${manifests_dir}/pluginmaster.json"
  local work_dir="${OUTPUT_ROOT}/work"
  local staged_host_dir="${SHARED_DIR}/tinyipc-native-host"
  local expected_file actual_file
  local target asset_slug original_entry_file upstream_version artifact_zip expected_url
  local -a expected_assets=()

  [[ -n "${BASE_URL_INPUT}" ]] || die "--base-url is required for local mode"
  assert_json_file "${pluginmaster_file}"

  while IFS= read -r target; do
    IFS='|' read -r asset_slug _ _ <<<"${target}"
    expected_assets+=("${asset_slug}.zip")

    original_entry_file="${work_dir}/${asset_slug}.original-entry.json"
    assert_json_file "${original_entry_file}"
    upstream_version="$(get_upstream_version "${original_entry_file}")"
    artifact_zip="${artifacts_dir}/${asset_slug}.zip"

    if [[ -f "${work_dir}/${asset_slug}.upstream.zip" ]]; then
      validate_plugin_zip "${asset_slug}" "${artifact_zip}" "${work_dir}/${asset_slug}.upstream.zip"
    else
      assert_zip_contains_basename "${artifact_zip}" "${asset_slug}.dll"
      assert_zip_contains_basename "${artifact_zip}" "TinyIpc.dll"
      assert_zip_contains_basename "${artifact_zip}" "XivIpc.dll"
    fi

    expected_url="${BASE_URL_INPUT}/artifacts/${asset_slug}.zip"
    assert_plugin_entry_matches_release "${pluginmaster_file}" "${asset_slug}" "${expected_url}" "${upstream_version}"
  done < <(selected_targets)

  expected_file="$(make_temp_file)"
  actual_file="$(make_temp_file)"
  printf '%s\n' "${expected_assets[@]}" | sorted_lines > "${expected_file}"
  find "${artifacts_dir}" -maxdepth 1 -type f -printf '%f\n' | sorted_lines > "${actual_file}"
  assert_sorted_sets_equal "local artifacts" "${expected_file}" "${actual_file}"
  rm -f "${expected_file}" "${actual_file}"

  [[ -d "${staged_host_dir}" ]] || die "Missing staged native host directory: ${staged_host_dir}"
  [[ -f "${staged_host_dir}/XivIpc.NativeHost" ]] || die "Missing staged native host executable"
  log "Validated local publish output at ${OUTPUT_ROOT}"
}

case "${MODE}" in
  plugin)
    validate_plugin_mode
    ;;
  semver)
    validate_semver_mode
    ;;
  local)
    validate_local_mode
    ;;
esac
