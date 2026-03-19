using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class FunctionalTests
{
    public static IEnumerable<object[]> Backends()
    {
        yield return new object[] { "direct" };
        yield return new object[] { "sidecar" };
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ManySequentialPublishes_AreDelivered(string backend)
    {
        using TestEnvironmentScope scope = new(backend);
        using var publisher = new TinyMessageBus(scope.ChannelName);
        using var subscriber = new TinyMessageBus(scope.ChannelName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var observed = new ConcurrentQueue<string>();
        var readerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task reader = Task.Run(async () =>
        {
            readerReady.TrySetResult();

            await foreach (var item in subscriber.SubscribeAsync(cts.Token))
            {
                observed.Enqueue(GetString(item));
                if (observed.Count >= 25)
                    break;
            }
        }, cts.Token);

        await readerReady.Task;
        await WaitForSubscriptionToSettleAsync(backend, cts.Token);

        for (int i = 0; i < 25; i++)
            await PublishStringAsync(publisher, $"msg-{i}");

        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        string[] actual = observed.ToArray();
        string[] expected = Enumerable.Range(0, 25).Select(i => $"msg-{i}").ToArray();

        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task SubscribeAsync_YieldsPublishedMessages(string backend)
    {
        using TestEnvironmentScope scope = new(backend);
        using var publisher = new TinyMessageBus(scope.ChannelName);
        using var subscriber = new TinyMessageBus(scope.ChannelName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var observed = new List<string>();
        var readerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task reader = Task.Run(async () =>
        {
            readerReady.TrySetResult();

            await foreach (var item in subscriber.SubscribeAsync(cts.Token))
            {
                observed.Add(GetString(item));
                if (observed.Count == 3)
                    break;
            }
        }, cts.Token);

        await readerReady.Task;
        await WaitForSubscriptionToSettleAsync(backend, cts.Token);

        await PublishStringAsync(publisher, "a");
        await PublishStringAsync(publisher, "b");
        await PublishStringAsync(publisher, "c");

        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "a", "b", "c" }, observed);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task MultiPublisherMultiSubscriber_AllSubscribersObserveAllMessages(string backend)
    {
        using TestEnvironmentScope scope = new(backend);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        const int publisherCount = 8;
        const int subscriberCount = 8;
        const int messagesPerPublisher = 25;
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

        var allObserved = new ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>();
        var readySignals = new TaskCompletionSource[subscriberCount];
        var readerTasks = new Task[subscriberCount];

        for (int i = 0; i < subscriberCount; i++)
        {
            int subscriberIndex = i;
            readySignals[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            allObserved[subscriberIndex] = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            readerTasks[i] = Task.Run(async () =>
            {
                readySignals[subscriberIndex].TrySetResult();

                await foreach (var item in subscribers[subscriberIndex].SubscribeAsync(cts.Token))
                {
                    string message = GetString(item);
                    allObserved[subscriberIndex].TryAdd(message, 0);

                    if (allObserved[subscriberIndex].Count >= expectedTotal)
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
                var rng = new Random(unchecked(Environment.TickCount * 31 + publisherIndex));

                await startGate.Task;

                for (int messageIndex = 0; messageIndex < messagesPerPublisher; messageIndex++)
                {
                    string payload = $"pub-{publisherIndex}-msg-{messageIndex}";
                    await PublishStringAsync(publishers[publisherIndex], payload);
                    await Task.Delay(rng.Next(0, 4), cts.Token);
                }
            }, cts.Token))
            .ToArray();

        startGate.TrySetResult();

        await Task.WhenAll(publisherTasks);
        await Task.WhenAll(readerTasks).WaitAsync(TimeSpan.FromSeconds(30));

        string[] expected = Enumerable.Range(0, publisherCount)
            .SelectMany(pub => Enumerable.Range(0, messagesPerPublisher)
                .Select(msg => $"pub-{pub}-msg-{msg}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        for (int i = 0; i < subscriberCount; i++)
        {
            string[] actual = allObserved[i].Keys
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedTotal, actual.Length);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TinyMemoryMappedFile_ReadWrite_RoundTrips()
    {
        using TestEnvironmentScope scope = new("direct");
        using var file = new TinyMemoryMappedFile(scope.ChannelName, 1024);

        file.Write(Encoding.UTF8.GetBytes("hello"));

        byte[] first = file.Read();
        Assert.Equal("hello", Encoding.UTF8.GetString(first));

        file.ReadWrite(current =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(current) + "-world"));

        byte[] second = file.Read();
        Assert.Equal("hello-world", Encoding.UTF8.GetString(second));
    }

    private static async Task PublishStringAsync(TinyMessageBus bus, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);

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
    {
        if (item is BinaryData data)
            return Encoding.UTF8.GetString(data.ToArray());

        if (item is TinyMessageReceivedEventArgs e)
            return Encoding.UTF8.GetString(e.Message);

        if (item is byte[] bytes)
            return Encoding.UTF8.GetString(bytes);

        if (item is IReadOnlyList<byte> list)
            return Encoding.UTF8.GetString(list is byte[] arr ? arr : list.ToArray());

        throw new InvalidOperationException(
            $"Unsupported SubscribeAsync payload type: {item.GetType().FullName}");
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
            ChannelName = $"xivipc-tests-{backend}-{Guid.NewGuid():N}";

            _previousBackend = Environment.GetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND");
            _previousHostPath = Environment.GetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH");
            _previousUnixShell = Environment.GetEnvironmentVariable("TINYIPC_UNIX_SHELL");
            _previousSharedDir = Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");
            _testSharedDir = Path.Combine(Path.GetTempPath(), "xivipc-tests", Guid.NewGuid().ToString("N"));

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
