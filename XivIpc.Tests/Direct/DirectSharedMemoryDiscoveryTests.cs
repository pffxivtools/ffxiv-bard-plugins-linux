using XivIpc.Internal;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class DirectSharedMemoryDiscoveryTests
{
    [Fact]
    public void BuildSharedObjectName_IsDeterministicWithinScope_AndVariesAcrossSharedDirectories()
    {
        const string channelName = "xivipc-direct-discovery";

        using IDisposable firstScope = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.SharedDirectory, "/tmp/xivipc-direct-scope-a"));

        string first = UnixSharedStorageHelpers.BuildSharedObjectName(channelName, "bus_shm");
        string second = UnixSharedStorageHelpers.BuildSharedObjectName(channelName, "bus_shm");

        Assert.Equal(first, second);

        using IDisposable secondScope = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.SharedDirectory, "/tmp/xivipc-direct-scope-b"));

        string third = UnixSharedStorageHelpers.BuildSharedObjectName(channelName, "bus_shm");

        Assert.NotEqual(first, third);
    }

    [Fact]
    public void UnixSharedMemoryTinyMessageBus_CreatesMetadataFileWithSizingAndGeneration()
    {
        using TestEnvironmentScope scope = new();
        using var bus = new UnixSharedMemoryTinyMessageBus(scope.ChannelName, maxPayloadBytes: 4096);

        string metadataPath = UnixSharedStorageHelpers.BuildSharedFilePath(scope.ChannelName, "busmeta");
        Assert.True(File.Exists(metadataPath));

        string metadata = File.ReadAllText(metadataPath);
        Assert.Contains("magic=TINYIPC_BUS_META", metadata, StringComparison.Ordinal);
        Assert.Contains("version=2", metadata, StringComparison.Ordinal);
        Assert.Contains($"channel={scope.ChannelName}", metadata, StringComparison.Ordinal);
        Assert.Contains("slotCount=", metadata, StringComparison.Ordinal);
        Assert.Contains("slotPayloadBytes=", metadata, StringComparison.Ordinal);
        Assert.Contains("imageSize=", metadata, StringComparison.Ordinal);
        Assert.Contains("generationId=", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void UnixSharedMemoryTinyMessageBus_ThrowsWhenConcurrentMemberRequestsLargerCapacityThanExistingBus()
    {
        using TestEnvironmentScope scope = new();
        using var smaller = new UnixSharedMemoryTinyMessageBus(scope.ChannelName, maxPayloadBytes: 4096);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => new UnixSharedMemoryTinyMessageBus(scope.ChannelName, maxPayloadBytes: 8192));

        Assert.Contains("incompatible", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly IDisposable _overrides;

        public TestEnvironmentScope()
        {
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-direct-discovery-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SharedDirectory);

            _overrides = TinyIpcEnvironment.Override(
                (TinyIpcEnvironment.MessageBusBackend, "direct"),
                (TinyIpcEnvironment.DirectStorageMode, "shared-memory"),
                (TinyIpcEnvironment.SharedDirectory, SharedDirectory),
                (TinyIpcEnvironment.SlotCount, "64"),
                (TinyIpcEnvironment.MessageTtlMs, "30000"));

            ChannelName = $"xivipc-direct-discovery-{Guid.NewGuid():N}";
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }

        public void Dispose()
        {
            _overrides.Dispose();
        }
    }
}
