using System.Net.Sockets;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class ProductionPathLifecycleTests
{
    private static readonly SidecarLifecycleTests Inner = new();

    [Fact]
    public Task FirstClient_StartsBrokerAndCreatesSocket()
        => Inner.FirstClient_StartsBrokerAndCreatesSocket(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task ConcurrentClientStartup_ReusesSingleBrokerProcess()
        => Inner.ConcurrentClientStartup_ReusesSingleBrokerProcess(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task Acquire_WhenBrokerAlreadyRunning_DoesNotSpawnReplacement()
        => Inner.Acquire_WhenBrokerAlreadyRunning_DoesNotSpawnReplacement(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task StaleSocket_IsRemovedAndBrokerRestarts()
        => Inner.StaleSocket_IsRemovedAndBrokerRestarts(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task LastClientDisconnect_ShutsDownBrokerAndRemovesSocket()
        => Inner.LastClientDisconnect_ShutsDownBrokerAndRemovesSocket(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task DisposeSingleClient_RemovesSessionAndShutsDownBroker()
        => Inner.DisposeSingleClient_RemovesSessionAndShutsDownBroker(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task DeletingSocketWhileBrokerAlive_ReplacesBrokerWithoutParallelHosts()
        => Inner.DeletingSocketWhileBrokerAlive_ReplacesBrokerWithoutParallelHosts(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task DeletingBrokerStateWhileBrokerAlive_DoesNotSpawnReplacementBroker()
        => Inner.DeletingBrokerStateWhileBrokerAlive_DoesNotSpawnReplacementBroker(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task LeakedRawSession_KeepsBrokerAlive_AfterManagedClientDisposes()
        => Inner.LeakedRawSession_KeepsBrokerAlive_AfterManagedClientDisposes(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task RingFile_IsCreatedAndRemovedWithBrokerLifecycle()
        => Inner.RingFile_IsCreatedAndRemovedWithBrokerLifecycle(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task BrokerWithoutClients_DoesNotExitWithinLegacyTwoSecondWindow()
        => Inner.BrokerWithoutClients_DoesNotExitWithinLegacyTwoSecondWindow(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public async Task LargerRequestedCapacity_WhileBrokerLive_UsesLoggedNoOpBus()
    {
        using TestEnvironmentScope scope = new();
        using var smaller = scope.CreateBus(maxPayloadBytes: 1024);
        await scope.WaitForLiveSocketAsync();

        using var larger = scope.CreateBus(maxPayloadBytes: 4096);

        string log = scope.ReadLatestLogOrEmpty();
        ProductionPathTestEnvironment.AssertBrokerRequiredNoOp(log);
        Assert.True(File.Exists(scope.SocketPath));
    }

    [Fact]
    public Task BrokerRestart_RecreatesRingWithLargerRequestedCapacity()
        => Inner.BrokerRestart_RecreatesRingWithLargerRequestedCapacity(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task HeartbeatTimeout_ShutsDownBrokerAfterIdleClientStopsHeartbeating()
        => Inner.HeartbeatTimeout_ShutsDownBrokerAfterIdleClientStopsHeartbeating(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task DisposingOneClient_DoesNotInterruptOtherLiveClients()
        => Inner.DisposingOneClient_DoesNotInterruptOtherLiveClients(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task DifferentChannels_ShareSingleBroker_AndChannelTeardownIsIndependent()
        => Inner.DifferentChannels_ShareSingleBroker_AndChannelTeardownIsIndependent(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task FiveChannels_CreateDistinctRingFiles_AndChannelTeardownIsIndependent()
        => Inner.FiveChannels_CreateDistinctRingFiles_AndChannelTeardownIsIndependent(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task FiveChannels_ParallelTraffic_HasNoCrosstalk_AndPreservesPerChannelOrder()
        => Inner.FiveChannels_ParallelTraffic_HasNoCrosstalk_AndPreservesPerChannelOrder(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task LargeRequestedBuffer_IsBudgetedWithoutOverflow()
        => Inner.LargeRequestedBuffer_IsBudgetedWithoutOverflow(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task RawUdsClient_CanPublishToTinyMessageBusSubscriber()
        => Inner.RawUdsClient_CanPublishToTinyMessageBusSubscriber(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task AbruptSocketDisconnect_LeavesBrokerOperational()
        => Inner.AbruptSocketDisconnect_LeavesBrokerOperational(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task MalformedHello_DoesNotKillBroker_AndGoodClientsStillWork()
        => Inner.MalformedHello_DoesNotKillBroker_AndGoodClientsStillWork(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task UnexpectedFrameBeforeHello_DoesNotKillBroker_AndGoodClientsStillWork()
        => Inner.UnexpectedFrameBeforeHello_DoesNotKillBroker_AndGoodClientsStillWork(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public void UnauthorizedGroup_UsesLoggedNoOpBus()
    {
        using TestEnvironmentScope scope = new(sharedGroup: "group-that-should-not-exist");
        using var bus = scope.CreateBus();

        string log = scope.ReadLatestLogOrEmpty();
        ProductionPathTestEnvironment.AssertBrokerRequiredNoOp(log);
        Assert.False(File.Exists(scope.SocketPath));
    }

    [Fact]
    public Task BrokerKill_IsRecoveredByNextClient()
        => Inner.BrokerKill_IsRecoveredByNextClient(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task BrokerArtifacts_AreCreatedWithStrictGroupPermissions()
        => Inner.BrokerArtifacts_AreCreatedWithStrictGroupPermissions(ProductionPathTestEnvironment.BackendName);

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);

        public TestEnvironmentScope(string? sharedGroup = null)
        {
            ChannelName = $"xivipc-production-lifecycle-{Guid.NewGuid():N}";
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-production-lifecycle-tests", Guid.NewGuid().ToString("N"));
            SocketPath = Path.Combine(SharedDirectory, "tinyipc-sidecar.sock");

            Directory.CreateDirectory(SharedDirectory);
            ProductionPathTestEnvironment.PrepareStagedNativeHost(SharedDirectory);

            Capture("TINYIPC_MESSAGE_BUS_BACKEND");
            Capture("TINYIPC_NATIVE_HOST_PATH");
            Capture("TINYIPC_SHARED_DIR");
            Capture("TINYIPC_SHARED_GROUP");
            Capture("TINYIPC_LOG_DIR");
            Capture("TINYIPC_LOG_LEVEL");
            Capture("TINYIPC_ENABLE_LOGGING");
            Capture("TINYIPC_FILE_NOTIFIER");

            ProductionPathTestEnvironment.ResetLogger();

            Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", null);
            Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory));
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", sharedGroup ?? ProductionPathTestEnvironment.ResolveSharedGroup());
            Environment.SetEnvironmentVariable("TINYIPC_LOG_DIR", ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory));
            Environment.SetEnvironmentVariable("TINYIPC_LOG_LEVEL", "info");
            Environment.SetEnvironmentVariable("TINYIPC_ENABLE_LOGGING", "1");
            Environment.SetEnvironmentVariable("TINYIPC_FILE_NOTIFIER", "auto");
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }
        public string SocketPath { get; }

        public TinyMessageBus CreateBus(int maxPayloadBytes = 1024)
            => new(new TinyMemoryMappedFile(ChannelName, maxPayloadBytes), disposeFile: true);

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

        public string ReadLatestLogOrEmpty()
            => ProductionPathTestEnvironment.ReadLatestLogOrEmpty(SharedDirectory);

        public void Dispose()
        {
            foreach ((string key, string? value) in _previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);

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

        private void Capture(string variableName)
            => _previousEnvironment[variableName] = Environment.GetEnvironmentVariable(variableName);
    }
}
