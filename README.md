# XivIpc

`XivIpc` is the Linux runtime and packaging repo for TinyIpc-based Dalamud plugins such as `BardToolbox`, `MidiBard 2`, and `Master Of Puppets`.

If you are here to install and use the plugins on Linux, this README is the guide. If you are building, testing, or packaging the repo itself, use [docs/DEVELOPING.md](docs/DEVELOPING.md).

## What This Repo Does

These plugins were originally built around Windows-only TinyIpc behavior. This repo ships Linux-compatible plugin packages and the native runtime pieces needed to make them work under Linux and Wine.

For most users, the important part is simple:

- install the plugin from this repo's custom Dalamud feed
- launch the game through xivlauncher on Linux
- use the plugin normally in game

With the current runtime defaults, the IPC side should work out of the box for the standard single-user setup.

## Supported Setup

Recommended:

- Linux
- `xivlauncher-core`
- xivlauncher's built-in XLCore Wine runtime

Compatibility notes:

- XLCore Wine is the preferred runtime.
- If you are not using XLCore Wine, use a recent Wine build.
- Steam Proton is not a supported primary path for this setup. Pressure Vessel sandboxing can interfere with the shared-path/native-host model these plugins use.

## Install

### 1. Add the custom Dalamud repo

Open `Dalamud Settings`, then add this URL under custom plugin repositories:

```text
https://raw.githubusercontent.com/pffxivtools/ffxiv-bard-plugins-linux/refs/heads/main/pluginmaster.json
```

### 2. Install the plugin you want

Open `Plugin Installer` and install one of the packaged plugins:

- `BardToolbox`
- `MidiBard 2`
- `Master Of Puppets`

### 3. Launch normally

Launch the game through xivlauncher on Linux and use the plugin as you normally would.

You should not need to manually install or stage `XivIpc.NativeHost`, create a broker directory, or export `TINYIPC_*` variables for the default setup.

## What Works Automatically

The current runtime path is much simpler than the older manual setup:

- the shared runtime directory defaults to `/tmp/tinyipc-shared-ffxiv`
- the native host is expected at `/tmp/tinyipc-shared-ffxiv/tinyipc-native-host/XivIpc.NativeHost`
- if that native host is missing, the runtime can download the latest published host automatically
- default single-user setups can run without `TINYIPC_SHARED_GROUP`

That means the standard path is "install plugin and run it", not "manually configure broker infrastructure first".

## Usage

This repo does not change the normal in-game usage of the packaged plugins. After installation:

- `MidiBard 2` is still used through its normal in-game UI and commands
- `BardToolbox` is still configured through its own windows and settings
- `Master Of Puppets` is still used through its own action and broadcast workflows

For feature-specific help, use each plugin's own docs, UI help, or community support links.

## Advanced Tips

### DXVK VRAM limit tweak

If you are running multiple clients, this DXVK config can help limit VRAM use per client:

```ini
dxgi.maxDeviceMemory = 2048
dxgi.maxSharedMemory = 2048
```

Example:

```bash
export DXVK_CONFIG_FILE=/home/shared/dxvk-alt.conf
```

Put the two `dxgi.*` lines in that file, then launch the client with `DXVK_CONFIG_FILE` pointing to it.

### Optional logging

If you need runtime logs for troubleshooting:

```bash
export TINYIPC_ENABLE_LOGGING=1
export TINYIPC_LOG_DIR=/tmp/tinyipc-logs
export TINYIPC_LOG_LEVEL=info
```

## Troubleshooting

### The plugin installs but clients do not see each other

Start with the simple checks:

- make sure all clients are using the same launcher/runtime family
- prefer XLCore Wine over Proton
- restart the affected clients after updating plugins

If you are running a more customized setup with multiple users or custom shared paths, refer to [docs/DEVELOPING.md](docs/DEVELOPING.md) for the lower-level runtime details.

### The native broker does not seem to start

In the default setup, this usually means one of:

- the client could not download or launch `XivIpc.NativeHost`
- the runtime environment is too restricted or sandboxed
- the shared temp path is not writable

Turn on logging with the variables above and check the generated `tinyipc-*.log` files.

### I am using Proton and it behaves inconsistently

That is expected enough that XLCore Wine should be treated as the supported path. Proton's containerization can block the shared filesystem and native broker behavior these plugins rely on.

### I need custom paths, shared groups, or manual staging

Those are advanced or developer-managed setups now. Use [docs/DEVELOPING.md](docs/DEVELOPING.md) for:

- custom `TINYIPC_*` environment variables
- shared-group and multi-user setups
- native host staging and publish scripts
- broker lifecycle and sidecar details

## Developer Links

If you are working on the repo itself:

- [docs/DEVELOPING.md](docs/DEVELOPING.md)
- [TinyIpc.Shim README](TinyIpc.Shim/README.md)
- [XivIpc README](XivIpc/README.md)
- [XivIpc.NativeHost README](XivIpc.NativeHost/README.md)
- [XivIpc.Tests README](XivIpc.Tests/README.md)
