using XivIpc.Internal;

namespace XivIpc.Messaging;

internal enum BrokeredStorageKind
{
    Journal,
    Ring
}

internal readonly record struct BrokeredStorageParticipant(
    long SessionId,
    Guid ClientInstanceId);

internal readonly record struct BrokeredStorageAttachInfo(
    BrokeredStorageKind StorageKind,
    string Path,
    int CapacityBytes,
    int MaxPayloadBytes,
    long StartSequence,
    long SessionId,
    int Length,
    int ProtocolVersion = 4);

internal readonly record struct BrokeredStoragePublishResult(
    long HeadSequenceBefore,
    long TailSequenceBefore,
    long HeadSequenceAfter,
    long TailSequenceAfter,
    int RetainedBytesBefore,
    int RetainedBytesAfter,
    bool PrunedExpired,
    bool Compacted);

internal readonly record struct BrokeredStorageDrainResult(
    List<byte[]> Messages,
    bool CaughtUpToTail,
    long TailSequenceObserved,
    long HeadSequenceObserved,
    long NextSequenceBefore,
    long NextSequenceAfter,
    long LagBefore,
    int RetainedBytes);

internal interface IBrokeredStorageFormat
{
    BrokeredStorageKind StorageKind { get; }
    IBrokeredHostStorage CreateHostStorage(string channelName, int requestedBufferBytes, string brokerDirectory, string instanceId, SidecarTransportSettings transportSettings);
    IBrokeredClientStorage AttachClientStorage(BrokeredStorageAttachInfo attachInfo, long minAgeMs);
}

internal interface IBrokeredHostStorage : IDisposable
{
    BrokeredStorageKind StorageKind { get; }
    string Path { get; }
    int CapacityBytes { get; }
    int MaxPayloadBytes { get; }
    int Length { get; }
    long MinMessageAgeMs { get; }
    int RequestedBufferBytes { get; }
    int EffectiveBudgetBytes { get; }
    bool BudgetWasCapped { get; }

    BrokeredStoragePublishResult Publish(BrokeredStorageParticipant sender, byte[] payload);
    long CaptureHeadSequence();
    long CaptureTailSequence();
    int CaptureHeadOffset();
    int CaptureTailOffset();
    BrokeredStorageAttachInfo CreateAttachInfo(long sessionId, int protocolVersion = 4);
    void ValidateRequestedBufferBytes(int requestedBufferBytes);
}

internal interface IBrokeredClientStorage : IDisposable
{
    BrokeredStorageKind StorageKind { get; }
    string Path { get; }
    int CapacityBytes { get; }
    int MaxPayloadBytes { get; }
    int Length { get; }

    BrokeredStorageDrainResult Drain(BrokeredStorageParticipant self, ref long nextSequence);
}
