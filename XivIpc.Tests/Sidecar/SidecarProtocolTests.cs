using System.Buffers.Binary;
using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

public sealed class SidecarProtocolTests
{
    [Fact]
    public void DecodeAttachRing_WithOversizedRingPathLength_ThrowsInvalidData()
    {
        SidecarFrame frame = new(SidecarFrameType.AttachRing, BuildAttachRingPayload(
            version: 4,
            ringPathLength: int.MaxValue,
            slotCount: 256,
            slotPayloadBytes: 1024,
            startSequence: 1,
            sessionId: 42,
            ringLength: 4096,
            ringPath: "/tmp/test.ring"));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SidecarProtocol.DecodeAttachRing(frame));
        Assert.Contains("ring path payload is invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeAttachRing_WithUnsupportedVersion_ThrowsInvalidData()
    {
        SidecarFrame frame = new(SidecarFrameType.AttachRing, BuildAttachRingPayload(
            version: 2,
            ringPathLength: "/tmp/test.ring".Length,
            slotCount: 256,
            slotPayloadBytes: 1024,
            startSequence: 1,
            sessionId: 42,
            ringLength: 4096,
            ringPath: "/tmp/test.ring"));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SidecarProtocol.DecodeAttachRing(frame));
        Assert.Contains("protocol version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeAttachRing_WithInvalidSlotCount_ThrowsInvalidData()
    {
        SidecarFrame frame = new(SidecarFrameType.AttachRing, BuildAttachRingPayload(
            version: 4,
            ringPathLength: "/tmp/test.ring".Length,
            slotCount: 0,
            slotPayloadBytes: 1024,
            startSequence: 1,
            sessionId: 42,
            ringLength: 4096,
            ringPath: "/tmp/test.ring"));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SidecarProtocol.DecodeAttachRing(frame));
        Assert.Contains("slot count", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeAttachRing_WithValidPayload_ReturnsAttach()
    {
        const string ringPath = "/tmp/test.ring";
        SidecarFrame frame = new(SidecarFrameType.AttachRing, BuildAttachRingPayload(
            version: 4,
            ringPathLength: ringPath.Length,
            slotCount: 256,
            slotPayloadBytes: 1024,
            startSequence: 7,
            sessionId: 42,
            ringLength: 8192,
            ringPath: ringPath));

        SidecarAttachRing attach = SidecarProtocol.DecodeAttachRing(frame);

        Assert.Equal(ringPath, attach.RingPath);
        Assert.Equal(256, attach.SlotCount);
        Assert.Equal(1024, attach.SlotPayloadBytes);
        Assert.Equal(7, attach.StartSequence);
        Assert.Equal(42, attach.SessionId);
        Assert.Equal(8192, attach.RingLength);
        Assert.Equal(4, attach.ProtocolVersion);
    }

    private static byte[] BuildAttachRingPayload(
        int version,
        int ringPathLength,
        int slotCount,
        int slotPayloadBytes,
        long startSequence,
        long sessionId,
        int ringLength,
        string ringPath)
    {
        byte[] ringPathBytes = System.Text.Encoding.UTF8.GetBytes(ringPath);
        byte[] payload = new byte[4 + 4 + 4 + 4 + 8 + 8 + 4 + 8 + ringPathBytes.Length];
        int offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), version);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), ringPathLength);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), slotCount);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), slotPayloadBytes);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset, 8), startSequence);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset, 8), sessionId);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), ringLength);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset, 8), 0);
        offset += 8;
        ringPathBytes.CopyTo(payload, offset);
        return payload;
    }
}
