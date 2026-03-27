# Two-Family Bus Refactor Checklist

## Summary

Refactor `XivIpc` into exactly two top-level bus families:

- direct bus family: no native host, storage selected by config
- sidecar bus family: native host broker, storage selected by config

Under that model:

- `UnixInMemoryTinyMessageBus` becomes a thin direct-bus wrapper over pluggable direct storage
- pure in-memory storage remains available but is treated as test-oriented, not real IPC
- direct native-Linux IPC is implemented through a shared-memory direct storage mode
- sidecar brokered IPC uses a shared brokered-storage abstraction used by both `UnixSidecarTinyMessageBus` and `XivIpc.NativeHost`
- env-var reading and defaulting is centralized into typed settings instead of being scattered through helpers and transports

For direct shared-memory discovery, reuse the existing shared-directory rendezvous model: deterministic shared-memory object names plus a small metadata file in the shared directory.

## First-Pass Status

This checklist tracks the refactor in implementable steps. Items marked complete reflect the first pass that has already landed in the working tree.

Completed in the first pass:

- added a typed runtime settings layer in `XivIpc/Internal/TinyIpcRuntimeSettings.cs`
- moved bus-family selection out of inline factory normalization and into typed settings
- added explicit direct-storage selection via `TINYIPC_DIRECT_STORAGE_MODE`
- centralized sidecar heartbeat/timeout resolution used by `UnixSidecarTinyMessageBus`
- re-enabled direct shared-memory selection through the public shim path
- added a focused direct shared-memory selection test
- added a direct-storage abstraction and a direct-family wrapper used by the shim factory
- converted `UnixInMemoryTinyMessageBus` into a thin wrapper over in-memory direct storage
- added shared brokered storage interfaces and a journal-backed brokered storage implementation
- moved both `UnixSidecarTinyMessageBus` and `XivIpc.NativeHost` onto the shared journal-backed brokered storage abstraction
- added a storage-neutral internal attach metadata type used between host and client code paths while keeping the current wire frame unchanged
- added ring-backed brokered storage behind the shared brokered storage abstraction
- added sidecar ring storage selection coverage and fixed child-process propagation for `TINYIPC_SIDECAR_STORAGE_MODE` and `TINYIPC_SLOT_COUNT`
- added typed shared-scope and broker-runtime settings used by the native host, shared-storage helpers, and sidecar process manager
- removed remaining raw `Environment.GetEnvironmentVariable` reads from host and shared-storage helper implementations
- added focused runtime-settings tests for backend selection, storage-mode selection, shared-scope defaults, and broker socket or idle-time overrides
- updated developer and project docs to describe the two-family plus storage-mode model and the current `journal` versus `ring` sidecar split
- added alias-compatibility and defaulting coverage for message-bus and storage-mode settings
- added public-shim direct behavior coverage for both `in-memory` and `shared-memory` storage modes
- documented the direct shared-memory discovery contract in developer docs
- added focused direct shared-memory discovery tests for deterministic object naming, rendezvous metadata creation, and incompatible concurrent sizing requests
- expanded sidecar ring coverage with lifecycle tests for cleanup, stale socket replacement, heartbeat timeout, and protocol-level capacity mismatch rejection
- added ring reconnect-recovery coverage for broker death followed by a new client attach
- centralized explicit backend/storage compatibility validation in `TinyIpcRuntimeSettings`
- added focused tests for invalid direct/sidecar backend and storage-mode combinations
- added a small direct shared-memory test host plus cross-process reattach coverage
- added staged-host production-path ring coverage for functional delivery and core lifecycle recovery cases
- added an environment-gated production-ring multi-user wrapper so the existing secondary-user harness can run against ring mode
- added a dedicated `sidecar-ring` multi-user wrapper and wired the official helper script plus developer commands to include the ring multi-user suites
- moved remaining logger, sidecar child-launch, native-host startup, shim auto-selection, and file-init backend reads behind `TinyIpcRuntimeSettings`
- extracted brokered client attach translation, storage creation, and format-specific effective sizing into `BrokeredClientAttachmentFactory`
- reorganized the runtime and test source trees into `Direct`, `Brokered`, and `Sidecar` folders so the on-disk structure matches the refactored architecture

