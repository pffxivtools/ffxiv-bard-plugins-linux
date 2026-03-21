# XivIpc

`XivIpc` provides Linux support for TinyIpc-based Dalamud plugins.

This repo exists for the plugins packaged by this project, including `BardToolbox`, `MidiBard 2`, and `MasterOfPuppets`, and for any other consumer that expects the `TinyIpc` API shape but needs to run on Linux, Wine, or xivlauncher's XLCore runtime. The public API surface stays TinyIpc-compatible through `TinyIpc.Shim`, while the transport underneath is replaced with a Linux-native implementation.

If you are looking for developer/build information, start with [docs/DEVELOPING.md](docs/DEVELOPING.md). This README is the user guide.

## Why This Exists

Upstream [steamcore/TinyIpc](https://github.com/steamcore/TinyIpc) describes TinyIpc as a lightweight, serverless .NET inter-process broadcast message bus for Windows desktop applications. Its own README also calls out two important constraints:

- it is serverless
- it is Windows-only

That is the problem this repo solves.

Linux, Wine, and Proton do not provide the same Windows-only named primitives that upstream TinyIpc depends on. On top of that, cross-client communication on Linux needs explicit handling for:

- shared filesystem paths
- Unix socket ownership
- group permissions
- Wine path translation
- native Linux process startup

This repo keeps the TinyIpc-facing API, but changes how it is implemented:

- `TinyIpc.Shim` preserves types such as `TinyMessageBus`, `TinyMemoryMappedFile`, and `TinyIpcOptions`
- `XivIpc` provides the Unix-side implementation
- `XivIpc.NativeHost` is the native Linux broker process

## How It Works

At runtime, the message bus is brokered instead of fully serverless.

1. A plugin creates a `TinyMessageBus` or `TinyMemoryMappedFile`.
2. `TinyIpc.Shim` forwards that work into the Unix implementation in `XivIpc`.
3. The client discovers or launches `XivIpc.NativeHost`.
4. The client connects to the broker over a Unix domain socket.
5. The broker creates and owns the channel journal files in the shared runtime directory.
6. Publishers send control messages to the broker, and subscribers drain from the broker-owned journal.
7. The broker tracks live sessions and shuts down after the last client disconnects and the idle timeout expires.

The current production path is:

- control plane: Unix domain socket
- message storage: broker-owned shared journal files
- shared storage: file-backed named shared files
- access control: Unix group ownership via `TINYIPC_SHARED_GROUP`

The important pieces in this repo are:

- [`TinyIpc.Shim`](TinyIpc.Shim/README.md): TinyIpc-compatible public facade
- [`XivIpc`](XivIpc/README.md): Unix-side transport and storage implementation
- [`XivIpc.NativeHost`](XivIpc.NativeHost/README.md): native Linux broker

## Runtime Requirements

The recommended setup is:

- Linux
- xivlauncher-core
- xivlauncher's built-in XLCore Wine runtime

Important compatibility notes:

- Steam Proton does not currently work for this setup because Pressure Vessel sandboxes the runtime and prevents the required shared-path/native-host model from working reliably.
- If you are not using XLCore Wine, use at least Wine 10.2.
- XLCore Wine is the preferred runtime.

## Install Guide

### 1. Create a shared Unix group

Create one Unix group that every game client using this IPC setup will share.

Example:

```bash
sudo groupadd steam
sudo usermod -aG steam myuser
```

Every cooperating client needs access through the same group, because the broker checks peer group membership before allowing a connection.

### 2. Create a shared root and runtime directory

Create a shared directory that all game clients can access. Use the setgid bit so new files and directories inherit the group.

Example layout:

```text
/home/shared/tinyipc-shared-ffxiv/
/home/shared/tmpfs/tinyipc-shared-ffxiv/
```

Recommended:

- use one shared root for durable payloads and staged binaries
- use one runtime directory for broker socket, broker state, and journal files
- make the runtime directory a `tmpfs` mount for speed if possible

Example permissions for the user-managed shared directory:

```text
drwxrwsr-x
```

The broker-managed runtime artifacts inside that directory are more restrictive. In normal operation the implementation expects group-owned broker directories and files and repairs them toward modes such as:

- directories: `2770`
- files: `660`

### 3. Place `XivIpc.NativeHost` in the shared area

Download or publish `XivIpc.NativeHost` and place it somewhere all clients can reference, for example:

```text
/home/shared/tinyipc-shared-ffxiv/tinyipc-native-host/XivIpc.NativeHost
```

That path is the native Linux executable the clients will launch when they need the broker.

### 4. Set the environment variables before launching

A working launcher example looks like this:

```bash
#!/usr/bin/env bash
set -euo pipefail

user="${1:-myuser}"

export TINYIPC_NATIVE_HOST_PATH="/home/shared/tinyipc-shared-ffxiv/tinyipc-native-host/XivIpc.NativeHost"
export TINYIPC_BROKER_DIR="Z:\\home\\shared\\tmpfs\\tinyipc-shared-ffxiv"
export TINYIPC_SHARED_DIR="Z:\\home\\shared\\tmpfs\\tinyipc-shared-ffxiv"
export TINYIPC_SHARED_GROUP="steam"

/usr/bin/xivlauncher-core
```

What each variable does:

- `TINYIPC_NATIVE_HOST_PATH`
  - Path to the native Linux broker executable.
  - Keep this as the real Unix path to `XivIpc.NativeHost`.
- `TINYIPC_BROKER_DIR`
  - Directory for the broker socket, state file, and broker-owned journals.
  - In most setups this should be the same location as `TINYIPC_SHARED_DIR`.
- `TINYIPC_SHARED_DIR`
  - Shared storage root used by the TinyIpc-compatible file and message-bus implementation.
  - A tmpfs-backed path is ideal for performance.
- `TINYIPC_SHARED_GROUP`
  - Required group used for broker authorization and filesystem ownership.
  - All cooperating game clients must share this group.

### 5. Path format matters

The example above mixes Unix and Wine-style paths on purpose.

- `TINYIPC_NATIVE_HOST_PATH` should point at the real Linux executable path.
- `TINYIPC_BROKER_DIR` and `TINYIPC_SHARED_DIR` are commonly provided as `Z:\...` paths in launcher/Wine-facing setups.

This repo normalizes paths depending on which runtime is reading them. That lets the Windows-side plugin runtime and the Linux native host agree on the same physical location.

As a rule:

- use a Unix path for the native host executable
- use the shared runtime path format that your launcher/runtime expects for the broker/shared directories
- keep `TINYIPC_BROKER_DIR` and `TINYIPC_SHARED_DIR` pointed at the same physical directory unless you have a specific reason to separate them

## What Gets Created At Runtime

Once the first client connects, the broker creates runtime files in the broker directory, including:

- the Unix socket
- the broker state file
- one or more broker-owned journal files for channels

The broker is singleton-per-directory. If multiple clients point at the same broker directory, they join the same broker instance.

## Troubleshooting

### The broker does not start

Check:

- `TINYIPC_NATIVE_HOST_PATH` points to a real `XivIpc.NativeHost` binary
- `TINYIPC_SHARED_GROUP` is set
- the shared group exists on the machine
- the launching user is a member of that group
- the shared and runtime directories are writable and group-accessible

### Clients do not see each other

Check:

- all clients use the same `TINYIPC_SHARED_GROUP`
- all clients use the same physical shared/runtime directory
- the runtime is not isolated behind a sandbox that blocks the shared path

### Steam Proton does not work

That is expected for this setup. Steam Proton uses Pressure Vessel sandboxing, which breaks the shared-path/native-host model this repo depends on. Use xivlauncher's XLCore Wine runtime instead.

### The broker seems to stay alive forever

The broker shuts down after all sessions disconnect and the idle timeout expires. If it stays alive, one client probably still has a live connection or leaked session.

### I want logs

Useful optional environment variables:

- `TINYIPC_ENABLE_LOGGING=1`
- `TINYIPC_LOG_DIR=/path/to/logs`
- `TINYIPC_LOG_LEVEL=info`

## For Developers

This README is intentionally user-focused.

If you are building, testing, or packaging this repo, use:

- [docs/DEVELOPING.md](docs/DEVELOPING.md)
- [TinyIpc.Shim README](TinyIpc.Shim/README.md)
- [XivIpc README](XivIpc/README.md)
- [XivIpc.NativeHost README](XivIpc.NativeHost/README.md)
- [XivIpc.Tests README](XivIpc.Tests/README.md)
