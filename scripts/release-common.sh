#!/usr/bin/env bash
set -Eeuo pipefail

log() {
  printf '[publish] %s\n' "$*" >&2
}

die() {
  printf '[publish] ERROR: %s\n' "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"
}

ensure_common_prereqs() {
  require_cmd bash
  require_cmd jq
  require_cmd dotnet
  require_cmd curl
  require_cmd unzip
  require_cmd tar
  require_cmd zip
  require_cmd find
  require_cmd python3
}

ensure_pluginmaster_file() {
  local output_file="$1"
  mkdir -p "$(dirname "${output_file}")"
  [[ -f "${output_file}" ]] || printf '[]\n' > "${output_file}"
  jq -e 'type == "array"' "${output_file}" >/dev/null 2>&1 || die "${output_file} must contain a JSON array"
}

resolve_bardtoolbox_local_dir() {
  local candidates=()
  [[ -n "${BARDTOOLBOX_LOCAL_DIR:-}" ]] && candidates+=("${BARDTOOLBOX_LOCAL_DIR}")
  candidates+=(
    "${REPO_ROOT}/BardToolbox"
    "${REPO_ROOT}/../BardToolbox"
    "${PWD}/BardToolbox"
  )

  local candidate
  local manifest_path="${BARDTOOLBOX_MANIFEST_PATH:-pluginmaster.json}"
  local publish_subdir="${BARDTOOLBOX_PUBLISH_SUBDIR:-publish/BardToolbox}"

  for candidate in "${candidates[@]}"; do
    [[ -n "${candidate}" ]] || continue
    if [[ -f "${candidate}/${manifest_path}" && -d "${candidate}/${publish_subdir}" ]]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  done

  return 1
}

bardtoolbox_release_target() {
  local repo="${BARDTOOLBOX_PRIVATE_REPO:-pffxivtools/BardToolbox}"
  local ref="${BARDTOOLBOX_PRIVATE_REF:-main}"
  local manifest_path="${BARDTOOLBOX_MANIFEST_PATH:-pluginmaster.json}"
  printf '%s\n' "BardToolbox|BardToolbox|github-private-pluginmaster|${repo}:${manifest_path}@${ref}|url-download||4x"
}

bardtoolbox_local_target() {
  local bard_local_dir
  bard_local_dir="$(resolve_bardtoolbox_local_dir)" || die "BardToolbox local repo not found. Expected ../BardToolbox with pluginmaster.json and publish/BardToolbox/"

  local bard_manifest_source_kind="local-pluginmaster"
  local bard_manifest_source_value="${bard_local_dir}/${BARDTOOLBOX_MANIFEST_PATH:-pluginmaster.json}"
  local bard_payload_source_kind="local-publish-dir"
  local bard_payload_source_value="${bard_local_dir}/${BARDTOOLBOX_PUBLISH_SUBDIR:-publish/BardToolbox}"

  printf '%s\n' "BardToolbox|BardToolbox|${bard_manifest_source_kind}|${bard_manifest_source_value}|${bard_payload_source_kind}|${bard_payload_source_value}|4x"
}

selected_targets() {
  local selection="${TOOL_SELECTION:-all}"
  local midi_manifest="${MIDIBARD2_PLUGINMASTER:-https://raw.githubusercontent.com/reckhou/DalamudPlugins-Ori/api6/pluginmaster.json}"
  local mop_manifest="${MASTEROFPUPPETS_PLUGINMASTER:-https://raw.githubusercontent.com/zunetrix/DalamudPlugins/refs/heads/main/pluginmaster.json}"
  local bard_target
  local source_mode="${BARDTOOLBOX_SOURCE_MODE:-}"

  if [[ -z "${source_mode}" ]]; then
    case "${PUBLISH_CONTEXT:-}" in
      local) source_mode="local" ;;
      release) source_mode="remote" ;;
      *)
        if [[ -n "${BARDTOOLBOX_LOCAL_DIR:-}" || -d "${REPO_ROOT}/../BardToolbox/publish/BardToolbox" || -d "${REPO_ROOT}/BardToolbox/publish/BardToolbox" ]]; then
          source_mode="local"
        else
          source_mode="remote"
        fi
        ;;
    esac
  fi

  case "${source_mode}" in
    local)
      bard_target="$(bardtoolbox_local_target)"
      ;;
    remote)
      bard_target="$(bardtoolbox_release_target)"
      ;;
    *)
      die "Unsupported BARDTOOLBOX_SOURCE_MODE: ${source_mode}"
      ;;
  esac

  case "${selection}" in
    all)
      printf '%s\n' \
        "${bard_target}" \
        "MidiBard2|MidiBard 2|url-pluginmaster|${midi_manifest}|url-download||3x" \
        "MasterOfPuppets|MasterOfPuppets|url-pluginmaster|${mop_manifest}|url-download||3x"
      ;;
    BardToolbox)
      printf '%s\n' "${bard_target}"
      ;;
    MidiBard2)
      printf '%s\n' "MidiBard2|MidiBard 2|url-pluginmaster|${midi_manifest}|url-download||3x"
      ;;
    MasterOfPuppets)
      printf '%s\n' "MasterOfPuppets|MasterOfPuppets|url-pluginmaster|${mop_manifest}|url-download||3x"
      ;;
    *)
      die "Unsupported TOOL_SELECTION: ${selection}"
      ;;
  esac
}

