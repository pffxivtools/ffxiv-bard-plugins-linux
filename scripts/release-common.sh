#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

: "${PUBLISH_TFM:=net9.0}"
: "${TINYIPC_ABI_FLAVOR_OVERRIDE:=}"

MASTEROFPUPPETS_PLUGINMASTER="https://raw.githubusercontent.com/zunetrix/DalamudPlugins/refs/heads/main/pluginmaster.json"
BARDTOOLBOX_PLUGINMASTER="https://raw.githubusercontent.com/reckhou/BardToolbox-Release/refs/heads/master/pluginmaster.json"
MIDIBARD2_PLUGINMASTER="https://raw.githubusercontent.com/reckhou/DalamudPlugins-Ori/api6/pluginmaster.json"

# format:
# "asset_slug|plugin_name_in_manifest|pluginmaster_url|default_abi_flavor"
TARGETS=(
  "BardToolbox|BardToolbox|${BARDTOOLBOX_PLUGINMASTER}|4x"
  "MidiBard2|MidiBard 2|${MIDIBARD2_PLUGINMASTER}|3x"
  "MasterOfPuppets|MasterOfPuppets|${MASTEROFPUPPETS_PLUGINMASTER}|3x"
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

ensure_common_prereqs() {
  require_cmd bash
  require_cmd curl
  require_cmd jq
  require_cmd zip
  require_cmd find
  require_cmd python3
  require_cmd dotnet
}

normalize_bool() {
  case "${1,,}" in
    1|true|yes|y|on) printf 'true\n' ;;
    *) printf 'false\n' ;;
  esac
}

selected_targets() {
  local selection="${TOOL_SELECTION:-all}"

  case "${selection}" in
    all)
      printf '%s\n' "${TARGETS[@]}"
      ;;
    BardToolbox|MidiBard2|MasterOfPuppets)
      local target
      for target in "${TARGETS[@]}"; do
        IFS='|' read -r asset_slug _ <<<"${target}"
        if [[ "${asset_slug}" == "${selection}" ]]; then
          printf '%s\n' "${target}"
          return
        fi
      done
      die "Unsupported TOOL_SELECTION: ${selection}"
      ;;
    *)
      die "Unsupported TOOL_SELECTION: ${selection}"
      ;;
  esac
}

resolve_abi_flavor() {
  local plugin_name="$1"
  local default_abi_flavor="$2"

  if [[ -n "${TINYIPC_ABI_FLAVOR_OVERRIDE}" ]]; then
    printf '%s\n' "${TINYIPC_ABI_FLAVOR_OVERRIDE}"
    return
  fi

  if [[ -n "${default_abi_flavor}" ]]; then
    printf '%s\n' "${default_abi_flavor}"
    return
  fi

  case "${plugin_name}" in
    BardToolbox) printf '4x\n' ;;
    "MidiBard 2"|MidiBard2|MasterOfPuppets) printf '3x\n' ;;
    *) printf 'compat\n' ;;
  esac
}