Not completed in the first pass:

- sidecar ring multi-user coverage

Remaining work:

- none for the first-pass refactor checklist

## 1. Centralize All Runtime Settings

- [x] Add a single typed config layer for message bus selection and transport settings.
- [x] Keep `TINYIPC_MESSAGE_BUS_BACKEND` as the top-level family selector.
- [x] Add a separate direct storage-mode setting via `TINYIPC_DIRECT_STORAGE_MODE`.
- [x] Add a separate sidecar storage-mode setting via `TINYIPC_SIDECAR_STORAGE_MODE`.
- [x] Centralize direct transport slot-count and TTL defaults in the typed settings layer.
- [x] Centralize sidecar heartbeat interval, heartbeat timeout, and TTL defaults in the typed settings layer.
- [x] Add a dedicated `SharedScopeSettings` type for shared dir, broker dir, shared group, path normalization, and naming scope.
- [x] Remove remaining direct `Environment.GetEnvironmentVariable` reads from bus, storage, host, and helper implementations.
- [x] Consolidate validation rules so defaults and invalid combinations are enforced in one place only.
- [x] Document the supported bus-family and storage-mode settings.

First-pass notes:

- `TinyIpcRuntimeSettings` now provides `MessageBusSelectionSettings`, `DirectTransportSettings`, `SharedScopeSettings`, `BrokerRuntimeSettings`, and `SidecarTransportSettings`.
- `TinyIpcRuntimeSettings` now also provides `LoggingSettings` and `NativeHostSettings`, and the remaining runtime callers consume those instead of reading env vars directly.

## 2. Keep Only Two Top-Level Bus Families

- [x] Make the backend factory instantiate only direct or sidecar top-level bus families, with `auto` resolving to one of those two paths.
- [x] Route direct selection through direct-storage kind selection instead of backend-string branching in the factory.
- [x] Convert `UnixInMemoryTinyMessageBus` into the thin direct-bus wrapper/entry point over a pluggable direct-storage abstraction.
- [x] Introduce a dedicated direct storage implementation type for in-memory transport.
- [x] Introduce a dedicated direct storage implementation type for shared-memory transport.
- [x] Reduce `UnixSidecarTinyMessageBus` to control-plane responsibilities only by moving storage-format logic below it.

First-pass notes:

- The factory now resolves `direct`, `sidecar`, and `auto` through typed settings.
- The direct path now goes through `UnixDirectTinyMessageBus`, which selects a direct storage implementation.
- Shared-memory storage currently uses an adapter around the existing `UnixSharedMemoryTinyMessageBus` implementation rather than a fully extracted storage-native implementation.
- `UnixSidecarTinyMessageBus` now keeps the session/control-plane loops while broker attach-frame decoding, attach-info translation, client-storage creation, and format-specific effective sizing live in `BrokeredClientAttachmentFactory`.

## 3. Introduce Separate Abstractions For Direct And Brokered Storage

### Direct Storage

- [x] Add a direct-storage abstraction covering publish, subscribe/drain, sizing validation, and lifecycle.
- [x] Refactor the direct family to depend on that abstraction rather than concrete in-memory/shared-memory bus classes.
- [x] Move in-memory transport behavior behind the direct-storage abstraction.
- [x] Move shared-memory transport behavior behind the direct-storage abstraction.

### Brokered Storage

- [x] Add `IBrokeredStorageFormat` or equivalent storage-kind factory/attach abstraction.
- [x] Add `IBrokeredHostStorage`.
- [x] Add `IBrokeredClientStorage`.
- [x] Extract the current journal-backed storage behind that abstraction without changing behavior.
- [x] Add ring-backed brokered storage behind the same abstraction.

