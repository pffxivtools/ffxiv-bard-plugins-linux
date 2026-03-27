# Dead Code Analysis And Cleanup Plan

## Goal

This document turns the initial dead-code review into an execution plan for `XivIpc`.

The intended first pass is conservative:

- keep the public `TinyIpc.Shim` compatibility surface stable
- avoid speculative removals in the production sidecar path
- remove only code that is strongly indicated to be stale or orphaned
- update stale docs alongside any confirmed cleanup so the repo stops describing old behavior as current

## Repo Facts That Drive The Plan

- The current production broker path is the journal-backed sidecar implementation, not the older brokered ring implementation.
- The supported direct fallback path is `UnixInMemoryTinyMessageBus`, selected from `TinyIpc.Shim/Internal/UnixMessageBusBackendFactory.cs`.
- The repo still contains an older POSIX shared-memory direct bus stack (`UnixSharedMemoryTinyMessageBus` and related types), but the current backend factory does not instantiate it.
- `TinyIpc.Shim` intentionally preserves Windows/TinyIpc-shaped constructors and methods that throw `PlatformNotSupportedException` on Unix. Those are compatibility surface, not dead code by default.
- The `ProductionPath*` test classes are thin wrappers over the parameterized suites, but they provide stable test names for the documented "production-auto" path and should not be treated as obvious duplication.

## Review Rules

Use these rules when deciding whether to remove something:

1. `Remove` only when there is repo-local proof that the code is not reachable or no longer selected.
2. `Keep` when the code is active, compatibility-critical, or a deliberately preserved alternative primitive.
3. `Defer` when removal would require a product decision, a public API break, or a "future direction" call rather than a code-health call.

## Inventory And Recommendations

### Remove

| Area | Files | Recommendation | Why |
| --- | --- | --- | --- |
| Orphaned POSIX shared-memory direct bus stack | `XivIpc/Messaging/UnixSharedMemoryTinyMessageBus.cs`, `XivIpc/Messaging/UnixSharedMemoryRegion.cs`, `XivIpc/Messaging/UnixSharedMemoryBackendUnavailableException.cs`, `XivIpc/Messaging/LinuxFutex.cs`, `XivIpc/Internal/RingSizingPolicy.cs`, `XivIpc.Tests/RingSizingPolicyTests.cs` | Remove in the first cleanup wave | This stack is not selected by `UnixMessageBusBackendFactory`. The active direct path is `UnixInMemoryTinyMessageBus`. The shared-memory stack has no direct tests and appears to be a stranded implementation branch rather than a maintained mode. `RingSizingPolicy` is only retained to support this stack and its dedicated tests. |
| Stale backend/docs references | `docs/DEVELOPING.md`, `XivIpc/README.md` | Remove or rewrite as part of the same patch | `docs/DEVELOPING.md` still lists `TINYIPC_MESSAGE_BUS_BACKEND=...|inmemory|...`, but current code normalizes `shared-memory`, `direct`, and `sidecar`; it does not expose `inmemory` as a current documented value. `XivIpc/README.md` still describes `BrokeredChannelRing` as the active transport primitive even though the production broker path is now journal-backed. |

### Keep

| Area | Files | Recommendation | Why |
| --- | --- | --- | --- |
| Journal-backed sidecar transport | `XivIpc/Messaging/BrokeredChannelJournal.cs`, `XivIpc/Internal/JournalSizingPolicy.cs`, `XivIpc/Messaging/UnixSidecarTinyMessageBus.cs`, `XivIpc.NativeHost/Program.cs`, sidecar protocol and journal tests | Keep | This is the current production path. It is directly exercised by the functional, lifecycle, multi-user, and runtime integration suites. |
| Brokered ring primitive | `XivIpc/Messaging/BrokeredChannelRing.cs` | Keep for now | This code is not the current broker implementation, but it is a coherent primitive rather than random residue. It is a reasonable candidate to preserve for future transport experiments or alternate retention policies. This matches the "keep even if not currently selected" category you called out. |
| Direct in-memory fallback | `XivIpc/Messaging/UnixInMemoryTinyMessageBus.cs`, `XivIpc.Tests/UnixInMemoryTinyMessageBusTests.cs`, `TinyIpc.Shim/Internal/UnixMessageBusBackendFactory.cs` | Keep | This is the active non-sidecar fallback path. It is selected by the factory and heavily tested. |
| Safe disabled fallback bus | `TinyIpc.Shim/Internal/DisabledShimMessageBus.cs` | Keep | This is not dead code. It is the compatibility-preserving failure mode used when backend startup fails. |
| Compatibility constructors and unsupported Windows-shape entry points | `TinyIpc.Shim/IO/TinyMemoryMappedFile.cs`, `TinyIpc.Shim/Synchronization/TinyReadWriteLock.cs`, `TinyIpc.Shim/Messaging/TinyMessageBus.cs` | Keep | Many overloads look unused locally, and some no-op logger parameters are intentionally ignored, but they preserve TinyIpc-shaped call sites. Removing them in a cleanup pass would silently turn internal cleanup into API surgery. |
| Process stamp, logging, runtime helpers | `XivIpc/Internal/TinyIpcProcessStamp.cs`, `XivIpc/Internal/TinyIpcLogger.cs`, `XivIpc/Internal/RuntimeEnvironmentDetector.cs` | Keep | These are used in startup, diagnostics, and test validation. They are utility code, but not dead utility code. |
| ProductionPath wrapper suites | `XivIpc.Tests/ProductionPathFunctionalTests.cs`, `XivIpc.Tests/ProductionPathParallelTests.cs`, `XivIpc.Tests/ProductionPathLifecycleTests.cs`, `XivIpc.Tests/ProductionPathMultiUserTests.cs`, `XivIpc.Tests/ProductionPathRuntimeIntegrationTests.cs`, `XivIpc.Tests/ProductionPathStartupTests.cs` | Keep | They intentionally give the repo stable "production-auto" entry points and isolate the published-host staging contract. They are wrappers, but useful wrappers. |

