# XivIpc.NativeHost

`XivIpc.NativeHost` is the native Linux broker process used by the sidecar transport.

## What It Is For

Clients do not exchange payloads directly. They connect to this host over a Unix socket so one broker can own channel state, brokered storage files, and session accounting for a shared directory.

## How It Works

- On startup the host normalizes its environment, resolves shared-scope and broker-runtime settings, ensures broker-access permissions are configured, creates the socket directory, and writes a broker state file.
- Each client performs a sidecar handshake. The broker authorizes the peer, creates or reuses brokered storage, and replies with the attachment data needed by the client.
- After attachment, the broker processes publish, heartbeat, and lifecycle frames for each session.
- The broker tracks channels and sessions in memory, updates the state snapshot, and shuts itself down when the session set becomes empty.
- `journal` is the default brokered production storage mode. `ring` is an alternate bounded retained mode.
- Socket, broker-storage, and state-file ownership are tied to the broker instance so stale artifacts can be cleaned up safely.

## Main Entry Points

- `Program.cs`
- `../XivIpc/Messaging/SidecarProtocol.cs`
- `../XivIpc/Messaging/BrokerStateFile.cs`
