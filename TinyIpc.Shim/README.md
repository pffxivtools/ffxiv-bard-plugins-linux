# TinyIpc.Shim

`TinyIpc.Shim` is the public compatibility layer that applications consume. It preserves the familiar TinyIpc-facing types such as `TinyMessageBus`, `TinyMemoryMappedFile`, and `TinyIpcOptions`, but routes them to this repo's Unix-focused implementation.

## What It Is For

This project exists so callers can keep using the TinyIpc-style API while the actual transport is provided by `XivIpc`.

- `TinyMessageBus` exposes publish/subscribe messaging for a named channel.
- `TinyMemoryMappedFile` exposes named shared storage.
- `TinyIpcOptions` carries sizing and timeout settings.

## How It Works

- `TinyMessageBus` validates that callers are using the shim `TinyMemoryMappedFile`, derives a `ChannelInfo`, and creates the real backend through `UnixMessageBusBackendFactory`.
- The active message-bus implementation is normally the brokered sidecar path from `XivIpc`; if initialization fails, the shim swaps in a disabled implementation so failures surface predictably.
- `TinyMemoryMappedFile` is a lazy wrapper over `XivIpc.IO.UnixTinyMemoryMappedFile`.
- Windows-only raw-handle constructors are intentionally unsupported here. The shim only supports the named Unix-style constructors.
- ABI-flavor compile constants (`3x`, `4x`, `5x`, `compat`) let publish scripts build plugin-specific compatibility variants from the same source.

## Main Entry Points

- `Messaging/TinyMessageBus.cs`
- `IO/TinyMemoryMappedFile.cs`
- `TinyIpcOptions.cs`