resolve_abi_flavor() {
  local plugin_name="$1"
  local default_abi_flavor="$2"
  local override_var="ABI_FLAVOR_${plugin_name//[^A-Za-z0-9]/_}"
  local override="${!override_var:-${TINYIPC_ABI_FLAVOR_OVERRIDE:-}}"
  if [[ -n "${override}" ]]; then
    printf '%s\n' "${override}"
  else
    printf '%s\n' "${default_abi_flavor}"
  fi
}

extract_zip_normalized() {
  local zip_path="$1"
  local extract_dir="$2"

  rm -rf "${extract_dir}"
  mkdir -p "${extract_dir}"

  python3 - "$zip_path" "$extract_dir" <<'PY'
import os
import shutil
import sys
import zipfile

zip_path = sys.argv[1]
out_dir = sys.argv[2]

with zipfile.ZipFile(zip_path) as zf:
    for info in zf.infolist():
        name = info.filename.replace('\\', '/').lstrip('/')
        if not name:
            continue
        target = os.path.join(out_dir, name)
        if info.is_dir() or name.endswith('/'):
            os.makedirs(target, exist_ok=True)
            continue
        parent = os.path.dirname(target)
        if parent:
            os.makedirs(parent, exist_ok=True)
        with zf.open(info) as src, open(target, 'wb') as dst:
            shutil.copyfileobj(src, dst)
PY
}

resolve_extracted_root() {
  local extract_dir="$1"
  local top_entries=()
  while IFS= read -r entry; do
    top_entries+=("$entry")
  done < <(find "$extract_dir" -mindepth 1 -maxdepth 1 -printf '%P\n' | sort)

  if [[ "${#top_entries[@]}" -eq 1 && -d "${extract_dir}/${top_entries[0]}" ]]; then
    printf '%s\n' "${extract_dir}/${top_entries[0]}"
  else
    printf '%s\n' "${extract_dir}"
  fi
}

prepare_plugin_tree_from_zip() {
  local zip_path="$1"
  local extract_dir="$2"
  extract_zip_normalized "${zip_path}" "${extract_dir}"
  resolve_extracted_root "${extract_dir}"
}

prepare_plugin_tree_from_dir() {
  local source_dir="$1"
  local stage_dir="$2"
  rm -rf "${stage_dir}"
  mkdir -p "${stage_dir}"
  cp -a "${source_dir}/." "${stage_dir}/"
  printf '%s\n' "${stage_dir}"
}

pack_nuget_project() {
  local project_path="$1"
  local output_dir="$2"
  local version="$3"
  local repository_url="$4"

  mkdir -p "${output_dir}"
  log "Packing ${project_path} ${version}"
  dotnet pack "${project_path}" \
    -c Release \
    -f net9.0 \
    -o "${output_dir}" \
    -p:ContinuousIntegrationBuild=true \
    -p:Version="${version}" \
    -p:PackageVersion="${version}" \
    -p:RepositoryUrl="${repository_url}" \
    -p:RepositoryType=git
}

publish_native_host() {
  local output_dir="$1"
  log "Publishing native host"
  rm -rf "${output_dir}"
  mkdir -p "${output_dir}"
  dotnet publish XivIpc.NativeHost/XivIpc.NativeHost.csproj \
    -c Release \
    -f net9.0 \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o "${output_dir}"
}

