using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class ProductionPathRuntimeIntegrationTests
{
    private static readonly SidecarRuntimeIntegrationTests Inner = new();

    [Fact]
    public Task Wine_Client_StartsBrokerAndReceivesFromNative_WhenEnabled()
        => Inner.Wine_Client_StartsBrokerAndReceivesFromNative_WhenEnabled(productionDiscovery: true);

    [Fact]
    public Task Proton_Client_StartsBrokerAndReceivesFromNative_WhenEnabled()
        => Inner.Proton_Client_StartsBrokerAndReceivesFromNative_WhenEnabled(productionDiscovery: true);

    [Fact]
    public Task Wine_Client_KilledWithoutDispose_CanReconnectToLiveBroker_WhenEnabled()
        => Inner.Wine_Client_KilledWithoutDispose_CanReconnectToLiveBroker_WhenEnabled(productionDiscovery: true);

    [Fact]
    public Task Wine_Client_Dispose_RemovesSession_AndBrokerShutsDown_WhenEnabled()
        => Inner.Wine_Client_Dispose_RemovesSession_AndBrokerShutsDown_WhenEnabled(productionDiscovery: true);

    [Fact]
    public Task Proton_Client_KilledWithoutDispose_CanReconnectToLiveBroker_WhenEnabled()
        => Inner.Proton_Client_KilledWithoutDispose_CanReconnectToLiveBroker_WhenEnabled(productionDiscovery: true);

    [Fact]
    public Task Proton_Client_Dispose_RemovesSession_AndBrokerShutsDown_WhenEnabled()
        => Inner.Proton_Client_Dispose_RemovesSession_AndBrokerShutsDown_WhenEnabled(productionDiscovery: true);
}
