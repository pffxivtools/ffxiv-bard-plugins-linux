#!/usr/bin/env bash
set -Eeuo pipefail

: "${PUBLISH_TFM:=net9.0}"
: "${BARDTOOLBOX_PRIVATE_REPO:=pffxivtools/BardToolbox}"
: "${BARDTOOLBOX_PRIVATE_REF:=main}"
: "${BARDTOOLBOX_LOCAL_DIR:=}"
: "${PUBLISH_CONTEXT:=release}"

: "${MASTEROFPUPPETS_PLUGINMASTER:=https://raw.githubusercontent.com/zunetrix/DalamudPlugins/refs/heads/main/pluginmaster.json}"
: "${MIDIBARD2_PLUGINMASTER:=https://raw.githubusercontent.com/reckhou/DalamudPlugins-Ori/api6/pluginmaster.json}"

# format:
# asset_slug|plugin_name_in_manifest|source_kind|source_value|default_abi_flavor
# source kinds:
#   local-bardtoolbox
#   github-url-pluginmaster
TARGETS=(
  "BardToolbox|BardToolbox|local-bardtoolbox|${BARDTOOLBOX_PRIVATE_REPO}|4x"
  "MidiBard2|MidiBard 2|github-url-pluginmaster|${MIDIBARD2_PLUGINMASTER}|3x"
  "MasterOfPuppets|MasterOfPuppets|github-url-pluginmaster|${MASTEROFPUPPETS_PLUGINMASTER}|3x"
)

log() { printf '[publish] %s\n' "$*"; }
warn() { printf '[publish] WARN: %s\n' "$*" >&2; }
die() { printf '[publish] ERROR: %s\n' "$*" >&2; exit 1; }
require_cmd() { command -v "$1" >/dev/null 2>&1 || die "Missing required command: $1"; }

ensure_common_prereqs() {
  require_cmd bash
  require_cmd jq
  require_cmd dotnet
  require_cmd zip
  require_cmd unzip
  require_cmd tar
  require_cmd find
  require_cmd python3
  require_cmd curl
  require_cmd base64
}

ensure_release_prereqs() {
  ensure_common_prereqs
  if [[ "${ACT:-false}" != "true" ]]; then
    require_cmd gh
  fi
}

selected_targets() {
  local selection="${TOOL_SELECTION:-all}"
  case "${selection}" in
    all)
      printf '%s\n' "${TARGETS[@]}"
      ;;
    BardToolbox|MidiBard2|MasterOfPuppets)
      local target asset_slug
      for target in "${TARGETS[@]}"; do
        IFS='|' read -r asset_slug _ <<<"${target}"
        if [[ "${asset_slug}" == "${selection}" ]]; then
          printf '%s\n' "${target}"
          return 0
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
  local override="${TINYIPC_ABI_FLAVOR_OVERRIDE:-}"

  if [[ -n "${override}" ]]; then
    printf '%s\n' "${override}"
    return 0
  fi

  if [[ -n "${default_abi_flavor}" ]]; then
    printf '%s\n' "${default_abi_flavor}"
    return 0
  fi

  case "${plugin_name}" in
    BardToolbox) printf '4x\n' ;;
    "MidiBard 2"|MidiBard2|MasterOfPuppets) printf '3x\n' ;;
    *) printf 'compat\n' ;;
  esac
}

resolve_github_token() {
  local token="${GH_TOKEN:-${BARDTOOLBOX_GH_TOKEN:-${GITHUB_TOKEN:-}}}"
  [[ -n "${token}" ]] || die "GH_TOKEN (or BARDTOOLBOX_GH_TOKEN/GITHUB_TOKEN) must be set for GitHub fetches"
  printf '%s\n' "${token}"
}

parse_raw_github_url() {
  local url="$1"

  python3 - "$url" <<'PY'
import sys
from urllib.parse import urlparse

url = sys.argv[1]
p = urlparse(url)
parts = [x for x in p.path.lstrip('/').split('/') if x]

if len(parts) < 4:
    sys.exit(1)

owner, repo = parts[0], parts[1]
rest = parts[2:]

if len(rest) >= 4 and rest[0] == 'refs' and rest[1] in ('heads', 'tags'):
    ref = '/'.join(rest[:3])
    path = '/'.join(rest[3:])
else:
    ref = rest[0]
    path = '/'.join(rest[1:])

if not path:
    sys.exit(1)

print(f"{owner}/{repo}")
print(path)
print(ref)
PY
}

