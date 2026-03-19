#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

PLUGINMASTER_OUT="${REPO_ROOT}/pluginmaster.json"
DIST_DIR="${REPO_ROOT}/dist"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

: "${TOOL_SELECTION:=all}"
: "${FORCE_REPUBLISH:=false}"
: "${PUBLISH_TFM:=net9.0}"
: "${TINYIPC_ABI_FLAVOR_OVERRIDE:=}"
: "${TINYIPC_SHARED_DIR:=/tmp/tinyipc-shared-ffxiv}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"
: "${GITHUB_TOKEN:?GITHUB_TOKEN must be set}"

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
  require_cmd curl
  require_cmd jq
  require_cmd zip
  require_cmd python3
  require_cmd dotnet
  require_cmd gh
}

clean_dist() {
  rm -rf "${DIST_DIR}"
  mkdir -p "${DIST_DIR}"
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

validate_published_runtime() {
  [[ -f "${DIST_DIR}/TinyIpc.dll" ]] || die "Missing ${DIST_DIR}/TinyIpc.dll after publish"
  [[ -f "${DIST_DIR}/TinyIpc.Win.dll" ]] || die "Missing ${DIST_DIR}/TinyIpc.Win.dll after publish"
  [[ -f "${DIST_DIR}/XivIpc.dll" ]] || die "Missing ${DIST_DIR}/XivIpc.dll after publish"
  [[ -f "${DIST_DIR}/XivIpc.Wine.dll" ]] || die "Missing ${DIST_DIR}/XivIpc.Wine.dll after publish"
  [[ -f "${DIST_DIR}/XivIpc.NativeHost" ]] || die "Missing ${DIST_DIR}/XivIpc.NativeHost after publish"
}

# format:
# slug|manifest_name|pluginmaster_url
TARGETS=(
  "bardtoolbox|BardToolbox|https://raw.githubusercontent.com/reckhou/BardToolbox-Release/refs/heads/master/pluginmaster.json"
  "midibard2|MidiBard 2|https://raw.githubusercontent.com/reckhou/DalamudPlugins-Ori/api6/pluginmaster.json"
  "masterofpuppets|MasterOfPuppets|https://raw.githubusercontent.com/zunetrix/DalamudPlugins/refs/heads/main/pluginmaster.json"
)

selected_targets() {
  case "${TOOL_SELECTION}" in
    all)
      printf '%s\n' "${TARGETS[@]}"
      ;;
    BardToolbox)
      printf '%s\n' "${TARGETS[0]}"
      ;;
    MidiBard2)
      printf '%s\n' "${TARGETS[1]}"
      ;;
    MasterOfPuppets)
      printf '%s\n' "${TARGETS[2]}"
      ;;
    *)
      die "Unsupported TOOL_SELECTION: ${TOOL_SELECTION}"
      ;;
  esac
}

normalize_bool() {
  case "${1,,}" in
    1|true|yes|y|on) printf 'true\n' ;;
    *) printf 'false\n' ;;
  esac
}

FORCE_REPUBLISH="$(normalize_bool "${FORCE_REPUBLISH}")"

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

        parent = os.path.dirname(target)
        if parent:
            os.makedirs(parent, exist_ok=True)

        with zf.open(info) as src, open(target, "wb") as dst:
            shutil.copyfileobj(src, dst)
PY
}

resolve_extracted_root() {
  local extract_dir="$1"
  mapfile -t top_entries < <(find "$extract_dir" -mindepth 1 -maxdepth 1 -printf '%P\n')

  if [[ "${#top_entries[@]}" -eq 1 && -d "${extract_dir}/${top_entries[0]}" ]]; then
    printf '%s\n' "${extract_dir}/${top_entries[0]}"
  else
    printf '%s\n' "${extract_dir}"
  fi
}

fetch_upstream_entry() {
  local manifest_url="$1"
  local plugin_name="$2"

  curl -fsSL "$manifest_url" | jq --arg plugin_name "$plugin_name" '
    if type == "array" then
      map(select(
        (.Name? == $plugin_name) or
        (.InternalName? == $plugin_name)
      )) | .[0]
    else
      empty
    end
  '
}

