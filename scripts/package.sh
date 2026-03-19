#!/bin/bash -e

dotnet clean
~/.dotnet/tools/scrub --ask false
rm -f TinyIpc.Shim.zip
7z a TinyIpc.Shim.zip \
    TinyIpc.Shim XivIpc XivIpc.NativeHost \
    TinyIpc.Shim.sln \
    .github -xr!.git