parse_github_release_download_url() {
  local url="$1"

  python3 - "$url" <<'PY'
import re
import sys
from urllib.parse import urlparse

url = sys.argv[1]
p = urlparse(url)
m = re.match(r'^/([^/]+/[^/]+)/releases/download/([^/]+)/([^/]+)$', p.path)
if not m:
    sys.exit(1)

print(m.group(1))
print(m.group(2))
print(m.group(3))
PY
}

download_file() {
  local url="$1"
  local output_file="$2"
  mkdir -p "$(dirname "${output_file}")"
  curl -fsSL --retry 3 --retry-delay 1 -o "${output_file}" "${url}"
}

gh_api_json() {
  local endpoint="$1"
  GH_TOKEN="$(resolve_github_token)" gh api "${endpoint}"
}

fetch_github_file_with_gh() {
  local owner_repo="$1"
  local path="$2"
  local ref="$3"
  local output_file="$4"

  mkdir -p "$(dirname "${output_file}")"

  local sha
  sha="$(
    gh_api_json "repos/${owner_repo}/contents/${path}?ref=${ref}" \
      | jq -r '.sha // empty'
  )"
  [[ -n "${sha}" && "${sha}" != "null" ]] || die "Could not resolve blob SHA for ${owner_repo}/${path}@${ref}"

  gh_api_json "repos/${owner_repo}/git/blobs/${sha}" \
    | jq -r '.content // empty' \
    | tr -d '\n' \
    | base64 -d > "${output_file}"

  [[ -s "${output_file}" ]] || die "Fetched empty GitHub file for ${owner_repo}/${path}@${ref}"
}

download_github_release_asset_with_gh() {
  local owner_repo="$1"
  local tag="$2"
  local asset_name="$3"
  local output_file="$4"
  local tmp_dir

  tmp_dir="$(mktemp -d)"
  mkdir -p "$(dirname "${output_file}")"

  GH_TOKEN="$(resolve_github_token)" \
    gh release download "${tag}" \
      --repo "${owner_repo}" \
      --pattern "${asset_name}" \
      --dir "${tmp_dir}" >/dev/null

  [[ -f "${tmp_dir}/${asset_name}" ]] || die "Failed to download ${owner_repo} ${tag} ${asset_name}"

  mv -f "${tmp_dir}/${asset_name}" "${output_file}"
  rm -rf "${tmp_dir}"
}

fetch_github_raw_url() {
  local url="$1"
  local output_file="$2"

  if [[ "${PUBLISH_CONTEXT}" == "local" ]]; then
    download_file "${url}" "${output_file}"
    return 0
  fi

  local parsed owner_repo path ref
  mapfile -t parsed < <(parse_raw_github_url "${url}") || die "Unsupported raw GitHub URL: ${url}"
  owner_repo="${parsed[0]}"
  path="${parsed[1]}"
  ref="${parsed[2]}"
  fetch_github_file_with_gh "${owner_repo}" "${path}" "${ref}" "${output_file}"
}

download_github_release_asset_from_url() {
  local url="$1"
  local output_file="$2"

  if [[ "${PUBLISH_CONTEXT}" == "local" ]]; then
    download_file "${url}" "${output_file}"
    return 0
  fi

  local parsed owner_repo tag asset_name
  mapfile -t parsed < <(parse_github_release_download_url "${url}") || die "Unsupported release URL: ${url}"
  owner_repo="${parsed[0]}"
  tag="${parsed[1]}"
  asset_name="${parsed[2]}"
  download_github_release_asset_with_gh "${owner_repo}" "${tag}" "${asset_name}" "${output_file}"
}

