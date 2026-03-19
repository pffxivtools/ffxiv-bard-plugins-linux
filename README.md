# TinyIpc.Shim

This workspace was restructured into a single-backend solution:

- `TinyIpc.Shim` — public TinyIpc-compatible facade
- `XivIpc` — internal sidecar + shared-file memory-mapped-file implementation
- `XivIpc.NativeHost` — native host sidecar
- `XivIpc.Tests` — functional tests centered on the sidecar/shared-file path

Notable simplifications:
- Removed backend switching and upstream TinyIpc alias references.
- `TinyMemoryMappedFile` always uses the shared-file implementation.
- `TinyMessageBus` always uses the sidecar transport.
- Permission application now skips Windows/Wine processes to avoid `libc` P/Invoke failures in Wine.
