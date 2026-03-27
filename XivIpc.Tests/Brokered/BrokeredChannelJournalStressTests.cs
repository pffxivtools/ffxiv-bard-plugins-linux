using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class BrokeredChannelJournalStressTests
{
    [Fact]
    public async Task BrokeredPath_SustainsConcurrentPublishers_AndSubscriberChurn_WhenEnabled()
    {
        if (!ShouldRunStress())
            return;

        using var scope = new StressEnvironmentScope();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var publishersScope = new StressDisposableList<TinyMessageBus>();
        using var subscribersScope = new StressDisposableList<TinyMessageBus>();

        const int publisherCount = 4;
        const int stableSubscriberCount = 3;
        const int messagesPerPublisher = 120;
        const int requestedBufferBytes = 4 * 1024;

        TinyMessageBus[] publishers = Enumerable.Range(0, publisherCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(new TinyMemoryMappedFile(scope.ChannelName, requestedBufferBytes), disposeFile: true);
                publishersScope.Add(bus);
                return bus;
            })
            .ToArray();

        TinyMessageBus[] subscribers = Enumerable.Range(0, stableSubscriberCount)
            .Select(_ =>
            {
                var bus = new TinyMessageBus(new TinyMemoryMappedFile(scope.ChannelName, requestedBufferBytes), disposeFile: true);
                subscribersScope.Add(bus);
                return bus;
            })
            .ToArray();

        await Task.WhenAll(publishers.Concat(subscribers).Select(bus => bus.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(10))));

        var observed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var validationFailures = new ConcurrentQueue<string>();

        Task[] subscriberTasks = subscribers
            .Select(bus => Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in bus.SubscribeAsync(cts.Token))
                    {
                        string message = Encoding.UTF8.GetString(GetBytes(item));
                        if (!message.StartsWith("stress-", StringComparison.Ordinal))
                            validationFailures.Enqueue($"Unexpected payload prefix: {message}");
                        else
                            observed.TryAdd(message, 0);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, cts.Token))
            .ToArray();

        Task churnTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                using var transient = new TinyMessageBus(new TinyMemoryMappedFile(scope.ChannelName, requestedBufferBytes), disposeFile: true);
                await transient.WaitForConnectedForDiagnosticsAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(75, cts.Token);
            }
        }, cts.Token);

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] publisherTasks = Enumerable.Range(0, publisherCount)
            .Select(publisherIndex => Task.Run(async () =>
            {
                await startGate.Task;
                for (int messageIndex = 0; messageIndex < messagesPerPublisher; messageIndex++)
                {
                    byte[] payload = Encoding.UTF8.GetBytes($"stress-pub-{publisherIndex:D2}-msg-{messageIndex:D4}");
                    await PublishBytesAsync(publishers[publisherIndex], payload);

                    if ((messageIndex % 5) == 0)
                        await Task.Delay(2, cts.Token);
                }
            }, cts.Token))
            .ToArray();

        startGate.TrySetResult();
        await Task.WhenAll(publisherTasks);
        await Task.Delay(2_000, cts.Token);

        cts.Cancel();

        try { await churnTask; } catch (OperationCanceledException) { }
        await Task.WhenAll(subscriberTasks);

        Assert.Empty(validationFailures);
        Assert.NotEmpty(observed);
    }

    private static bool ShouldRunStress()
    {
        string? raw = Environment.GetEnvironmentVariable("TINYIPC_ENABLE_JOURNAL_STRESS_TESTS");
        return string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

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

        for (int index = 1; index < parameters.Length; index++)
            args[index] = parameters[index].HasDefaultValue ? parameters[index].DefaultValue : GetDefault(parameters[index].ParameterType);

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

    private static byte[] GetBytes(object item)
    {
        if (item is BinaryData data)
            return data.ToArray();

        if (item is TinyMessageReceivedEventArgs e)
            return e.Message is byte[] bytes ? bytes : e.Message.ToArray();

        if (item is byte[] raw)
            return raw;

        if (item is IReadOnlyList<byte> list)
            return list is byte[] arr ? arr : list.ToArray();

        throw new InvalidOperationException($"Unsupported SubscribeAsync payload type: {item.GetType().FullName}");
    }

    private sealed class StressEnvironmentScope : IDisposable
    {
        private readonly IDisposable _overrides;
        private readonly string _sharedDirectory;

        public StressEnvironmentScope()
        {
            ChannelName = $"xivipc-journal-stress-{Guid.NewGuid():N}";
            _sharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-journal-stress", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sharedDirectory);
            ProductionPathTestEnvironment.ResetLogger();

            var overrides = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [TinyIpcEnvironment.MessageBusBackend] = "sidecar",
                [TinyIpcEnvironment.SharedDirectory] = _sharedDirectory,
                [TinyIpcEnvironment.SharedGroup] = ProductionPathTestEnvironment.ResolveSharedGroup(),
                [TinyIpcEnvironment.NativeHostPath] = XivIpc.Messaging.UnixSidecarProcessManager.ResolveNativeHostPath(),
                [TinyIpcEnvironment.LogDirectory] = _sharedDirectory,
                [TinyIpcEnvironment.LogLevel] = "info",
                [TinyIpcEnvironment.EnableLogging] = "1",
                [TinyIpcEnvironment.FileNotifier] = "auto",
                [TinyIpcEnvironment.MessageTtlMs] = "50",
                [TinyIpcEnvironment.BrokerIdleShutdownMs] = "2000"
            };

            if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.UnixShell)))
                overrides[TinyIpcEnvironment.UnixShell] = "/bin/sh";

            _overrides = TinyIpcEnvironment.Override(overrides);
        }

        public string ChannelName { get; }

        public void Dispose()
        {
            _overrides.Dispose();
            ProductionPathTestEnvironment.ResetLogger();

            try
            {
                if (Directory.Exists(_sharedDirectory))
                    Directory.Delete(_sharedDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class StressDisposableList<T> : IDisposable where T : IDisposable
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