fetch_manifest_for_target() {
  local source_kind="$1"
  local source_value="$2"
  local output_file="$3"

  case "${source_kind}" in
    local-bardtoolbox)
      local repo_dir manifest_file
      repo_dir="$(resolve_bardtoolbox_local_dir)" || die "BardToolbox local repo not found. Expected ../BardToolbox with pluginmaster.json and dist/"
      manifest_file="${repo_dir}/pluginmaster.json"
      [[ -f "${manifest_file}" ]] || die "BardToolbox local repo not found. Expected ${repo_dir}/pluginmaster.json"
      cp -f "${manifest_file}" "${output_file}"
      ;;
    github-url-pluginmaster)
      fetch_github_raw_url "${source_value}" "${output_file}"
      ;;
    *)
      die "Unsupported source kind for manifest fetch: ${source_kind}"
      ;;
  esac
}

extract_plugin_entry() {
  local manifest_file="$1"
  local plugin_name="$2"

  jq --arg plugin_name "${plugin_name}" '
    if type == "array" then
      first(
        .[]
        | select(
            (.Name? == $plugin_name) or
            (.InternalName? == $plugin_name)
          )
      )
    elif type == "object" then
      if (.Name? == $plugin_name) or (.InternalName? == $plugin_name) then
        .
      else
        empty
      end
    else
      empty
    end
  ' "${manifest_file}"
}

get_upstream_version() {
  local entry_file="$1"
  jq -r '.AssemblyVersion // .Version // .TestingAssemblyVersion // empty' "${entry_file}"
}

