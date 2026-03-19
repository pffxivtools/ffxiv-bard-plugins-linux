using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using TinyIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class ParallelFunctionalTests
{
    public static IEnumerable<object[]> Backends()
    {
        yield return new object[] { "direct" };
        yield return new object[] { "sidecar" };
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ParallelReadersAndWriters_AllSubscribersObserveAllMessagesExactlyOncePerPayload(string backend)
    {
        using TestEnvironmentScope scope = new(backend);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        const int publisherCount = 6;
        const int subscriberCount = 6;
        const int messagesPerPublisher = 80;
        int expectedTotal = publisherCount * messagesPerPublisher;

        using var publishersScope = new DisposableList<TinyMessageBus>();
        using var subscribersScope = new DisposableList<TinyMessageBus>();

        TinyMessageBus[] publishers = Enumerable.Range(0, publisherCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(scope.ChannelName);
                publishersScope.Add(bus);
                return bus;
            })
            .ToArray();

        TinyMessageBus[] subscribers = Enumerable.Range(0, subscriberCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(scope.ChannelName);
                subscribersScope.Add(bus);
                return bus;
            })
            .ToArray();

        var observedBySubscriber = new ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>();
        var readySignals = new TaskCompletionSource[subscriberCount];
        var subscriberTasks = new Task[subscriberCount];

        for (int i = 0; i < subscriberCount; i++)
        {
            int subscriberIndex = i;
            readySignals[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            observedBySubscriber[subscriberIndex] = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            subscriberTasks[i] = Task.Run(async () =>
            {
                readySignals[subscriberIndex].TrySetResult();

                await foreach (var item in subscribers[subscriberIndex].SubscribeAsync(cts.Token))
                {
                    string message = GetString(item);
                    observedBySubscriber[subscriberIndex].TryAdd(message, 0);

                    if (observedBySubscriber[subscriberIndex].Count >= expectedTotal)
                        break;
                }
            }, cts.Token);
        }

        await Task.WhenAll(readySignals.Select(x => x.Task));
        await WaitForSubscriptionToSettleAsync(backend, cts.Token);

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] publisherTasks = Enumerable.Range(0, publisherCount)
            .Select(publisherIndex => Task.Run(async () =>
            {
                var rng = new Random(unchecked(Environment.TickCount * 397 + publisherIndex));

                await startGate.Task;

                for (int messageIndex = 0; messageIndex < messagesPerPublisher; messageIndex++)
                {
                    string payload = $"pub-{publisherIndex:D2}-msg-{messageIndex:D4}";
                    await PublishStringAsync(publishers[publisherIndex], payload);

                    if ((messageIndex % 7) == 0)
                        await Task.Delay(rng.Next(0, 4), cts.Token);
                }
            }, cts.Token))
            .ToArray();

        startGate.TrySetResult();

        await Task.WhenAll(publisherTasks);
        await Task.WhenAll(subscriberTasks).WaitAsync(TimeSpan.FromSeconds(45), cts.Token);

        string[] expected = Enumerable.Range(0, publisherCount)
            .SelectMany(pub => Enumerable.Range(0, messagesPerPublisher)
                .Select(msg => $"pub-{pub:D2}-msg-{msg:D4}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        for (int i = 0; i < subscriberCount; i++)
        {
            string[] actual = observedBySubscriber[i].Keys
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedTotal, actual.Length);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ParallelBursts_AcrossMultipleRounds_RemainStable(string backend)
    {
        using TestEnvironmentScope scope = new(backend);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int publisherCount = 4;
        const int subscriberCount = 4;
        const int rounds = 5;
        const int messagesPerPublisherPerRound = 30;
        int expectedTotal = publisherCount * rounds * messagesPerPublisherPerRound;

        using var publishersScope = new DisposableList<TinyMessageBus>();
        using var subscribersScope = new DisposableList<TinyMessageBus>();

        TinyMessageBus[] publishers = Enumerable.Range(0, publisherCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(scope.ChannelName);
                publishersScope.Add(bus);
                return bus;
            })
            .ToArray();

        TinyMessageBus[] subscribers = Enumerable.Range(0, subscriberCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(scope.ChannelName);
                subscribersScope.Add(bus);
                return bus;
            })
            .ToArray();

        var observedBySubscriber = new ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>();
        var readySignals = new TaskCompletionSource[subscriberCount];
        var subscriberTasks = new Task[subscriberCount];

        for (int i = 0; i < subscriberCount; i++)
        {
            int subscriberIndex = i;
            readySignals[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            observedBySubscriber[subscriberIndex] = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            subscriberTasks[i] = Task.Run(async () =>
            {
                readySignals[subscriberIndex].TrySetResult();

                await foreach (var item in subscribers[subscriberIndex].SubscribeAsync(cts.Token))
                {
                    observedBySubscriber[subscriberIndex].TryAdd(GetString(item), 0);

                    if (observedBySubscriber[subscriberIndex].Count >= expectedTotal)
                        break;
                }
            }, cts.Token);
        }

        await Task.WhenAll(readySignals.Select(x => x.Task));
        await WaitForSubscriptionToSettleAsync(backend, cts.Token);

        for (int round = 0; round < rounds; round++)
        {
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task[] publisherTasks = Enumerable.Range(0, publisherCount)
                .Select(publisherIndex => Task.Run(async () =>
                {
                    var rng = new Random(unchecked(Environment.TickCount * 113 + publisherIndex * 17 + round * 1009));

                    await startGate.Task;

                    for (int messageIndex = 0; messageIndex < messagesPerPublisherPerRound; messageIndex++)
                    {
                        string payload = $"round-{round:D2}-pub-{publisherIndex:D2}-msg-{messageIndex:D4}";
                        await PublishStringAsync(publishers[publisherIndex], payload);

                        if ((messageIndex % 5) == 0)
                            await Task.Delay(rng.Next(0, 3), cts.Token);
                    }
                }, cts.Token))
                .ToArray();

            startGate.TrySetResult();
            await Task.WhenAll(publisherTasks);

            await Task.Delay(string.Equals(backend, "sidecar", StringComparison.OrdinalIgnoreCase) ? 150 : 20, cts.Token);
        }

        await Task.WhenAll(subscriberTasks).WaitAsync(TimeSpan.FromSeconds(45), cts.Token);

        string[] expected = Enumerable.Range(0, rounds)
            .SelectMany(round => Enumerable.Range(0, publisherCount)
                .SelectMany(pub => Enumerable.Range(0, messagesPerPublisherPerRound)
                    .Select(msg => $"round-{round:D2}-pub-{pub:D2}-msg-{msg:D4}")))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        for (int i = 0; i < subscriberCount; i++)
        {
            string[] actual = observedBySubscriber[i].Keys
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedTotal, actual.Length);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ParallelWriters_WithLargePayloads_AreDeliveredToAllReaders(string backend)
    {
        using TestEnvironmentScope scope = new(backend);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int publisherCount = 4;
        const int subscriberCount = 4;
        const int messagesPerPublisher = 32;
        const int payloadBytes = 32 * 1024;
        int expectedTotal = publisherCount * messagesPerPublisher;

        using var publishersScope = new DisposableList<TinyMessageBus>();
        using var subscribersScope = new DisposableList<TinyMessageBus>();

        TinyMessageBus[] publishers = Enumerable.Range(0, publisherCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(scope.ChannelName);
                publishersScope.Add(bus);
                return bus;
            })
            .ToArray();

        TinyMessageBus[] subscribers = Enumerable.Range(0, subscriberCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(scope.ChannelName);
                subscribersScope.Add(bus);
                return bus;
            })
            .ToArray();

        var observedBySubscriber = new ConcurrentDictionary<int, ConcurrentDictionary<string, byte[]>>();
        var readySignals = new TaskCompletionSource[subscriberCount];
        var subscriberTasks = new Task[subscriberCount];

        for (int i = 0; i < subscriberCount; i++)
        {
            int subscriberIndex = i;
            readySignals[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            observedBySubscriber[subscriberIndex] = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);

            subscriberTasks[i] = Task.Run(async () =>
            {
                readySignals[subscriberIndex].TrySetResult();

                await foreach (var item in subscribers[subscriberIndex].SubscribeAsync(cts.Token))
                {
                    byte[] bytes = GetBytes(item);
                    string id = ExtractId(bytes);
                    observedBySubscriber[subscriberIndex].TryAdd(id, bytes);

                    if (observedBySubscriber[subscriberIndex].Count >= expectedTotal)
                        break;
                }
            }, cts.Token);
        }

        await Task.WhenAll(readySignals.Select(x => x.Task));
        await WaitForSubscriptionToSettleAsync(backend, cts.Token);

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] publisherTasks = Enumerable.Range(0, publisherCount)
            .Select(publisherIndex => Task.Run(async () =>
            {
                var rng = new Random(unchecked(Environment.TickCount * 173 + publisherIndex));

                await startGate.Task;

                for (int messageIndex = 0; messageIndex < messagesPerPublisher; messageIndex++)
                {
                    byte[] payload = BuildLargePayload(publisherIndex, messageIndex, payloadBytes);
                    await PublishBytesAsync(publishers[publisherIndex], payload);

                    if ((messageIndex % 4) == 0)
                        await Task.Delay(rng.Next(0, 4), cts.Token);
                }
            }, cts.Token))
            .ToArray();

        startGate.TrySetResult();

        await Task.WhenAll(publisherTasks);
        await Task.WhenAll(subscriberTasks).WaitAsync(TimeSpan.FromSeconds(60), cts.Token);

        string[] expectedIds = Enumerable.Range(0, publisherCount)
            .SelectMany(pub => Enumerable.Range(0, messagesPerPublisher)
                .Select(msg => $"pub={pub:D2};msg={msg:D4};"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        for (int subscriberIndex = 0; subscriberIndex < subscriberCount; subscriberIndex++)
        {
            string[] actualIds = observedBySubscriber[subscriberIndex].Keys
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedIds, actualIds);

            foreach ((string id, byte[] payload) in observedBySubscriber[subscriberIndex])
            {
                Assert.True(payload.Length >= payloadBytes, $"Payload for {id} was unexpectedly short.");
                Assert.True(ValidateLargePayload(payload), $"Payload for {id} failed deterministic validation.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task SubscriberChurn_DuringParallelPublishing_DoesNotBreakStableSubscribers(string backend)
    {
        using TestEnvironmentScope scope = new(backend);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const int stableSubscriberCount = 3;
        const int publisherCount = 5;
        const int messagesPerPublisher = 60;
        int expectedStableTotal = publisherCount * messagesPerPublisher;

        using var stableSubscribersScope = new DisposableList<TinyMessageBus>();
        using var publishersScope = new DisposableList<TinyMessageBus>();

        TinyMessageBus[] stableSubscribers = Enumerable.Range(0, stableSubscriberCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(scope.ChannelName);
                stableSubscribersScope.Add(bus);
                return bus;
            })
            .ToArray();

        TinyMessageBus[] publishers = Enumerable.Range(0, publisherCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(scope.ChannelName);
                publishersScope.Add(bus);
                return bus;
            })
            .ToArray();

        var observedByStableSubscriber = new ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>();
        var stableReadySignals = new TaskCompletionSource[stableSubscriberCount];
        var stableReaderTasks = new Task[stableSubscriberCount];

        for (int i = 0; i < stableSubscriberCount; i++)
        {
            int subscriberIndex = i;
            stableReadySignals[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            observedByStableSubscriber[subscriberIndex] = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            stableReaderTasks[i] = Task.Run(async () =>
            {
                stableReadySignals[subscriberIndex].TrySetResult();

                await foreach (var item in stableSubscribers[subscriberIndex].SubscribeAsync(cts.Token))
                {
                    observedByStableSubscriber[subscriberIndex].TryAdd(GetString(item), 0);

                    if (observedByStableSubscriber[subscriberIndex].Count >= expectedStableTotal)
                        break;
                }
            }, cts.Token);
        }

        await Task.WhenAll(stableReadySignals.Select(x => x.Task));
        await WaitForSubscriptionToSettleAsync(backend, cts.Token);

        Task churnTask = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                using var tempBus = new TinyMessageBus(scope.ChannelName);
                using var localCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                Task reader = Task.Run(async () =>
                {
                    int seen = 0;
                    await foreach (var item in tempBus.SubscribeAsync(localCts.Token))
                    {
                        seen++;
                        if (seen >= 10)
                            break;
                    }
                }, localCts.Token);

                await WaitForSubscriptionToSettleAsync(backend, cts.Token);
                await Task.Delay(i % 2 == 0 ? 25 : 60, cts.Token);
                localCts.Cancel();

                try
                {
                    await reader.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (TimeoutException)
                {
                }
            }
        }, cts.Token);

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] publisherTasks = Enumerable.Range(0, publisherCount)
            .Select(publisherIndex => Task.Run(async () =>
            {
                var rng = new Random(unchecked(Environment.TickCount * 271 + publisherIndex));

                await startGate.Task;

                for (int messageIndex = 0; messageIndex < messagesPerPublisher; messageIndex++)
                {
                    string payload = $"pub-{publisherIndex:D2}-msg-{messageIndex:D4}";
                    await PublishStringAsync(publishers[publisherIndex], payload);

                    if ((messageIndex % 6) == 0)
                        await Task.Delay(rng.Next(0, 4), cts.Token);
                }
            }, cts.Token))
            .ToArray();

        startGate.TrySetResult();

        await Task.WhenAll(publisherTasks);
        await churnTask;
        await Task.WhenAll(stableReaderTasks).WaitAsync(TimeSpan.FromSeconds(45), cts.Token);

        string[] expected = Enumerable.Range(0, publisherCount)
            .SelectMany(pub => Enumerable.Range(0, messagesPerPublisher)
                .Select(msg => $"pub-{pub:D2}-msg-{msg:D4}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        for (int i = 0; i < stableSubscriberCount; i++)
        {
            string[] actual = observedByStableSubscriber[i].Keys
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, actual);
        }
    }

    private static async Task PublishStringAsync(TinyMessageBus bus, string value)
        => await PublishBytesAsync(bus, Encoding.UTF8.GetBytes(value));

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

    private static async Task WaitForSubscriptionToSettleAsync(string backend, CancellationToken cancellationToken)
    {
        if (string.Equals(backend, "sidecar", StringComparison.OrdinalIgnoreCase))
            await Task.Delay(750, cancellationToken);
        else
            await Task.Delay(25, cancellationToken);
    }

    private static string GetString(object item)
        => Encoding.UTF8.GetString(GetBytes(item));

    private static byte[] GetBytes(object item)
    {
        if (item is BinaryData data)
            return data.ToArray();

        if (item is TinyMessageReceivedEventArgs e)
            return e.Message;

        if (item is byte[] bytes)
            return bytes;

        if (item is IReadOnlyList<byte> list)
            return list is byte[] arr ? arr : list.ToArray();

        throw new InvalidOperationException(
            $"Unsupported SubscribeAsync payload type: {item.GetType().FullName}");
    }

    private static byte[] BuildLargePayload(int publisherIndex, int messageIndex, int totalBytes)
    {
        string header = $"pub={publisherIndex:D2};msg={messageIndex:D4};";
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);

        if (headerBytes.Length >= totalBytes)
            throw new InvalidOperationException("Large payload header is too large for configured payload length.");

        byte[] payload = new byte[totalBytes];
        Buffer.BlockCopy(headerBytes, 0, payload, 0, headerBytes.Length);

        for (int i = headerBytes.Length; i < payload.Length; i++)
            payload[i] = (byte)((publisherIndex * 31 + messageIndex * 17 + i) % 251);

        return payload;
    }

    private static string ExtractId(byte[] payload)
    {
        int firstSep = Array.IndexOf(payload, (byte)';');
        int secondSep = firstSep >= 0 ? Array.IndexOf(payload, (byte)';', firstSep + 1) : -1;

        if (firstSep < 0 || secondSep < 0)
            throw new InvalidOperationException("Payload header was malformed.");

        return Encoding.UTF8.GetString(payload, 0, secondSep + 1);
    }

    private static bool ValidateLargePayload(byte[] payload)
    {
        string id = ExtractId(payload);

        string[] parts = id.Split(';', StringSplitOptions.RemoveEmptyEntries);
        int pub = int.Parse(parts[0]["pub=".Length..]);
        int msg = int.Parse(parts[1]["msg=".Length..]);

        int headerLength = Encoding.UTF8.GetByteCount(id);

        for (int i = headerLength; i < payload.Length; i++)
        {
            byte expected = (byte)((pub * 31 + msg * 17 + i) % 251);
            if (payload[i] != expected)
                return false;
        }

        return true;
    }

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly string? _previousBackend;
        private readonly string? _previousHostPath;
        private readonly string? _previousUnixShell;
        private readonly string? _previousSharedDir;
        private readonly string _testSharedDir;

        public string Backend { get; }
        public string ChannelName { get; }

        public TestEnvironmentScope(string backend)
        {
            Backend = backend;
            ChannelName = $"xivipc-parallel-tests-{backend}-{Guid.NewGuid():N}";

            _previousBackend = Environment.GetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND");
            _previousHostPath = Environment.GetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH");
            _previousUnixShell = Environment.GetEnvironmentVariable("TINYIPC_UNIX_SHELL");
            _previousSharedDir = Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");
            _testSharedDir = Path.Combine(Path.GetTempPath(), "xivipc-parallel-tests", Guid.NewGuid().ToString("N"));

            Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", backend);

            if (string.Equals(backend, "sidecar", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(_testSharedDir);
                Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", _testSharedDir);

                string? hostPath = ResolveNativeHostPath();
                if (!string.IsNullOrWhiteSpace(hostPath))
                    Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", hostPath);

                if (string.IsNullOrWhiteSpace(_previousUnixShell) && OperatingSystem.IsLinux())
                    Environment.SetEnvironmentVariable("TINYIPC_UNIX_SHELL", "/bin/sh");
            }
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", _previousBackend);
            Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", _previousHostPath);
            Environment.SetEnvironmentVariable("TINYIPC_UNIX_SHELL", _previousUnixShell);
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", _previousSharedDir);

            try
            {
                if (Directory.Exists(_testSharedDir))
                    Directory.Delete(_testSharedDir, recursive: true);
            }
            catch
            {
            }
        }

        private static string? ResolveNativeHostPath()
        {
            string? explicitPath = Environment.GetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
                return explicitPath;

            string baseDir = AppContext.BaseDirectory;

            string[] candidates =
            {
                Path.Combine(baseDir, "XivIpc.NativeHost"),
                Path.Combine(baseDir, "XivIpc.NativeHost.dll"),
                Path.Combine(baseDir, "XivIpc.NativeHost.exe"),

                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net10.0", "XivIpc.NativeHost.dll")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net9.0", "XivIpc.NativeHost.dll")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net10.0", "XivIpc.NativeHost.dll")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net9.0", "XivIpc.NativeHost.dll")),

                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net10.0", "XivIpc.NativeHost")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net9.0", "XivIpc.NativeHost")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net10.0", "XivIpc.NativeHost")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net9.0", "XivIpc.NativeHost"))
            };

            return candidates.FirstOrDefault(File.Exists);
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
