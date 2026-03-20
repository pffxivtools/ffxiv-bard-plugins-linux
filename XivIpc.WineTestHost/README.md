# XivIpc.WineTestHost

`XivIpc.WineTestHost` is a small command-line client used by the runtime integration tests under Wine and Proton.

## What It Is For

The test suite needs a Windows-process client that exercises the public shim from outside the native Linux test process. This host provides that executable surface.

## How It Works

- Commands such as `connect`, `hold`, `publish`, and `subscribe-once` create a `TinyMessageBus` over a named `TinyMemoryMappedFile`.
- The process reports simple line-oriented markers like `CONNECTED`, `DISPOSED`, `PUBLISHED`, and `MESSAGE:<base64>` so the native test harness can drive it and assert outcomes.
- `TINYIPC_TEST_MAX_PAYLOAD_BYTES` controls the bus sizing used by the helper.
- The host is intentionally minimal: it exists to prove the runtime contract, not to contain transport logic.

## Main Entry Points

- `Program.cs`
- `../scripts/run-wine-test-host.sh`
- `../scripts/run-proton-test-host.sh`