archive_native_host() {
  local publish_dir="$1"
  local output_archive="$2"
  mkdir -p "$(dirname "${output_archive}")"
  tar -C "${publish_dir}" -czf "${output_archive}" .
}

publish_shim_flavor() {
  local abi_flavor="$1"
  local output_dir="$2"
  log "Publishing TinyIpc shim for ABI flavor ${abi_flavor}"
  rm -rf "${output_dir}"
  mkdir -p "${output_dir}"
  dotnet publish TinyIpc.Shim/TinyIpc.Shim.csproj \
    -c Release \
    -f net9.0 \
    -o "${output_dir}" \
    -p:ContinuousIntegrationBuild=true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -p:TinyIpcAbiFlavor="${abi_flavor}"

  [[ -f "${output_dir}/TinyIpc.dll" ]] || die "Missing ${output_dir}/TinyIpc.dll"
  [[ -f "${output_dir}/XivIpc.dll" ]] || die "Missing ${output_dir}/XivIpc.dll"
}

parse_github_repo_spec() {
  local spec="$1"
  local repo_with_path ref owner_repo path
  repo_with_path="${spec%%@*}"
  ref="${spec##*@}"
  [[ "${repo_with_path}" == *:* ]] || die "Invalid GitHub repo spec (missing path): ${spec}"
  owner_repo="${repo_with_path%%:*}"
  path="${repo_with_path#*:}"
  printf '%s\n%s\n%s\n' "${owner_repo}" "${path}" "${ref}"
}

fetch_github_file() {
  local owner_repo="$1"
  local path="$2"
  local ref="$3"
  local output_file="$4"
  local token="${5:-}"
  local url="https://api.github.com/repos/${owner_repo}/contents/${path}?ref=${ref}"

  mkdir -p "$(dirname "${output_file}")"

  if [[ -n "${token}" ]]; then
    curl -fsSL \
      -H "Authorization: Bearer ${token}" \
      -H "Accept: application/vnd.github.raw+json" \
      "${url}" \
      -o "${output_file}"
  else
    curl -fsSL \
      -H "Accept: application/vnd.github.raw+json" \
      "${url}" \
      -o "${output_file}"
  fi
}

fetch_manifest_for_target() {
  local source_kind="$1"
  local source_value="$2"
  local output_file="$3"

  mkdir -p "$(dirname "${output_file}")"
  case "${source_kind}" in
    local-pluginmaster)
      [[ -f "${source_value}" ]] || die "Local pluginmaster not found: ${source_value}"
      cp -f "${source_value}" "${output_file}"
      ;;
    github-private-pluginmaster)
      local parsed owner_repo manifest_path manifest_ref token
      mapfile -t parsed < <(parse_github_repo_spec "${source_value}")
      owner_repo="${parsed[0]}"
      manifest_path="${parsed[1]}"
      manifest_ref="${parsed[2]}"
      token="${BARDTOOLBOX_GH_TOKEN:-${GH_TOKEN:-${GITHUB_TOKEN:-}}}"
      [[ -n "${token}" ]] || die "BARDTOOLBOX_GH_TOKEN (or GH_TOKEN/GITHUB_TOKEN) must be set to fetch ${owner_repo}/${manifest_path}"
      fetch_github_file "${owner_repo}" "${manifest_path}" "${manifest_ref}" "${output_file}" "${token}"
      ;;
    url-pluginmaster)
      curl -fsSL "${source_value}" -o "${output_file}"
      ;;
    *)
      die "Unsupported manifest source kind: ${source_kind}"
      ;;
  esac
}

extract_plugin_entry() {
  local manifest_file="$1"
  local plugin_name="$2"
  jq --arg pluginName "${plugin_name}" '
    if type == "array" then
      first(.[] | select((.Name? == $pluginName) or (.InternalName? == $pluginName)))
    else
      .
    end
  ' "${manifest_file}"
}

get_upstream_version() {
  local entry_file="$1"
  jq -r '.AssemblyVersion // .Version // .VersionString // empty' "${entry_file}"
}

parse_download_url_parts() {
  local url="$1"
  local without_scheme="${url#*://}"
  local path="/${without_scheme#*/}"
  printf '%s\n' "${path}"
}