## 4. Make Sidecar Storage Shared Between Host And Client

- [x] Use the brokered storage abstraction in `UnixSidecarTinyMessageBus`.
- [x] Use the same brokered storage abstraction in `XivIpc.NativeHost`.
- [x] Move storage creation and attach logic behind the shared abstraction.
- [x] Move storage kind selection behind the shared abstraction.
- [x] Move storage-specific sizing behind the shared abstraction.
- [x] Move head/tail/cursor semantics behind the shared abstraction.
- [x] Move path or object identifier handling behind the shared abstraction.
- [x] Move cleanup and diagnostics hooks behind the shared abstraction.
- [x] Replace journal-specific attach metadata with a storage-neutral attach contract.

Storage-neutral attach metadata should carry:

- [x] storage kind
- [x] identifier or path
- [x] mapped length
- [x] capacity or slot metadata
- [x] initial sequence or cursor state
- [x] storage or protocol version

## 5. Define Direct Shared-Memory Discovery Explicitly

- [x] Support direct shared-memory as an explicit storage mode selected through centralized config.
- [x] Document the direct shared-memory discovery contract.
- [x] Ensure shared-memory object names are deterministic from channel name plus shared-scope hash.
- [x] Store generation, version, and sizing metadata in a small rendezvous metadata file in the shared directory.
- [x] Reuse the shared-dir, group, and path-normalization helpers through centralized shared-scope settings.
- [x] Add direct shared-memory tests covering reopen and reattach across processes.
- [x] Add direct shared-memory tests covering deterministic discovery, mismatch detection, and cleanup.

First-pass notes:

- The first pass re-enabled direct shared-memory selection through the public shim and verified message delivery.
- The direct shared-memory implementation already uses a deterministic shared object name plus a `busmeta` rendezvous file in the shared directory.
- `DirectSharedMemoryDiscoveryTests` now verify deterministic naming, metadata-file creation, and incompatible concurrent sizing detection.
- Cleanup and reopen after the last participant exits are covered by `DirectStorageBehaviorTests`.
- Cross-process reattach remains the main unverified direct shared-memory scenario.

## 6. Clarify Which Storage Modes Are Real IPC Versus Test-Only

- [x] Update docs to state that direct `in-memory` storage is local-process only and primarily useful for tests.
- [x] Update docs to state that direct `shared-memory` storage is the real native-Linux direct IPC mode.
- [x] Update docs to state that sidecar `journal` is the default production brokered mode.
- [x] Update docs to state that sidecar `ring` is an alternate retained mode for bounded storage behavior and future experimentation.
- [x] Align README and developer docs terminology with the two-family plus storage-mode model.

## 7. Migration Sequence

- [x] 1. Add typed settings and move the first wave of env-var reads behind them.
- [x] 2. Refactor factory selection into the two-family model.
- [x] 3. Extract the direct-storage abstraction and convert `UnixInMemoryTinyMessageBus` into the thin direct wrapper.
- [x] 4. Reintroduce native-Linux shared-memory as a direct storage implementation using explicit shared-dir metadata discovery.
- [x] 5. Extract brokered-storage abstraction around the current journal path.
- [x] 6. Convert both `UnixSidecarTinyMessageBus` and native host to the brokered abstraction.
- [x] 7. Add ring-backed brokered storage behind the same abstraction.
- [x] 8. Update docs and tests to reflect the new bus and storage terminology and config model.

Status note:

- Step 4 is now complete for the direct shared-memory discovery contract and focused verification slice.
- Step 7 is complete for implementation plus core brokered ring verification. Ring-specific multi-user or production-path coverage is still outstanding.

## Public APIs And Interfaces

### External Compatibility

- [x] Keep the public `TinyIpc.Shim` API shape unchanged in the first pass.
- [x] Preserve current backend-family env-var behavior where practical.
- [x] Add storage-mode configuration rather than redefining backend-family values.
- [x] Document that in-memory direct mode is test-oriented.

