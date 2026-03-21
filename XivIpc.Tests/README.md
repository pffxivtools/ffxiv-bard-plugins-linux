# XivIpc.Tests

`XivIpc.Tests` covers the production path end to end: direct shared-file access, brokered sidecar behavior, lifecycle edge cases, multi-user permissions, and Wine/Proton runtime integration.

## What It Is For

This project verifies that the public shim and the internal transport behave correctly in realistic Linux environments.

## How It Works

- `TestEnvironmentScope` and `ProductionPathTestEnvironment` create isolated shared directories, staged host payloads, and runtime-specific environment variables.
- Functional tests cover normal publish/subscribe and shared-file behavior.
- Lifecycle tests cover broker startup, singleton reuse, stale artifact replacement, shutdown, and leaked-session behavior.
- Multi-user and runtime integration tests validate group-based access plus cross-runtime communication with the Wine test host.
- Log assertions and state-file assertions are used heavily so tests verify the launch contract as well as message delivery.

## Optional Stress Coverage

The broker journal stress coverage is opt-in so normal CI stays deterministic and bounded.

Run it with:

```bash
TINYIPC_ENABLE_JOURNAL_STRESS_TESTS=1 \
dotnet test XivIpc.Tests/XivIpc.Tests.csproj -v minimal --framework net9.0 \
  --filter "FullyQualifiedName~XivIpc.Tests.BrokeredChannelJournalStressTests"
```

## Main Entry Points

- `FunctionalTests.cs`
- `SidecarLifecycleTests.cs`
- `SidecarMultiUserTests.cs`
- `SidecarRuntimeIntegrationTests.cs`
- `ProductionPathTestEnvironment.cs`
