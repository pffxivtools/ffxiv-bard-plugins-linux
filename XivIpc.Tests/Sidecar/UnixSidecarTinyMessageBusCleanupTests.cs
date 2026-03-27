using System.Collections.Concurrent;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

public sealed class UnixSidecarTinyMessageBusCleanupTests
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
}