download_file() {
  local url="$1"
  local out="$2"
  curl -fL --retry 3 --retry-delay 1 -o "$out" "$url"
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

get_upstream_version() {
  local entry_file="$1"

  jq -r '
    .AssemblyVersion
    // .Version
    // .TestingAssemblyVersion
    // empty
  ' "$entry_file"
}

# Extract zip while normalizing Windows '\' path separators to '/'
extract_zip_normalized() {
  local zip_file="$1"
  local extract_dir="$2"

  python3 - "$zip_file" "$extract_dir" <<'PY'
import os
import shutil
import sys
import zipfile

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

  local top_entries
  mapfile -t top_entries < <(find "$extract_dir" -mindepth 1 -maxdepth 1 -printf '%P\n')

  if [[ "${#top_entries[@]}" -eq 1 && -d "${extract_dir}/${top_entries[0]}" ]]; then
    printf '%s\n' "${extract_dir}/${top_entries[0]}"
  else
    printf '%s\n' "${extract_dir}"
  fi
}

publish_shim_flavor() {
  local abi_flavor="$1"
  local shim_dist_dir="$2"

  rm -rf "${shim_dist_dir}"
  mkdir -p "${shim_dist_dir}"

  dotnet publish -c Release -f "${PUBLISH_TFM}" TinyIpc.Shim \
    -p:TinyIpcAbiFlavor="${abi_flavor}" \
    -o "${shim_dist_dir}"

  [[ -f "${shim_dist_dir}/TinyIpc.dll" ]] || die "Missing ${shim_dist_dir}/TinyIpc.dll"
  [[ -f "${shim_dist_dir}/XivIpc.dll" ]] || die "Missing ${shim_dist_dir}/XivIpc.dll"
}

publish_native_host() {
  local host_dist_dir="$1"

  rm -rf "${host_dist_dir}"
  mkdir -p "${host_dist_dir}"

  dotnet publish -c Release -f net9.0 -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true \
    XivIpc.NativeHost \
    -o "${host_dist_dir}"

  [[ -f "${host_dist_dir}/XivIpc.NativeHost" ]] || die "Missing ${host_dist_dir}/XivIpc.NativeHost"
}

install_runtime_files() {
  local plugin_root="$1"
  local shim_dist_dir="$2"

  while IFS= read -r -d '' artifact; do
    local filename
    local replaced_any=0
    filename="$(basename "$artifact")"

    while IFS= read -r -d '' target; do
      cp -f "${artifact}" "${target}"
      replaced_any=1
    done < <(find "${plugin_root}" -type f -name "${filename}" -print0)

    if [[ "${replaced_any}" -eq 0 ]]; then
      cp -f "${artifact}" "${plugin_root}/${filename}"
    fi
  done < <(find "${shim_dist_dir}" -maxdepth 1 -type f \
    \( -name '*.dll' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \) -print0)
}

create_plugin_zip() {
  local plugin_root="$1"
  local output_zip="$2"

  rm -f "${output_zip}"
  (
    cd "${plugin_root}"
    zip -qr "${output_zip}" .
  )
}

ensure_pluginmaster_file() {
  local pluginmaster_file="$1"

  if [[ ! -f "${pluginmaster_file}" ]]; then
    printf '[]\n' > "${pluginmaster_file}"
  fi

  jq -e 'type == "array"' "${pluginmaster_file}" >/dev/null \
    || die "${pluginmaster_file} must contain a JSON array"
}

merge_plugin_entry() {
  local pluginmaster_file="$1"
  local new_entry_file="$2"
  local tmp_file="${pluginmaster_file}.tmp"

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
  ' "${pluginmaster_file}" > "${tmp_file}"

  mv "${tmp_file}" "${pluginmaster_file}"
}

sort_pluginmaster_file() {
  local pluginmaster_file="$1"
  local tmp_file="${pluginmaster_file}.sorted"

  jq 'sort_by(.Name // .InternalName // "")' "${pluginmaster_file}" > "${tmp_file}"
  mv "${tmp_file}" "${pluginmaster_file}"
}

build_republished_entry() {
  local original_entry_file="$1"
  local asset_url="$2"
  local output_file="$3"

  jq --arg dl "${asset_url}" '
    .DownloadLinkInstall = $dl
    | .DownloadLinkUpdate = $dl
    | .DownloadLinkTesting = $dl
    | .IsHide = false
  ' "${original_entry_file}" > "${output_file}"
}

github_release_asset_url() {
  local repository="$1"
  local tag="$2"
  local asset_name="$3"
  printf 'https://github.com/%s/releases/download/%s/%s\n' "${repository}" "${tag}" "${asset_name}"
}

pack_nuget_project() {
  local project_path="$1"
  local output_dir="$2"
  local package_version="$3"
  local repository_url="$4"

  mkdir -p "${output_dir}"

  dotnet pack "${project_path}" -c Release -o "${output_dir}" \
    -p:Version="${package_version}" \
    -p:PackageVersion="${package_version}" \
    -p:ContinuousIntegrationBuild=true \
    -p:RepositoryUrl="${repository_url}"
}

archive_native_host() {
  local published_dir="$1"
  local output_file="$2"

  require_cmd tar

  rm -f "${output_file}"
  (
    cd "${published_dir}"
    tar -czf "${output_file}" .
  )
}
