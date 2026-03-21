using System.Collections.Concurrent;
using XivIpc.Internal;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

public sealed class UnixSidecarTinyMessageBusExceptionHandlingTests
{
    [Fact]
    public async Task ConnectionCleanup_WaitsForConnectionTasks_BeforeDisposingResources()
    {
        var eventLoopTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new ConcurrentQueue<string>();

        Task cleanupTask = UnixSidecarTinyMessageBus.RunConnectionCleanupForTestsAsync(
            cancelConnection: () => steps.Enqueue("cancel"),
            sendDisposeFrame: () => steps.Enqueue("dispose-frame"),
            shutdownTransport: () => steps.Enqueue("shutdown"),
            eventLoopTask: eventLoopTcs.Task,
            heartbeatTask: heartbeatTcs.Task,
            disposeResources: () => steps.Enqueue("dispose-resources"),
            waitForConnectionTasks: TimeSpan.FromSeconds(2));

        await Task.Delay(100);
        Assert.DoesNotContain("dispose-resources", steps);

        eventLoopTcs.SetResult(true);

        await Task.Delay(100);
        Assert.DoesNotContain("dispose-resources", steps);

        heartbeatTcs.SetResult(true);

        await cleanupTask.WaitAsync(TimeSpan.FromSeconds(2));

        string[] observed = steps.ToArray();
        Assert.Equal("cancel", observed[0]);
        Assert.Equal("dispose-frame", observed[1]);
        Assert.Equal("shutdown", observed[2]);
        Assert.Equal("dispose-resources", observed[^1]);
    }

    [Fact]
    public async Task ConnectionCleanup_LogsThrownExceptions_AndStillCompletes()
    {
        using var logScope = new TinyIpcLogScope();

        int disposeCalls = 0;

        await UnixSidecarTinyMessageBus.RunConnectionCleanupForTestsAsync(
            cancelConnection: () => throw new InvalidOperationException("cancel boom"),
            sendDisposeFrame: () => throw new InvalidOperationException("dispose boom"),
            shutdownTransport: () => throw new InvalidOperationException("shutdown boom"),
            eventLoopTask: Task.CompletedTask,
            heartbeatTask: Task.CompletedTask,
            disposeResources: () =>
            {
                Interlocked.Increment(ref disposeCalls);
                throw new InvalidOperationException("dispose resources boom");
            },
            waitForConnectionTasks: TimeSpan.FromMilliseconds(250));

        Assert.Equal(1, disposeCalls);

        string logs = logScope.ReadAllLogs();
        Assert.Contains("event=ConnectionCleanupCancelFailed", logs, StringComparison.Ordinal);
        Assert.Contains("event=ConnectionCleanupDisposeFrameFailed", logs, StringComparison.Ordinal);
        Assert.Contains("event=ConnectionCleanupShutdownFailed", logs, StringComparison.Ordinal);
        Assert.Contains("event=ConnectionCleanupDisposeResourcesFailed", logs, StringComparison.Ordinal);
        Assert.Contains("cancel boom", logs, StringComparison.Ordinal);
        Assert.Contains("dispose boom", logs, StringComparison.Ordinal);
        Assert.Contains("shutdown boom", logs, StringComparison.Ordinal);
        Assert.Contains("dispose resources boom", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionCleanup_LogsTimeoutWaitingForDependentTasks_AndStillDisposesResources()
    {
        using var logScope = new TinyIpcLogScope();

        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposeCalls = 0;

        await UnixSidecarTinyMessageBus.RunConnectionCleanupForTestsAsync(
            cancelConnection: () => { },
            sendDisposeFrame: null,
            shutdownTransport: () => { },
            eventLoopTask: never.Task,
            heartbeatTask: null,
            disposeResources: () => Interlocked.Increment(ref disposeCalls),
            waitForConnectionTasks: TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, disposeCalls);

        string logs = logScope.ReadAllLogs();
        Assert.Contains("event=ConnectionCleanupWaitTimedOut", logs, StringComparison.Ordinal);
        Assert.Contains("dependentTaskCount=\"1\"", logs, StringComparison.Ordinal);
    }

    private sealed class TinyIpcLogScope : IDisposable
    {
        private readonly string _logDirectory;
        private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);

        public TinyIpcLogScope()
        {
            _logDirectory = Path.Combine(Path.GetTempPath(), "XivIpc.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_logDirectory);

            CaptureAndSet("TINYIPC_ENABLE_LOGGING", "1");
            CaptureAndSet("TINYIPC_LOG_LEVEL", "debug");
            CaptureAndSet("TINYIPC_LOG_DIR", _logDirectory);
            CaptureAndSet("TINYIPC_LOG_FILE_COUNT", "1");
            CaptureAndSet("TINYIPC_LOG_MAX_BYTES", "1048576");

            TinyIpcLogger.ResetForTests();
            TinyIpcLogger.EnsureInitialized(RuntimeEnvironmentDetector.Detect());
        }

        public string ReadAllLogs()
        {
            TinyIpcLogger.ResetForTests();

            string[] files = Directory.GetFiles(_logDirectory, "*.log", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);

            return string.Join(
                Environment.NewLine,
                files.Select(File.ReadAllText));
        }

        public void Dispose()
        {
            TinyIpcLogger.ResetForTests();

            foreach ((string key, string? value) in _previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);

            try
            {
                if (Directory.Exists(_logDirectory))
                    Directory.Delete(_logDirectory, recursive: true);
            }
            catch
            {
            }
        }

        private void CaptureAndSet(string name, string value)
        {
            _previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}