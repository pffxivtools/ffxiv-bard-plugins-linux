using XivIpc.Internal;

namespace XivIpc.Messaging;

internal sealed class BrokeredJournalStorageFormat : IBrokeredStorageFormat
{
    internal static BrokeredJournalStorageFormat Instance { get; } = new();

    private BrokeredJournalStorageFormat()
    {
    }

    public BrokeredStorageKind StorageKind => BrokeredStorageKind.Journal;

    public IBrokeredHostStorage CreateHostStorage(string channelName, int requestedBufferBytes, string brokerDirectory, string instanceId, SidecarTransportSettings transportSettings)
        => new BrokeredJournalHostStorage(channelName, requestedBufferBytes, brokerDirectory, instanceId, transportSettings.MessageTtlMs);

    public IBrokeredClientStorage AttachClientStorage(BrokeredStorageAttachInfo attachInfo, long minAgeMs)
    {
        if (attachInfo.StorageKind != BrokeredStorageKind.Journal)
            throw new InvalidOperationException($"Cannot attach storage kind '{attachInfo.StorageKind}' with the journal storage format.");

        return new BrokeredJournalClientStorage(
            BrokeredChannelJournal.Attach(
                attachInfo.Path,
                attachInfo.CapacityBytes,
                attachInfo.MaxPayloadBytes,
                attachInfo.Length,
                minAgeMs));
    }
}

internal sealed class BrokeredJournalHostStorage : IBrokeredHostStorage
{
    private readonly BrokeredChannelJournal _journal;

    public BrokeredJournalHostStorage(string channelName, int requestedBufferBytes, string brokerDirectory, string instanceId, long minAgeMs)
    {
        Path = UnixSharedStorageHelpers.BuildSharedFilePath(brokerDirectory, $"{channelName}_{instanceId}", "broker-journal");
        Sizing = JournalSizingPolicy.Compute(requestedBufferBytes, BrokeredChannelJournal.HeaderBytes);
        _journal = BrokeredChannelJournal.Create(Path, Sizing.BudgetBytes, Sizing.MaxPayloadBytes, minAgeMs);
    }

    public BrokeredStorageKind StorageKind => BrokeredStorageKind.Journal;
    public string Path { get; }
    public int CapacityBytes => _journal.CapacityBytes;
    public int MaxPayloadBytes => _journal.MaxPayloadBytes;
    public int Length => _journal.Length;
    public long MinMessageAgeMs => _journal.MinAgeMs;
    public JournalSizing Sizing { get; }
    public int RequestedBufferBytes => Sizing.RequestedBufferBytes;
    public int EffectiveBudgetBytes => Sizing.BudgetBytes;
    public bool BudgetWasCapped => Sizing.WasCapped;

    public BrokeredStoragePublishResult Publish(BrokeredStorageParticipant sender, byte[] payload)
    {
        BrokeredJournalPublishResult result = _journal.Publish(sender.ClientInstanceId, payload);
        return new BrokeredStoragePublishResult(
            result.HeadSequenceBefore,
            result.TailSequenceBefore,
            result.HeadSequenceAfter,
            result.TailSequenceAfter,
            result.RetainedBytesBefore,
            result.RetainedBytesAfter,
            result.PrunedExpired,
            result.Compacted);
    }

    public long CaptureHeadSequence() => _journal.CaptureHeadSequence();
    public long CaptureTailSequence() => _journal.CaptureTailSequence();
    public int CaptureHeadOffset() => _journal.CaptureHeadOffset();
    public int CaptureTailOffset() => _journal.CaptureTailOffset();

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
        JournalSizing requestedSizing = JournalSizingPolicy.Compute(requestedBufferBytes, BrokeredChannelJournal.HeaderBytes);
        if (requestedSizing.MaxPayloadBytes > MaxPayloadBytes || requestedSizing.ImageSize > Length)
        {
            throw new InvalidOperationException(
                $"Existing brokered journal is smaller than requested. " +
                $"existingCapacityBytes={CapacityBytes}, existingMaxPayloadBytes={MaxPayloadBytes}, existingImageSize={Length}; " +
                $"requestedBufferBytes={requestedBufferBytes}, requestedMaxPayloadBytes={requestedSizing.MaxPayloadBytes}, requestedImageSize={requestedSizing.ImageSize}.");
        }
    }

    public void Dispose() => _journal.Dispose();
}

internal sealed class BrokeredJournalClientStorage : IBrokeredClientStorage
{
    private readonly BrokeredChannelJournal _journal;

    public BrokeredJournalClientStorage(BrokeredChannelJournal journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public BrokeredStorageKind StorageKind => BrokeredStorageKind.Journal;
    public string Path => _journal.FilePath;
    public int CapacityBytes => _journal.CapacityBytes;
    public int MaxPayloadBytes => _journal.MaxPayloadBytes;
    public int Length => _journal.Length;

    public BrokeredStorageDrainResult Drain(BrokeredStorageParticipant self, ref long nextSequence)
    {
        BrokeredJournalDrainResult result = _journal.Drain(self.ClientInstanceId, ref nextSequence);
        return new BrokeredStorageDrainResult(
            result.Messages,
            result.CaughtUpToTail,
            result.TailSequenceObserved,
            result.HeadSequenceObserved,
            result.NextSequenceBefore,
            result.NextSequenceAfter,
            result.LagBefore,
            result.RetainedBytes);
    }

    public void Dispose() => _journal.Dispose();
}
