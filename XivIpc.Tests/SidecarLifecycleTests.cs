using System.Buffers.Binary;
using System.Collections.Concurrent;
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
    public static IEnumerable<object[]> Modes()
    {
        yield return new object[] { "sidecar" };
    }

    public static IEnumerable<object[]> SidecarOnlyModes()
    {
        yield return new object[] { "sidecar" };
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task FirstClient_StartsBrokerAndCreatesSocket(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        Assert.False(File.Exists(scope.SocketPath));
        Assert.False(File.Exists(scope.StatePath));

        using var bus = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();
        await scope.WaitForBrokerStateAsync(state => state.SessionCount == 1);

        Assert.True(File.Exists(scope.SocketPath));
        Assert.True(File.Exists(scope.StatePath));
        Assert.NotEqual(0, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ConcurrentClientStartup_ReusesSingleBrokerProcess(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

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

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Acquire_WhenBrokerAlreadyRunning_DoesNotSpawnReplacement(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        using UnixSidecarProcessManager.Lease firstLease = UnixSidecarProcessManager.Acquire();
        await scope.WaitForLiveSocketAsync();
        BrokerStateSnapshot firstState = await scope.WaitForBrokerStateAsync(static state => state.SessionCount == 0);

        using UnixSidecarProcessManager.Lease secondLease = UnixSidecarProcessManager.Acquire();
        BrokerStateSnapshot secondState = await scope.WaitForBrokerStateAsync(static state => state.SessionCount == 0);

        Assert.Equal(firstLease.SocketPath, secondLease.SocketPath);
        Assert.Equal(firstState.Pid, secondState.Pid);
        Assert.Equal(firstState.InstanceId, secondState.InstanceId);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task StaleSocket_IsRemovedAndBrokerRestarts(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        Directory.CreateDirectory(scope.SharedDirectory);
        await File.WriteAllTextAsync(scope.SocketPath, "stale");

        using var bus = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        Assert.True(File.Exists(scope.SocketPath));
        Assert.NotEqual(0, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task LastClientDisconnect_ShutsDownBrokerAndRemovesSocket(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        var publisher = scope.CreateBus();
        var subscriber = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();
        int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
        Assert.NotEqual(0, pid);

        publisher.Dispose();
        subscriber.Dispose();

        await scope.WaitForBrokerShutdownAsync();
        Assert.False(File.Exists(scope.StatePath));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task DisposeSingleClient_RemovesSessionAndShutsDownBroker(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        var bus = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();
        BrokerStateSnapshot liveState = await scope.WaitForBrokerStateAsync(state => state.SessionCount == 1);

        bus.Dispose();

        await scope.WaitForBrokerShutdownAsync();
        Assert.False(File.Exists(scope.StatePath));
        Assert.NotEqual(0, liveState.Pid);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task DeletingSocketWhileBrokerAlive_ReplacesBrokerWithoutParallelHosts(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        using var bus = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();
        BrokerStateSnapshot before = await scope.WaitForBrokerStateAsync(state => state.SessionCount == 1);

        File.Delete(scope.SocketPath);

        using var replacementClient = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();
        BrokerStateSnapshot after = await scope.WaitForBrokerStateAsync(state => state.SessionCount >= 1 && state.InstanceId != before.InstanceId);

        Assert.NotEqual(before.InstanceId, after.InstanceId);
        Assert.NotEqual(before.Pid, after.Pid);
        scope.AssertProcessExited(before.Pid);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task DeletingBrokerStateWhileBrokerAlive_DoesNotSpawnReplacementBroker(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        using var first = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();
        BrokerStateSnapshot before = await scope.WaitForBrokerStateAsync(state => state.SessionCount == 1);

        File.Delete(scope.StatePath);
        Assert.False(File.Exists(scope.StatePath));

        using var second = scope.CreateBus();
        BrokerStateSnapshot after = await scope.WaitForBrokerStateAsync(state => state.SessionCount == 2);

        Assert.Equal(before.Pid, after.Pid);
        Assert.Equal(before.InstanceId, after.InstanceId);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task LeakedRawSession_KeepsBrokerAlive_AfterManagedClientDisposes(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        using var managedClient = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        using Socket leakedSocket = ConnectRawSocket(scope.SocketPath);
        _ = AttachRawSession(scope.ChannelName, 1024, leakedSocket);
        await scope.WaitForBrokerStateAsync(state => state.SessionCount == 2);

        managedClient.Dispose();
        BrokerStateSnapshot stillLive = await scope.WaitForBrokerStateAsync(state => state.SessionCount == 1);

        Assert.True(File.Exists(scope.SocketPath));
        Assert.NotEqual(0, stillLive.Pid);

        leakedSocket.Dispose();
        await scope.WaitForBrokerShutdownAsync();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task RingFile_IsCreatedAndRemovedWithBrokerLifecycle(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

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

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task BrokerWithoutClients_DoesNotExitWithinLegacyTwoSecondWindow(string mode)
    {
        using TestEnvironmentScope scope = new(mode, brokerIdleShutdownMs: null);

        using UnixSidecarProcessManager.Lease lease = UnixSidecarProcessManager.Acquire();
        await scope.WaitForLiveSocketAsync();
        BrokerStateSnapshot state = await scope.WaitForBrokerStateAsync(static snapshot => snapshot.SessionCount == 0);

        await Task.Delay(TimeSpan.FromSeconds(3));

        BrokerStateSnapshot? stillLive = BrokerStateSnapshot.TryRead(scope.StatePath);
        Assert.NotNull(stillLive);
        Assert.Equal(state.Pid, stillLive!.Pid);
        Assert.True(File.Exists(scope.SocketPath));

        scope.KillBrokerProcess(stillLive.Pid);
        scope.AssertProcessExited(stillLive.Pid);
    }

    [Theory]
    [MemberData(nameof(SidecarOnlyModes))]
    public async Task LargerRequestedCapacity_WhileBrokerLive_Throws(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        using var smaller = scope.CreateBus(maxPayloadBytes: 1024);
        await scope.WaitForLiveSocketAsync();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.CreateBus(maxPayloadBytes: 4096));

        Assert.Contains("payload capacity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SidecarOnlyModes))]
    public async Task BrokerRestart_RecreatesRingWithLargerRequestedCapacity(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

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

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task HeartbeatTimeout_ShutsDownBrokerAfterIdleClientStopsHeartbeating(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

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
        await scope.WaitForBrokerStateAsync(state => state.SessionCount == 1);

        await scope.WaitForBrokerShutdownAsync();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task DisposingOneClient_DoesNotInterruptOtherLiveClients(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

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

    [Theory]
    [MemberData(nameof(SidecarOnlyModes))]
    public async Task DifferentChannels_ShareSingleBroker_AndChannelTeardownIsIndependent(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

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

    [Theory]
    [MemberData(nameof(SidecarOnlyModes))]
    public async Task FiveChannels_CreateDistinctRingFiles_AndChannelTeardownIsIndependent(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
        using UnixSidecarProcessManager.Lease lease = UnixSidecarProcessManager.Acquire();
        const int requestedBufferBytes = 64 * 1024;
        const int recreatedRequestedBufferBytes = 96 * 1024;

        string[] channelNames = Enumerable.Range(0, 5)
            .Select(index => $"channel-{index}")
            .ToArray();

        Socket[] sockets = channelNames
            .Select(_ => ConnectRawSocket(lease.SocketPath))
            .ToArray();

        try
        {
            SidecarAttachRing[] attaches = channelNames
                .Select((channelName, index) => AttachRawSession(channelName, requestedBufferBytes, sockets[index]))
                .ToArray();

            await scope.WaitForLiveSocketAsync();
            BrokerStateSnapshot liveState = await scope.WaitForBrokerStateAsync(state => state.SessionCount == channelNames.Length);
            Assert.NotEqual(0, liveState.Pid);

            string[] ringPaths = attaches.Select(attach => attach.RingPath).ToArray();
            Assert.Equal(channelNames.Length, ringPaths.Length);
            Assert.Equal(channelNames.Length, ringPaths.Distinct(StringComparer.Ordinal).Count());

            foreach (string ringPath in ringPaths)
            {
                Assert.True(File.Exists(ringPath));
                Assert.Equal($"{scope.SharedGroup} 660", GetStat(ringPath, "%G %a"));
            }

            sockets[0].Dispose();
            await WaitForFileRemovedAsync(ringPaths[0]);

            for (int index = 1; index < ringPaths.Length; index++)
                Assert.True(File.Exists(ringPaths[index]));

            using var recreatedSocket = ConnectRawSocket(lease.SocketPath);
            SidecarAttachRing recreatedAttach = AttachRawSession(channelNames[0], recreatedRequestedBufferBytes, recreatedSocket);

            Assert.True(File.Exists(recreatedAttach.RingPath));
            Assert.DoesNotContain(recreatedAttach.RingPath, ringPaths.Skip(1));
        }
        finally
        {
            foreach (Socket socket in sockets)
            {
                try
                {
                    socket.Dispose();
                }
                catch
                {
                }
            }

            await scope.WaitForBrokerShutdownAsync();
        }
    }

    [Theory]
    [MemberData(nameof(SidecarOnlyModes))]
    public async Task FiveChannels_ParallelTraffic_HasNoCrosstalk_AndPreservesPerChannelOrder(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        const int requestedBufferBytes = 64 * 1024;

        const int channelCount = 5;
        const int messagesPerChannel = 40;

        string[] channelNames = Enumerable.Range(0, channelCount)
            .Select(index => $"parallel-channel-{index}")
            .ToArray();

        TinyMessageBus[] publishers = channelNames
            .Select(channelName => scope.CreateBus(channelName, maxPayloadBytes: requestedBufferBytes))
            .ToArray();

        TinyMessageBus[] subscribers = channelNames
            .Select(channelName => scope.CreateBus(channelName, maxPayloadBytes: requestedBufferBytes))
            .ToArray();

        try
        {
            await scope.WaitForLiveSocketAsync();
            int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
            Assert.NotEqual(0, pid);

            var ringPathsByChannel = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            using var lease = UnixSidecarProcessManager.Acquire();
            Socket[] ringSockets = channelNames
                .Select(_ => ConnectRawSocket(lease.SocketPath))
                .ToArray();

            try
            {
                foreach ((string channelName, int index) in channelNames.Select((name, index) => (name, index)))
                {
                    SidecarAttachRing attach = AttachRawSession(channelName, requestedBufferBytes, ringSockets[index]);
                    ringPathsByChannel[channelName] = attach.RingPath;
                }

                Assert.Equal(channelCount, ringPathsByChannel.Count);
                Assert.Equal(channelCount, ringPathsByChannel.Values.Distinct(StringComparer.Ordinal).Count());
                foreach (string ringPath in ringPathsByChannel.Values)
                    Assert.True(File.Exists(ringPath));

                Task<string[]>[] subscriberTasks = channelNames
                    .Select((channelName, index) => ReceiveMessagesAsync(subscribers[index], messagesPerChannel, cts.Token))
                    .ToArray();

                await WaitForSubscriptionToSettleAsync(cts.Token);

                var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task[] publisherTasks = channelNames
                    .Select((channelName, index) => Task.Run(async () =>
                    {
                        await startGate.Task;

                        for (int messageIndex = 0; messageIndex < messagesPerChannel; messageIndex++)
                        {
                            string payload = $"ch-{index:D2}-msg-{messageIndex:D4}";
                            await PublishBytesAsync(publishers[index], Encoding.UTF8.GetBytes(payload));

                            if ((messageIndex % 5) == 0)
                                await Task.Delay(1, cts.Token);
                        }
                    }, cts.Token))
                    .ToArray();

                startGate.TrySetResult();

                await Task.WhenAll(publisherTasks);
                string[][] observed = await Task.WhenAll(subscriberTasks).WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

                for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
                {
                    string[] expected = Enumerable.Range(0, messagesPerChannel)
                        .Select(messageIndex => $"ch-{channelIndex:D2}-msg-{messageIndex:D4}")
                        .ToArray();

                    Assert.Equal(expected, observed[channelIndex]);
                    Assert.All(observed[channelIndex], message => Assert.StartsWith($"ch-{channelIndex:D2}-", message, StringComparison.Ordinal));
                }

                Assert.Equal(pid, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());
            }
            finally
            {
                foreach (Socket socket in ringSockets)
                    socket.Dispose();
            }
        }
        finally
        {
            foreach (TinyMessageBus bus in subscribers)
                bus.Dispose();

            foreach (TinyMessageBus bus in publishers)
                bus.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(SidecarOnlyModes))]
    public async Task LargeRequestedBuffer_IsBudgetedWithoutOverflow(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
        const int requestedBufferBytes = 1 << 24;

        using var bus = scope.CreateBus("large-buffer-channel", requestedBufferBytes);
        await scope.WaitForLiveSocketAsync();

        using UnixSidecarProcessManager.Lease lease = UnixSidecarProcessManager.Acquire();
        using Socket socket = ConnectRawSocket(lease.SocketPath);
        SidecarAttachRing attach = AttachRawSession("large-buffer-channel", requestedBufferBytes, socket);

        RingSizing expected = RingSizingPolicy.Compute(requestedBufferBytes, 256, BrokeredChannelRing.HeaderBytes, BrokeredChannelRing.SlotHeaderBytes);
        Assert.Equal(expected.SlotPayloadBytes, attach.SlotPayloadBytes);
        Assert.Equal(expected.ImageSize, attach.RingLength);
        Assert.True(File.Exists(attach.RingPath));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task RawUdsClient_CanPublishToTinyMessageBusSubscriber(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
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

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task AbruptSocketDisconnect_LeavesBrokerOperational(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
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

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task MalformedHello_DoesNotKillBroker_AndGoodClientsStillWork(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
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

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task UnexpectedFrameBeforeHello_DoesNotKillBroker_AndGoodClientsStillWork(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
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

    [Theory]
    [MemberData(nameof(SidecarOnlyModes))]
    public async Task UnauthorizedGroup_IsRejected(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
        scope.SetSharedGroup("tinyipc-group-that-does-not-exist");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => scope.CreateBus());
        Assert.Contains("shared group", ex.Message, StringComparison.OrdinalIgnoreCase);
        await scope.WaitForBrokerShutdownAsync();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task BrokerKill_IsRecoveredByNextClient(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

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

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task BrokerArtifacts_AreCreatedWithStrictGroupPermissions(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

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

        BrokerStateSnapshot state = await scope.WaitForBrokerStateAsync(current => current.SessionCount == 1);
        Assert.Equal($"{scope.SharedGroup} 2770", GetStat(scope.SharedDirectory, "%G %a"));
        Assert.Equal($"{scope.SharedGroup} 660", GetStat(scope.SocketPath, "%G %a"));
        Assert.Equal($"{scope.SharedGroup} 660", GetStat(attach.RingPath, "%G %a"));
        Assert.Equal($"{scope.SharedGroup} 660", GetStat(scope.StatePath, "%G %a"));
        Assert.Equal(scope.SocketPath, state.SocketPath);

        socket.Dispose();
        await scope.WaitForBrokerShutdownAsync();
    }

    private static async Task<byte[]> ReceiveSingleMessageAsync(TinyMessageBus bus, CancellationToken cancellationToken)
    {
        await foreach (var item in bus.SubscribeAsync(cancellationToken))
            return GetBytes(item);

        throw new InvalidOperationException("SubscribeAsync completed before receiving a message.");
    }

    private static async Task<string[]> ReceiveMessagesAsync(TinyMessageBus bus, int expectedCount, CancellationToken cancellationToken)
    {
        var messages = new List<string>(expectedCount);

        await foreach (var item in bus.SubscribeAsync(cancellationToken))
        {
            messages.Add(Encoding.UTF8.GetString(GetBytes(item)));
            if (messages.Count >= expectedCount)
                break;
        }

        if (messages.Count != expectedCount)
            throw new InvalidOperationException($"Expected {expectedCount} messages but observed {messages.Count}.");

        return messages.ToArray();
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

    private static string GetStat(string path, string format)
    {
        using var process = Process.Start(new ProcessStartInfo("stat")
        {
            ArgumentList = { "-c", format, path },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start stat.");

        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"stat failed for '{path}': {error}");

        return output;
    }

    private static async Task WaitForFileRemovedAsync(string path)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path))
                return;

            await Task.Delay(50);
        }

        Assert.False(File.Exists(path), $"Expected file '{path}' to be removed.");
    }

    private static async Task WaitForSubscriptionToSettleAsync(CancellationToken cancellationToken)
        => await Task.Delay(750, cancellationToken);

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly string? _previousBackend;
        private readonly string? _previousHostPath;
        private readonly string? _previousUnixShell;
        private readonly string? _previousSharedDir;
        private readonly string? _previousSharedGroup;
        private readonly string? _previousLogDir;
        private readonly string? _previousLogLevel;
        private readonly string? _previousLoggingEnabled;
        private readonly string? _previousFileNotifier;
        private readonly string? _previousBrokerIdleShutdownMs;

        public TestEnvironmentScope(string mode, int? brokerIdleShutdownMs = 2_000)
        {
            ChannelName = $"xivipc-lifecycle-{Guid.NewGuid():N}";
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-lifecycle-tests", Guid.NewGuid().ToString("N"));
            SocketPath = Path.Combine(SharedDirectory, "tinyipc-sidecar.sock");
            StatePath = Path.Combine(SharedDirectory, "tinyipc-sidecar.state.json");
            SharedGroup = ResolveCurrentSharedGroup();

            _previousBackend = Environment.GetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND");
            _previousHostPath = Environment.GetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH");
            _previousUnixShell = Environment.GetEnvironmentVariable("TINYIPC_UNIX_SHELL");
            _previousSharedDir = Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");
            _previousSharedGroup = Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP");
            _previousLogDir = Environment.GetEnvironmentVariable("TINYIPC_LOG_DIR");
            _previousLogLevel = Environment.GetEnvironmentVariable("TINYIPC_LOG_LEVEL");
            _previousLoggingEnabled = Environment.GetEnvironmentVariable("TINYIPC_ENABLE_LOGGING");
            _previousFileNotifier = Environment.GetEnvironmentVariable("TINYIPC_FILE_NOTIFIER");
            _previousBrokerIdleShutdownMs = Environment.GetEnvironmentVariable("TINYIPC_BROKER_IDLE_SHUTDOWN_MS");

            Directory.CreateDirectory(SharedDirectory);
            ProductionPathTestEnvironment.ResetLogger();

            if (ProductionPathTestEnvironment.IsProductionPath(mode))
            {
                ProductionPathTestEnvironment.PrepareStagedNativeHost(SharedDirectory);
                Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", null);
                Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", null);
                Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory));
                Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", SharedGroup);
                Environment.SetEnvironmentVariable("TINYIPC_LOG_DIR", ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory));
                Environment.SetEnvironmentVariable("TINYIPC_LOG_LEVEL", "info");
                Environment.SetEnvironmentVariable("TINYIPC_ENABLE_LOGGING", "1");
                Environment.SetEnvironmentVariable("TINYIPC_FILE_NOTIFIER", "auto");
                Environment.SetEnvironmentVariable("TINYIPC_BROKER_IDLE_SHUTDOWN_MS", brokerIdleShutdownMs?.ToString());
            }
            else
            {
                Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", "sidecar");
                Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", SharedDirectory);
                Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", SharedGroup);
                Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", UnixSidecarProcessManager.ResolveNativeHostPath());
                Environment.SetEnvironmentVariable("TINYIPC_BROKER_IDLE_SHUTDOWN_MS", brokerIdleShutdownMs?.ToString());
            }

            if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(_previousUnixShell))
                Environment.SetEnvironmentVariable("TINYIPC_UNIX_SHELL", "/bin/sh");
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }
        public string SharedGroup { get; }
        public string SocketPath { get; }
        public string StatePath { get; }

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

        public async Task<BrokerStateSnapshot> WaitForBrokerStateAsync(Func<BrokerStateSnapshot, bool> predicate)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            BrokerStateSnapshot? last = null;

            while (DateTime.UtcNow < deadline)
            {
                BrokerStateSnapshot? state = BrokerStateSnapshot.TryRead(StatePath);
                if (state is not null)
                {
                    last = state;
                    if (predicate(state))
                        return state;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"Timed out waiting for broker state predicate. last={last}");
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
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (!File.Exists(StatePath))
                    break;

                await Task.Delay(50);
            }

            if (UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics() != 0)
                _ = UnixSidecarProcessManager.WaitForOwnedProcessExitForDiagnostics(TimeSpan.FromSeconds(10));
        }

        public void AssertProcessExited(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                bool exited = process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds);
                Assert.True(exited, $"Expected process {pid} to exit.");
            }
            catch (ArgumentException)
            {
            }
        }

        public void KillBrokerProcess(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", _previousBackend);
            Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", _previousHostPath);
            Environment.SetEnvironmentVariable("TINYIPC_UNIX_SHELL", _previousUnixShell);
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", _previousSharedDir);
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", _previousSharedGroup);
            Environment.SetEnvironmentVariable("TINYIPC_LOG_DIR", _previousLogDir);
            Environment.SetEnvironmentVariable("TINYIPC_LOG_LEVEL", _previousLogLevel);
            Environment.SetEnvironmentVariable("TINYIPC_ENABLE_LOGGING", _previousLoggingEnabled);
            Environment.SetEnvironmentVariable("TINYIPC_FILE_NOTIFIER", _previousFileNotifier);
            Environment.SetEnvironmentVariable("TINYIPC_BROKER_IDLE_SHUTDOWN_MS", _previousBrokerIdleShutdownMs);
            ProductionPathTestEnvironment.ResetLogger();

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
