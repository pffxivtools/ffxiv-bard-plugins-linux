#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

DIST_DIR="${REPO_ROOT}/dist"
SHIM_DIST_DIR="${DIST_DIR}/shim"
HOST_DIST_DIR="${DIST_DIR}/native-host"

OUTPUT_DIR="/home/shared/plugins"
WORK_DIR="${OUTPUT_DIR}/work"
ARTIFACTS_DIR="${OUTPUT_DIR}/artifacts"
MANIFESTS_DIR="${OUTPUT_DIR}/manifests"

: "${PUBLISH_TFM:=net9.0}"
: "${TINYIPC_ABI_FLAVOR_OVERRIDE:=}"
: "${TINYIPC_SHARED_DIR:=/home/shared/tinyipc-shared-ffxiv}"

BASE_URL="https://example.com/dalamud"

MASTEROFPUPPETS_PLUGINMASTER="https://raw.githubusercontent.com/zunetrix/DalamudPlugins/refs/heads/main/pluginmaster.json"
BARDTOOLBOX_PLUGINMASTER="https://raw.githubusercontent.com/reckhou/BardToolbox-Release/refs/heads/master/pluginmaster.json"
MIDIBARD2_PLUGINMASTER="https://raw.githubusercontent.com/reckhou/DalamudPlugins-Ori/api6/pluginmaster.json"

# format:
# "artifact_slug|plugin_name_in_manifest|pluginmaster_url"
TARGETS=(
  "MasterOfPuppets|MasterOfPuppets|${MASTEROFPUPPETS_PLUGINMASTER}"
  "BardToolbox|BardToolbox|${BARDTOOLBOX_PLUGINMASTER}"
  "MidiBard2|MidiBard 2|${MIDIBARD2_PLUGINMASTER}"
)

log() {
  printf '[INFO] %s\n' "$*"
}

warn() {
  printf '[WARN] %s\n' "$*" >&2
}

die() {
  printf '[ERROR] %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Missing required command: $1"
}

ensure_prereqs() {
  require_cmd bash
  require_cmd curl
  require_cmd jq
  require_cmd zip
  require_cmd find
  require_cmd python3
  require_cmd dotnet

  [[ -f "${SHIM_DIST_DIR}/TinyIpc.dll" ]] || die "Missing ${SHIM_DIST_DIR}/TinyIpc.dll"
  [[ -f "${SHIM_DIST_DIR}/XivIpc.dll" ]] || die "Missing ${SHIM_DIST_DIR}/XivIpc.dll"

  [[ -f "${HOST_DIST_DIR}/XivIpc.NativeHost" ]] || die "Missing ${HOST_DIST_DIR}/XivIpc.NativeHost"
}

clean_dist() {
  rm -rf "${DIST_DIR}"
  mkdir -p "${SHIM_DIST_DIR}" "${HOST_DIST_DIR}"
}

resolve_abi_flavor() {
  local plugin_name="$1"

  if [[ -n "${TINYIPC_ABI_FLAVOR_OVERRIDE}" ]]; then
    printf '%s\n' "${TINYIPC_ABI_FLAVOR_OVERRIDE}"
    return
  fi

  case "${plugin_name}" in
    BardToolbox) printf '4x\n' ;;
    "MidiBard 2"|MidiBard2|MasterOfPuppets) printf '3x\n' ;;
    *) printf '5x\n' ;;
  esac
}