get_local_plugin_version_from_pluginmaster() {
  local pluginmaster_file="$1"
  local plugin_name="$2"

  if [[ ! -f "${pluginmaster_file}" ]]; then
    return 0
  fi

  jq -r --arg plugin_name "${plugin_name}" '
    if type == "array" then
      first(
        .[]
        | select(
            (.Name? == $plugin_name) or
            (.InternalName? == $plugin_name)
          )
        | (.AssemblyVersion // .Version // .TestingAssemblyVersion // empty)
      ) // empty
    else
      empty
    end
  ' "${pluginmaster_file}"
}

plugin_version_changed() {
  local local_version="$1"
  local upstream_version="$2"

  [[ -n "${upstream_version}" ]] || return 0
  [[ -z "${local_version}" ]] && return 0
  [[ "${local_version}" != "${upstream_version}" ]]
}

resolve_bardtoolbox_local_dir() {
  local candidates=()

  [[ -n "${BARDTOOLBOX_LOCAL_DIR}" ]] && candidates+=("${BARDTOOLBOX_LOCAL_DIR}")
  candidates+=("${PWD}/BardToolbox" "${PWD}/../BardToolbox")

  local c
  for c in "${candidates[@]}"; do
    if [[ -f "${c}/pluginmaster.json" && -d "${c}/dist" ]]; then
      printf '%s\n' "${c}"
      return 0
    fi
  done

  return 1
}

copy_tree_contents() {
  local src="$1"
  local dst="$2"

  rm -rf "${dst}"
  mkdir -p "${dst}"
  (
    cd "${src}"
    find . -mindepth 1 -maxdepth 1 -exec cp -a {} "${dst}/" \;
  )
}

extract_zip_normalized() {
  local zip_file="$1"
  local extract_dir="$2"

  python3 - "$zip_file" "$extract_dir" <<'PY'
import os
import shutil
import sys
import zipfile

zip_path, out_dir = sys.argv[1], sys.argv[2]
os.makedirs(out_dir, exist_ok=True)

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

  mapfile -t top_entries < <(find "${extract_dir}" -mindepth 1 -maxdepth 1 -printf '%P\n')

  if [[ "${#top_entries[@]}" -eq 1 && -d "${extract_dir}/${top_entries[0]}" ]]; then
    printf '%s\n' "${extract_dir}/${top_entries[0]}"
  else
    printf '%s\n' "${extract_dir}"
  fi
}

prepare_plugin_tree_from_zip() {
  local zip_path="$1"
  local extract_dir="$2"

  rm -rf "${extract_dir}"
  mkdir -p "${extract_dir}"
  extract_zip_normalized "${zip_path}" "${extract_dir}"
  resolve_extracted_root "${extract_dir}"
}

publish_shim_flavor() {
  local abi_flavor="$1"
  local shim_dist_dir="$2"

  rm -rf "${shim_dist_dir}"
  mkdir -p "${shim_dist_dir}"

  dotnet publish TinyIpc.Shim/TinyIpc.Shim.csproj \
    -c Release \
    -f "${PUBLISH_TFM}" \
    -p:TinyIpcAbiFlavor="${abi_flavor}" \
    -o "${shim_dist_dir}"

  [[ -f "${shim_dist_dir}/TinyIpc.dll" ]] || die "Missing ${shim_dist_dir}/TinyIpc.dll"
  [[ -f "${shim_dist_dir}/XivIpc.dll" ]] || die "Missing ${shim_dist_dir}/XivIpc.dll"
}

publish_native_host() {
  local host_dist_dir="$1"

  rm -rf "${host_dist_dir}"
  mkdir -p "${host_dist_dir}"

  dotnet publish XivIpc.NativeHost/XivIpc.NativeHost.csproj \
    -c Release \
    -f net9.0 \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -o "${host_dist_dir}"

  [[ -f "${host_dist_dir}/XivIpc.NativeHost" ]] || die "Missing ${host_dist_dir}/XivIpc.NativeHost"
}

install_runtime_files() {
  local plugin_root="$1"
  local dist_dir="$2"

  [[ -d "${dist_dir}" ]] || die "Missing dist directory: ${dist_dir}"

  mkdir -p "${plugin_root}"

  cp -a "${dist_dir}/." "${plugin_root}/"
}

create_plugin_zip() {
  local plugin_root="$1"
  local output_zip="$2"

  rm -f "${output_zip}"
  mkdir -p "$(dirname "${output_zip}")"

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

  jq -e 'type == "array"' "${pluginmaster_file}" >/dev/null || die "${pluginmaster_file} must contain a JSON array"
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
  local last_update="${LAST_UPDATE_OVERRIDE:-$(date +%s)}"

  jq --arg dl "${asset_url}" --arg lu "${last_update}" '
    .DownloadLinkInstall = $dl
    | .DownloadLinkUpdate = $dl
    | .DownloadLinkTesting = $dl
    | .LastUpdate = $lu
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

  dotnet pack "${project_path}" \
    -c Release \
    -o "${output_dir}" \
    -p:Version="${package_version}" \
    -p:PackageVersion="${package_version}" \
    -p:ContinuousIntegrationBuild=true \
    -p:RepositoryUrl="${repository_url}"
}

archive_native_host() {
  local published_dir="$1"
  local output_file="$2"
  local host_file="${published_dir}/XivIpc.NativeHost"

  [[ -f "${host_file}" ]] || die "Missing ${host_file}"

  rm -f "${output_file}"
  mkdir -p "$(dirname "${output_file}")"

  tar -C "${published_dir}" -czf "${output_file}" "XivIpc.NativeHost"
}

download_plugin_payload_for_release() {
  local source_kind="$1"
  local source_value="$2"
  local download_url="$3"
  local output_file="$4"

  case "${source_kind}" in
    local-bardtoolbox)
      if [[ "${PUBLISH_CONTEXT}" != "local" ]]; then
        die "local-bardtoolbox payloads are only supported by publish-local.sh or local test fixtures"
      fi
      download_file "${download_url}" "${output_file}"
      ;;
    github-url-pluginmaster)
      if [[ "${download_url}" == https://raw.githubusercontent.com/* ]]; then
        fetch_github_raw_url "${download_url}" "${output_file}"
      elif [[ "${download_url}" == https://github.com/*/releases/download/* ]]; then
        download_github_release_asset_from_url "${download_url}" "${output_file}"
      else
        if [[ "${PUBLISH_CONTEXT}" == "local" ]]; then
          download_file "${download_url}" "${output_file}"
        else
          die "Unsupported GitHub download URL: ${download_url}"
        fi
      fi
      ;;
    *)
      die "Unsupported source kind for payload download: ${source_kind}"
      ;;
  esac
}
