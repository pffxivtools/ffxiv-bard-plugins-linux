using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class ProductionPathRingLifecycleTests
{
    private static readonly SidecarLifecycleTests Inner = new();

    [Fact]
    public Task FirstClient_StartsBrokerAndCreatesSocket()
        => Inner.FirstClient_StartsBrokerAndCreatesSocket(ProductionPathTestEnvironment.RingBackendName);

    [Fact]
    public Task StaleSocket_IsRemovedAndBrokerRestarts()
        => Inner.StaleSocket_IsRemovedAndBrokerRestarts(ProductionPathTestEnvironment.RingBackendName);

    [Fact]
    public Task LastClientDisconnect_ShutsDownBrokerAndRemovesSocket()
        => Inner.LastClientDisconnect_ShutsDownBrokerAndRemovesSocket(ProductionPathTestEnvironment.RingBackendName);

    [Fact]
    public Task HeartbeatTimeout_ShutsDownBrokerAfterIdleClientStopsHeartbeating()
        => Inner.HeartbeatTimeout_ShutsDownBrokerAfterIdleClientStopsHeartbeating(ProductionPathTestEnvironment.RingBackendName);

    [Fact]
    public Task BrokerKill_IsRecoveredByNextClient()
        => Inner.BrokerKill_IsRecoveredByNextClient(ProductionPathTestEnvironment.RingBackendName);
}