### Defer

| Area | Files | Recommendation | Why |
| --- | --- | --- | --- |
| Public-but-effectively-internal `XivIpc` surface | `XivIpc/Messaging/ChannelInfo.cs`, `XivIpc/Messaging/UnixSidecarTinyMessageBus.cs`, `XivIpc/Messaging/XivMessageReceivedEventArgs.cs` | Defer to a breaking-change pass | These types are public in `XivIpc`, but repo-local usage is mostly internal or via `TinyIpc.Shim`. They may be good candidates for internalization or package-surface trimming later, but not in a non-breaking cleanup pass. |
| Whether to retire the brokered ring primitive later | `XivIpc/Messaging/BrokeredChannelRing.cs`, `XivIpc/README.md` | Defer after product direction is explicit | If the project decides it will never use a broker-owned ring again, remove it later. Until then, treat it as a deliberately retained alternative primitive and update docs so it is not described as the current production path. |
| Backend alias normalization | `TinyIpc.Shim/Internal/UnixMessageBusBackendFactory.cs`, `XivIpc/IO/UnixTinyMemoryMappedFile.cs` | Defer | Aliases such as `shared-memory`, `sharedmemory`, and `shm` may be there for backward compatibility even if the implementation now routes to the in-memory direct path. Cleaning these up should be a separate env-var contract decision. |

## Recommended First Cleanup Wave

### Patch 1: Remove confirmed stale code

Remove the orphaned POSIX shared-memory stack:

- `XivIpc/Messaging/UnixSharedMemoryTinyMessageBus.cs`
- `XivIpc/Messaging/UnixSharedMemoryRegion.cs`
- `XivIpc/Messaging/UnixSharedMemoryBackendUnavailableException.cs`
- `XivIpc/Messaging/LinuxFutex.cs`
- `XivIpc/Internal/RingSizingPolicy.cs`
- `XivIpc.Tests/RingSizingPolicyTests.cs`

Then adjust any now-unused imports or references that disappear because of that removal.

### Patch 2: Align docs with the actual architecture

Update docs so they stop preserving stale architecture statements:

- in `docs/DEVELOPING.md`, document backend values that are actually supported today
- in `XivIpc/README.md`, describe the journal-backed sidecar path as the active broker transport
- if `BrokeredChannelRing` is kept, describe it as an alternate or retained primitive, not the current production implementation

## Items To Explicitly Avoid In This Pass

- Do not remove `TinyIpc.Shim` overloads just because logger parameters are unused or constructors throw on Unix.
- Do not remove the direct in-memory backend; it is still the current fallback.
- Do not internalize or delete public `XivIpc` types in this pass.
- Do not add analyzer/build-policy changes as part of the cleanup. Keep this pass focused on code and docs.

## Verification

After the first cleanup wave, run:

```bash
dotnet build TinyIpc.Shim.sln -v minimal
```

```bash
dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 \
  --filter "FullyQualifiedName~XivIpc.Tests.FunctionalTests|FullyQualifiedName~XivIpc.Tests.ParallelFunctionalTests|FullyQualifiedName~XivIpc.Tests.SidecarLifecycleTests|FullyQualifiedName~XivIpc.Tests.SidecarMultiUserTests"
```

Recommended targeted spot checks after the removal patch:

- `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.ProductionPathStartupTests"`
- `dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 --filter "FullyQualifiedName~XivIpc.Tests.UnixInMemoryTinyMessageBusTests|FullyQualifiedName~XivIpc.Tests.BrokeredChannelJournalTests|FullyQualifiedName~XivIpc.Tests.BrokeredChannelJournalConcurrencyTests"`

## Follow-Up Passes

If the first cleanup lands cleanly, the next pass should review:

1. whether `XivIpc` should expose `ChannelInfo`, `UnixSidecarTinyMessageBus`, and `XivMessageReceivedEventArgs` publicly at all
2. whether backend alias compatibility should be simplified
3. whether the retained brokered ring primitive should stay as a future-use transport building block or be retired later
