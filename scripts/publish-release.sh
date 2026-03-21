#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

# shellcheck source=./release-common.sh
source "${SCRIPT_DIR}/release-common.sh"

: "${PUBLISH_SCHEME:?PUBLISH_SCHEME must be set to semver or plugins}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"
: "${TOOL_SELECTION:=all}"

DIST_ROOT="${REPO_ROOT}/dist/github"
ASSETS_DIR="${DIST_ROOT}/assets"
WORK_DIR="${DIST_ROOT}/work"
METADATA_DIR="${DIST_ROOT}/metadata"
PLUGINMASTER_OUTPUT="${REPO_ROOT}/pluginmaster.json"
DIST_PLUGINMASTER_COPY="${DIST_ROOT}/pluginmaster.json"

reset_output_dirs() {
  rm -rf "${DIST_ROOT}"
  mkdir -p "${ASSETS_DIR}" "${WORK_DIR}" "${METADATA_DIR}"
}

ensure_release_prereqs() {
  ensure_common_prereqs
  require_cmd mktemp
}

semver_release_notes() {
  local version="$1"
  local upstream_summary_file="$2"

  jq -r --arg version "${version}" '
    [
      "Semver release " + $version,
      "",
      "Republished plugin inputs:",
      (.[] | "- " + .pluginName + " " + .upstreamVersion)
    ] | join("\n")
  ' "${upstream_summary_file}"
}

plugin_release_notes() {
  local plugin_name="$1"
  local upstream_version="$2"
  local manifest_url="$3"

  printf 'Republished %s %s with TinyIpc shim replacement.\n\nUpstream manifest:\n%s\n' \
    "${plugin_name}" "${upstream_version}" "${manifest_url}"
}

