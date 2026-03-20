# Developing

## Overview

This repo contains:

- `TinyIpc.Shim`: public compatibility surface
- `XivIpc`: Linux/Wine/Proton IPC internals
- `XivIpc.NativeHost`: native Linux broker process for the sidecar backend
- `XivIpc.WineTestHost`: Windows test client used under Wine/Proton
- `XivIpc.Tests`: functional, lifecycle, runtime, and multi-user coverage

The current production sidecar architecture is:

- control plane over Unix domain sockets
- broker-owned shared-file ring buffers
- one native Linux broker process per shared directory
- strict group-based access for cross-user sidecar mode

## Prerequisites

- .NET SDK supporting `net9.0` and `net10.0`
- Linux for sidecar, multi-user, Wine, and Proton validation
- a valid shared group for brokered sidecar mode, typically `steam`

Useful environment variables:

- `TINYIPC_MESSAGE_BUS_BACKEND=sidecar|direct|inmemory|auto`
- `TINYIPC_SHARED_DIR=/path/to/shared-dir`
- `TINYIPC_SHARED_GROUP=steam`
- `TINYIPC_NATIVE_HOST_PATH=/path/to/XivIpc.NativeHost`
- `TINYIPC_UNIX_SHELL=/bin/sh`

## Build

Build everything:

```bash
dotnet build TinyIpc.Shim.sln -v minimal
```

Build just the test project:

```bash
dotnet build XivIpc.Tests/XivIpc.Tests.csproj -v minimal
```

## Core Test Commands

Run the main native test matrix on Linux:

```bash
dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net10.0 \
  --filter "FullyQualifiedName~XivIpc.Tests.FunctionalTests|FullyQualifiedName~XivIpc.Tests.ParallelFunctionalTests|FullyQualifiedName~XivIpc.Tests.SidecarLifecycleTests|FullyQualifiedName~XivIpc.Tests.SidecarMultiUserTests"
```

Run only lifecycle coverage:

```bash
dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net10.0 \
  --filter "FullyQualifiedName~XivIpc.Tests.SidecarLifecycleTests"
```

Run only multi-user coverage:

```bash
TINYIPC_ENABLE_MULTIUSER_TESTS=1 \
TINYIPC_MULTIUSER_SECONDARY_USER=bff14bard01 \
TINYIPC_SHARED_GROUP=steam \
dotnet test XivIpc.Tests/XivIpc.Tests.csproj --no-build --framework net10.0 -v normal \
  --filter "FullyQualifiedName~XivIpc.Tests.SidecarMultiUserTests"
```

## Multi-User Sidecar Validation

The brokered sidecar path now requires a valid shared group. For cross-user tests:

- the primary and secondary users must both be members of `TINYIPC_SHARED_GROUP`
- the broker socket, state file, startup lock, and ring files are expected to be group-owned
- the tests start the broker through the normal client path, not by launching the host directly

The repo includes a helper script that publishes the runtime artifacts and runs:

- native multi-user sidecar tests
- Wine runtime integration
- Proton runtime integration

```bash
scripts/run-local-multiuser-tests.sh
```

Important environment overrides for that script:

- `TINYIPC_SHARED_GROUP`
- `TINYIPC_MULTIUSER_SECONDARY_USER`
- `TINYIPC_WINE_SECONDARY_USER`
- `TINYIPC_PROTON_SECONDARY_USER`
- `TINYIPC_RUNTIME_TEST_ARTIFACT_ROOT`

## Wine And Proton Runtime Tests

These tests are environment-gated and use the published Windows test host plus published native broker host.

Build or publish artifacts through the helper script, or set them manually:

- `TINYIPC_RUNTIME_TEST_HOST_PATH`
- `TINYIPC_RUNTIME_NATIVE_HOST_PATH`

Run Wine runtime integration:

```bash
TINYIPC_ENABLE_WINE_TESTS=1 \
TINYIPC_RUNTIME_TEST_HOST_PATH=/abs/path/XivIpc.WineTestHost.exe \
TINYIPC_RUNTIME_NATIVE_HOST_PATH=/abs/path/XivIpc.NativeHost \
dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net10.0 \
  --filter "FullyQualifiedName~XivIpc.Tests.SidecarRuntimeIntegrationTests"
```

Run Proton runtime integration:

```bash
TINYIPC_ENABLE_PROTON_TESTS=1 \
TINYIPC_RUNTIME_TEST_HOST_PATH=/abs/path/XivIpc.WineTestHost.exe \
TINYIPC_RUNTIME_NATIVE_HOST_PATH=/abs/path/XivIpc.NativeHost \
dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net10.0 \
  --filter "FullyQualifiedName~XivIpc.Tests.SidecarRuntimeIntegrationTests"
```

## Publishing Helpers

Local publish and staging:

```bash
scripts/publish-local.sh
```

Dalamud publish packaging:

```bash
scripts/publish-dalamud.sh
```

Those scripts also stage `XivIpc.NativeHost` into the shared directory. In brokered sidecar mode, staged directories and files should be group-owned and non-world-accessible.

## Troubleshooting

If sidecar startup fails, check:

- `TINYIPC_SHARED_GROUP` is set and resolvable on the machine
- `TINYIPC_SHARED_DIR` is writable by the launching user
- the shared directory artifacts have the expected modes:
  - directories: `2770`
  - files: `660`
  - staged executable host payload: `770`

If you suspect duplicate brokers:

- inspect the shared directory for `tinyipc-sidecar.state.json`
- confirm the `pid` in that file matches the live broker
- if the socket file was deleted while the broker stayed alive, the process manager should terminate that unreachable broker before replacing it

If the broker appears to live forever:

- verify `sessionCount` in `tinyipc-sidecar.state.json`
- a non-zero session count means a client still holds or leaked a live connection
- lifecycle tests cover clean dispose, abrupt disconnect, and leaked-session behavior separately
