#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

FAKE_BIN="$TMP_ROOT/bin"
FIXTURES="$TMP_ROOT/fixtures"
mkdir -p "$FAKE_BIN" "$FIXTURES"

make_zip() {
  local src="$1" out="$2"
  mkdir -p "$(dirname "$out")"
  (cd "$src" && zip -qr "$out" .)
}

mkdir -p "$FIXTURES/BardToolboxRepo/publish/BardToolbox"
cat > "$FIXTURES/BardToolboxRepo/pluginmaster.json" <<'JSON'
[
  {
    "Name": "BardToolbox",
    "InternalName": "BardToolbox",
    "AssemblyVersion": "1.2.3",
    "DownloadLinkInstall": "https://github.com/pffxivtools/BardToolbox/releases/download/1.2.3/BardToolbox.zip",
    "DownloadLinkUpdate": "https://github.com/pffxivtools/BardToolbox/releases/download/1.2.3/BardToolbox.zip",
    "DownloadLinkTesting": "https://github.com/pffxivtools/BardToolbox/releases/download/1.2.3/BardToolbox.zip"
  }
]
JSON
printf 'bard dll\n' > "$FIXTURES/BardToolboxRepo/publish/BardToolbox/BardToolbox.dll"
printf 'bard json\n' > "$FIXTURES/BardToolboxRepo/publish/BardToolbox/BardToolbox.json"
printf 'orig tinyipc\n' > "$FIXTURES/BardToolboxRepo/publish/BardToolbox/TinyIpc.dll"
make_zip "$FIXTURES/BardToolboxRepo/publish/BardToolbox" "$FIXTURES/BardToolboxRepo/BardToolbox.zip"

mkdir -p "$FIXTURES/MidiBard2Src"
printf 'midi dll\n' > "$FIXTURES/MidiBard2Src/MidiBard2.dll"
printf 'orig tinyipc\n' > "$FIXTURES/MidiBard2Src/TinyIpc.dll"
make_zip "$FIXTURES/MidiBard2Src" "$FIXTURES/MidiBard2.zip"
cat > "$FIXTURES/MidiBard2.pluginmaster.json" <<'JSON'
[
  {
    "Name": "MidiBard 2",
    "InternalName": "MidiBard2",
    "AssemblyVersion": "2.0.0",
    "DownloadLinkInstall": "https://raw.githubusercontent.com/reckhou/DalamudPlugins-Ori/api6/plugins/MidiBard2/latest.zip",
    "DownloadLinkUpdate": "https://raw.githubusercontent.com/reckhou/DalamudPlugins-Ori/api6/plugins/MidiBard2/latest.zip",
    "DownloadLinkTesting": "https://raw.githubusercontent.com/reckhou/DalamudPlugins-Ori/api6/plugins/MidiBard2/latest.zip"
  }
]
JSON

mkdir -p "$FIXTURES/MopSrc"
printf 'mop dll\n' > "$FIXTURES/MopSrc/MasterOfPuppets.dll"
printf 'orig tinyipc\n' > "$FIXTURES/MopSrc/TinyIpc.dll"
make_zip "$FIXTURES/MopSrc" "$FIXTURES/MasterOfPuppets.zip"
cat > "$FIXTURES/MasterOfPuppets.pluginmaster.json" <<'JSON'
[
  {
    "Name": "MasterOfPuppets",
    "InternalName": "MasterOfPuppets",
    "AssemblyVersion": "3.0.0",
    "DownloadLinkInstall": "https://raw.githubusercontent.com/zunetrix/DalamudPlugins/main/plugins/MasterOfPuppets/latest.zip",
    "DownloadLinkUpdate": "https://raw.githubusercontent.com/zunetrix/DalamudPlugins/main/plugins/MasterOfPuppets/latest.zip",
    "DownloadLinkTesting": "https://raw.githubusercontent.com/zunetrix/DalamudPlugins/main/plugins/MasterOfPuppets/latest.zip"
  }
]
JSON

cat > "$FAKE_BIN/dotnet" <<'EOF2'
#!/usr/bin/env bash
set -Eeuo pipefail
cmd="$1"; shift
out=""
project=""
version=""
abi="compat"
rid=""
while [[ "$#" -gt 0 ]]; do
  case "$1" in
    -o) out="$2"; shift 2 ;;
    -p:PackageVersion=*|-p:Version=*) version="${1#*=}"; shift ;;
    -p:TinyIpcAbiFlavor=*) abi="${1#*=}"; shift ;;
    -r) rid="$2"; shift 2 ;;
    *.csproj|*.sln) project="$1"; shift ;;
    *) shift ;;
  esac
