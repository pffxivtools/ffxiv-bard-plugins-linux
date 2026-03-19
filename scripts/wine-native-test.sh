#!/bin/bash
set -euo pipefail

export TINYIPC_NATIVE_HOST_PORT="40131"
export WINEFSYNC=0
export WINEESYNC=0

./scripts/run-wine-test-host.sh \
    cmd /c start /wait /unix /home/rahul/personal/TinyIpc.Linux.Shim/dist/XivIpc.NativeHost