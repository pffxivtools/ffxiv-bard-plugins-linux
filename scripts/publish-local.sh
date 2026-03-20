#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

# shellcheck source=./release-common.sh
source "${SCRIPT_DIR}/release-common.sh"

DIST_DIR="${REPO_ROOT}/dist"
SHIM_DIST_DIR="${DIST_DIR}/shim"
HOST_DIST_DIR="${DIST_DIR}/native-host"

OUTPUT_DIR="/home/shared/plugins"
WORK_DIR="${OUTPUT_DIR}/work"
ARTIFACTS_DIR="${OUTPUT_DIR}/artifacts"
MANIFESTS_DIR="${OUTPUT_DIR}/manifests"

: "${TINYIPC_SHARED_DIR:=/home/shared/tinyipc-shared-ffxiv}"
: "${BASE_URL:=https://example.com/dalamud}"

ensure_local_prereqs() {
  ensure_common_prereqs
  require_cmd install
  require_cmd getent
  require_cmd chgrp
  require_cmd chmod
}

ensure_shared_root_permissions() {
  local shared_root="${TINYIPC_SHARED_DIR}"
  local shared_group="${TINYIPC_SHARED_GROUP:-steam}"

  install -d -- "${shared_root}"

  if getent group "${shared_group}" >/dev/null 2>&1; then
    chgrp -- "${shared_group}" "${shared_root}"
    chmod 2770 -- "${shared_root}"
  else
    chmod 1777 -- "${shared_root}"
  fi
}

stage_native_host_payload() {
  local staging_dir="${TINYIPC_SHARED_DIR}/tinyipc-native-host"
  local shared_group="${TINYIPC_SHARED_GROUP:-steam}"

  ensure_shared_root_permissions

  rm -rf -- "${staging_dir}"
  install -d -m 2770 -- "${staging_dir}"

  if getent group "${shared_group}" >/dev/null 2>&1; then
    chgrp -- "${shared_group}" "${staging_dir}"
    chmod 2770 -- "${staging_dir}"
  fi

  while IFS= read -r -d '' artifact; do
    local dest="${staging_dir}/$(basename "${artifact}")"
    install -m 660 -- "${artifact}" "${dest}"

    if [[ -x "${artifact}" ]]; then
      chmod 770 -- "${dest}"
    fi

    if getent group "${shared_group}" >/dev/null 2>&1; then
      chgrp -- "${shared_group}" "${dest}"
    fi
  done < <(find "${HOST_DIST_DIR}" -maxdepth 1 -type f -print0)

  log "Staged native host payload: ${staging_dir}"
}

reset_dirs() {
  rm -rf "${OUTPUT_DIR}"
  mkdir -p "${WORK_DIR}" "${ARTIFACTS_DIR}" "${MANIFESTS_DIR}"
}

make_placeholder_url() {
  local filename="$1"
  printf '%s/artifacts/%s\n' "${BASE_URL}" "${filename}"
}

main() {
  ensure_local_prereqs
  dotnet build TinyIpc.Shim.sln -v minimal
  reset_dirs
  printf '[]\n' > "${MANIFESTS_DIR}/pluginmaster.json"

  local target
  while IFS= read -r target; do
    IFS='|' read -r asset_slug plugin_name pluginmaster_url default_abi_flavor <<<"${target}"

    local abi_flavor
    abi_flavor="$(resolve_abi_flavor "${plugin_name}" "${default_abi_flavor}")"
    log "Publishing TinyIpc ABI flavor ${abi_flavor} for ${plugin_name}"
    publish_shim_flavor "${abi_flavor}" "${SHIM_DIST_DIR}"
    publish_native_host "${HOST_DIST_DIR}"
    stage_native_host_payload

    log "Processing ${plugin_name}"
    log "Fetching pluginmaster: ${pluginmaster_url}"

    local manifest_file="${WORK_DIR}/${asset_slug}.pluginmaster.json"
    download_file "${pluginmaster_url}" "${manifest_file}"

    local download_url
    download_url="$(get_download_link_install "${manifest_file}" "${plugin_name}")"
    [[ -n "${download_url}" && "${download_url}" != "null" ]] || die "Could not find DownloadLinkInstall for ${plugin_name}"

    local original_entry_file="${WORK_DIR}/${asset_slug}.original-entry.json"
    extract_plugin_entry "${manifest_file}" "${plugin_name}" > "${original_entry_file}"
    jq -e '.' "${original_entry_file}" >/dev/null 2>&1 || die "Could not extract manifest entry for ${plugin_name}"

    local upstream_zip="${WORK_DIR}/${asset_slug}.upstream.zip"
    download_file "${download_url}" "${upstream_zip}"

    local extract_dir="${WORK_DIR}/${asset_slug}.extract"
    rm -rf "${extract_dir}"
    mkdir -p "${extract_dir}"
    extract_zip_normalized "${upstream_zip}" "${extract_dir}"

    local plugin_root
    plugin_root="$(resolve_extracted_root "${extract_dir}")"
    log "Plugin root resolved to: ${plugin_root}"

    install_runtime_files "${plugin_root}" "${SHIM_DIST_DIR}"

    local artifact_filename="${asset_slug}.zip"
    local artifact_path="${ARTIFACTS_DIR}/${artifact_filename}"
    create_plugin_zip "${plugin_root}" "${artifact_path}"

    local placeholder_url
    placeholder_url="$(make_placeholder_url "${artifact_filename}")"

    local republished_entry_file="${WORK_DIR}/${asset_slug}.republished-entry.json"
    build_republished_entry "${original_entry_file}" "${placeholder_url}" "${republished_entry_file}"
    merge_plugin_entry "${MANIFESTS_DIR}/pluginmaster.json" "${republished_entry_file}"

    log "Created artifact: ${artifact_path}"
    log "Placeholder URL: ${placeholder_url}"
  done < <(selected_targets)

  sort_pluginmaster_file "${MANIFESTS_DIR}/pluginmaster.json"

  log "Generated manifest: ${MANIFESTS_DIR}/pluginmaster.json"
  log "Artifacts directory: ${ARTIFACTS_DIR}"
  log "Native host staged at: ${TINYIPC_SHARED_DIR}/tinyipc-native-host"
  log "Done"
}

main "$@"
