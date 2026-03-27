# XivIpc

`XivIpc` contains the Unix-side implementation details behind the public shim. It owns the direct and sidecar message-bus transports, the sidecar protocol and broker lifecycle, brokered storage formats, and the shared-file `TinyMemoryMappedFile` implementation.

## What It Is For

This project provides the transport and storage primitives that make the Linux, Wine, and Proton runtime path work.

- Direct message delivery through in-memory or native shared-memory storage.
- Broker-backed message delivery over a Unix socket control plane plus brokered shared-file storage.
- Shared-file memory-mapped-file semantics for named payload storage.
- Runtime and filesystem helpers for shared directories, permissions, logging, and process stamps.

## How It Works

- `UnixDirectTinyMessageBus` selects direct storage for the direct bus family. `in-memory` is local-process only and mainly useful for tests; `shared-memory` is the native Linux direct IPC mode.
- `UnixSidecarTinyMessageBus` connects to the native broker, attaches to brokered storage, queues outbound messages, and reconnects in the background if the broker disappears.
- `UnixSidecarProcessManager` discovers or launches `XivIpc.NativeHost`, enforces the single-broker-per-directory rule, and waits for a live socket plus state file before handing the lease to callers.
- `BrokeredChannelJournal` is the default brokered production storage format.
- `BrokeredChannelRing` is an alternate brokered storage primitive retained for bounded-storage behavior and future experimentation.
- `UnixTinyMemoryMappedFile` implements named file-backed shared storage with a small on-disk header, process-local locking, and OS file locking.
- `UnixSharedStorageHelpers` centralizes path resolution, permission handling, and runtime-specific path normalization.

## Main Entry Points

- `Messaging/UnixDirectTinyMessageBus.cs`
- `Messaging/UnixSidecarTinyMessageBus.cs`
- `Messaging/UnixSidecarProcessManager.cs`
- `IO/UnixTinyMemoryMappedFile.cs`
- `Messaging/BrokeredChannelJournal.cs`
- `Messaging/BrokeredChannelRing.cs`