find_local_payload_file() {
  local local_repo_dir="$1"
  local download_url="$2"
  local asset_name="${download_url##*/}"
  local relative_path
  relative_path="$(parse_download_url_parts "${download_url}")"

  local candidates=(
    "${local_repo_dir}/${relative_path#/}"
    "${local_repo_dir}/${asset_name}"
    "${local_repo_dir}/plugins/${asset_name%.*}/latest.zip"
    "${local_repo_dir}/plugins/${asset_name}"
  )

  local candidate
  for candidate in "${candidates[@]}"; do
    if [[ -f "${candidate}" ]]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  done

  return 1
}

get_download_link_install_from_entry() {
  local entry_file="$1"
  jq -r '.DownloadLinkInstall // empty' "${entry_file}"
}

download_plugin_payload_for_release() {
  local source_kind="$1"
  local download_url="$2"
  local output_file="$3"
  local local_source_kind="${4:-}"
  local local_source_value="${5:-}"
  mkdir -p "$(dirname "${output_file}")"

  case "${local_source_kind}" in
    local-repo)
      local candidate
      candidate="$(find_local_payload_file "${local_source_value}" "${download_url}")" || die "Could not locate local plugin payload for ${download_url} under ${local_source_value}"
      cp -f "${candidate}" "${output_file}"
      ;;
    local-publish-dir)
      [[ -d "${local_source_value}" ]] || die "Local publish dir not found: ${local_source_value}"
      printf '%s\n' "${local_source_value}"
      return 0
      ;;
    *)
      case "${source_kind}" in
        github-private-pluginmaster|github-private-release)
          local token="${BARDTOOLBOX_GH_TOKEN:-${GH_TOKEN:-${GITHUB_TOKEN:-}}}"
          [[ -n "${token}" ]] || die "BARDTOOLBOX_GH_TOKEN (or GH_TOKEN/GITHUB_TOKEN) must be set to download private release asset ${download_url}"
          curl -fsSL -H "Authorization: Bearer ${token}" "${download_url}" -o "${output_file}"
          ;;
        *)
          curl -fsSL "${download_url}" -o "${output_file}"
          ;;
      esac
      ;;
  esac
}

github_release_asset_url() {
  local repository="$1"
  local tag="$2"
  local asset_name="$3"
  printf 'https://github.com/%s/releases/download/%s/%s\n' "${repository}" "${tag}" "${asset_name}"
}

build_republished_entry() {
  local original_entry_file="$1"
  local release_url="$2"
  local output_file="$3"
  local unix_time
  unix_time="$(date +%s)"

  jq --arg releaseUrl "${release_url}" --arg lastUpdate "${unix_time}" '
    .DownloadLinkInstall = $releaseUrl
    | .DownloadLinkUpdate = $releaseUrl
    | .DownloadLinkTesting = $releaseUrl
    | .LastUpdate = $lastUpdate
  ' "${original_entry_file}" > "${output_file}"
}

merge_plugin_entry() {
  local pluginmaster_file="$1"
  local entry_file="$2"
  local tmp
  tmp="$(mktemp)"
  jq --slurpfile entry "${entry_file}" '
    . as $existing
    | ($entry[0].InternalName // $entry[0].Name) as $name
    | [ $existing[] | select((.InternalName // .Name) != $name) ] + [$entry[0]]
  ' "${pluginmaster_file}" > "${tmp}"
  mv "${tmp}" "${pluginmaster_file}"
}

sort_pluginmaster_file() {
  local pluginmaster_file="$1"
  local tmp
  tmp="$(mktemp)"
  jq 'sort_by(.InternalName // .Name // "")' "${pluginmaster_file}" > "${tmp}"
  mv "${tmp}" "${pluginmaster_file}"
}

install_runtime_files() {
  local plugin_root="$1"
  local shim_dir="$2"

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
  done < <(find "${shim_dir}" -maxdepth 1 -type f     \( -name '*.dll' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \) -print0)
}

emit_plugin_folder_manifest() {
  local entry_file="$1"
  local asset_slug="$2"
  local plugin_root="$3"
  jq -n \
    --arg assetSlug "${asset_slug}" \
    --arg generatedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    --slurpfile pluginEntry "${entry_file}" '
      {
        assetSlug: $assetSlug,
        generatedAt: $generatedAt,
        pluginEntry: $pluginEntry[0]
      }
    ' > "${plugin_root}/xivipc.republish.json"
}

create_plugin_zip() {
  local plugin_root="$1"
  local output_zip="$2"
  mkdir -p "$(dirname "${output_zip}")"
  rm -f "${output_zip}"
  (
    cd "${plugin_root}"
    zip -qr "${output_zip}" .
  )
}
