#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
FIXTURES_DIR="${REPO_ROOT}/test-fixtures"
FAKEBIN_DIR="${FIXTURES_DIR}/fakebin"
OUTPUT_ROOT="${REPO_ROOT}/.test-out"

mkdir -p "${FIXTURES_DIR}" "${FAKEBIN_DIR}"

cat > "${FAKEBIN_DIR}/dotnet" <<'DOTNET'
#!/usr/bin/env bash
set -Eeuo pipefail
cmd="$1"; shift || true
case "$cmd" in
  build)
    exit 0
    ;;
  publish)
    out=""; project=""; tfm=""; rid=""; abi="";
    while [[ $# -gt 0 ]]; do
      case "$1" in
        -o) out="$2"; shift 2 ;;
        -f) tfm="$2"; shift 2 ;;
        -r) rid="$2"; shift 2 ;;
        -p:TinyIpcAbiFlavor=*) abi="${1#*=}"; shift ;;
        *.csproj|TinyIpc.Shim|XivIpc.NativeHost) project="$1"; shift ;;
        *) shift ;;
      esac
    done
    mkdir -p "$out"
    case "$project" in
      TinyIpc.Shim/TinyIpc.Shim.csproj|TinyIpc.Shim)
        printf 'shim abi=%s tfm=%s\n' "$abi" "$tfm" > "$out/TinyIpc.dll"
        printf 'xivipc\n' > "$out/XivIpc.dll"
        printf '{}\n' > "$out/TinyIpc.deps.json"
        printf '{}\n' > "$out/TinyIpc.runtimeconfig.json"
        ;;
      XivIpc.NativeHost/XivIpc.NativeHost.csproj|XivIpc.NativeHost)
        printf '#!/usr/bin/env bash\necho native host rid=%s tfm=%s\n' "$rid" "$tfm" > "$out/XivIpc.NativeHost"
        chmod +x "$out/XivIpc.NativeHost"
        ;;
      *)
        echo "unknown publish project: $project" >&2
        exit 1
        ;;
    esac
    ;;
  pack)
    out=""; version="0.0.0"; project="";
    while [[ $# -gt 0 ]]; do
      case "$1" in
        -o) out="$2"; shift 2 ;;
        -p:Version=*) version="${1#*=}"; shift ;;
        *.csproj) project="$1"; shift ;;
        *) shift ;;
      esac
    done
    mkdir -p "$out"
    base="$(basename "$project" .csproj)"
    printf 'package %s %s\n' "$base" "$version" > "$out/${base}.${version}.nupkg"
    ;;
  *)
    echo "unsupported fake dotnet command: $cmd" >&2
    exit 1
    ;;
esac
DOTNET
chmod +x "${FAKEBIN_DIR}/dotnet"

make_plugin_fixture() {
  local slug="$1"
  local display_name="$2"
  local version="$3"
  local repo_dir="${FIXTURES_DIR}/${slug}"
  rm -rf "${repo_dir}"
  mkdir -p "${repo_dir}/payload/${slug}"
  printf 'dummy %s dll\n' "$slug" > "${repo_dir}/payload/${slug}/${slug}.dll"
  printf '{"name":"%s"}\n' "$slug" > "${repo_dir}/payload/${slug}/${slug}.json"
  (cd "${repo_dir}/payload" && zip -qr "${repo_dir}/latest.zip" "${slug}")
  cat > "${repo_dir}/pluginmaster.json" <<JSON
[
  {
    "Name": "${display_name}",
    "InternalName": "${slug}",
    "AssemblyVersion": "${version}",
    "DownloadLinkInstall": "file://${repo_dir}/latest.zip"
  }
]
JSON
}

make_plugin_fixture "BardToolbox" "BardToolbox" "1.2.3"
make_plugin_fixture "MidiBard2" "MidiBard 2" "2.3.4"
make_plugin_fixture "MasterOfPuppets" "MasterOfPuppets" "3.4.5"

export PATH="${FAKEBIN_DIR}:$PATH"
export BARDTOOLBOX_LOCAL_DIR="${FIXTURES_DIR}/BardToolbox"
export MIDIBARD2_PLUGINMASTER="file://${FIXTURES_DIR}/MidiBard2/pluginmaster.json"
export MASTEROFPUPPETS_PLUGINMASTER="file://${FIXTURES_DIR}/MasterOfPuppets/pluginmaster.json"

rm -rf "${OUTPUT_ROOT}"
mkdir -p "${OUTPUT_ROOT}"

OUTPUT_DIR="${OUTPUT_ROOT}/plugins" TINYIPC_SHARED_DIR="${OUTPUT_ROOT}/shared" bash "${SCRIPT_DIR}/publish-local.sh"
PUBLISH_SCHEME=plugins GITHUB_REPOSITORY=example/repo bash "${SCRIPT_DIR}/publish-release.sh"
PUBLISH_SCHEME=semver GITHUB_REPOSITORY=example/repo RELEASE_VERSION=9.9.9 bash "${SCRIPT_DIR}/publish-release.sh"

echo "Fixture publish tests passed"