download_upstream_zip() {
  local entry_file="$1"
  jq -r '.DownloadLinkInstall // empty' "$entry_file"
}

get_upstream_version() {
  local entry_file="$1"
  jq -r '
    .AssemblyVersion
    // .Version
    // .TestingAssemblyVersion
    // empty
  ' "$entry_file"
}

publish_shim() {
  local abi_flavor="$1"
  log "Publishing TinyIpc shim"
  clean_dist
  dotnet publish -c Release -f "${PUBLISH_TFM}" TinyIpc.Shim -p:TinyIpcAbiFlavor="${abi_flavor}" -o "${DIST_DIR}"
  dotnet publish -c Release -f net10.0 -r linux-x64 --self-contained true -p:PublishSingleFile=true XivIpc.NativeHost -o "${DIST_DIR}"
  validate_published_runtime
  stage_native_host_payload
}

stage_native_host_payload() {
  local staging_dir="${TINYIPC_SHARED_DIR}/tinyipc-native-host"
  rm -rf "${staging_dir}"
  mkdir -p "${staging_dir}"

  while IFS= read -r -d '' artifact; do
    cp -f "${artifact}" "${staging_dir}/"
  done < <(find "${DIST_DIR}" -maxdepth 1 -type f -print0)

  log "Staged native host payload: ${staging_dir}"
}

