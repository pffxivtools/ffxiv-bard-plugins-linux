using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class SidecarRingSelectionTests
{
    private const string BarrierPrefix = "__tinyipc_ring_test_barrier__:";

    [Fact]
    public async Task TinyMessageBus_SidecarRingStorage_DeliversMessages()
    {
        using RingTestScope scope = new();
        using var publisher = scope.CreateBus();
        using var subscriber = scope.CreateBus();

        try
        {
            await publisher.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(10));
            await subscriber.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for sidecar ring buses to connect. Logs:{Environment.NewLine}{scope.ReadAllLogsOrEmpty()}{Environment.NewLine}{ex}");
        }

        var observed = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.MessageReceived += (_, args) =>
        {
            string message = Encoding.UTF8.GetString(args.BinaryData.ToArray());

            if (message.StartsWith(BarrierPrefix, StringComparison.Ordinal))
            {
                ready.TrySetResult();
                return;
            }

            observed.Enqueue(message);
            if (observed.Count >= 3)
                received.TrySetResult();
        };

        while (!ready.Task.IsCompleted)
        {
            await publisher.PublishAsync(Encoding.UTF8.GetBytes($"{BarrierPrefix}{Guid.NewGuid():N}"));
            await Task.WhenAny(ready.Task, Task.Delay(100));
        }

        await publisher.PublishAsync(Encoding.UTF8.GetBytes("a"));
        await publisher.PublishAsync(Encoding.UTF8.GetBytes("b"));
        await publisher.PublishAsync(Encoding.UTF8.GetBytes("c"));

        try
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for sidecar ring messages. Logs:{Environment.NewLine}{scope.ReadAllLogsOrEmpty()}{Environment.NewLine}{ex}");
        }

        Assert.Equal(new[] { "a", "b", "c" }, observed.ToArray());
    }

    [Fact]
    public async Task TinyMessageBus_SidecarRingStorage_RemovesRingFileAfterLastClientDisconnect()
    {
        using RingTestScope scope = new();
        using var publisher = scope.CreateBus();
        using var subscriber = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();
        await WaitForConnectedAsync([publisher, subscriber], TimeSpan.FromSeconds(10));

        using Socket socket = scope.ConnectRawSocket();
        SidecarAttachRing attach = AttachRawSession(scope.ChannelName, 4096, socket);
        string ringPath = attach.RingPath;

        Assert.True(File.Exists(ringPath));

        socket.Dispose();
        subscriber.Dispose();
        publisher.Dispose();

        await scope.WaitForBrokerShutdownAsync();
        await scope.WaitForFileRemovedAsync(ringPath);
    }

    [Fact]
    public async Task TinyMessageBus_SidecarRingStorage_RejectsLargerCapacityWhileBrokerLive()
    {
        using RingTestScope scope = new();
        using var smaller = scope.CreateBus(maxPayloadBytes: 64 * 1024);

        await scope.WaitForLiveSocketAsync();
        await smaller.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(10));

        using Socket socket = scope.ConnectRawSocket();
        SidecarProtocol.WriteHello(socket, new SidecarHello(
            scope.ChannelName,
            128 * 1024,
            Environment.ProcessId,
            HeartbeatIntervalMs: 2_000,
            HeartbeatTimeoutMs: 60_000));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SidecarProtocol.ReadAttachRing(socket));
        Assert.Contains("smaller than requested", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TinyMessageBus_SidecarRingStorage_HeartbeatTimeout_ShutsDownBrokerAfterIdleClientStopsHeartbeating()
    {
        using RingTestScope scope = new();
        using UnixSidecarProcessManager.Lease lease = UnixSidecarProcessManager.Acquire();
        using Socket socket = scope.ConnectRawSocket(lease.SocketPath);

        SidecarProtocol.WriteHello(socket, new SidecarHello(
            scope.ChannelName,
            4096,
            Environment.ProcessId,
            HeartbeatIntervalMs: 100,
            HeartbeatTimeoutMs: 500));

        _ = SidecarProtocol.ReadAttachRing(socket);
        SidecarFrame ready = SidecarProtocol.ReadFrame(socket);

        Assert.Equal(SidecarFrameType.Ready, ready.Type);
        await scope.WaitForBrokerStateAsync(static state => state.SessionCount == 1);
        await scope.WaitForBrokerShutdownAsync();
    }

    [Fact]
    public async Task TinyMessageBus_SidecarRingStorage_StaleSocket_IsRemovedAndBrokerRestarts()
    {
        using RingTestScope scope = new();

        Directory.CreateDirectory(scope.SharedDirectory);
        await File.WriteAllTextAsync(scope.SocketPath, "stale");

        using var bus = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();
        await bus.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TinyMessageBus_SidecarRingStorage_BrokerKill_IsRecoveredByNextClient()
    {
        using RingTestScope scope = new();

        using UnixSidecarProcessManager.Lease starter = UnixSidecarProcessManager.Acquire();
        await scope.WaitForLiveSocketAsync();
        BrokerStateSnapshot liveState = await scope.WaitForBrokerStateAsync(static state => state.SessionCount == 0);
        int pid = liveState.Pid;
        Assert.NotEqual(0, pid);

        using (Process broker = Process.GetProcessById(pid))
        {
            broker.Kill(entireProcessTree: true);
            broker.WaitForExit();
        }

        using var publisher = scope.CreateBus();
        using var subscriber = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();
        await WaitForConnectedAsync([publisher, subscriber], TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(subscriber, cts.Token);
        await Task.Delay(750, cts.Token);

        byte[] payload = Encoding.UTF8.GetBytes("after-broker-restart");
        await publisher.PublishAsync(payload);

        Assert.Equal(payload, await receiveTask);
    }

    private static SidecarAttachRing AttachRawSession(string channelName, int maxPayloadBytes, Socket socket)
    {
        SidecarProtocol.WriteHello(socket, new SidecarHello(
            channelName,
            maxPayloadBytes,
            Environment.ProcessId,
            HeartbeatIntervalMs: 2_000,
            HeartbeatTimeoutMs: 60_000));

        SidecarAttachRing attach = SidecarProtocol.ReadAttachRing(socket);
        SidecarFrame ready = SidecarProtocol.ReadFrame(socket);
        Assert.Equal(SidecarFrameType.Ready, ready.Type);
        return attach;
    }

    private static string ResolveCurrentBuildNativeHostPath()
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net9.0", "XivIpc.NativeHost")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net9.0", "XivIpc.NativeHost.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net9.0", "XivIpc.NativeHost")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net9.0", "XivIpc.NativeHost.dll"))
        ];

        string? existing = candidates.FirstOrDefault(File.Exists);
        return existing ?? throw new InvalidOperationException("Could not resolve the current XivIpc.NativeHost build output for the sidecar ring selection test.");
    }

    private static async Task WaitForConnectedAsync(IEnumerable<TinyMessageBus> buses, TimeSpan timeout)
    {
        foreach (TinyMessageBus bus in buses)
            await bus.WaitForConnectedForDiagnosticsAsync(timeout);
    }

    private static async Task<byte[]> ReceiveSingleMessageAsync(TinyMessageBus bus, CancellationToken cancellationToken)
    {
        await foreach (var item in bus.SubscribeAsync(cancellationToken))
            return GetBytes(item);

        throw new InvalidOperationException("SubscribeAsync completed before receiving a message.");
    }

    private static byte[] GetBytes(object item)
        => item switch
        {
            byte[] bytes => bytes,
            ArraySegment<byte> segment => segment.ToArray(),
            ReadOnlyMemory<byte> memory => memory.ToArray(),
            BinaryData data => data.ToArray(),
            _ => throw new InvalidOperationException($"Unsupported payload type '{item.GetType()}'.")
        };

    private sealed class RingTestScope : IDisposable
    {
        private readonly IDisposable _overrides;

        public RingTestScope()
        {
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-sidecar-ring-tests", Guid.NewGuid().ToString("N"));
            ChannelName = $"xivipc-sidecar-ring-{Guid.NewGuid():N}";
            SocketPath = Path.Combine(SharedDirectory, "tinyipc-sidecar.sock");
            StatePath = Path.Combine(SharedDirectory, "tinyipc-sidecar.state.json");
            SharedGroup = ProductionPathTestEnvironment.ResolveSharedGroup();

            Directory.CreateDirectory(SharedDirectory);

            _overrides = TinyIpcEnvironment.Override(
                (TinyIpcEnvironment.MessageBusBackend, "sidecar"),
                (TinyIpcEnvironment.SidecarStorageMode, "ring"),
                (TinyIpcEnvironment.SharedDirectory, SharedDirectory),
                (TinyIpcEnvironment.SharedGroup, SharedGroup),
                (TinyIpcEnvironment.NativeHostPath, ResolveCurrentBuildNativeHostPath()),
                (TinyIpcEnvironment.EnableLogging, "1"),
                (TinyIpcEnvironment.LogDirectory, SharedDirectory),
                (TinyIpcEnvironment.LogLevel, "debug"),
                (TinyIpcEnvironment.SlotCount, "64"),
                (TinyIpcEnvironment.MessageTtlMs, "30000"),
                (TinyIpcEnvironment.BrokerIdleShutdownMs, "2000"),
                (TinyIpcEnvironment.UnixShell, "/bin/sh"));
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }
        public string SharedGroup { get; }
        public string SocketPath { get; }
        public string StatePath { get; }

        public TinyMessageBus CreateBus(int maxPayloadBytes = 4096)
            => CreateBus(ChannelName, maxPayloadBytes);

        public TinyMessageBus CreateBus(string channelName, int maxPayloadBytes = 4096)
            => new(new TinyMemoryMappedFile(channelName, maxPayloadBytes), disposeFile: true);

        public Socket ConnectRawSocket(string? socketPath = null)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath ?? SocketPath));
            return socket;
        }

        public string ReadAllLogsOrEmpty()
        {
            if (!Directory.Exists(SharedDirectory))
                return string.Empty;

            return string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(SharedDirectory, "tinyipc-*.log", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .Select(static path => File.ReadAllText(path)));
        }

        public async Task WaitForLiveSocketAsync(TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(SocketPath))
                    return;

                await Task.Delay(50);
            }

            throw new TimeoutException($"Timed out waiting for broker socket '{SocketPath}'. Logs:{Environment.NewLine}{ReadAllLogsOrEmpty()}");
        }

        public async Task<BrokerStateSnapshot> WaitForBrokerStateAsync(Func<BrokerStateSnapshot, bool> predicate, TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline)
            {
                BrokerStateSnapshot? state = BrokerStateSnapshot.TryRead(StatePath);
                if (state is not null && predicate(state))
                    return state;

                await Task.Delay(50);
            }

            throw new TimeoutException($"Timed out waiting for broker state predicate. Logs:{Environment.NewLine}{ReadAllLogsOrEmpty()}");
        }

        public async Task WaitForBrokerShutdownAsync(TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline)
            {
                if (!File.Exists(SocketPath) && BrokerStateSnapshot.TryRead(StatePath) is null)
                    return;

                await Task.Delay(50);
            }

            throw new TimeoutException($"Timed out waiting for broker shutdown. Logs:{Environment.NewLine}{ReadAllLogsOrEmpty()}");
        }

        public async Task WaitForFileRemovedAsync(string path, TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline)
            {
                if (!File.Exists(path))
                    return;

                await Task.Delay(50);
            }

            throw new TimeoutException($"Timed out waiting for file removal: {path}. Logs:{Environment.NewLine}{ReadAllLogsOrEmpty()}");
        }

        public void Dispose()
        {
            _overrides.Dispose();

            BrokerStateSnapshot? state = BrokerStateSnapshot.TryRead(StatePath);
            if (state is not null)
            {
                try
                {
                    using Process process = Process.GetProcessById(state.Pid);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5_000);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
