#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/XivIpc.WineTestHost/XivIpc.WineTestHost.csproj"
OUTPUT_DIR="${1:-$ROOT_DIR/published/wine-test-host}"

dotnet publish "$PROJECT" \
  -c Debug \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o "$OUTPUT_DIR"

printf '%s\n' "$OUTPUT_DIR"
