using System.Text;
using XivIpc.Internal;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class BrokeredRingStorageTests
{
    [Fact]
    public void RingHostAndClientStorage_PublishAndDrainMessages()
    {
        string sharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-brokered-ring-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sharedDirectory);

        using IDisposable overrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.SharedDirectory, sharedDirectory),
            (TinyIpcEnvironment.SharedGroup, ProductionPathTestEnvironment.ResolveSharedGroup()));

        SidecarTransportSettings transportSettings = new(
            SidecarStorageKind.Ring,
            RingSlotCount: 64,
            HeartbeatIntervalMs: 2_000,
            HeartbeatTimeoutMs: 60_000,
            MessageTtlMs: 1_000);

        using IBrokeredHostStorage hostStorage = BrokeredRingStorageFormat.Instance.CreateHostStorage(
            "brokered-ring-storage-test",
            requestedBufferBytes: 4096,
            sharedDirectory,
            Guid.NewGuid().ToString("N"),
            transportSettings);

        BrokeredStorageAttachInfo attachInfo = hostStorage.CreateAttachInfo(sessionId: 42);
        using IBrokeredClientStorage clientStorage = BrokeredRingStorageFormat.Instance.AttachClientStorage(attachInfo, transportSettings.MessageTtlMs);

        BrokeredStorageParticipant publisher = new(SessionId: 42, ClientInstanceId: Guid.NewGuid());
        BrokeredStorageParticipant subscriber = new(SessionId: 84, ClientInstanceId: Guid.NewGuid());
        long nextSequence = attachInfo.StartSequence;

        hostStorage.Publish(publisher, Encoding.UTF8.GetBytes("alpha"));
        hostStorage.Publish(publisher, Encoding.UTF8.GetBytes("beta"));

        BrokeredStorageDrainResult drain = clientStorage.Drain(subscriber, ref nextSequence);

        string[] observed = drain.Messages.Select(Encoding.UTF8.GetString).ToArray();
        Assert.Equal(new[] { "alpha", "beta" }, observed);
        Assert.Equal(attachInfo.StartSequence + 2, nextSequence);
    }
}
