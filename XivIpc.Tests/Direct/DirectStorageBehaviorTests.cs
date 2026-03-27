using System.Collections.Concurrent;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class DirectStorageBehaviorTests
{
    [Theory]
    [InlineData("in-memory")]
    [InlineData("shared-memory")]
    public async Task TinyMessageBus_DirectStorage_DeliversMessagesInOrder(string storageMode)
    {
        using TestEnvironmentScope scope = new(storageMode);
        using var publisher = scope.CreateBus(maxPayloadBytes: 4096);
        using var subscriber = scope.CreateBus(maxPayloadBytes: 4096);

        var observed = new ConcurrentQueue<string>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.MessageReceived += (_, args) =>
        {
            string message = Encoding.UTF8.GetString(args.BinaryData.ToArray());
            observed.Enqueue(message);
            if (observed.Count >= 3)
                received.TrySetResult();
        };

        await publisher.PublishAsync(Encoding.UTF8.GetBytes("a"));
        await publisher.PublishAsync(Encoding.UTF8.GetBytes("b"));
        await publisher.PublishAsync(Encoding.UTF8.GetBytes("c"));

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "a", "b", "c" }, observed.ToArray());
    }

    [Theory]
    [InlineData("in-memory")]
    [InlineData("shared-memory")]
    public async Task TinyMessageBus_DirectStorage_MultipleSubscribersObserveAllMessages(string storageMode)
    {
        using TestEnvironmentScope scope = new(storageMode);
        using var publisherA = scope.CreateBus(maxPayloadBytes: 4096);
        using var publisherB = scope.CreateBus(maxPayloadBytes: 4096);
        using var subscriberA = scope.CreateBus(maxPayloadBytes: 4096);
        using var subscriberB = scope.CreateBus(maxPayloadBytes: 4096);

        using var observerA = new MessageObserver(subscriberA, expectedCount: 6);
        using var observerB = new MessageObserver(subscriberB, expectedCount: 6);

        await publisherA.PublishAsync(Encoding.UTF8.GetBytes("a-1"));
        await publisherB.PublishAsync(Encoding.UTF8.GetBytes("b-1"));
        await publisherA.PublishAsync(Encoding.UTF8.GetBytes("a-2"));
        await publisherB.PublishAsync(Encoding.UTF8.GetBytes("b-2"));
        await publisherA.PublishAsync(Encoding.UTF8.GetBytes("a-3"));
        await publisherB.PublishAsync(Encoding.UTF8.GetBytes("b-3"));

        await Task.WhenAll(
            observerA.WaitForCountAsync(TimeSpan.FromSeconds(10)),
            observerB.WaitForCountAsync(TimeSpan.FromSeconds(10)));

        string[] expected = ["a-1", "b-1", "a-2", "b-2", "a-3", "b-3"];
        Assert.Equal(expected, observerA.Messages.Select(Decode).ToArray());
        Assert.Equal(expected, observerB.Messages.Select(Decode).ToArray());
    }

    [Theory]
    [InlineData("in-memory")]
    [InlineData("shared-memory")]
    public void TinyMessageBus_DirectStorage_AllowsReopenWithDifferentCapacityAfterDispose(string storageMode)
    {
        using TestEnvironmentScope scope = new(storageMode);

        using (TinyMessageBus first = scope.CreateBus(maxPayloadBytes: 1024))
        {
        }

        using TinyMessageBus reopened = scope.CreateBus(maxPayloadBytes: 4096);
        Assert.NotNull(reopened);
    }

    private static string Decode(byte[] payload)
        => Encoding.UTF8.GetString(payload);

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly IDisposable _overrides;

        public TestEnvironmentScope(string storageMode)
        {
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-direct-storage-tests", storageMode, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SharedDirectory);

            _overrides = TinyIpcEnvironment.Override(
                (TinyIpcEnvironment.MessageBusBackend, "direct"),
                (TinyIpcEnvironment.DirectStorageMode, storageMode),
                (TinyIpcEnvironment.SharedDirectory, SharedDirectory),
                (TinyIpcEnvironment.SlotCount, "64"),
                (TinyIpcEnvironment.MessageTtlMs, "30000"));

            ChannelName = $"xivipc-direct-storage-{storageMode}-{Guid.NewGuid():N}";
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }

        public TinyMessageBus CreateBus(int maxPayloadBytes)
            => new(new TinyMemoryMappedFile(ChannelName, maxPayloadBytes), disposeFile: true);

        public void Dispose()
        {
            _overrides.Dispose();
        }
    }

    private sealed class MessageObserver : IDisposable
    {
        private readonly TinyMessageBus _bus;
        private readonly TaskCompletionSource _received;

        public MessageObserver(TinyMessageBus bus, int expectedCount)
        {
            _bus = bus;
            ExpectedCount = expectedCount;
            _received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _bus.MessageReceived += OnMessageReceived;
        }

        public int ExpectedCount { get; }
        public ConcurrentQueue<byte[]> Messages { get; } = new();

        public Task WaitForCountAsync(TimeSpan timeout)
            => _received.Task.WaitAsync(timeout);

        public void Dispose()
        {
            _bus.MessageReceived -= OnMessageReceived;
        }

        private void OnMessageReceived(object? sender, TinyMessageReceivedEventArgs e)
        {
            Messages.Enqueue(e.BinaryData.ToArray());
            if (Messages.Count >= ExpectedCount)
                _received.TrySetResult();
        }
    }
}
