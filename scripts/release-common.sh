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
}

BARDTOOLBOX_OWNER_DEFAULT="pffxivtools"
BARDTOOLBOX_REPO_DEFAULT="BardToolbox"
BARDTOOLBOX_MANIFEST_PATH_DEFAULT="pluginmaster.json"
BARDTOOLBOX_MANIFEST_REF_DEFAULT="main"

ensure_pluginmaster_file() {
  local output_file="$1"
  mkdir -p "$(dirname "${output_file}")"
  [[ -f "${output_file}" ]] || printf '[]\n' > "${output_file}"
}

selected_targets() {
  local selection="${TOOL_SELECTION:-all}"
  case "${selection}" in
    all|BardToolbox)
      printf '%s\n' \
        "BardToolbox|BardToolbox|github-private-pluginmaster|${BARDTOOLBOX_OWNER:-$BARDTOOLBOX_OWNER_DEFAULT}/${BARDTOOLBOX_REPO:-$BARDTOOLBOX_REPO_DEFAULT}:${BARDTOOLBOX_MANIFEST_PATH:-$BARDTOOLBOX_MANIFEST_PATH_DEFAULT}@${BARDTOOLBOX_MANIFEST_REF:-$BARDTOOLBOX_MANIFEST_REF_DEFAULT}|linux-x64|repo|${BARDTOOLBOX_OWNER:-$BARDTOOLBOX_OWNER_DEFAULT}/${BARDTOOLBOX_REPO:-$BARDTOOLBOX_REPO_DEFAULT}"
      ;;
    MidiBard2|MasterOfPuppets)
      die "${selection} is not yet configured in scripts/release-common.sh. BardToolbox is fully wired up for private repo publishing; add additional manifest sources before publishing ${selection}."
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
  local override="${!override_var:-}"
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
  unzip -q -o "${zip_path}" -d "${extract_dir}"
}

resolve_extracted_root() {
  local extract_dir="$1"
  local entries=()
  while IFS= read -r entry; do
    entries+=("${entry}")
  done < <(find "${extract_dir}" -mindepth 1 -maxdepth 1)

  if [[ "${#entries[@]}" -eq 1 && -d "${entries[0]}" ]]; then
    printf '%s\n' "${entries[0]}"
  else
    printf '%s\n' "${extract_dir}"
  fi
}

pack_nuget_project() {
  local project_path="$1"
  local output_dir="$2"
  local version="$3"
  local repository_url="$4"

  log "Packing ${project_path} ${version}"
  dotnet pack "${project_path}" \
    -c Release \
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
  dotnet publish XivIpc.NativeHost/XivIpc.NativeHost.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained false \
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
  log "Publishing TinyIpc shim for ${abi_flavor}"
  dotnet publish TinyIpc.Shim/TinyIpc.Shim.csproj \
    -c Release \
    -o "${output_dir}" \
    -p:ContinuousIntegrationBuild=true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -p:RuntimeIdentifier="${abi_flavor}"

  local native_host_dir="${output_dir}/XivIpc.NativeHost"
  dotnet publish XivIpc.NativeHost/XivIpc.NativeHost.csproj \
    -c Release \
    -r "${abi_flavor}" \
    --self-contained false \
    -o "${native_host_dir}"
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

  case "${source_kind}" in
    github-private-pluginmaster)
      local parsed owner_repo manifest_path manifest_ref
      mapfile -t parsed < <(parse_github_repo_spec "${source_value}")
      owner_repo="${parsed[0]}"
      manifest_path="${parsed[1]}"
      manifest_ref="${parsed[2]}"
      [[ -n "${BARDTOOLBOX_GH_TOKEN:-}" ]] || die "BARDTOOLBOX_GH_TOKEN must be set to fetch ${owner_repo}/${manifest_path}"
      fetch_github_file "${owner_repo}" "${manifest_path}" "${manifest_ref}" "${output_file}" "${BARDTOOLBOX_GH_TOKEN}"
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
      map(select((.InternalName // .Name // "") == $pluginName))[0]
    else
      .
    end
  ' "${manifest_file}"
}

get_upstream_version() {
  local entry_file="$1"
  jq -r '.AssemblyVersion // .Version // .VersionString // empty' "${entry_file}"
}

get_download_link_install() {
  local manifest_file="$1"
  local plugin_name="$2"
  jq -r --arg pluginName "${plugin_name}" '
    if type == "array" then
      map(select((.InternalName // .Name // "") == $pluginName))[0].DownloadLinkInstall // empty
    else
      .DownloadLinkInstall // empty
    end
  ' "${manifest_file}"
}

download_plugin_payload_for_release() {
  local source_kind="$1"
  local download_url="$2"
  local output_file="$3"
  mkdir -p "$(dirname "${output_file}")"

  case "${source_kind}" in
    github-private-pluginmaster)
      [[ -n "${BARDTOOLBOX_GH_TOKEN:-}" ]] || die "BARDTOOLBOX_GH_TOKEN must be set to download private release asset ${download_url}"
      curl -fsSL -H "Authorization: Bearer ${BARDTOOLBOX_GH_TOKEN}" "${download_url}" -o "${output_file}"
      ;;
    *)
      curl -fsSL "${download_url}" -o "${output_file}"
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
  local runtime_dir="${plugin_root}/x64/linux"

  mkdir -p "${runtime_dir}"
  cp -a "${shim_dir}/." "${runtime_dir}/"
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
