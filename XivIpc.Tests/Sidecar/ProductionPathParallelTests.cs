using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class ProductionPathParallelTests
{
    private static readonly ParallelFunctionalTests Inner = new();

    [Fact]
    public Task ParallelReadersAndWriters_AllSubscribersObserveAllMessagesExactlyOncePerPayload()
        => Inner.ParallelReadersAndWriters_AllSubscribersObserveAllMessagesExactlyOncePerPayload(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task ParallelBursts_AcrossMultipleRounds_RemainStable()
        => Inner.ParallelBursts_AcrossMultipleRounds_RemainStable(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task ParallelWriters_WithLargePayloads_AreDeliveredToAllReaders()
        => Inner.ParallelWriters_WithLargePayloads_AreDeliveredToAllReaders(ProductionPathTestEnvironment.BackendName);

    [Fact]
    public Task SubscriberChurn_DuringParallelPublishing_DoesNotBreakStableSubscribers()
        => Inner.SubscriberChurn_DuringParallelPublishing_DoesNotBreakStableSubscribers(ProductionPathTestEnvironment.BackendName);
}
