# XivIpc

> **⚠️ DEPRECATED — Do not install FFXIV plugins from this repo**
>
> The upstream plugins now bundle XivIpc directly. They detect the runtime environment and
> use XivIpc on Linux without needing shims or custom builds from this repo.
>
> **BardToolbox** may not continue to be updated here. Migrate to **MasterOfPuppets** instead.
>
> | Plugin | Source |
> |--------|--------|
> | MasterOfPuppets | [github.com/zunetrix/MasterOfPuppets](https://github.com/zunetrix/MasterOfPuppets) |
> | MidiBard2 | [github.com/zunetrix/MidiBard2](https://github.com/zunetrix/MidiBard2) / [github.com/reckhou/MidiBard2](https://github.com/reckhou/MidiBard2) |
>
> Install plugins from the upstream Dalamud repos instead of this repo's feed:
>
> | Repo | Plugin URL |
> |------|------------|
> | Official MidiBard2 (stable) | `https://raw.githubusercontent.com/reckhou/DalamudPlugins-Ori/api6/pluginmaster.json` |
> | Zune repo (Alpha/Beta, MasterOfPuppets) | `https://raw.githubusercontent.com/zunetrix/DalamudPlugins/main/pluginmaster.json` |
>
> This repo is left as-is and CI will continue to run, but the plugins published here should
> no longer be used.

If you play FFXIV on Linux and want to play music through the game's performance system, these plugins let you do it. XivIpc is the Linux IPC runtime that makes them work under Wine — it replaces the Windows-only TinyIpc library these plugins were originally built around.

## Which Plugin Should I Use?

| Plugin | What it does | Status |
|--------|-------------|--------|
| **Master Of Puppets** | Broadcast and automate performance actions across multiple clients. The most actively developed option. | **Recommended** — still actively maintained |
| **MidiBard 2** | Play MIDI files through the game's performance system. Supports solo and ensemble play. | Actively maintained upstream |
| **BardToolbox** | Older tool for MIDI playback and performance automation. | No longer actively maintained here — migrate to MasterOfPuppets |

## Quick Start

If you're new to FFXIV on Linux, start with the [XIVLauncher install guide](https://goatcorp.github.io/faq/steamdeck.html). Once you have XIVLauncher and Dalamud running, getting a plugin takes two steps:

1. **Add the plugin repo** — open Dalamud Settings, go to the Experimental tab, and add one of the URLs from the banner above under "Custom Plugin Repositories".
2. **Install the plugin** — open the Plugin Installer, find the plugin, and install it.

Launch the game and use the plugin normally. The IPC backend handles itself — you don't need to install anything extra or set any environment variables.

### In-Game Commands

| Plugin | Command |
|--------|---------|
| MidiBard 2 | `/mb` or `/midibard` |
| Master Of Puppets | `/mop` |
| BardToolbox | `/btb` |

## Environment

### Runtime

| Runtime | Works? | Notes |
|---------|--------|-------|
| XLCore Wine (XIVLauncher default) | ✅ | Recommended — tested and supported |
| Other Wine builds | ✅ | Should work with a recent Wine |
| Steam Proton | ⚠️ | Pressure Vessel sandboxing can block the shared filesystem access these plugins need |

### What Runs Automatically

The IPC backend starts on its own when a plugin needs it. It creates a shared runtime directory at `/tmp/tinyipc-shared-ffxiv` and downloads the native broker binary from GitHub if it isn't there yet. For a normal single-client setup, that's all there is to it.

### DXVK Memory

Running multiple clients can exhaust your GPU memory. Limit it per client with a DXVK config:

```ini
dxgi.maxDeviceMemory = 2048
dxgi.maxSharedMemory = 2048
```

Save that to a file (say `~/.config/dxvk.conf`) and set `DXVK_CONFIG_FILE=~/.config/dxvk.conf` before launching the client.

### Logging

If something isn't working, turn on logging to see what the IPC backend is doing:

```bash
export TINYIPC_ENABLE_LOGGING=1
export TINYIPC_LOG_DIR=/tmp/tinyipc-logs
export TINYIPC_LOG_LEVEL=info
```

Logs will appear in `TINYIPC_LOG_DIR` as `tinyipc-*.log` files.

## Running Multiple Clients

If you want to run multiple game profiles side by side (for example, to have one character play music while another performs), see the full guide at [docs/RUN_GAME_CLIENT.md](docs/RUN_GAME_CLIENT.md).

The basic approach: create separate Linux users for each profile, give them display access, and launch XIVLauncher under the right user with the right environment. The guide includes a `run-other.sh` script that handles this.

## Troubleshooting

### Clients don't see each other

- Make sure all clients are using the same runtime family (XLCore Wine, not a mix of Wine and Proton)
- Restart all clients after updating plugins
- If you're running a custom multi-user setup, check that everyone is in the same shared group

### The plugin installs but doesn't seem to work

Enable logging (see above) and check the logs. Common causes:

- The native broker binary couldn't download or start — check your network and that `/tmp` is writable
- Proton's sandbox is blocking access to the shared runtime directory
- The runtime directory was removed or is on a filesystem without POSIX shared memory support

### I need to customize paths or use a shared group

Advanced setup options — custom `TINYIPC_*` environment variables, manual broker staging, multi-user groups — are covered in [docs/DEVELOPING.md](docs/DEVELOPING.md).

### I'm using the legacy install from this repo's feed

Legacy install instructions are at [docs/INSTALL.md](docs/INSTALL.md). But you should switch to the upstream repos — they bundle XivIpc directly and are kept up to date.

## For Developers

- [docs/DEVELOPING.md](docs/DEVELOPING.md) — build, test, publish, architecture
- [TinyIpc.Shim/README.md](TinyIpc.Shim/README.md) — public API compatibility surface
- [XivIpc/README.md](XivIpc/README.md) — IPC internals
- [XivIpc.NativeHost/README.md](XivIpc.NativeHost/README.md) — native broker process
- [XivIpc.Tests/README.md](XivIpc.Tests/README.md) — test coverage
