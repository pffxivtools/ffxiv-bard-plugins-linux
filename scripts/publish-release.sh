#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

source "${SCRIPT_DIR}/release-common.sh"

: "${PUBLISH_SCHEME:?PUBLISH_SCHEME must be set to semver or plugins}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"

DIST_DIR="${REPO_ROOT}/dist/github"
WORK_DIR="${DIST_DIR}/work"
ASSETS_DIR="${DIST_DIR}/assets"
METADATA_DIR="${DIST_DIR}/metadata"
RELEASE_ITEMS_DIR="${METADATA_DIR}/release-items"
PLUGINMASTER_FILE="${DIST_DIR}/pluginmaster.json"
ROOT_PLUGINMASTER_FILE="${REPO_ROOT}/pluginmaster.json"
SHIM_WORK_ROOT="${WORK_DIR}"
NATIVE_HOST_DIR="${WORK_DIR}/native-host"

reset_release_dirs() {
  rm -rf "${DIST_DIR}"
  mkdir -p "${WORK_DIR}" "${ASSETS_DIR}" "${METADATA_DIR}" "${RELEASE_ITEMS_DIR}"
  printf '[]\n' > "${PLUGINMASTER_FILE}"
}

release_notes_for_plugin() {
  local plugin_name="$1" upstream_version="$2"
  cat <<TXT
Republished ${plugin_name} with TinyIpc.Shim and XivIpc runtime files.

Upstream version: ${upstream_version:-unknown}
TXT
}

semver_release_notes() {
  local version="$1"
  cat <<TXT
Release ${version}

Includes TinyIpc.Shim and XivIpc packages, native host payload, and republished plugin artifacts.
TXT
}

build_plugin_tracking_release_item() {
  local asset_slug="$1" plugin_name="$2" version="$3" asset_path="$4" output_file="$5"
  local tag="${asset_slug}-${version}"
  local title="${plugin_name} ${version}"
  local notes
  notes="$(release_notes_for_plugin "$plugin_name" "$version")"
  jq -n \
    --arg tag "$tag" \
    --arg title "$title" \
    --arg notes "$notes" \
    --arg asset "$asset_path" \
    '{tag:$tag,title:$title,notes:$notes,assets:[$asset]}' > "$output_file"
}

build_semver_release_item() {
  local version="$1" output_file="$2"
  jq -n \
    --arg tag "$version" \
    --arg title "$version" \
    --arg notes "$(semver_release_notes "$version")" \
    --argjson assets "$(find "$ASSETS_DIR" -maxdepth 1 -type f -printf '%p\n' | jq -R . | jq -s .)" \
    '{tag:$tag,title:$title,notes:$notes,assets:$assets}' > "$output_file"
}

main() {
  ensure_common_prereqs
  reset_release_dirs

  local target
  while IFS= read -r target; do
    IFS='|' read -r asset_slug plugin_name source_kind source_value default_abi_flavor <<<"${target}"
    log "Processing ${plugin_name}"

    local manifest_file="${WORK_DIR}/${asset_slug}.pluginmaster.json"
    if [[ "${source_kind}" == "local-bardtoolbox" ]]; then
      source_kind="github-url-pluginmaster"
      source_value="https://raw.githubusercontent.com/${BARDTOOLBOX_PRIVATE_REPO}/${BARDTOOLBOX_PRIVATE_REF}/pluginmaster.json"
    fi
    fetch_manifest_for_target "$source_kind" "$source_value" "$manifest_file"

    local original_entry_file="${WORK_DIR}/${asset_slug}.original-entry.json"
    extract_plugin_entry "$manifest_file" "$plugin_name" > "$original_entry_file"
    jq -e '.' "$original_entry_file" >/dev/null 2>&1 || die "Could not extract manifest entry for ${plugin_name}"

    local download_url upstream_version abi_flavor upstream_zip extract_dir plugin_root shim_dir artifact_filename artifact_path republished_entry_file placeholder_url release_item_file
    download_url="$(jq -r '.DownloadLinkInstall // empty' "$original_entry_file")"
    [[ -n "$download_url" && "$download_url" != "null" ]] || die "Could not find DownloadLinkInstall for ${plugin_name}"
    upstream_version="$(get_upstream_version "$original_entry_file")"
    abi_flavor="$(resolve_abi_flavor "$plugin_name" "$default_abi_flavor")"
    shim_dir="${SHIM_WORK_ROOT}/${asset_slug}.shim"
    publish_shim_flavor "$abi_flavor" "$shim_dir"

    upstream_zip="${WORK_DIR}/${asset_slug}.upstream.zip"
    download_plugin_payload_for_release "$source_kind" "$source_value" "$download_url" "$upstream_zip"
    extract_dir="${WORK_DIR}/${asset_slug}.extract"
    plugin_root="$(prepare_plugin_tree_from_zip "$upstream_zip" "$extract_dir")"
    install_runtime_files "$plugin_root" "$shim_dir"

    artifact_filename="${asset_slug}.zip"
    if [[ "$PUBLISH_SCHEME" == "plugins" ]]; then
      artifact_filename="${asset_slug}-${upstream_version:-unknown}.zip"
    fi
    artifact_path="${ASSETS_DIR}/${artifact_filename}"
    create_plugin_zip "$plugin_root" "$artifact_path"

    local target_tag
    if [[ "$PUBLISH_SCHEME" == "plugins" ]]; then
      target_tag="${asset_slug}-${upstream_version:-unknown}"
    else
      target_tag="${RELEASE_VERSION:?RELEASE_VERSION must be set for semver publishing}"
    fi
    placeholder_url="$(github_release_asset_url "$GITHUB_REPOSITORY" "$target_tag" "$artifact_filename")"
    republished_entry_file="${WORK_DIR}/${asset_slug}.republished-entry.json"
    build_republished_entry "$original_entry_file" "$placeholder_url" "$republished_entry_file"
    merge_plugin_entry "$PLUGINMASTER_FILE" "$republished_entry_file"

    if [[ "$PUBLISH_SCHEME" == "plugins" ]]; then
      release_item_file="${RELEASE_ITEMS_DIR}/${asset_slug}.json"
      build_plugin_tracking_release_item "$asset_slug" "$plugin_name" "$upstream_version" "$artifact_path" "$release_item_file"
    fi
  done < <(selected_targets)

  if [[ "$PUBLISH_SCHEME" == "semver" ]]; then
    local version="${RELEASE_VERSION:?RELEASE_VERSION must be set for semver publishing}"
    pack_nuget_project TinyIpc.Shim/TinyIpc.Shim.csproj "$ASSETS_DIR" "$version" "https://github.com/${GITHUB_REPOSITORY}"
    pack_nuget_project XivIpc/XivIpc.csproj "$ASSETS_DIR" "$version" "https://github.com/${GITHUB_REPOSITORY}"
    publish_native_host "$NATIVE_HOST_DIR"
    archive_native_host "$NATIVE_HOST_DIR" "$ASSETS_DIR/XivIpc.NativeHost-linux-x64.tar.gz"
    build_semver_release_item "$version" "${RELEASE_ITEMS_DIR}/semver.json"
  fi

  sort_pluginmaster_file "$PLUGINMASTER_FILE"
  cp -f "$PLUGINMASTER_FILE" "$ROOT_PLUGINMASTER_FILE"
  jq -s '{releases: .}' ${RELEASE_ITEMS_DIR}/*.json > "${METADATA_DIR}/releases.json"
  log "Release metadata written to ${METADATA_DIR}/releases.json"
  log "Pluginmaster written to ${ROOT_PLUGINMASTER_FILE}"
}

main "$@"
