using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class ProductionPathRingFunctionalTests
{
    private static readonly FunctionalTests Inner = new();

    [Fact]
    public Task ManySequentialPublishes_AreDelivered()
        => Inner.ManySequentialPublishes_AreDelivered(ProductionPathTestEnvironment.RingBackendName);

    [Fact]
    public Task SubscribeAsync_YieldsPublishedMessages()
        => Inner.SubscribeAsync_YieldsPublishedMessages(ProductionPathTestEnvironment.RingBackendName);

    [Fact]
    public Task MultiPublisherMultiSubscriber_AllSubscribersObserveAllMessages()
        => Inner.MultiPublisherMultiSubscriber_AllSubscribersObserveAllMessages(ProductionPathTestEnvironment.RingBackendName);
}
