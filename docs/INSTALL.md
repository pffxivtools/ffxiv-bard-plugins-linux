# Legacy Install

> **⚠️ This repo is deprecated. Use the upstream plugin repos instead.**
>
> These instructions are retained for reference only. See the [README](../README.md) for
> upstream plugin repo URLs to use instead.

## 1. Add the custom Dalamud repo

Open `Dalamud Settings`, then add this URL under custom plugin repositories:

```text
https://raw.githubusercontent.com/pffxivtools/ffxiv-bard-plugins-linux/refs/heads/main/pluginmaster.json
```

## 2. Install the plugin you want

Open `Plugin Installer` and install one of the packaged plugins:

- `BardToolbox`
- `MidiBard 2`
- `Master Of Puppets`

## 3. Launch normally

Launch the game through xivlauncher on Linux and use the plugin as you normally would.

You should not need to manually install or stage `XivIpc.NativeHost`, create a broker directory, or export `TINYIPC_*` variables for the default setup.
