using XivIpc.Internal;

namespace XivIpc.Messaging;

internal readonly record struct BrokeredClientAttachment(
    IBrokeredClientStorage Storage,
    BrokeredStorageAttachInfo AttachInfo,
    int EffectiveBudgetBytes);

internal static class BrokeredClientAttachmentFactory
{
    internal static BrokeredClientAttachment AttachFromFrame(
        SidecarFrame frame,
        string channelName,
        int requestedBufferBytes,
        SidecarTransportSettings transportSettings)
    {
        LogAttachFrameReceived(channelName, frame);

        SidecarAttachRing attach = DecodeAttachFrame(channelName, frame);
        BrokeredStorageKind storageKind = InferStorageKind(attach.RingPath, transportSettings.Storage);
        BrokeredStorageAttachInfo attachInfo = new(
            storageKind,
            attach.RingPath,
            attach.SlotCount,
            attach.SlotPayloadBytes,
            attach.StartSequence,
            attach.SessionId,
            attach.RingLength,
            attach.ProtocolVersion);

        IBrokeredClientStorage storage = BrokeredStorageFormats.Resolve(attachInfo.StorageKind)
            .AttachClientStorage(attachInfo, transportSettings.MessageTtlMs);

        int effectiveBudgetBytes = ComputeEffectiveBudgetBytes(requestedBufferBytes, transportSettings);
        return new BrokeredClientAttachment(storage, attachInfo, effectiveBudgetBytes);
    }

    private static BrokeredStorageKind InferStorageKind(string path, SidecarStorageKind configuredStorage)
    {
        if (path.Contains("broker-ring", StringComparison.Ordinal))
            return BrokeredStorageKind.Ring;

        if (path.Contains("broker-journal", StringComparison.Ordinal))
            return BrokeredStorageKind.Journal;

        return configuredStorage == SidecarStorageKind.Ring
            ? BrokeredStorageKind.Ring
            : BrokeredStorageKind.Journal;
    }

    private static int ComputeEffectiveBudgetBytes(int requestedBufferBytes, SidecarTransportSettings transportSettings)
    {
        return transportSettings.Storage switch
        {
            SidecarStorageKind.Ring => RingSizingPolicy.Compute(
                requestedBufferBytes,
                transportSettings.RingSlotCount,
                BrokeredChannelRing.HeaderBytes,
                BrokeredChannelRing.SlotHeaderBytes).BudgetBytes,
            _ => JournalSizingPolicy.Compute(
                requestedBufferBytes,
                BrokeredChannelJournal.HeaderBytes).BudgetBytes
        };
    }

    private static SidecarAttachRing DecodeAttachFrame(string channelName, SidecarFrame frame)
    {
        if (frame.Type == SidecarFrameType.Error)
        {
            string message = frame.Payload.Length == 0
                ? "Broker attach failed."
                : System.Text.Encoding.UTF8.GetString(frame.Payload.Span);
            TinyIpcLogger.Warning(
                nameof(BrokeredClientAttachmentFactory),
                "AttachRejectedByBroker",
                "Broker rejected sidecar attach.",
                null,
                ("channel", channelName),
                ("payloadLength", frame.Payload.Length),
                ("brokerError", message));
            throw new InvalidOperationException(message);
        }

        try
        {
            return SidecarProtocol.DecodeAttachRing(frame);
        }
        catch (Exception ex)
        {
            TinyIpcLogger.Warning(
                nameof(BrokeredClientAttachmentFactory),
                "AttachRingDecodeFailed",
                "Failed to decode broker ATTACH_RING frame.",
                ex,
                ("channel", channelName),
                ("frameType", frame.Type),
                ("payloadLength", frame.Payload.Length),
                ("payloadPreview", BuildPayloadPreview(frame.Payload.Span)));
            throw;
        }
    }

    private static void LogAttachFrameReceived(string channelName, SidecarFrame frame)
    {
        TinyIpcLogger.Info(
            nameof(BrokeredClientAttachmentFactory),
            "AttachRingFrameReceived",
            "Received broker attach frame.",
            ("channel", channelName),
            ("frameType", frame.Type),
            ("payloadLength", frame.Payload.Length),
            ("payloadPreview", BuildPayloadPreview(frame.Payload.Span)));
    }

    private static string BuildPayloadPreview(ReadOnlySpan<byte> payload)
    {
        int count = Math.Min(payload.Length, 32);
        return count == 0 ? string.Empty : Convert.ToHexString(payload[..count]);
    }
}
