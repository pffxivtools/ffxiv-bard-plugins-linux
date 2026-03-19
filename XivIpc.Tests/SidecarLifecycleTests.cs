using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class SidecarLifecycleTests
{
    [Fact]
    public async Task FirstClient_StartsBrokerAndCreatesSocket()
    {
        using TestEnvironmentScope scope = new();

        Assert.False(File.Exists(scope.SocketPath));

        using var bus = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();

        Assert.True(File.Exists(scope.SocketPath));
        Assert.NotEqual(0, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());
    }

    [Fact]
    public async Task ConcurrentClientStartup_ReusesSingleBrokerProcess()
    {
        using TestEnvironmentScope scope = new();

        TinyMessageBus[] buses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => scope.CreateBus())));

        try
        {
            await scope.WaitForLiveSocketAsync();

            int firstPid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
            Assert.NotEqual(0, firstPid);

            using var extraBus = scope.CreateBus();
            int secondPid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();

            Assert.Equal(firstPid, secondPid);
            Assert.True(File.Exists(scope.SocketPath));
        }
        finally
        {
            foreach (TinyMessageBus bus in buses)
                bus.Dispose();
        }
    }

    [Fact]
    public async Task StaleSocket_IsRemovedAndBrokerRestarts()
    {
        using TestEnvironmentScope scope = new();

        Directory.CreateDirectory(scope.SharedDirectory);
        await File.WriteAllTextAsync(scope.SocketPath, "stale");

        using var bus = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        Assert.True(File.Exists(scope.SocketPath));
        Assert.NotEqual(0, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());
    }

    [Fact]
    public async Task LastClientDisconnect_ShutsDownBrokerAndRemovesSocket()
    {
        using TestEnvironmentScope scope = new();

        var publisher = scope.CreateBus();
        var subscriber = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();
        int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
        Assert.NotEqual(0, pid);

        publisher.Dispose();
        subscriber.Dispose();

        await scope.WaitForBrokerShutdownAsync();
    }

    [Fact]
    public async Task RingFile_IsCreatedAndRemovedWithBrokerLifecycle()
    {
        using TestEnvironmentScope scope = new();

        using UnixSidecarProcessManager.Lease lease = UnixSidecarProcessManager.Acquire();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(lease.SocketPath));

        SidecarProtocol.WriteHello(socket, new SidecarHello(
            scope.ChannelName,
            1024,
            Environment.ProcessId,
            HeartbeatIntervalMs: 100,
            HeartbeatTimeoutMs: 5_000));

        SidecarAttachRing attach = SidecarProtocol.ReadAttachRing(socket);
        SidecarFrame ready = SidecarProtocol.ReadFrame(socket);

        Assert.Equal(SidecarFrameType.Ready, ready.Type);
        Assert.True(File.Exists(attach.RingPath));

        socket.Dispose();
        await scope.WaitForBrokerShutdownAsync();

        Assert.False(File.Exists(attach.RingPath));
    }

    [Fact]
    public async Task LargerRequestedCapacity_WhileBrokerLive_Throws()
    {
        using TestEnvironmentScope scope = new();

        using var smaller = scope.CreateBus(maxPayloadBytes: 1024);
        await scope.WaitForLiveSocketAsync();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.CreateBus(maxPayloadBytes: 4096));

        Assert.Contains("payload capacity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrokerRestart_RecreatesRingWithLargerRequestedCapacity()
    {
        using TestEnvironmentScope scope = new();

        using (TinyMessageBus smaller = scope.CreateBus(maxPayloadBytes: 1024))
        {
            await scope.WaitForLiveSocketAsync();
        }

        await scope.WaitForBrokerShutdownAsync();

        using var publisher = scope.CreateBus(maxPayloadBytes: 4096);
        using var subscriber = scope.CreateBus(maxPayloadBytes: 4096);
        await scope.WaitForLiveSocketAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(subscriber, cts.Token);
        await Task.Delay(750, cts.Token);

        byte[] expected = Enumerable.Range(0, 3072).Select(i => (byte)(i % 251)).ToArray();
        await PublishBytesAsync(publisher, expected);

        byte[] actual = await receiveTask;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task HeartbeatTimeout_ShutsDownBrokerAfterIdleClientStopsHeartbeating()
    {
        using TestEnvironmentScope scope = new();

        using UnixSidecarProcessManager.Lease lease = UnixSidecarProcessManager.Acquire();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(lease.SocketPath));

        SidecarProtocol.WriteHello(socket, new SidecarHello(
            scope.ChannelName,
            1024,
            Environment.ProcessId,
            HeartbeatIntervalMs: 100,
            HeartbeatTimeoutMs: 500));

        _ = SidecarProtocol.ReadAttachRing(socket);
        SidecarFrame ready = SidecarProtocol.ReadFrame(socket);

        Assert.Equal(SidecarFrameType.Ready, ready.Type);

        await scope.WaitForBrokerShutdownAsync();
    }

    [Fact]
    public async Task DisposingOneClient_DoesNotInterruptOtherLiveClients()
    {
        using TestEnvironmentScope scope = new();

        using var publisher = scope.CreateBus();
        using var droppedSubscriber = scope.CreateBus();
        using var survivingSubscriber = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();
        int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
        Assert.NotEqual(0, pid);

        droppedSubscriber.Dispose();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(survivingSubscriber, cts.Token);
        await Task.Delay(750, cts.Token);

        byte[] expected = Encoding.UTF8.GetBytes("still-alive");
        await PublishBytesAsync(publisher, expected);

        byte[] actual = await receiveTask;
        Assert.Equal(expected, actual);
        Assert.Equal(pid, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());
        Assert.True(File.Exists(scope.SocketPath));
    }

    [Fact]
    public async Task DifferentChannels_ShareSingleBroker_AndChannelTeardownIsIndependent()
    {
        using TestEnvironmentScope scope = new();

        using var channelAPublisher = scope.CreateBus("channel-a", maxPayloadBytes: 1024);
        using var channelASubscriber = scope.CreateBus("channel-a", maxPayloadBytes: 1024);
        using var channelBPublisher = scope.CreateBus("channel-b", maxPayloadBytes: 1024);
        using var channelBSubscriber = scope.CreateBus("channel-b", maxPayloadBytes: 1024);

        await scope.WaitForLiveSocketAsync();
        int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
        Assert.NotEqual(0, pid);

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            Task<byte[]> receiveTask = ReceiveSingleMessageAsync(channelBSubscriber, cts.Token);
            await Task.Delay(750, cts.Token);
            await PublishBytesAsync(channelBPublisher, Encoding.UTF8.GetBytes("before-recreate"));
            Assert.Equal(Encoding.UTF8.GetBytes("before-recreate"), await receiveTask);
        }

        channelAPublisher.Dispose();
        channelASubscriber.Dispose();

        using var largerChannelAPublisher = await scope.CreateBusWhenAvailableAsync("channel-a", maxPayloadBytes: 4096);
        using var largerChannelASubscriber = await scope.CreateBusWhenAvailableAsync("channel-a", maxPayloadBytes: 4096);

        Assert.Equal(pid, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            byte[] payloadA = Enumerable.Range(0, 3072).Select(i => (byte)(i % 239)).ToArray();
            Task<byte[]> receiveTask = ReceiveSingleMessageAsync(largerChannelASubscriber, cts.Token);
            await Task.Delay(750, cts.Token);
            await PublishBytesAsync(largerChannelAPublisher, payloadA);
            Assert.Equal(payloadA, await receiveTask);
        }

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            Task<byte[]> receiveTask = ReceiveSingleMessageAsync(channelBSubscriber, cts.Token);
            await Task.Delay(750, cts.Token);
            await PublishBytesAsync(channelBPublisher, Encoding.UTF8.GetBytes("after-recreate"));
            Assert.Equal(Encoding.UTF8.GetBytes("after-recreate"), await receiveTask);
        }
    }

    [Fact]
    public async Task RawUdsClient_CanPublishToTinyMessageBusSubscriber()
    {
        using TestEnvironmentScope scope = new();
        using var subscriber = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        using var socket = ConnectRawSocket(scope.SocketPath);
        AttachRawSession(scope.ChannelName, 1024, socket);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(subscriber, cts.Token);
        await Task.Delay(750, cts.Token);

        byte[] payload = Encoding.UTF8.GetBytes("raw-uds-publish");
        SidecarProtocol.WritePublish(socket, payload);

        Assert.Equal(payload, await receiveTask);
    }

    [Fact]
    public async Task AbruptSocketDisconnect_LeavesBrokerOperational()
    {
        using TestEnvironmentScope scope = new();
        using var publisher = scope.CreateBus();
        using var subscriber = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();
        int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
        Assert.NotEqual(0, pid);

        using (Socket socket = ConnectRawSocket(scope.SocketPath))
        {
            _ = AttachRawSession(scope.ChannelName, 1024, socket);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(subscriber, cts.Token);
        await Task.Delay(750, cts.Token);

        byte[] payload = Encoding.UTF8.GetBytes("after-abrupt-disconnect");
        await PublishBytesAsync(publisher, payload);

        Assert.Equal(payload, await receiveTask);
        Assert.Equal(pid, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());
    }

    [Fact]
    public async Task MalformedHello_DoesNotKillBroker_AndGoodClientsStillWork()
    {
        using TestEnvironmentScope scope = new();
        using var publisher = scope.CreateBus();
        using var subscriber = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();
        int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
        Assert.NotEqual(0, pid);

        using (Socket socket = ConnectRawSocket(scope.SocketPath))
        {
            byte[] malformedHello = BuildFrame(SidecarFrameType.Hello, new byte[] { 1, 2, 3 });
            SendAll(socket, malformedHello);
            socket.Shutdown(SocketShutdown.Both);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(subscriber, cts.Token);
        await Task.Delay(750, cts.Token);

        byte[] payload = Encoding.UTF8.GetBytes("after-malformed-hello");
        await PublishBytesAsync(publisher, payload);

        Assert.Equal(payload, await receiveTask);
        Assert.Equal(pid, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());
    }

    [Fact]
    public async Task UnexpectedFrameBeforeHello_DoesNotKillBroker_AndGoodClientsStillWork()
    {
        using TestEnvironmentScope scope = new();
        using var publisher = scope.CreateBus();
        using var subscriber = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();
        int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
        Assert.NotEqual(0, pid);

        using (Socket socket = ConnectRawSocket(scope.SocketPath))
        {
            SidecarProtocol.WritePublish(socket, Encoding.UTF8.GetBytes("not-hello"));
            socket.Shutdown(SocketShutdown.Both);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(subscriber, cts.Token);
        await Task.Delay(750, cts.Token);

        byte[] payload = Encoding.UTF8.GetBytes("after-unexpected-frame");
        await PublishBytesAsync(publisher, payload);

        Assert.Equal(payload, await receiveTask);
        Assert.Equal(pid, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());
    }

    [Fact]
    public async Task UnauthorizedGroup_IsRejected()
    {
        using TestEnvironmentScope scope = new();
        scope.SetSharedGroup("tinyipc-group-that-does-not-exist");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => scope.CreateBus());
        Assert.Contains("exited before socket", ex.Message, StringComparison.OrdinalIgnoreCase);
        await scope.WaitForBrokerShutdownAsync();
    }

    [Fact]
    public async Task BrokerKill_IsRecoveredByNextClient()
    {
        using TestEnvironmentScope scope = new();

        using (TinyMessageBus firstClient = scope.CreateBus())
        {
            await scope.WaitForLiveSocketAsync();
            int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
            Assert.NotEqual(0, pid);

            using Process broker = Process.GetProcessById(pid);
            broker.Kill(entireProcessTree: true);
            broker.WaitForExit();
        }

        _ = UnixSidecarProcessManager.WaitForOwnedProcessExitForDiagnostics(TimeSpan.FromSeconds(5));

        using var publisher = scope.CreateBus();
        using var subscriber = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(subscriber, cts.Token);
        await Task.Delay(750, cts.Token);

        byte[] payload = Encoding.UTF8.GetBytes("after-broker-restart");
        await PublishBytesAsync(publisher, payload);

        Assert.Equal(payload, await receiveTask);
    }

    private static async Task<byte[]> ReceiveSingleMessageAsync(TinyMessageBus bus, CancellationToken cancellationToken)
    {
        await foreach (var item in bus.SubscribeAsync(cancellationToken))
            return GetBytes(item);

        throw new InvalidOperationException("SubscribeAsync completed before receiving a message.");
    }

    private static async Task PublishBytesAsync(TinyMessageBus bus, byte[] bytes)
    {
        MethodInfo[] methods = typeof(TinyMessageBus)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "PublishAsync")
            .ToArray();

        MethodInfo? binaryDataOverload = methods.FirstOrDefault(m =>
        {
            ParameterInfo[] parameters = m.GetParameters();
            return parameters.Length >= 1 && parameters[0].ParameterType == typeof(BinaryData);
        });

        if (binaryDataOverload is not null)
        {
            object?[] args = BuildArguments(binaryDataOverload, new BinaryData(bytes));
            await InvokePublishAsync(bus, binaryDataOverload, args);
            return;
        }

        MethodInfo? byteArrayOverload = methods.FirstOrDefault(m =>
        {
            ParameterInfo[] parameters = m.GetParameters();
            return parameters.Length >= 1 && parameters[0].ParameterType == typeof(byte[]);
        });

        if (byteArrayOverload is not null)
        {
            object?[] args = BuildArguments(byteArrayOverload, bytes);
            await InvokePublishAsync(bus, byteArrayOverload, args);
            return;
        }

        throw new InvalidOperationException("Could not find a supported TinyMessageBus.PublishAsync overload.");
    }

    private static object?[] BuildArguments(MethodInfo method, object firstArgument)
    {
        ParameterInfo[] parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = firstArgument;

        for (int i = 1; i < parameters.Length; i++)
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : GetDefault(parameters[i].ParameterType);

        return args;
    }

    private static async Task InvokePublishAsync(TinyMessageBus bus, MethodInfo method, object?[] args)
    {
        object? result = method.Invoke(bus, args);
        if (result is Task task)
        {
            await task;
            return;
        }

        throw new InvalidOperationException("TinyMessageBus.PublishAsync did not return a Task.");
    }

    private static object? GetDefault(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static byte[] GetBytes(object item)
    {
        if (item is BinaryData data)
            return data.ToArray();

        if (item is TinyMessageReceivedEventArgs e)
            return e.Message is byte[] bytes ? bytes : e.Message.ToArray();

        if (item is byte[] raw)
            return raw;

        if (item is IReadOnlyList<byte> list)
            return list is byte[] arr ? arr : list.ToArray();

        throw new InvalidOperationException($"Unsupported SubscribeAsync payload type: {item.GetType().FullName}");
    }

    private static Socket ConnectRawSocket(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        return socket;
    }

    private static SidecarAttachRing AttachRawSession(string channelName, int maxPayloadBytes, Socket socket)
    {
        SidecarProtocol.WriteHello(socket, new SidecarHello(
            channelName,
            maxPayloadBytes,
            Environment.ProcessId,
            HeartbeatIntervalMs: 100,
            HeartbeatTimeoutMs: 5_000));

        SidecarAttachRing attach = SidecarProtocol.ReadAttachRing(socket);
        SidecarFrame ready = SidecarProtocol.ReadFrame(socket);

        Assert.Equal(SidecarFrameType.Ready, ready.Type);
        return attach;
    }

    private static byte[] BuildFrame(SidecarFrameType type, ReadOnlySpan<byte> payload)
    {
        byte[] buffer = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), 1 + payload.Length);
        buffer[4] = (byte)type;
        payload.CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    private static void SendAll(Socket socket, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int written = socket.Send(buffer, offset, buffer.Length - offset, SocketFlags.None);
            if (written <= 0)
                throw new IOException("Socket send returned no bytes.");

            offset += written;
        }
    }

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly string? _previousBackend;
        private readonly string? _previousHostPath;
        private readonly string? _previousUnixShell;
        private readonly string? _previousSharedDir;
        private readonly string? _previousSharedGroup;

        public TestEnvironmentScope()
        {
            ChannelName = $"xivipc-lifecycle-{Guid.NewGuid():N}";
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-lifecycle-tests", Guid.NewGuid().ToString("N"));
            SocketPath = Path.Combine(SharedDirectory, "tinyipc-sidecar.sock");

            _previousBackend = Environment.GetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND");
            _previousHostPath = Environment.GetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH");
            _previousUnixShell = Environment.GetEnvironmentVariable("TINYIPC_UNIX_SHELL");
            _previousSharedDir = Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");
            _previousSharedGroup = Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP");

            Directory.CreateDirectory(SharedDirectory);
            Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", "sidecar");
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", SharedDirectory);
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", ResolveCurrentSharedGroup());
            Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", UnixSidecarProcessManager.ResolveNativeHostPath());

            if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(_previousUnixShell))
                Environment.SetEnvironmentVariable("TINYIPC_UNIX_SHELL", "/bin/sh");
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }
        public string SocketPath { get; }

        public TinyMessageBus CreateBus(int maxPayloadBytes = 1024)
            => CreateBus(ChannelName, maxPayloadBytes);

        public TinyMessageBus CreateBus(string channelName, int maxPayloadBytes = 1024)
            => new(new TinyMemoryMappedFile(channelName, maxPayloadBytes), disposeFile: true);

        public async Task<TinyMessageBus> CreateBusWhenAvailableAsync(string channelName, int maxPayloadBytes = 1024)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            Exception? last = null;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    return CreateBus(channelName, maxPayloadBytes);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("payload capacity", StringComparison.OrdinalIgnoreCase))
                {
                    last = ex;
                    await Task.Delay(50);
                }
                catch (IOException ex)
                {
                    last = ex;
                    await Task.Delay(50);
                }
            }

            throw new TimeoutException($"Timed out waiting to recreate brokered channel '{channelName}' with payload capacity {maxPayloadBytes}.", last);
        }

        public void SetSharedGroup(string groupName)
            => Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", groupName);

        public async Task WaitForLiveSocketAsync()
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            Exception? last = null;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(SocketPath))
                    {
                        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"Timed out waiting for live broker socket '{SocketPath}'.", last);
        }

        public async Task WaitForSocketRemovedAsync()
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (!File.Exists(SocketPath))
                    return;

                await Task.Delay(50);
            }

            Assert.False(File.Exists(SocketPath), $"Expected broker socket '{SocketPath}' to be removed.");
        }

        public async Task WaitForBrokerShutdownAsync()
        {
            await WaitForSocketRemovedAsync();

            if (UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics() != 0)
                _ = UnixSidecarProcessManager.WaitForOwnedProcessExitForDiagnostics(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", _previousBackend);
            Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", _previousHostPath);
            Environment.SetEnvironmentVariable("TINYIPC_UNIX_SHELL", _previousUnixShell);
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", _previousSharedDir);
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", _previousSharedGroup);

            try
            {
                if (Directory.Exists(SharedDirectory))
                    Directory.Delete(SharedDirectory, recursive: true);
            }
            catch
            {
            }
        }

        private static string ResolveCurrentSharedGroup()
        {
            using var process = Process.Start(new ProcessStartInfo("id", "-gn")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Failed to start 'id -gn' to resolve the shared group.");

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("Failed to resolve the current user's primary group for sidecar tests.");

            return output;
        }
    }
}
