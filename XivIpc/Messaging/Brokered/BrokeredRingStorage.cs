using XivIpc.Internal;

namespace XivIpc.Messaging;

internal sealed class BrokeredRingStorageFormat : IBrokeredStorageFormat
{
    internal static BrokeredRingStorageFormat Instance { get; } = new();

    private BrokeredRingStorageFormat()
    {
    }

    public BrokeredStorageKind StorageKind => BrokeredStorageKind.Ring;

    public IBrokeredHostStorage CreateHostStorage(string channelName, int requestedBufferBytes, string brokerDirectory, string instanceId, SidecarTransportSettings transportSettings)
        => new BrokeredRingHostStorage(channelName, requestedBufferBytes, brokerDirectory, instanceId, transportSettings.RingSlotCount);

    public IBrokeredClientStorage AttachClientStorage(BrokeredStorageAttachInfo attachInfo, long minAgeMs)
    {
        if (attachInfo.StorageKind != BrokeredStorageKind.Ring)
            throw new InvalidOperationException($"Cannot attach storage kind '{attachInfo.StorageKind}' with the ring storage format.");

        return new BrokeredRingClientStorage(
            BrokeredChannelRing.Attach(
                attachInfo.Path,
                attachInfo.CapacityBytes,
                attachInfo.MaxPayloadBytes));
    }
}

internal sealed class BrokeredRingHostStorage : IBrokeredHostStorage
{
    private readonly BrokeredChannelRing _ring;
    private readonly RingSizing _sizing;
    private readonly int _ringSlotCount;

    public BrokeredRingHostStorage(string channelName, int requestedBufferBytes, string brokerDirectory, string instanceId, int ringSlotCount)
    {
        _ringSlotCount = ringSlotCount;
        _sizing = RingSizingPolicy.Compute(
            requestedBufferBytes,
            ringSlotCount,
            BrokeredChannelRing.HeaderBytes,
            BrokeredChannelRing.SlotHeaderBytes);

        Path = UnixSharedStorageHelpers.BuildSharedFilePath(brokerDirectory, $"{channelName}_{instanceId}", "broker-ring");
        _ring = BrokeredChannelRing.Create(Path, _sizing.SlotCount, _sizing.SlotPayloadBytes);
    }

    public BrokeredStorageKind StorageKind => BrokeredStorageKind.Ring;
    public string Path { get; }
    public int CapacityBytes => _ring.SlotCount;
    public int MaxPayloadBytes => _ring.SlotPayloadBytes;
    public int Length => _ring.Length;
    public long MinMessageAgeMs => 0;
    public int RequestedBufferBytes => _sizing.RequestedBufferBytes;
    public int EffectiveBudgetBytes => _sizing.BudgetBytes;
    public bool BudgetWasCapped => _sizing.WasCapped;

    public BrokeredStoragePublishResult Publish(BrokeredStorageParticipant sender, byte[] payload)
    {
        BrokeredRingPublishResult result = _ring.Publish(sender.SessionId, payload);
        int retainedBytesBefore = checked((int)((result.HeadBefore - result.TailBefore) * (BrokeredChannelRing.SlotHeaderBytes + _ring.SlotPayloadBytes)));
        int retainedBytesAfter = checked((int)((result.HeadAfter - result.TailAfter) * (BrokeredChannelRing.SlotHeaderBytes + _ring.SlotPayloadBytes)));
        return new BrokeredStoragePublishResult(
            result.HeadBefore,
            result.TailBefore,
            result.HeadAfter,
            result.TailAfter,
            retainedBytesBefore,
            retainedBytesAfter,
            result.OverwroteOldest,
            false);
    }

    public long CaptureHeadSequence() => _ring.CaptureHead();
    public long CaptureTailSequence() => _ring.CaptureTail();
    public int CaptureHeadOffset() => checked((int)(CaptureHeadSequence() * (BrokeredChannelRing.SlotHeaderBytes + _ring.SlotPayloadBytes)));
    public int CaptureTailOffset() => checked((int)(CaptureTailSequence() * (BrokeredChannelRing.SlotHeaderBytes + _ring.SlotPayloadBytes)));

    public BrokeredStorageAttachInfo CreateAttachInfo(long sessionId, int protocolVersion = 4)
        => new(
            StorageKind,
            Path,
            CapacityBytes,
            MaxPayloadBytes,
            CaptureHeadSequence(),
            sessionId,
            Length,
            protocolVersion);

    public void ValidateRequestedBufferBytes(int requestedBufferBytes)
    {
        RingSizing requestedSizing = RingSizingPolicy.Compute(
            requestedBufferBytes,
            _ringSlotCount,
            BrokeredChannelRing.HeaderBytes,
            BrokeredChannelRing.SlotHeaderBytes);

        if (requestedSizing.SlotPayloadBytes > MaxPayloadBytes || requestedSizing.ImageSize > Length)
        {
            throw new InvalidOperationException(
                $"Existing brokered ring is smaller than requested. " +
                $"existingSlotCount={CapacityBytes}, existingMaxPayloadBytes={MaxPayloadBytes}, existingImageSize={Length}; " +
                $"requestedBufferBytes={requestedBufferBytes}, requestedSlotPayloadBytes={requestedSizing.SlotPayloadBytes}, requestedImageSize={requestedSizing.ImageSize}.");
        }
    }

    public void Dispose() => _ring.Dispose();
}

internal sealed class BrokeredRingClientStorage : IBrokeredClientStorage
{
    private readonly BrokeredChannelRing _ring;

    public BrokeredRingClientStorage(BrokeredChannelRing ring)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
    }

    public BrokeredStorageKind StorageKind => BrokeredStorageKind.Ring;
    public string Path => _ring.FilePath;
    public int CapacityBytes => _ring.SlotCount;
    public int MaxPayloadBytes => _ring.SlotPayloadBytes;
    public int Length => _ring.Length;

    public BrokeredStorageDrainResult Drain(BrokeredStorageParticipant self, ref long nextSequence)
    {
        BrokeredRingDrainResult result = _ring.Drain(self.SessionId, ref nextSequence);
        int retainedBytes = checked(result.RetainedDepth * (BrokeredChannelRing.SlotHeaderBytes + _ring.SlotPayloadBytes));
        return new BrokeredStorageDrainResult(
            result.Messages,
            result.CaughtUpToTail,
            result.TailObserved,
            result.HeadObserved,
            result.NextSequenceBefore,
            result.NextSequenceAfter,
            result.LagBefore,
            retainedBytes);
    }

    public void Dispose() => _ring.Dispose();
}
