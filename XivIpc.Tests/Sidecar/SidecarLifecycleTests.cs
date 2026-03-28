using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TinyIpc;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class SidecarLifecycleTests
{
    private const int DefaultRequestedBufferBytes = 64 * 1024;

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
        LogTestEvent($"Test Start: FirstClient_StartsBrokerAndCreatesSocket(mode={mode})");
        var testStart = Stopwatch.StartNew();

        using TestEnvironmentScope scope = new(mode);

        Assert.False(File.Exists(scope.SocketPath));
        Assert.False(File.Exists(scope.StatePath));

        using var bus = scope.CreateBus();

        await scope.WaitForLiveSocketAsync();
        await scope.WaitForBrokerStateAsync(state => state.SessionCount == 1);

        Assert.True(File.Exists(scope.SocketPath));
        Assert.True(File.Exists(scope.StatePath));
        Assert.NotEqual(0, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());

        testStart.Stop();
        LogTestEvent($"Test End: FirstClient_StartsBrokerAndCreatesSocket(mode={mode}) - Duration: {testStart.Elapsed:mm\\:ss\\.fff}");
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
        _ = AttachRawSession(scope.ChannelName, DefaultRequestedBufferBytes, leakedSocket);
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
            DefaultRequestedBufferBytes,
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

        using var smaller = scope.CreateBus(maxPayloadBytes: 64 * 1024);
        await scope.WaitForLiveSocketAsync();
        await smaller.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(10));

        using var larger = scope.CreateBus(maxPayloadBytes: 128 * 1024);
        await Task.Delay(500);

        string logs = scope.ReadAllLogsOrEmpty();
        Assert.Contains("AttachRejectedByBroker", logs, StringComparison.Ordinal);
        Assert.Contains("smaller than requested", logs, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SidecarOnlyModes))]
    public async Task BrokerRestart_RecreatesRingWithLargerRequestedCapacity(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        using (TinyMessageBus smaller = scope.CreateBus(maxPayloadBytes: 64 * 1024))
        {
            await scope.WaitForLiveSocketAsync();
        }

        await scope.WaitForBrokerShutdownAsync();

        using var publisher = scope.CreateBus(maxPayloadBytes: 128 * 1024);
        using var subscriber = scope.CreateBus(maxPayloadBytes: 128 * 1024);
        await scope.WaitForLiveSocketAsync();
        await WaitForConnectedAsync(new[] { publisher, subscriber }, TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(subscriber, cts.Token);
        await Task.Delay(750, cts.Token);

        byte[] expected = Enumerable.Range(0, 3072).Select(i => (byte)(i % 251)).ToArray();
        await PublishBytesAsync(publisher, expected);

        byte[] actual;
        try
        {
            actual = await receiveTask;
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"Timed out receiving broker-restart message. Logs:{Environment.NewLine}{scope.ReadAllLogsOrEmpty()}", ex);
        }
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
            DefaultRequestedBufferBytes,
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

        using var channelAPublisher = scope.CreateBus("channel-a", maxPayloadBytes: 64 * 1024);
        using var channelASubscriber = scope.CreateBus("channel-a", maxPayloadBytes: 64 * 1024);
        using var channelBPublisher = scope.CreateBus("channel-b", maxPayloadBytes: 64 * 1024);
        using var channelBSubscriber = scope.CreateBus("channel-b", maxPayloadBytes: 64 * 1024);

        await scope.WaitForLiveSocketAsync();
        await WaitForConnectedAsync(
            new[] { channelAPublisher, channelASubscriber, channelBPublisher, channelBSubscriber },
            TimeSpan.FromSeconds(10));
        int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
        Assert.NotEqual(0, pid);

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            Task<byte[]> receiveTask = ReceiveSingleMessageAsync(channelBSubscriber, cts.Token);
            await Task.Delay(750, cts.Token);
            await PublishBytesAsync(channelBPublisher, Encoding.UTF8.GetBytes("before-recreate"));
            try
            {
                Assert.Equal(Encoding.UTF8.GetBytes("before-recreate"), await receiveTask);
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException($"Timed out receiving channel-b pre-recreate message. Logs:{Environment.NewLine}{scope.ReadAllLogsOrEmpty()}", ex);
            }
        }

        channelAPublisher.Dispose();
        channelASubscriber.Dispose();

        using var largerChannelAPublisher = await scope.CreateBusWhenAvailableAsync("channel-a", maxPayloadBytes: 128 * 1024);
        using var largerChannelASubscriber = await scope.CreateBusWhenAvailableAsync("channel-a", maxPayloadBytes: 128 * 1024);
        await WaitForConnectedAsync(new[] { largerChannelAPublisher, largerChannelASubscriber, channelBPublisher, channelBSubscriber }, TimeSpan.FromSeconds(10));

        Assert.Equal(pid, UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics());

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            byte[] payloadA = Enumerable.Range(0, 3072).Select(i => (byte)(i % 239)).ToArray();
            Task<byte[]> receiveTask = ReceiveSingleMessageAsync(largerChannelASubscriber, cts.Token);
            await Task.Delay(750, cts.Token);
            await PublishBytesAsync(largerChannelAPublisher, payloadA);
            try
            {
                Assert.Equal(payloadA, await receiveTask);
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException($"Timed out receiving larger channel-a message. Logs:{Environment.NewLine}{scope.ReadAllLogsOrEmpty()}", ex);
            }
        }

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            Task<byte[]> receiveTask = ReceiveSingleMessageAsync(channelBSubscriber, cts.Token);
            await Task.Delay(750, cts.Token);
            await PublishBytesAsync(channelBPublisher, Encoding.UTF8.GetBytes("after-recreate"));
            try
            {
                Assert.Equal(Encoding.UTF8.GetBytes("after-recreate"), await receiveTask);
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException($"Timed out receiving channel-b post-recreate message. Logs:{Environment.NewLine}{scope.ReadAllLogsOrEmpty()}", ex);
            }
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
            await WaitForConnectedAsync(publishers.Concat(subscribers), TimeSpan.FromSeconds(10));
            int pid = UnixSidecarProcessManager.GetOwnedProcessIdForDiagnostics();
            Assert.NotEqual(0, pid);

            var observedByChannel = channelNames.ToDictionary(
                static name => name,
                static _ => new ConcurrentQueue<string>(),
                StringComparer.Ordinal);

            Task<string[]>[] subscriberTasks = channelNames
                .Select((channelName, index) => CaptureMessagesAsync(subscribers[index], messagesPerChannel, observedByChannel[channelName], cts.Token))
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

            string[][] observed;
            try
            {
                observed = await Task.WhenAll(subscriberTasks).WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
            }
            catch (TimeoutException ex)
            {
                string details = string.Join(
                    ", ",
                    channelNames.Select((name, index) =>
                        $"{name}=observed:{observedByChannel[name].Count}/published:{publishers[index].MessagesPublished}/received:{subscribers[index].MessagesReceived}"));
                throw new TimeoutException($"Timed out waiting for all channel subscribers. Partial counters: {details}", ex);
            }

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

        JournalSizing expected = JournalSizingPolicy.Compute(requestedBufferBytes, BrokeredChannelJournal.HeaderBytes);
        Assert.Equal(expected.MaxPayloadBytes, attach.SlotPayloadBytes);
        Assert.Equal(expected.ImageSize, attach.RingLength);
        Assert.True(File.Exists(attach.RingPath));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task JournalPruning_IsLogged_AndDrainCatchesUpToRetainedTail(string mode)
    {
        using TestEnvironmentScope scope = new(mode, messageTtlMs: 1_000);
        const int requestedBufferBytes = 2_048;

        using TinyMessageBus publisher = scope.CreateBus("backlog-channel", requestedBufferBytes);
        await scope.WaitForLiveSocketAsync();
        await publisher.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(5));

        using UnixSidecarProcessManager.Lease lease = UnixSidecarProcessManager.Acquire();
        using Socket rawSocket = ConnectRawSocket(lease.SocketPath);
        SidecarAttachRing attach = AttachRawSession("backlog-channel", requestedBufferBytes, rawSocket);

        for (int index = 0; index < 8; index++)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"early-{index:D3}-" + new string('a', 180));
            await PublishBytesAsync(publisher, payload);
        }

        await Task.Delay(1_100);

        for (int index = 0; index < 12; index++)
        {
            byte[] payload = Encoding.UTF8.GetBytes($"late-{index:D3}-" + new string('b', 180));
            await PublishBytesAsync(publisher, payload);
        }

        using BrokeredChannelJournal journal = BrokeredChannelJournal.Attach(
            attach.RingPath,
            attach.SlotCount,
            attach.SlotPayloadBytes,
            attach.RingLength,
            scope.MessageTtlMs);
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (journal.CaptureHeadSequence() >= 20)
                break;

            await Task.Delay(25);
        }

        long nextSequence = 0;
        BrokeredJournalDrainResult drain = journal.Drain(Guid.Empty, ref nextSequence);

        string[] actual = drain.Messages.Select(static payload => Encoding.UTF8.GetString(payload)).ToArray();
        Assert.True(drain.CaughtUpToTail);
        Assert.NotEmpty(actual);
        Assert.Contains(actual, static entry => entry.StartsWith("late-", StringComparison.Ordinal));
        Assert.True(drain.TailSequenceObserved > 0);

        rawSocket.Dispose();
        publisher.Dispose();
        await scope.WaitForBrokerShutdownAsync();

        string log = scope.ReadAllLogsOrEmpty();
        Assert.Contains("event=ChannelCreated", log, StringComparison.Ordinal);
        Assert.Contains("event=JournalPrunedOrCompacted", log, StringComparison.Ordinal);
        Assert.Contains("event=ChannelLifetimeSummary", log, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ConcurrentManagedPublishers_AdvanceJournalAndDeliverAllMessages(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
        const int requestedBufferBytes = 8_192;
        const int publisherCount = 3;
        const int subscriberCount = 2;
        const int messagesPerPublisher = 20;
        int expectedTotal = publisherCount * messagesPerPublisher;
        string channelName = "journal-concurrent";

        using var publishersScope = new LifecycleDisposableList<TinyMessageBus>();
        using var subscribersScope = new LifecycleDisposableList<TinyMessageBus>();

        TinyMessageBus[] publishers = Enumerable.Range(0, publisherCount)
            .Select(_ =>
            {
                var bus = scope.CreateBus(channelName, requestedBufferBytes);
                publishersScope.Add(bus);
                return bus;
            })
            .ToArray();

        TinyMessageBus[] subscribers = Enumerable.Range(0, subscriberCount)
            .Select(_ =>
            {
                var bus = scope.CreateBus(channelName, requestedBufferBytes);
                subscribersScope.Add(bus);
                return bus;
            })
            .ToArray();

        await scope.WaitForLiveSocketAsync();
        await WaitForConnectedAsync(publishers.Concat(subscribers), TimeSpan.FromSeconds(10));

        using UnixSidecarProcessManager.Lease lease = UnixSidecarProcessManager.Acquire();
        using Socket rawSocket = ConnectRawSocket(lease.SocketPath);
        SidecarAttachRing attach = AttachRawSession(channelName, requestedBufferBytes, rawSocket, Guid.NewGuid());
        using BrokeredChannelJournal journal = BrokeredChannelJournal.Attach(
            attach.RingPath,
            attach.SlotCount,
            attach.SlotPayloadBytes,
            attach.RingLength,
            scope.MessageTtlMs);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<string[]>[] receiveTasks = subscribers
            .Select(subscriber => ReceiveMessagesAsync(subscriber, expectedTotal, new ConcurrentQueue<string>(), cts.Token))
            .ToArray();

        await WaitForSubscriptionToSettleAsync(cts.Token);

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] publishTasks = Enumerable.Range(0, publisherCount)
            .Select(publisherIndex => Task.Run(async () =>
            {
                await startGate.Task;
                for (int messageIndex = 0; messageIndex < messagesPerPublisher; messageIndex++)
                {
                    byte[] payload = Encoding.UTF8.GetBytes($"journal-pub-{publisherIndex:D2}-msg-{messageIndex:D3}");
                    await PublishBytesAsync(publishers[publisherIndex], payload);
                }
            }, cts.Token))
            .ToArray();

        startGate.TrySetResult();
        await Task.WhenAll(publishTasks);

        string[] expected = Enumerable.Range(0, publisherCount)
            .SelectMany(pub => Enumerable.Range(0, messagesPerPublisher)
                .Select(msg => $"journal-pub-{pub:D2}-msg-{msg:D3}"))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        foreach (Task<string[]> receiveTask in receiveTasks)
        {
            string[] actual = (await receiveTask)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, actual);
        }

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (journal.CaptureHeadSequence() >= expectedTotal)
                break;

            await Task.Delay(25, cts.Token);
        }

        Assert.Equal(expectedTotal, journal.CaptureHeadSequence());
        Assert.Equal(0, journal.CaptureTailSequence());

        long nextSequence = 0;
        BrokeredJournalDrainResult drain = journal.Drain(Guid.Empty, ref nextSequence);
        string[] drained = drain.Messages.Select(Encoding.UTF8.GetString).OrderBy(static value => value, StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedTotal, nextSequence);
        Assert.Equal(expected, drained);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task RawClientDrain_SkipsSelfPublishes_ButObservesPeerPublishes(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
        const int requestedBufferBytes = 4_096;
        const string channelName = "raw-self-filter";
        Guid rawClientId = Guid.NewGuid();

        using var managedPublisher = scope.CreateBus(channelName, requestedBufferBytes);
        using var managedSubscriber = scope.CreateBus(channelName, requestedBufferBytes);
        await scope.WaitForLiveSocketAsync();
        await WaitForConnectedAsync(new[] { managedPublisher, managedSubscriber }, TimeSpan.FromSeconds(10));

        using Socket rawSocket = ConnectRawSocket(scope.SocketPath);
        SidecarAttachRing attach = AttachRawSession(channelName, requestedBufferBytes, rawSocket, rawClientId);
        using BrokeredChannelJournal journal = BrokeredChannelJournal.Attach(
            attach.RingPath,
            attach.SlotCount,
            attach.SlotPayloadBytes,
            attach.RingLength,
            scope.MessageTtlMs);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<byte[]> receiveTask = ReceiveSingleMessageAsync(managedSubscriber, cts.Token);
        await WaitForSubscriptionToSettleAsync(cts.Token);

        byte[] selfPayload = Encoding.UTF8.GetBytes("raw-self-publish");
        SidecarProtocol.WritePublish(rawSocket, selfPayload);
        Assert.Equal(selfPayload, await receiveTask);

        DateTime selfDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < selfDeadline)
        {
            if (journal.CaptureHeadSequence() >= 1)
                break;

            await Task.Delay(25, cts.Token);
        }

        long nextSequence = 0;
        BrokeredJournalDrainResult selfDrain = journal.Drain(rawClientId, ref nextSequence);
        Assert.Empty(selfDrain.Messages);
        Assert.Equal(1, nextSequence);

        byte[] peerPayload = Encoding.UTF8.GetBytes("managed-peer-publish");
        await PublishBytesAsync(managedPublisher, peerPayload);

        DateTime peerDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < peerDeadline)
        {
            if (journal.CaptureHeadSequence() >= 2)
                break;

            await Task.Delay(25, cts.Token);
        }

        BrokeredJournalDrainResult peerDrain = journal.Drain(rawClientId, ref nextSequence);
        Assert.Equal(2, nextSequence);
        Assert.Equal(new[] { "managed-peer-publish" }, peerDrain.Messages.Select(Encoding.UTF8.GetString).ToArray());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task RawUdsClient_CanPublishToTinyMessageBusSubscriber(string mode)
    {
        using TestEnvironmentScope scope = new(mode);
        using var subscriber = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        using var socket = ConnectRawSocket(scope.SocketPath);
        AttachRawSession(scope.ChannelName, DefaultRequestedBufferBytes, socket);

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
            _ = AttachRawSession(scope.ChannelName, DefaultRequestedBufferBytes, socket);
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

        using var bus = scope.CreateBus();
        string logs = await scope.WaitForLogMarkerAsync("ReconnectAttemptFailed");
        Assert.Contains("could not be resolved", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("event=ReconnectSucceeded", logs, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task BrokerKill_IsRecoveredByNextClient(string mode)
    {
        using TestEnvironmentScope scope = new(mode);

        using UnixSidecarProcessManager.Lease starter = UnixSidecarProcessManager.Acquire();
        await scope.WaitForLiveSocketAsync();
        BrokerStateSnapshot liveState = await scope.WaitForBrokerStateAsync(state => state.SessionCount == 0);
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
        await publisher.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(5));
        await subscriber.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(5));

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
            DefaultRequestedBufferBytes,
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

    private static async Task<string[]> ReceiveMessagesAsync(TinyMessageBus bus, int expectedCount, ConcurrentQueue<string> observedMessages, CancellationToken cancellationToken)
    {
        var messages = new List<string>(expectedCount);

        await foreach (var item in bus.SubscribeAsync(cancellationToken))
        {
            string message = Encoding.UTF8.GetString(GetBytes(item));
            messages.Add(message);
            observedMessages.Enqueue(message);
            if (messages.Count >= expectedCount)
                break;
        }

        if (messages.Count != expectedCount)
            throw new InvalidOperationException($"Expected {expectedCount} messages but observed {messages.Count}.");

        return messages.ToArray();
    }

    private static Task<string[]> CaptureMessagesAsync(TinyMessageBus bus, int expectedCount, ConcurrentQueue<string> observedMessages, CancellationToken cancellationToken)
    {
        var messages = new List<string>(expectedCount);
        var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        EventHandler<TinyMessageReceivedEventArgs>? handler = null;

        handler = (_, args) =>
        {
            string message = Encoding.UTF8.GetString(GetBytes(args));
            observedMessages.Enqueue(message);
            messages.Add(message);
            if (messages.Count >= expectedCount)
            {
                bus.MessageReceived -= handler;
                registration.Dispose();
                tcs.TrySetResult(messages.ToArray());
            }
        };

        bus.MessageReceived += handler;
        registration = cancellationToken.Register(() =>
        {
            bus.MessageReceived -= handler;
            tcs.TrySetCanceled(cancellationToken);
        });

        return tcs.Task;
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
        => AttachRawSession(channelName, maxPayloadBytes, socket, Guid.Empty);

    private static SidecarAttachRing AttachRawSession(string channelName, int maxPayloadBytes, Socket socket, Guid clientInstanceId)
    {
        SidecarProtocol.WriteHello(socket, new SidecarHello(
            channelName,
            maxPayloadBytes,
            Environment.ProcessId,
            HeartbeatIntervalMs: 100,
            HeartbeatTimeoutMs: 5_000,
            ClientInstanceId: clientInstanceId));

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

    private static Task WaitForConnectedAsync(IEnumerable<TinyMessageBus> buses, TimeSpan timeout)
        => Task.WhenAll(buses.Select(bus => bus.WaitForConnectedForDiagnosticsAsync(timeout)));

    private static void LogTestEvent(string message)
    {
        var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff");
        Console.WriteLine($"[TEST:{timestamp}] {message}");
    }

    private async Task RunTimedTestAsync(string testName, Func<Task> testAction)
    {
        LogTestEvent($"Test Start: {testName}");
        var testStart = Stopwatch.StartNew();
        await testAction();
        testStart.Stop();
        LogTestEvent($"Test End: {testName} - Duration: {testStart.Elapsed:mm\\:ss\\.fff}");
    }

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _overrideValues = new(StringComparer.Ordinal);
        private readonly IDisposable _overrides;

        public TestEnvironmentScope(string mode, int? brokerIdleShutdownMs = 2_000, long? messageTtlMs = null)
        {
            LogTestEvent($"TestEnvironmentScope.Start: mode={mode}");
            var scopeStart = Stopwatch.StartNew();

            ChannelName = $"xivipc-lifecycle-{Guid.NewGuid():N}";
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-lifecycle-tests", Guid.NewGuid().ToString("N"));
            SocketPath = Path.Combine(SharedDirectory, "tinyipc-sidecar.sock");
            StatePath = Path.Combine(SharedDirectory, "tinyipc-sidecar.state.json");
            SharedGroup = ResolveCurrentSharedGroup();
            MessageTtlMs = messageTtlMs ?? (long)TinyIpcOptions.DefaultMinMessageAge.TotalMilliseconds;

            Directory.CreateDirectory(SharedDirectory);
            ProductionPathTestEnvironment.ResetLogger();
            if (ProductionPathTestEnvironment.IsProductionPath(mode))
            {
                ProductionPathTestEnvironment.PrepareStagedNativeHost(SharedDirectory);
                _overrideValues[TinyIpcEnvironment.MessageBusBackend] = null;
                _overrideValues[TinyIpcEnvironment.NativeHostPath] = null;
                _overrideValues[TinyIpcEnvironment.SharedDirectory] = ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory);
                _overrideValues[TinyIpcEnvironment.SharedGroup] = SharedGroup;
                _overrideValues[TinyIpcEnvironment.LogDirectory] = ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory);
                _overrideValues[TinyIpcEnvironment.LogLevel] = "info";
                _overrideValues[TinyIpcEnvironment.EnableLogging] = "1";
                _overrideValues[TinyIpcEnvironment.FileNotifier] = "auto";
                _overrideValues[TinyIpcEnvironment.BrokerIdleShutdownMs] = brokerIdleShutdownMs?.ToString();
                _overrideValues[TinyIpcEnvironment.MessageTtlMs] = MessageTtlMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

                if (ProductionPathTestEnvironment.IsProductionRingPath(mode))
                    _overrideValues[TinyIpcEnvironment.SidecarStorageMode] = "ring";
            }
            else
            {
                _overrideValues[TinyIpcEnvironment.MessageBusBackend] = "sidecar";
                _overrideValues[TinyIpcEnvironment.SharedDirectory] = SharedDirectory;
                _overrideValues[TinyIpcEnvironment.SharedGroup] = SharedGroup;
                _overrideValues[TinyIpcEnvironment.NativeHostPath] = UnixSidecarProcessManager.ResolveNativeHostPath();
                _overrideValues[TinyIpcEnvironment.LogDirectory] = SharedDirectory;
                _overrideValues[TinyIpcEnvironment.LogLevel] = "info";
                _overrideValues[TinyIpcEnvironment.EnableLogging] = "1";
                _overrideValues[TinyIpcEnvironment.FileNotifier] = "auto";
                _overrideValues[TinyIpcEnvironment.BrokerIdleShutdownMs] = brokerIdleShutdownMs?.ToString();
                _overrideValues[TinyIpcEnvironment.MessageTtlMs] = MessageTtlMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.UnixShell)))
                _overrideValues[TinyIpcEnvironment.UnixShell] = "/bin/sh";

            _overrides = TinyIpcEnvironment.Override(_overrideValues);

            scopeStart.Stop();
            LogTestEvent($"TestEnvironmentScope.Start completed in {scopeStart.Elapsed:mm\\:ss\\.fff}");
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }
        public string SharedGroup { get; }
        public string SocketPath { get; }
        public string StatePath { get; }
        public long MessageTtlMs { get; }

        public TinyMessageBus CreateBus(int maxPayloadBytes = DefaultRequestedBufferBytes)
            => CreateBus(ChannelName, maxPayloadBytes);

        public TinyMessageBus CreateBus(string channelName, int maxPayloadBytes = DefaultRequestedBufferBytes)
            => new(new TinyMemoryMappedFile(channelName, maxPayloadBytes), disposeFile: true);

        public string ReadLatestLogOrEmpty()
            => ProductionPathTestEnvironment.ReadLatestLogOrEmpty(SharedDirectory);

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

        public async Task<string> WaitForLogMarkerAsync(string marker, TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline)
            {
                string logs = ReadAllLogsOrEmpty();
                if (logs.Contains(marker, StringComparison.Ordinal))
                    return logs;

                await Task.Delay(50);
            }

            return ReadAllLogsOrEmpty();
        }

        public async Task<TinyMessageBus> CreateBusWhenAvailableAsync(string channelName, int maxPayloadBytes = DefaultRequestedBufferBytes)
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
            => _overrideValues[TinyIpcEnvironment.SharedGroup] = groupName;

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

            string logs = ReadAllLogsOrEmpty();
            throw new TimeoutException(
                $"Timed out waiting for live broker socket '{SocketPath}'. Logs:{Environment.NewLine}{logs}",
                last);
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
            LogTestEvent($"TestEnvironmentScope.Dispose: channel={ChannelName}");
            var disposeStart = Stopwatch.StartNew();

            _overrides.Dispose();
            ProductionPathTestEnvironment.ResetLogger();

            try
            {
                if (Directory.Exists(SharedDirectory))
                    Directory.Delete(SharedDirectory, recursive: true);
            }
            catch
            {
            }

            disposeStart.Stop();
            LogTestEvent($"TestEnvironmentScope.Dispose completed in {disposeStart.Elapsed:mm\\:ss\\.fff}");
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

    private sealed class LifecycleDisposableList<T> : IDisposable where T : IDisposable
    {
        private readonly List<T> _items = new();

        public void Add(T item) => _items.Add(item);

        public void Dispose()
        {
            foreach (T item in _items)
                item.Dispose();
        }
    }
}