emit_metadata() {
  local release_items_dir="$1"
  local output_file="$2"

  jq -n \
    --arg pluginmasterPath "${DIST_PLUGINMASTER_COPY}" \
    --slurpfile releases <(jq -s '.' "${release_items_dir}"/*.json) \
    '{
      pluginmasterPath: $pluginmasterPath,
      releases: $releases[0]
    }' > "${output_file}"
}

pack_semver_artifacts() {
  local release_version="$1"
  local repository_url="https://github.com/${GITHUB_REPOSITORY}"
  local release_items_dir="${METADATA_DIR}/release-items"
  local semver_tag="${release_version}"
  local semver_release_file="${release_items_dir}/semver.json"
  local upstream_summary_file="${WORK_DIR}/semver.upstream-summary.json"
  local host_publish_dir="${WORK_DIR}/native-host"

  mkdir -p "${release_items_dir}"
  printf '[]\n' > "${PLUGINMASTER_OUTPUT}"
  printf '[]\n' > "${upstream_summary_file}"

  pack_nuget_project "XivIpc/XivIpc.csproj" "${ASSETS_DIR}" "${release_version}" "${repository_url}"
  pack_nuget_project "TinyIpc.Shim/TinyIpc.Shim.csproj" "${ASSETS_DIR}" "${release_version}" "${repository_url}"

  publish_native_host "${host_publish_dir}"
  local native_host_asset="${ASSETS_DIR}/XivIpc.NativeHost-linux-x64-${release_version}.tar.gz"
  archive_native_host "${host_publish_dir}" "${native_host_asset}"

  local target
  while IFS= read -r target; do
    IFS='|' read -r asset_slug plugin_name pluginmaster_url default_abi_flavor <<<"${target}"

    local abi_flavor
    abi_flavor="$(resolve_abi_flavor "${plugin_name}" "${default_abi_flavor}")"

    local shim_dir="${WORK_DIR}/${asset_slug}.shim"
    publish_shim_flavor "${abi_flavor}" "${shim_dir}"

    local manifest_file="${WORK_DIR}/${asset_slug}.pluginmaster.json"
    local original_entry_file="${WORK_DIR}/${asset_slug}.original-entry.json"
    local upstream_zip="${WORK_DIR}/${asset_slug}.upstream.zip"
    local extract_dir="${WORK_DIR}/${asset_slug}.extract"
    local republished_entry="${WORK_DIR}/${asset_slug}.republished-entry.json"
    local release_url

    log "Fetching upstream manifest: ${pluginmaster_url}"
    download_file "${pluginmaster_url}" "${manifest_file}"
    extract_plugin_entry "${manifest_file}" "${plugin_name}" > "${original_entry_file}"
    jq -e '.' "${original_entry_file}" >/dev/null 2>&1 || die "Could not find manifest entry for ${plugin_name}"

    local upstream_version
    upstream_version="$(get_upstream_version "${original_entry_file}")"
    [[ -n "${upstream_version}" ]] || die "Could not determine upstream version for ${plugin_name}"

    local asset_name="${asset_slug}-${upstream_version}.zip"
    local asset_path="${ASSETS_DIR}/${asset_name}"

    local download_url
    download_url="$(get_download_link_install "${manifest_file}" "${plugin_name}")"
    [[ -n "${download_url}" && "${download_url}" != "null" ]] || die "Missing DownloadLinkInstall for ${plugin_name}"

    download_file "${download_url}" "${upstream_zip}"
    rm -rf "${extract_dir}"
    mkdir -p "${extract_dir}"
    extract_zip_normalized "${upstream_zip}" "${extract_dir}"

    local plugin_root
    plugin_root="$(resolve_extracted_root "${extract_dir}")"
    install_runtime_files "${plugin_root}" "${shim_dir}"
    create_plugin_zip "${plugin_root}" "${asset_path}"

    release_url="$(github_release_asset_url "${GITHUB_REPOSITORY}" "${semver_tag}" "${asset_name}")"
    build_republished_entry "${original_entry_file}" "${release_url}" "${republished_entry}"
    merge_plugin_entry "${PLUGINMASTER_OUTPUT}" "${republished_entry}"

    jq --arg pluginName "${plugin_name}" --arg upstreamVersion "${upstream_version}" \
      '. += [{"pluginName": $pluginName, "upstreamVersion": $upstreamVersion}]' \
      "${upstream_summary_file}" > "${upstream_summary_file}.tmp"
    mv "${upstream_summary_file}.tmp" "${upstream_summary_file}"
  done < <(selected_targets)

  sort_pluginmaster_file "${PLUGINMASTER_OUTPUT}"
  cp "${PLUGINMASTER_OUTPUT}" "${DIST_PLUGINMASTER_COPY}"

  local notes_file="${WORK_DIR}/semver.notes.md"
  semver_release_notes "${release_version}" "${upstream_summary_file}" > "${notes_file}"

  jq -n \
    --arg tag "${semver_tag}" \
    --arg title "Release ${release_version}" \
    --rawfile notes "${notes_file}" \
    --arg nativeHost "${native_host_asset}" \
    --slurpfile assets <(find "${ASSETS_DIR}" -maxdepth 1 -type f | sort | jq -R . | jq -s .) \
    '{
      tag: $tag,
      title: $title,
      notes: $notes,
      assets: $assets[0]
    }' > "${semver_release_file}"
}

pack_plugin_release_artifacts() {
  local release_items_dir="${METADATA_DIR}/release-items"

  mkdir -p "${release_items_dir}"
  ensure_pluginmaster_file "${PLUGINMASTER_OUTPUT}"

  local target
  while IFS= read -r target; do
    IFS='|' read -r asset_slug plugin_name pluginmaster_url default_abi_flavor <<<"${target}"

    local abi_flavor
    abi_flavor="$(resolve_abi_flavor "${plugin_name}" "${default_abi_flavor}")"
    local shim_dir="${WORK_DIR}/${asset_slug}.shim"
    publish_shim_flavor "${abi_flavor}" "${shim_dir}"

    local entry_file="${WORK_DIR}/${asset_slug}.entry.json"
    fetch_upstream_entry "${pluginmaster_url}" "${plugin_name}" > "${entry_file}"
    jq -e '.' "${entry_file}" >/dev/null 2>&1 || die "Could not find plugin entry for ${plugin_name}"

    local upstream_version
    upstream_version="$(get_upstream_version "${entry_file}")"
    [[ -n "${upstream_version}" ]] || die "Could not determine upstream version for ${plugin_name}"

    local upstream_zip_url
    upstream_zip_url="$(jq -r '.DownloadLinkInstall // empty' "${entry_file}")"
    [[ -n "${upstream_zip_url}" ]] || die "Missing DownloadLinkInstall for ${plugin_name}"

    local upstream_zip="${WORK_DIR}/${asset_slug}.upstream.zip"
    local extract_dir="${WORK_DIR}/${asset_slug}.extract"
    local asset_name="${asset_slug}-${upstream_version}.zip"
    local asset_path="${ASSETS_DIR}/${asset_name}"
    local tag="${asset_slug}-${upstream_version}"
    local notes_file="${WORK_DIR}/${asset_slug}.notes.md"
    local republished_entry="${WORK_DIR}/${asset_slug}.republished-entry.json"
    local release_url

    download_file "${upstream_zip_url}" "${upstream_zip}"
    rm -rf "${extract_dir}"
    mkdir -p "${extract_dir}"
    extract_zip_normalized "${upstream_zip}" "${extract_dir}"

    local plugin_root
    plugin_root="$(resolve_extracted_root "${extract_dir}")"
    install_runtime_files "${plugin_root}" "${shim_dir}"
    create_plugin_zip "${plugin_root}" "${asset_path}"

    release_url="$(github_release_asset_url "${GITHUB_REPOSITORY}" "${tag}" "${asset_name}")"
    build_republished_entry "${entry_file}" "${release_url}" "${republished_entry}"
    merge_plugin_entry "${PLUGINMASTER_OUTPUT}" "${republished_entry}"

    plugin_release_notes "${plugin_name}" "${upstream_version}" "${pluginmaster_url}" > "${notes_file}"

    jq -n \
      --arg tag "${tag}" \
      --arg title "${plugin_name} ${upstream_version}" \
      --rawfile notes "${notes_file}" \
      --arg assetPath "${asset_path}" \
      '{
        tag: $tag,
        title: $title,
        notes: $notes,
        assets: [$assetPath]
      }' > "${release_items_dir}/${asset_slug}.json"
  done < <(selected_targets)

  sort_pluginmaster_file "${PLUGINMASTER_OUTPUT}"
  cp "${PLUGINMASTER_OUTPUT}" "${DIST_PLUGINMASTER_COPY}"
}

main() {
  ensure_release_prereqs
  reset_output_dirs

  case "${PUBLISH_SCHEME}" in
    semver)
      : "${RELEASE_VERSION:?RELEASE_VERSION must be set for semver publishing}"
      pack_semver_artifacts "${RELEASE_VERSION}"
      ;;
    plugins)
      pack_plugin_release_artifacts
      ;;
    *)
      die "Unsupported PUBLISH_SCHEME: ${PUBLISH_SCHEME}"
      ;;
  esac

  emit_metadata "${METADATA_DIR}/release-items" "${METADATA_DIR}/releases.json"
  log "Release metadata written to ${METADATA_DIR}/releases.json"
  log "Pluginmaster written to ${PLUGINMASTER_OUTPUT}"
}

main "$@"