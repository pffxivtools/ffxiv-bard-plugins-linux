#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
FIXTURE_ROOT="${1:-${REPO_ROOT}/.github/act/fixtures/publish}"
FIXTURE_ROOT="$(realpath -m "${FIXTURE_ROOT}")"
FIXTURE_VERSION_MODE="${FIXTURE_VERSION_MODE:-changed}"

rm -rf "${FIXTURE_ROOT}"
mkdir -p "${FIXTURE_ROOT}"

make_plugin_zip() {
  local plugin_dir="$1"
  local plugin_dll_name="$2"
  local output_zip="$3"

  mkdir -p "${plugin_dir}"
  mkdir -p "$(dirname "${output_zip}")"
  printf 'stub for %s\n' "${plugin_dll_name}" > "${plugin_dir}/${plugin_dll_name}.dll"
  printf '{"name":"%s"}\n' "${plugin_dll_name}" > "${plugin_dir}/${plugin_dll_name}.json"
  output_zip="$(realpath -m "${output_zip}")"
  (
    cd "${plugin_dir}"
    zip -qr "${output_zip}" .
  )
}

version_for_plugin() {
  local plugin_name="$1"

  case "${FIXTURE_VERSION_MODE}" in
    changed)
      case "${plugin_name}" in
        BardToolbox) printf '99.99.99-fixture.1\n' ;;
        MidiBard2) printf '98.98.98-fixture.1\n' ;;
        MasterOfPuppets) printf '97.97.97-fixture.1\n' ;;
        *) return 1 ;;
      esac
      ;;
    unchanged)
      jq -r --arg plugin_name "${plugin_name}" '
        first(
          .[]
          | select(
              (.Name? == $plugin_name) or
              (.InternalName? == $plugin_name)
            )
          | (.AssemblyVersion // .Version // .TestingAssemblyVersion // empty)
        ) // empty
      ' "${REPO_ROOT}/pluginmaster.json"
      ;;
    *)
      printf 'Unsupported FIXTURE_VERSION_MODE: %s\n' "${FIXTURE_VERSION_MODE}" >&2
      return 1
      ;;
  esac
}

BARDTOOLBOX_VERSION="$(version_for_plugin "BardToolbox")"
MIDIBARD2_VERSION="$(version_for_plugin "MidiBard2")"
MASTEROFPUPPETS_VERSION="$(version_for_plugin "MasterOfPuppets")"

# BardToolbox local fixture repo
BARDTOOLBOX_REPO="${FIXTURE_ROOT}/BardToolbox"
mkdir -p "${BARDTOOLBOX_REPO}/dist/assets" "${BARDTOOLBOX_REPO}/src/BardToolbox"
make_plugin_zip "${BARDTOOLBOX_REPO}/src/BardToolbox" "BardToolbox" "${BARDTOOLBOX_REPO}/dist/assets/BardToolbox-upstream.zip"
cat > "${BARDTOOLBOX_REPO}/pluginmaster.json" <<JSON
[
  {
    "Author": "fixture",
    "Name": "BardToolbox",
    "InternalName": "BardToolbox",
    "AssemblyVersion": "${BARDTOOLBOX_VERSION}",
    "TestingAssemblyVersion": "${BARDTOOLBOX_VERSION}",
    "DownloadLinkInstall": "file://${BARDTOOLBOX_REPO}/dist/assets/BardToolbox-upstream.zip",
    "DownloadLinkUpdate": "file://${BARDTOOLBOX_REPO}/dist/assets/BardToolbox-upstream.zip",
    "DownloadLinkTesting": "file://${BARDTOOLBOX_REPO}/dist/assets/BardToolbox-upstream.zip",
    "IsHide": false,
    "LastUpdate": "0"
  }
]
JSON

# Public plugin fixture manifests
mkdir -p "${FIXTURE_ROOT}/manifests" "${FIXTURE_ROOT}/assets/MidiBard2" "${FIXTURE_ROOT}/assets/MasterOfPuppets"
make_plugin_zip "${FIXTURE_ROOT}/assets/MidiBard2/root" "MidiBard2" "${FIXTURE_ROOT}/assets/MidiBard2/MidiBard2-upstream.zip"
make_plugin_zip "${FIXTURE_ROOT}/assets/MasterOfPuppets/root" "MasterOfPuppets" "${FIXTURE_ROOT}/assets/MasterOfPuppets/MasterOfPuppets-upstream.zip"

cat > "${FIXTURE_ROOT}/manifests/MidiBard2.pluginmaster.json" <<JSON
[
  {
    "Author": "fixture",
    "Name": "MidiBard 2",
    "InternalName": "MidiBard2",
    "AssemblyVersion": "${MIDIBARD2_VERSION}",
    "TestingAssemblyVersion": "${MIDIBARD2_VERSION}",
    "DownloadLinkInstall": "file://${FIXTURE_ROOT}/assets/MidiBard2/MidiBard2-upstream.zip",
    "DownloadLinkUpdate": "file://${FIXTURE_ROOT}/assets/MidiBard2/MidiBard2-upstream.zip",
    "DownloadLinkTesting": "file://${FIXTURE_ROOT}/assets/MidiBard2/MidiBard2-upstream.zip",
    "IsHide": false,
    "LastUpdate": "0"
  }
]
JSON

cat > "${FIXTURE_ROOT}/manifests/MasterOfPuppets.pluginmaster.json" <<JSON
[
  {
    "Author": "fixture",
    "Name": "MasterOfPuppets",
    "InternalName": "MasterOfPuppets",
    "AssemblyVersion": "${MASTEROFPUPPETS_VERSION}",
    "TestingAssemblyVersion": "${MASTEROFPUPPETS_VERSION}",
    "DownloadLinkInstall": "file://${FIXTURE_ROOT}/assets/MasterOfPuppets/MasterOfPuppets-upstream.zip",
    "DownloadLinkUpdate": "file://${FIXTURE_ROOT}/assets/MasterOfPuppets/MasterOfPuppets-upstream.zip",
    "DownloadLinkTesting": "file://${FIXTURE_ROOT}/assets/MasterOfPuppets/MasterOfPuppets-upstream.zip",
    "IsHide": false,
    "LastUpdate": "0"
  }
]
JSON

printf 'Fixtures created at %s (mode=%s)\n' "${FIXTURE_ROOT}" "${FIXTURE_VERSION_MODE}"