install_tinyipc_files() {
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
  done < <(find "${DIST_DIR}" -maxdepth 1 -type f \
    \( -name '*.dll' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' -o -name 'XivIpc.NativeHost' \) -print0)
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

ensure_release_with_asset() {
  local tag="$1"
  local title="$2"
  local notes_file="$3"
  local asset_path="$4"

  if gh release view "$tag" >/dev/null 2>&1; then
    log "Release ${tag} already exists"
    if [[ "${FORCE_REPUBLISH}" == "true" ]]; then
      log "Force enabled; replacing asset on ${tag}"
      gh release upload "$tag" "$asset_path" --clobber
      gh release edit "$tag" --title "$title" --notes-file "$notes_file"
    fi
  else
    log "Creating release ${tag}"
    gh release create "$tag" "$asset_path" \
      --title "$title" \
      --notes-file "$notes_file"
  fi
}

release_asset_url() {
  local tag="$1"
  local asset_name="$2"

  local release_json
  release_json="$(gh api "repos/${GITHUB_REPOSITORY}/releases/tags/${tag}")"

  jq -r --arg asset_name "$asset_name" '
    .assets[]
    | select(.name == $asset_name)
    | .browser_download_url
  ' <<<"$release_json"
}

ensure_pluginmaster_file() {
  if [[ ! -f "${PLUGINMASTER_OUT}" ]]; then
    printf '[]\n' > "${PLUGINMASTER_OUT}"
  fi

  jq -e 'type == "array"' "${PLUGINMASTER_OUT}" >/dev/null \
    || die "${PLUGINMASTER_OUT} must contain a JSON array"
}

merge_plugin_entry() {
  local new_entry_file="$1"
  local tmp_file="${TMP_DIR}/pluginmaster.merged.json"

  jq --slurpfile new "${new_entry_file}" '
    ($new[0]) as $n
    | if type != "array" then
        [$n]
      else
        (map(
          if ((.Name? == $n.Name) or (.InternalName? == $n.InternalName))
          then $n
          else .
          end
        )) as $updated
        | if any(.[]; ((.Name? == $n.Name) or (.InternalName? == $n.InternalName))) then
            $updated
          else
            $updated + [$n]
          end
      end
  ' "${PLUGINMASTER_OUT}" > "${tmp_file}"

  mv "${tmp_file}" "${PLUGINMASTER_OUT}"
}

build_republished_entry() {
  local original_entry_file="$1"
  local asset_url="$2"
  local output_file="$3"

  jq --arg dl "$asset_url" '
    .DownloadLinkInstall = $dl
    | .DownloadLinkUpdate = $dl
    | .DownloadLinkTesting = $dl
  ' "${original_entry_file}" > "${output_file}"
}

process_target() {
  local slug="$1"
  local plugin_name="$2"
  local manifest_url="$3"

  log "Processing ${plugin_name}"
  log "Fetching upstream manifest: ${manifest_url}"

  local entry_file="${TMP_DIR}/${slug}.entry.json"
  fetch_upstream_entry "${manifest_url}" "${plugin_name}" > "${entry_file}"

  jq -e '.' "${entry_file}" >/dev/null 2>&1 || die "Could not find plugin entry for ${plugin_name}"

  local upstream_version
  upstream_version="$(get_upstream_version "${entry_file}")"
  [[ -n "${upstream_version}" ]] || die "Could not determine upstream version for ${plugin_name}"

  local tag="${slug}-v${upstream_version}"
  local asset_name="${slug}-${upstream_version}.zip"
  local asset_url=""

  if gh release view "${tag}" >/dev/null 2>&1 && [[ "${FORCE_REPUBLISH}" != "true" ]]; then
    log "Release already exists for ${plugin_name} ${upstream_version}; reusing it"
    asset_url="$(release_asset_url "${tag}" "${asset_name}")"
    [[ -n "${asset_url}" ]] || die "Could not get browser_download_url for ${tag}/${asset_name}"
  else
    local upstream_zip_url
    upstream_zip_url="$(download_upstream_zip "${entry_file}")"
    [[ -n "${upstream_zip_url}" ]] || die "No DownloadLinkInstall in upstream entry for ${plugin_name}"

    local upstream_zip="${TMP_DIR}/${slug}.upstream.zip"
    local extract_dir="${TMP_DIR}/${slug}.extract"
    mkdir -p "${extract_dir}"

    log "Downloading upstream zip: ${upstream_zip_url}"
    curl -fL --retry 3 --retry-delay 1 -o "${upstream_zip}" "${upstream_zip_url}"

    extract_zip_normalized "${upstream_zip}" "${extract_dir}"

    local plugin_root
    plugin_root="$(resolve_extracted_root "${extract_dir}")"
    log "Resolved plugin root: ${plugin_root}"

    install_tinyipc_files "${plugin_root}"

    local packaged_zip="${TMP_DIR}/${asset_name}"
    create_plugin_zip "${plugin_root}" "${packaged_zip}"

    local notes_file="${TMP_DIR}/${slug}.notes.md"
    cat > "${notes_file}" <<EOF
Republished ${plugin_name} ${upstream_version} with TinyIpc shim replacement.

Upstream manifest:
${manifest_url}

Upstream version:
${upstream_version}
EOF

    ensure_release_with_asset \
      "${tag}" \
      "${plugin_name} ${upstream_version}" \
      "${notes_file}" \
      "${packaged_zip}"

    asset_url="$(release_asset_url "${tag}" "${asset_name}")"
    [[ -n "${asset_url}" ]] || die "Could not get browser_download_url for ${tag}/${asset_name}"
  fi

  local republished_entry="${TMP_DIR}/${slug}.republished-entry.json"
  build_republished_entry "${entry_file}" "${asset_url}" "${republished_entry}"
  merge_plugin_entry "${republished_entry}"

  log "Updated ${plugin_name} -> ${asset_url}"
}

main() {
  ensure_prereqs
  ensure_pluginmaster_file
    local abi_flavor
    abi_flavor="$(resolve_abi_flavor "${plugin_name}")"
    log "Publishing TinyIpc ABI flavor ${abi_flavor} for ${plugin_name}"
    publish_shim "${abi_flavor}"

  while IFS='|' read -r slug plugin_name manifest_url; do
    process_target "${slug}" "${plugin_name}" "${manifest_url}"
  done < <(selected_targets)

  # Keep output deterministic
  local sorted="${TMP_DIR}/pluginmaster.sorted.json"
  jq 'sort_by(.Name // .InternalName // "")' "${PLUGINMASTER_OUT}" > "${sorted}"
  mv "${sorted}" "${PLUGINMASTER_OUT}"

  log "Finished updating ${PLUGINMASTER_OUT}"
}

main "$@"