### Internal Interfaces And Types

- [x] Add typed runtime settings objects.
- [x] Add storage-kind enums for direct and sidecar families.
- [x] Add a direct-storage interface and concrete implementations behind it.
- [x] Add brokered host/client storage interfaces.
- [x] Add a storage-neutral attach metadata type.

## Test Plan

### Config And Selection

- [x] Add focused tests for family selection from `TINYIPC_MESSAGE_BUS_BACKEND`.
- [x] Add focused tests for storage-mode resolution and defaults.
- [x] Add focused tests for invalid family and storage combinations.
- [x] Add focused tests for alias compatibility and defaulting behavior.

### Direct Family

- [x] Add a direct shared-memory selection test through the public shim.
- [x] Run or add direct behavior coverage for `direct + in-memory`.
- [x] Run or add direct behavior coverage for `direct + shared-memory`.
- [x] Cover single publisher and subscriber behavior.
- [x] Cover multiple publishers and subscribers.
- [x] Cover reopen and reattach across processes.
- [x] Cover metadata mismatch detection.
- [x] Cover sizing validation for incompatible concurrent larger-capacity attach attempts.
- [x] Cover cleanup and reopen after the last participant exits.
- [x] Add focused discovery tests for deterministic object naming and metadata-file creation.

### Sidecar Family

- [x] Keep the existing sidecar journal path passing through functional and lifecycle validation in the first pass.
- [x] Run or add brokered behavior coverage for `sidecar + journal` under the new abstraction.
- [x] Run or add brokered behavior coverage for `sidecar + ring`.
- [x] Cover broker startup and attach.
- [x] Cover reconnect and heartbeat end-to-end in ring mode.
- [x] Cover message ordering and delivery.
- [x] Cover lifecycle cleanup.
- [x] Cover stale artifact replacement.
- [x] Cover capacity mismatch rejection.
- [x] Cover heartbeat timeout shutdown behavior in ring mode.
- [x] Cover production-path behavior after the brokered abstraction lands in ring mode.
- [x] Cover multi-user behavior after the brokered abstraction lands in ring mode.

Sidecar ring note:

- `ProductionPathRingMultiUserTests` is now wired up, but it remains environment-gated and was not validated in this workspace because the multi-user test env vars are not set.
- `SidecarRingMultiUserTests`, `ProductionPathMultiUserTests`, and `ProductionPathRingMultiUserTests` are now included in the documented and scripted multi-user validation command paths.

Current status:

- all first-pass checklist items are complete in the working tree
- the managed secondary-user subscriber path now passes for journal, ring, production journal, and production ring multi-user validation
- real multi-user validation has been executed on this machine with `sudo -n`, the `steam` shared group, and a configured secondary user

To close the final unchecked item:

- complete

### First-Pass Verification Completed