publish_shim_flavor() {
  local abi_flavor="$1"

  clean_dist

  dotnet publish -f "${PUBLISH_TFM}" TinyIpc.Shim \
    -p:TinyIpcAbiFlavor="${abi_flavor}" \
    -o "${SHIM_DIST_DIR}"

  dotnet publish -f net10.0 -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true \
    XivIpc.NativeHost \
    -o "${HOST_DIST_DIR}"

  ensure_prereqs
  stage_native_host_payload
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

install_runtime_files() {
  local plugin_root="$1"

  while IFS= read -r -d '' artifact; do
    local filename
    local replaced_any=0
    filename="$(basename "$artifact")"

    while IFS= read -r -d '' target; do
      cp -f "${artifact}" "$target"
      replaced_any=1
    done < <(find "$plugin_root" -type f -name "$filename" -print0)

    if [[ "$replaced_any" -eq 0 ]]; then
      cp -f "${artifact}" "${plugin_root}/${filename}"
    fi
  done < <(find "${SHIM_DIST_DIR}" -maxdepth 1 -type f \
    \( -name '*.dll' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \) -print0)
}

reset_dirs() {
  rm -rf "${OUTPUT_DIR}"
  mkdir -p "${WORK_DIR}" "${ARTIFACTS_DIR}" "${MANIFESTS_DIR}"
}

download_file() {
  local url="$1"
  local out="$2"
  curl -fL --retry 3 --retry-delay 1 -o "$out" "$url"
}

extract_plugin_entry() {
  local manifest_file="$1"
  local plugin_name="$2"

  jq --arg plugin_name "$plugin_name" '
    if type == "array" then
      map(select(
        (.Name? == $plugin_name) or
        (.InternalName? == $plugin_name)
      )) | .[0]
    else
      empty
    end
  ' "$manifest_file"
}

get_download_link_install() {
  local manifest_file="$1"
  local plugin_name="$2"

  jq -r --arg plugin_name "$plugin_name" '
    if type == "array" then
      map(select(
        (.Name? == $plugin_name) or
        (.InternalName? == $plugin_name)
      )) | .[0].DownloadLinkInstall // empty
    else
      empty
    end
  ' "$manifest_file"
}

# Extract zip while normalizing Windows '\' path separators to '/'
extract_zip_normalized() {
  local zip_file="$1"
  local extract_dir="$2"

  python3 - "$zip_file" "$extract_dir" <<'PY'
import os
import sys
import zipfile
import shutil

zip_path = sys.argv[1]
out_dir = sys.argv[2]

with zipfile.ZipFile(zip_path) as zf:
    for info in zf.infolist():
        name = info.filename.replace("\\", "/").lstrip("/")
        if not name:
            continue

        target = os.path.join(out_dir, name)

        if info.is_dir() or name.endswith("/"):
            os.makedirs(target, exist_ok=True)
            continue

        os.makedirs(os.path.dirname(target), exist_ok=True)
        with zf.open(info) as src, open(target, "wb") as dst:
            shutil.copyfileobj(src, dst)
PY
}

resolve_extracted_root() {
  local extract_dir="$1"

  local top_entries
  mapfile -t top_entries < <(find "$extract_dir" -mindepth 1 -maxdepth 1 -printf '%P\n')

  if [[ "${#top_entries[@]}" -eq 1 && -d "${extract_dir}/${top_entries[0]}" ]]; then
    printf '%s\n' "${extract_dir}/${top_entries[0]}"
  else
    printf '%s\n' "${extract_dir}"
  fi
}

install_tinyipc_files() {
  local plugin_root="$1"
  install_runtime_files "$plugin_root"
}

create_plugin_zip() {
  local plugin_root="$1"
  local output_zip="$2"

  rm -f "$output_zip"
  (
    cd "$plugin_root"
    zip -qr "$output_zip" .
  )
}

make_placeholder_url() {
  local filename="$1"
  printf '%s/artifacts/%s\n' "$BASE_URL" "$filename"
}

build_republished_entry() {
  local original_entry_file="$1"
  local placeholder_zip_url="$2"

  jq \
    --arg dl "$placeholder_zip_url" \
    '
    .DownloadLinkInstall = $dl
    | .DownloadLinkUpdate = $dl
    | .DownloadLinkTesting = $dl
    | .IsHide = false
    ' \
    "$original_entry_file"
}

main() {
  dotnet build
  reset_dirs

  local combined_entries_file="${MANIFESTS_DIR}/entries.jsonl"
  : > "$combined_entries_file"

  local target
  for target in "${TARGETS[@]}"; do
    IFS='|' read -r artifact_slug plugin_name pluginmaster_url <<<"$target"

    local abi_flavor
    abi_flavor="$(resolve_abi_flavor "${plugin_name}")"
    log "Publishing TinyIpc ABI flavor ${abi_flavor} for ${plugin_name}"
    publish_shim_flavor "${abi_flavor}"

    log "Processing ${plugin_name}"
    log "Fetching pluginmaster: ${pluginmaster_url}"

    local manifest_file="${WORK_DIR}/${artifact_slug}.pluginmaster.json"
    download_file "$pluginmaster_url" "$manifest_file"

    local download_url
    download_url="$(get_download_link_install "$manifest_file" "$plugin_name")"
    [[ -n "$download_url" && "$download_url" != "null" ]] || die "Could not find DownloadLinkInstall for ${plugin_name}"

    log "DownloadLinkInstall: ${download_url}"

    local entry_json
    entry_json="$(extract_plugin_entry "$manifest_file" "$plugin_name")"
    [[ -n "$entry_json" && "$entry_json" != "null" ]] || die "Could not extract manifest entry for ${plugin_name}"

    local original_entry_file="${WORK_DIR}/${artifact_slug}.original-entry.json"
    printf '%s\n' "$entry_json" > "$original_entry_file"

    local upstream_zip="${WORK_DIR}/${artifact_slug}.upstream.zip"
    download_file "$download_url" "$upstream_zip"

    local extract_dir="${WORK_DIR}/${artifact_slug}.extract"
    rm -rf "$extract_dir"
    mkdir -p "$extract_dir"
    extract_zip_normalized "$upstream_zip" "$extract_dir"

    local plugin_root
    plugin_root="$(resolve_extracted_root "$extract_dir")"
    log "Plugin root resolved to: ${plugin_root}"

    install_tinyipc_files "$plugin_root"

    local artifact_filename="${artifact_slug}.zip"
    local artifact_path="${ARTIFACTS_DIR}/${artifact_filename}"
    create_plugin_zip "$plugin_root" "$artifact_path"

    local placeholder_url
    placeholder_url="$(make_placeholder_url "$artifact_filename")"

    local republished_entry_file="${WORK_DIR}/${artifact_slug}.republished-entry.json"
    build_republished_entry "$original_entry_file" "$placeholder_url" > "$republished_entry_file"

    cat "$republished_entry_file" >> "$combined_entries_file"
    printf '\n' >> "$combined_entries_file"

    log "Created artifact: ${artifact_path}"
    log "Placeholder URL: ${placeholder_url}"
  done

  jq -s '.' "$combined_entries_file" > "${MANIFESTS_DIR}/pluginmaster.json"

  log "Generated manifest: ${MANIFESTS_DIR}/pluginmaster.json"
  log "Artifacts directory: ${ARTIFACTS_DIR}"
  log "Native host staged at: ${TINYIPC_SHARED_DIR}/tinyipc-native-host"
  log "Done"
}

main "$@"
