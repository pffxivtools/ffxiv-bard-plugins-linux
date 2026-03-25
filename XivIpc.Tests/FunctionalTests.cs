using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Diagnostics;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class FunctionalTests
{
    private const string BarrierPrefix = "__tinyipc_test_barrier__:";

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
            await foreach (var item in subscriber.SubscribeAsync(cts.Token))
            {
                string message = GetString(item);
                if (TryHandleBarrier(message, readerReady))
                    continue;

                observed.Enqueue(message);
                if (observed.Count >= 25)
                    break;
            }
        }, cts.Token);

        await WaitForBusesReadyAsync(backend, new[] { publisher, subscriber }, cts.Token);
        await AwaitSubscriptionsReadyAsync(publisher, new[] { readerReady }, cts.Token);

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
            await foreach (var item in subscriber.SubscribeAsync(cts.Token))
            {
                string message = GetString(item);
                if (TryHandleBarrier(message, readerReady))
                    continue;

                observed.Add(message);
                if (observed.Count == 3)
                    break;
            }
        }, cts.Token);

        await WaitForBusesReadyAsync(backend, new[] { publisher, subscriber }, cts.Token);
        await AwaitSubscriptionsReadyAsync(publisher, new[] { readerReady }, cts.Token);

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

        await WaitForBusesReadyAsync(backend, publishers.Concat(subscribers), cts.Token);

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
                await foreach (var item in subscribers[subscriberIndex].SubscribeAsync(cts.Token))
                {
                    string message = GetString(item);
                    if (TryHandleBarrier(message, readySignals[subscriberIndex]))
                        continue;

                    allObserved[subscriberIndex].TryAdd(message, 0);

                    if (allObserved[subscriberIndex].Count >= expectedTotal)
                        break;
                }
            }, cts.Token);
        }

        await AwaitSubscriptionsReadyAsync(publishers[0], readySignals, cts.Token);

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
        await WaitForExpectedSubscriberCountsAsync(scope, backend, allObserved, expectedTotal, cts.Token);
        await Task.WhenAll(readerTasks).WaitAsync(TimeSpan.FromSeconds(10));

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

    private static bool TryHandleBarrier(string message, TaskCompletionSource readySignal)
    {
        if (!message.StartsWith(BarrierPrefix, StringComparison.Ordinal))
            return false;

        readySignal.TrySetResult();
        return true;
    }

    private static async Task AwaitSubscriptionsReadyAsync(TinyMessageBus barrierPublisher, IEnumerable<TaskCompletionSource> readySignals, CancellationToken cancellationToken)
    {
        Task[] waitTasks = readySignals.Select(static signal => signal.Task).ToArray();
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (waitTasks.All(static task => task.IsCompleted))
                return;

            string barrier = BarrierPrefix + Guid.NewGuid().ToString("N");
            await PublishStringAsync(barrierPublisher, barrier);

            try
            {
                await Task.WhenAll(waitTasks).WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
                return;
            }
            catch (TimeoutException)
            {
            }
        }

        throw new TimeoutException("Timed out waiting for subscribers to observe the live subscription barrier.");
    }

    private static async Task WaitForBusesReadyAsync(string backend, IEnumerable<TinyMessageBus> buses, CancellationToken cancellationToken)
    {
        if (!IsSidecarStyleBackend(backend))
            return;

        Task[] tasks = buses
            .Select(bus => bus.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(30)))
            .ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(40), cancellationToken);
    }

    private static async Task WaitForExpectedSubscriberCountsAsync(
        TestEnvironmentScope scope,
        string backend,
        ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> allObserved,
        int expectedTotal,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (allObserved.Values.All(messages => messages.Count >= expectedTotal))
                return;

            await Task.Delay(100, cancellationToken);
        }

        string counts = string.Join(
            ", ",
            allObserved
                .OrderBy(pair => pair.Key)
                .Select(pair => $"subscriber[{pair.Key}]={pair.Value.Count}/{expectedTotal}"));

        string diagnostics = string.Empty;
        if (IsSidecarStyleBackend(backend))
        {
            string logTail = scope.ReadLatestLogTail();
            if (!string.IsNullOrWhiteSpace(logTail))
                diagnostics = $"{Environment.NewLine}Latest sidecar log tail:{Environment.NewLine}{logTail}";
        }

        throw new TimeoutException($"Timed out waiting for all subscribers to observe all messages. {counts}{diagnostics}");
    }

    private static bool IsSidecarStyleBackend(string backend)
        => string.Equals(backend, "sidecar", StringComparison.OrdinalIgnoreCase)
            || ProductionPathTestEnvironment.IsProductionPath(backend);

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
        private readonly IDisposable _overrides;
        private readonly string _testSharedDir;

        public string Backend { get; }
        public string ChannelName { get; }

        public TestEnvironmentScope(string backend)
        {
            Backend = backend;
            ChannelName = $"xivipc-tests-{backend}-{Guid.NewGuid():N}";

            _testSharedDir = Path.Combine(Path.GetTempPath(), "xivipc-tests", Guid.NewGuid().ToString("N"));

            ProductionPathTestEnvironment.ResetLogger();
            var overrides = new Dictionary<string, string?>(StringComparer.Ordinal);

            if (ProductionPathTestEnvironment.IsProductionPath(backend))
            {
                Directory.CreateDirectory(_testSharedDir);
                ProductionPathTestEnvironment.PrepareStagedNativeHost(_testSharedDir);

                overrides[TinyIpcEnvironment.MessageBusBackend] = null;
                overrides[TinyIpcEnvironment.NativeHostPath] = null;
                overrides[TinyIpcEnvironment.SharedDirectory] = ProductionPathTestEnvironment.ToWindowsStylePath(_testSharedDir);
                overrides[TinyIpcEnvironment.SharedGroup] = ProductionPathTestEnvironment.ResolveSharedGroup();
                overrides[TinyIpcEnvironment.LogDirectory] = ProductionPathTestEnvironment.ToWindowsStylePath(_testSharedDir);
                overrides[TinyIpcEnvironment.LogLevel] = "info";
                overrides[TinyIpcEnvironment.EnableLogging] = "1";
                overrides[TinyIpcEnvironment.FileNotifier] = "auto";
            }
            else
            {
                overrides[TinyIpcEnvironment.MessageBusBackend] = backend;
            }

            if (string.Equals(backend, "sidecar", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(_testSharedDir);
                overrides[TinyIpcEnvironment.SharedDirectory] = _testSharedDir;
                overrides[TinyIpcEnvironment.SharedGroup] = ResolveCurrentSharedGroup();
                overrides[TinyIpcEnvironment.LogDirectory] = _testSharedDir;
                overrides[TinyIpcEnvironment.LogLevel] = "info";
                overrides[TinyIpcEnvironment.EnableLogging] = "1";

                string? hostPath = ResolveNativeHostPath();
                if (!string.IsNullOrWhiteSpace(hostPath))
                    overrides[TinyIpcEnvironment.NativeHostPath] = hostPath;

                if (string.IsNullOrWhiteSpace(TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.UnixShell)) && OperatingSystem.IsLinux())
                    overrides[TinyIpcEnvironment.UnixShell] = "/bin/sh";
            }

            _overrides = TinyIpcEnvironment.Override(overrides);
        }

        public string ReadLatestLogTail()
        {
            string contents = ProductionPathTestEnvironment.ReadLatestLogOrEmpty(_testSharedDir);
            if (string.IsNullOrWhiteSpace(contents))
                return string.Empty;

            string[] lines = contents
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(20)
                .ToArray();

            return string.Join(Environment.NewLine, lines);
        }

        public void Dispose()
        {
            _overrides.Dispose();
            ProductionPathTestEnvironment.ResetLogger();

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
            string? explicitPath = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.NativeHostPath);
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