- [x] `dotnet build TinyIpc.Shim.sln -v minimal`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.UnixInMemoryTinyMessageBusTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.DirectSharedMemorySelectionTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.FunctionalTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.SidecarLifecycleTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.BrokeredChannelJournalTests|FullyQualifiedName~XivIpc.Tests.BrokeredChannelJournalConcurrencyTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.FunctionalTests|FullyQualifiedName~XivIpc.Tests.SidecarLifecycleTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.BrokeredRingStorageTests|FullyQualifiedName~XivIpc.Tests.SidecarRingSelectionTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.BrokeredChannelJournalTests|FullyQualifiedName~XivIpc.Tests.FunctionalTests|FullyQualifiedName~XivIpc.Tests.SidecarLifecycleTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.TinyIpcRuntimeSettingsTests|FullyQualifiedName~XivIpc.Tests.SidecarRingSelectionTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.TinyIpcRuntimeSettingsTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.DirectStorageBehaviorTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.DirectSharedMemoryDiscoveryTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.SidecarRingSelectionTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.TinyIpcRuntimeSettingsTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.DirectSharedMemoryCrossProcessTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.ProductionPathRingFunctionalTests|FullyQualifiedName~XivIpc.Tests.ProductionPathRingLifecycleTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.ProductionPathRingMultiUserTests"` (environment-gated; no multi-user runtime validation executed here)
- [x] `dotnet build TinyIpc.Shim.sln -v minimal` after env centralization cleanup
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.TinyIpcRuntimeSettingsTests|FullyQualifiedName~XivIpc.Tests.SidecarRingSelectionTests"`
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.ProductionPathRingFunctionalTests|FullyQualifiedName~XivIpc.Tests.ProductionPathRingLifecycleTests"`
- [x] `dotnet build TinyIpc.Shim.sln -v minimal` after brokered client attach extraction
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.SidecarRingSelectionTests|FullyQualifiedName~XivIpc.Tests.TinyIpcRuntimeSettingsTests"`
- [x] `dotnet build TinyIpc.Shim.sln -v minimal` after adding ring multi-user wrappers to the scripted validation path
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.SidecarRingMultiUserTests|FullyQualifiedName~XivIpc.Tests.ProductionPathMultiUserTests|FullyQualifiedName~XivIpc.Tests.ProductionPathRingMultiUserTests"` (wrapper wiring only; this workspace could not execute real cross-user validation)
- [x] `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 -m:1 --filter "FullyQualifiedName~XivIpc.Tests.FunctionalTests|FullyQualifiedName~XivIpc.Tests.ParallelFunctionalTests|FullyQualifiedName~XivIpc.Tests.SidecarLifecycleTests|FullyQualifiedName~XivIpc.Tests.SidecarRingSelectionTests|FullyQualifiedName~XivIpc.Tests.TinyIpcRuntimeSettingsTests|FullyQualifiedName~XivIpc.Tests.ProductionPathRingFunctionalTests|FullyQualifiedName~XivIpc.Tests.ProductionPathRingLifecycleTests"` after the `Direct/Brokered/Sidecar` folder reorganization
- [x] `env TINYIPC_ENABLE_MULTIUSER_TESTS=1 TINYIPC_MULTIUSER_SECONDARY_USER=bff14bard01 TINYIPC_SHARED_GROUP=steam dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 -m:1 --filter "FullyQualifiedName~XivIpc.Tests.SidecarMultiUserTests|FullyQualifiedName~XivIpc.Tests.SidecarRingMultiUserTests|FullyQualifiedName~XivIpc.Tests.ProductionPathMultiUserTests|FullyQualifiedName~XivIpc.Tests.ProductionPathRingMultiUserTests"`

### Latest Attempted Validation

- real multi-user validation passed with `TINYIPC_ENABLE_MULTIUSER_TESTS=1`, `TINYIPC_MULTIUSER_SECONDARY_USER=bff14bard01`, and `TINYIPC_SHARED_GROUP=steam`

## Acceptance Criteria

- [x] Only the centralized config layer reads env vars directly.
- [x] Only two top-level bus families remain from the factory's point of view.
- [x] `UnixInMemoryTinyMessageBus` is a thin direct wrapper over pluggable direct storage.
- [x] Direct shared-memory uses an explicit shared-dir metadata discovery contract.
- [x] `UnixSidecarTinyMessageBus` and native host share the same brokered storage abstraction.
- [x] Journal remains the default sidecar storage mode through the shared brokered abstraction.
- [x] No public `TinyIpc.Shim` compatibility break has been introduced in the first pass.

## Assumptions And Defaults

- [x] Pure in-memory remains in the product as a useful local or test mode.
- [x] Real direct IPC is intended to be native-Linux shared memory.
- [x] Sidecar will not use shared-memory storage in this design.
- [x] Shared directory remains the common rendezvous scope across runtimes.
- [x] Existing env vars are preserved unless there is a specific reason to deprecate an alias later.
