using System.Collections.Concurrent;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class DirectSharedMemorySelectionTests
{
    [Fact]
    public async Task TinyMessageBus_DirectSharedMemoryStorage_DeliversMessages()
    {
        string sharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-direct-shm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sharedDirectory);

        using IDisposable overrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.MessageBusBackend, "direct"),
            (TinyIpcEnvironment.DirectStorageMode, "shared-memory"),
            (TinyIpcEnvironment.SharedDirectory, sharedDirectory),
            (TinyIpcEnvironment.SlotCount, "64"),
            (TinyIpcEnvironment.MessageTtlMs, "30000"));

        string channelName = $"xivipc-direct-shm-{Guid.NewGuid():N}";

        using var publisher = new TinyMessageBus(new TinyMemoryMappedFile(channelName, 4096), disposeFile: true);
        using var subscriber = new TinyMessageBus(new TinyMemoryMappedFile(channelName, 4096), disposeFile: true);

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

        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "a", "b", "c" }, observed.ToArray());
    }
}