done
case "$cmd" in
  build)
    exit 0 ;;
  publish)
    mkdir -p "$out"
    case "$project" in
      *TinyIpc.Shim.csproj)
        printf 'shim %s\n' "$abi" > "$out/TinyIpc.dll"
        printf 'xivipc from shim\n' > "$out/XivIpc.dll"
        printf '{}' > "$out/TinyIpc.deps.json"
        printf '{}' > "$out/TinyIpc.runtimeconfig.json"
        ;;
      *XivIpc.NativeHost.csproj)
        printf '#!/bin/sh\nexit 0\n' > "$out/XivIpc.NativeHost"
        chmod +x "$out/XivIpc.NativeHost"
        ;;
      *) exit 1 ;;
    esac
    ;;
  pack)
    mkdir -p "$out"
    base="$(basename "$project" .csproj)"
    touch "$out/${base}.${version}.nupkg"
    ;;
  *) exit 1 ;;
esac
EOF2
chmod +x "$FAKE_BIN/dotnet"

cat > "$FAKE_BIN/gh" <<'EOF2'
#!/usr/bin/env bash
set -Eeuo pipefail
FIXTURES_DIR="__FIXTURES__"
sub="$1"; shift
case "$sub" in
  api)
    route=""
    while [[ "$#" -gt 0 ]]; do
      case "$1" in
        repos/*) route="$1"; shift ;;
        *) shift ;;
      esac
    done
    case "$route" in
      repos/pffxivtools/BardToolbox/contents/pluginmaster.json?ref=main)
        cat "$FIXTURES_DIR/BardToolboxRepo/pluginmaster.json" ;;
      repos/reckhou/DalamudPlugins-Ori/contents/pluginmaster.json?ref=api6)
        cat "$FIXTURES_DIR/MidiBard2.pluginmaster.json" ;;
      repos/zunetrix/DalamudPlugins/contents/pluginmaster.json?ref=refs/heads/main)
        cat "$FIXTURES_DIR/MasterOfPuppets.pluginmaster.json" ;;
      repos/reckhou/DalamudPlugins-Ori/contents/plugins/MidiBard2/latest.zip?ref=api6)
        cat "$FIXTURES_DIR/MidiBard2.zip" ;;
      repos/zunetrix/DalamudPlugins/contents/plugins/MasterOfPuppets/latest.zip?ref=refs/heads/main|repos/zunetrix/DalamudPlugins/contents/plugins/MasterOfPuppets/latest.zip?ref=main)
        cat "$FIXTURES_DIR/MasterOfPuppets.zip" ;;
      *) echo "unsupported route: $route" >&2; exit 1 ;;
    esac
    ;;
  release)
    sub2="$1"; shift
    case "$sub2" in
      download)
        tag="$1"; shift
        repo=""; pattern=""; dir=""
        while [[ "$#" -gt 0 ]]; do
          case "$1" in
            --repo) repo="$2"; shift 2 ;;
            --pattern) pattern="$2"; shift 2 ;;
            --dir) dir="$2"; shift 2 ;;
            *) shift ;;
          esac
        done
        mkdir -p "$dir"
        if [[ "$repo" == "pffxivtools/BardToolbox" && "$pattern" == "BardToolbox.zip" ]]; then
          cp "$FIXTURES_DIR/BardToolboxRepo/BardToolbox.zip" "$dir/BardToolbox.zip"
        else
          echo "unsupported release download: $repo $tag $pattern" >&2; exit 1
        fi
        ;;
      *) exit 1 ;;
    esac
    ;;
  --version)
    echo gh-fake ;;
  *) echo "unsupported gh subcommand: $sub" >&2; exit 1 ;;
esac
EOF2
sed -i "s|__FIXTURES__|$FIXTURES|g" "$FAKE_BIN/gh"
chmod +x "$FAKE_BIN/gh"

export PATH="$FAKE_BIN:$PATH"
export GH_TOKEN=fake
export BARDTOOLBOX_LOCAL_DIR="$FIXTURES/BardToolboxRepo"
export TINYIPC_SHARED_GROUP=doesnotexist

cd "$REPO_ROOT"

OUTPUT_DIR="$TMP_ROOT/output" TINYIPC_SHARED_DIR="$TMP_ROOT/shared" BASE_URL="https://example.invalid" bash scripts/publish-local.sh
[[ -f "$TMP_ROOT/output/artifacts/BardToolbox.zip" ]]
[[ -f "$TMP_ROOT/output/artifacts/MidiBard2.zip" ]]
[[ -f "$TMP_ROOT/output/artifacts/MasterOfPuppets.zip" ]]

PUBLISH_SCHEME=plugins GITHUB_REPOSITORY=example/repo bash scripts/publish-release.sh
jq -e '.releases | length == 3' dist/github/metadata/releases.json >/dev/null

PUBLISH_SCHEME=semver RELEASE_VERSION=1.2.3 GITHUB_REPOSITORY=example/repo TOOL_SELECTION=BardToolbox bash scripts/publish-release.sh
jq -e '.releases | length == 1' dist/github/metadata/releases.json >/dev/null
compgen -G 'dist/github/assets/*.nupkg' >/dev/null
compgen -G 'dist/github/assets/*.tar.gz' >/dev/null
