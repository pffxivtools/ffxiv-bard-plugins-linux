using System.Collections.Concurrent;
using System.Text;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class UnixInMemoryTinyMessageBusTests
{
    [Fact]
    public async Task PublishAsync_SinglePublisherSingleSubscriber_DeliversMessagesInOrder()
    {
        using var env = new SharedMemoryTestEnvironment(slotCount: 64);
        using var publisher = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 1024);
        using var subscriber = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 1024);

        using var observer = new BusObserver(subscriber, expectedCount: 10);
        await observer.WaitUntilAttachedAsync();

        for (int i = 0; i < 10; i++)
            await publisher.PublishAsync(ToBytes($"msg-{i:D2}"));

        await observer.WaitForCountAsync(TimeSpan.FromSeconds(10));

        string[] actual = observer.Messages
            .Select(Encoding.UTF8.GetString)
            .ToArray();

        string[] expected = Enumerable.Range(0, 10)
            .Select(i => $"msg-{i:D2}")
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ParallelPublishersParallelSubscribers_AllSubscribersObserveAllMessages()
    {
        using var env = new SharedMemoryTestEnvironment(slotCount: 1024);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        const int publisherCount = 8;
        const int subscriberCount = 8;
        const int messagesPerPublisher = 100;
        int expectedTotal = publisherCount * messagesPerPublisher;

        using var publishers = new DisposableList<UnixInMemoryTinyMessageBus>();
        using var subscribers = new DisposableList<UnixInMemoryTinyMessageBus>();
        using var observers = new DisposableList<BusObserver>();

        UnixInMemoryTinyMessageBus[] publisherBuses = Enumerable.Range(0, publisherCount)
            .Select(_ =>
            {
                var bus = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 2048);
                publishers.Add(bus);
                return bus;
            })
            .ToArray();

        BusObserver[] subscriberObservers = Enumerable.Range(0, subscriberCount)
            .Select(_ =>
            {
                var bus = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 2048);
                subscribers.Add(bus);

                var observer = new BusObserver(bus, expectedTotal, deduplicate: true);
                observers.Add(observer);
                return observer;
            })
            .ToArray();

        await Task.WhenAll(subscriberObservers.Select(x => x.WaitUntilAttachedAsync()));
        await Task.Delay(50, cts.Token);

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] publisherTasks = Enumerable.Range(0, publisherCount)
            .Select(pub => Task.Run(async () =>
            {
                var rng = new Random(unchecked(Environment.TickCount * 73 + pub));
                await startGate.Task;

                for (int msg = 0; msg < messagesPerPublisher; msg++)
                {
                    await publisherBuses[pub].PublishAsync(ToBytes($"pub-{pub:D2}-msg-{msg:D4}"));

                    if ((msg % 9) == 0)
                        await Task.Delay(rng.Next(0, 3), cts.Token);
                }
            }, cts.Token))
            .ToArray();

        startGate.TrySetResult();

        await Task.WhenAll(publisherTasks);
        await Task.WhenAll(subscriberObservers.Select(x => x.WaitForCountAsync(TimeSpan.FromSeconds(30))));

        string[] expected = Enumerable.Range(0, publisherCount)
            .SelectMany(pub => Enumerable.Range(0, messagesPerPublisher)
                .Select(msg => $"pub-{pub:D2}-msg-{msg:D4}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        foreach (BusObserver observer in subscriberObservers)
        {
            string[] actual = observer.Messages
                .Select(Encoding.UTF8.GetString)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedTotal, actual.Length);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task PublishAsync_OnSingleBus_IsSafeUnderConcurrentWriters()
    {
        using var env = new SharedMemoryTestEnvironment(slotCount: 1024);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var bus = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 1024);
        using var subscriber = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 1024);
        using var observer = new BusObserver(subscriber, expectedCount: 500, deduplicate: true);

        await observer.WaitUntilAttachedAsync();
        await Task.Delay(25, cts.Token);

        const int writerTasks = 10;
        const int messagesPerWriter = 50;

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] writers = Enumerable.Range(0, writerTasks)
            .Select(writerId => Task.Run(async () =>
            {
                await startGate.Task;

                for (int i = 0; i < messagesPerWriter; i++)
                    await bus.PublishAsync(ToBytes($"writer-{writerId:D2}-msg-{i:D3}"));
            }, cts.Token))
            .ToArray();

        startGate.TrySetResult();

        await Task.WhenAll(writers);
        await observer.WaitForCountAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(writerTasks * messagesPerWriter, observer.Messages.Count);

        string[] expected = Enumerable.Range(0, writerTasks)
            .SelectMany(w => Enumerable.Range(0, messagesPerWriter)
                .Select(i => $"writer-{w:D2}-msg-{i:D3}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        string[] actual = observer.Messages
            .Select(Encoding.UTF8.GetString)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SmallRingBuffer_UnderWraparound_DeliversValidUncorruptedMessages()
    {
        using var env = new SharedMemoryTestEnvironment(slotCount: 8);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using var publisher = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 4096);
        using var subscriber = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 4096);
        using var observer = new BusObserver(subscriber, expectedCount: 32, deduplicate: true);

        await observer.WaitUntilAttachedAsync();
        await Task.Delay(25, cts.Token);

        for (int i = 0; i < 200; i++)
        {
            byte[] payload = BuildPatternPayload(i, 2048);
            await publisher.PublishAsync(payload);

            if ((i % 4) == 0)
                await Task.Yield();
        }

        await observer.WaitForAtLeastCountAsync(8, TimeSpan.FromSeconds(10));

        byte[][] actual = observer.Messages.ToArray();
        Assert.NotEmpty(actual);

        for (int i = 0; i < actual.Length; i++)
            Assert.True(ValidatePatternPayload(actual[i]), $"Payload at index {i} failed deterministic validation.");
    }

    [Fact]
    public void Constructor_AllowsOpeningSmallerLocalRequestAgainstLargerExistingImage()
    {
        using var largerEnv = new SharedMemoryTestEnvironment(slotCount: 256);

        using (var larger = new UnixInMemoryTinyMessageBus(largerEnv.ChannelName, maxPayloadBytes: 8192))
        {
        }

        using var smallerEnv = new SharedMemoryTestEnvironment(
            channelName: largerEnv.ChannelName,
            slotCount: 64);

        using var smaller = new UnixInMemoryTinyMessageBus(smallerEnv.ChannelName, maxPayloadBytes: 1024);

        Assert.NotNull(smaller);
    }

    [Fact]
    public void Constructor_ThrowsWhenExistingPayloadCapacityIsSmallerThanRequested()
    {
        using var envSmall = new SharedMemoryTestEnvironment(slotCount: 64);

        using (var smaller = new UnixInMemoryTinyMessageBus(envSmall.ChannelName, maxPayloadBytes: 1024))
        {
        }

        using var envLarge = new SharedMemoryTestEnvironment(
            channelName: envSmall.ChannelName,
            slotCount: 64);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => new UnixInMemoryTinyMessageBus(envLarge.ChannelName, maxPayloadBytes: 4096));

        Assert.True(
            ex.Message.Contains("incompatible", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("payload capacity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LargePayloads_AreDeliveredWithoutCorruption()
    {
        using var env = new SharedMemoryTestEnvironment(slotCount: 128);
        using var publisher = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 64 * 1024);
        using var subscriber = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 64 * 1024);
        using var observer = new BusObserver(subscriber, expectedCount: 24);

        await observer.WaitUntilAttachedAsync();

        for (int i = 0; i < 24; i++)
            await publisher.PublishAsync(BuildPatternPayload(i, 48 * 1024));

        await observer.WaitForCountAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(24, observer.Messages.Count);
        foreach (byte[] payload in observer.Messages)
            Assert.True(ValidatePatternPayload(payload));
    }

    [Fact]
    public async Task PublishAsync_ThrowsForPayloadLargerThanConfiguredMaximum()
    {
        using var env = new SharedMemoryTestEnvironment(slotCount: 32);
        using var bus = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 128);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await bus.PublishAsync(new byte[129]));
    }

    [Fact]
    public async Task Dispose_PreventsFurtherPublishes()
    {
        using var env = new SharedMemoryTestEnvironment(slotCount: 32);
        var bus = new UnixInMemoryTinyMessageBus(env.ChannelName, maxPayloadBytes: 256);

        bus.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await bus.PublishAsync(ToBytes("after-dispose")));
    }

    private static byte[] ToBytes(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] BuildPatternPayload(int id, int length)
    {
        string header = $"id={id:D6};";
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);

        if (headerBytes.Length >= length)
            throw new InvalidOperationException("Header is too large for payload length.");

        byte[] payload = new byte[length];
        Buffer.BlockCopy(headerBytes, 0, payload, 0, headerBytes.Length);

        for (int i = headerBytes.Length; i < payload.Length; i++)
            payload[i] = (byte)((id * 29 + i * 7) % 251);

        return payload;
    }

    private static bool ValidatePatternPayload(byte[] payload)
    {
        int sep = Array.IndexOf(payload, (byte)';');
        if (sep < 0)
            return false;

        string header = Encoding.UTF8.GetString(payload, 0, sep + 1);
        if (!header.StartsWith("id=", StringComparison.Ordinal) || !header.EndsWith(';'))
            return false;

        int id = int.Parse(header[3..^1]);

        for (int i = sep + 1; i < payload.Length; i++)
        {
            byte expected = (byte)((id * 29 + i * 7) % 251);
            if (payload[i] != expected)
                return false;
        }

        return true;
    }

    private sealed class BusObserver : IDisposable
    {
        private readonly object _gate = new();
        private readonly int _expectedCount;
        private readonly bool _deduplicate;
        private readonly TaskCompletionSource _attached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<string>? _seen;

        public ConcurrentQueue<byte[]> Messages { get; } = new();

        public BusObserver(UnixInMemoryTinyMessageBus bus, int expectedCount, bool deduplicate = false)
        {
            _expectedCount = expectedCount;
            _deduplicate = deduplicate;
            _seen = deduplicate ? new HashSet<string>(StringComparer.Ordinal) : null;

            bus.MessageReceived += OnMessageReceived;
            _attached.TrySetResult();
        }

        public Task WaitUntilAttachedAsync() => _attached.Task;

        public async Task WaitForCountAsync(TimeSpan timeout)
        {
            await _completed.Task.WaitAsync(timeout);

            if (Messages.Count < _expectedCount)
                throw new TimeoutException($"Observer finished early with {Messages.Count} / {_expectedCount} messages.");
        }

        public async Task WaitForAtLeastCountAsync(int minimumCount, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (Messages.Count >= minimumCount)
                    return;

                await Task.Delay(10);
            }

            throw new TimeoutException($"Observer did not reach {minimumCount} messages.");
        }

        private void OnMessageReceived(object? sender, XivMessageReceivedEventArgs e)
        {
            byte[] copy = e.Message.ToArray();

            if (_deduplicate)
            {
                string key = Convert.ToBase64String(copy);

                lock (_gate)
                {
                    if (!_seen!.Add(key))
                        return;

                    Messages.Enqueue(copy);

                    if (Messages.Count >= _expectedCount)
                        _completed.TrySetResult();
                }

                return;
            }

            Messages.Enqueue(copy);

            if (Messages.Count >= _expectedCount)
                _completed.TrySetResult();
        }

        public void Dispose()
        {
            _completed.TrySetResult();
        }
    }

    private sealed class SharedMemoryTestEnvironment : IDisposable
    {
        private readonly string? _previousSlotCount;
        private readonly string? _previousTtlMs;

        public string ChannelName { get; }

        public SharedMemoryTestEnvironment(int slotCount, long ttlMs = 30_000, string? channelName = null)
        {
            ChannelName = channelName ?? $"xivipc-shm-tests-{Guid.NewGuid():N}";

            _previousSlotCount = Environment.GetEnvironmentVariable("TINYIPC_SLOT_COUNT");
            _previousTtlMs = Environment.GetEnvironmentVariable("TINYIPC_MESSAGE_TTL_MS");

            Environment.SetEnvironmentVariable("TINYIPC_SLOT_COUNT", slotCount.ToString());
            Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_TTL_MS", ttlMs.ToString());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TINYIPC_SLOT_COUNT", _previousSlotCount);
            Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_TTL_MS", _previousTtlMs);
        }
    }

    private sealed class DisposableList<T> : IDisposable where T : IDisposable
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