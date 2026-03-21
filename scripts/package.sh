#!/bin/bash -e

dotnet clean
~/.dotnet/tools/scrub --ask false
rm -f XivIpc.zip
7z a XivIpc.zip \
    TinyIpc.Shim XivIpc XivIpc.NativeHost \
    XivIpc.Tests XivIpc.WineTestHost TinyIpc.Shim.sln \
    .github -xr!.git