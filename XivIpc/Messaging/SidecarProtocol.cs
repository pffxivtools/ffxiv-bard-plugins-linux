using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XivIpc.Internal
{
    internal enum SidecarFrameType : byte
    {
        Hello = 1,
        Publish = 2,
        Dispose = 3,
        Heartbeat = 4,
        Ready = 11,
        Message = 12,
        Error = 13
    }

    internal readonly record struct SidecarHello(
        string Channel,
        int MaxBytes,
        int OwnerPid,
        int HeartbeatIntervalMs,
        int HeartbeatTimeoutMs,
        int ProtocolVersion = 1)
    {

    }

    internal readonly record struct SidecarFrame(SidecarFrameType Type, ReadOnlyMemory<byte> Payload);

    internal static class SidecarProtocol
    {
        private const int LengthPrefixBytes = 4;

        public static async ValueTask WriteHelloAsync(PipeWriter writer, SidecarHello hello, CancellationToken cancellationToken = default)
        {
            byte[] channelBytes = Encoding.UTF8.GetBytes(hello.Channel ?? string.Empty);
            int payloadLength = 4 + 4 + 4 + 4 + 4 + 4 + channelBytes.Length;
            Span<byte> header = stackalloc byte[LengthPrefixBytes + 1 + 24];
            BinaryPrimitives.WriteInt32LittleEndian(header[..4], 1 + payloadLength);
            header[4] = (byte)SidecarFrameType.Hello;
            int offset = 5;
            BinaryPrimitives.WriteInt32LittleEndian(header[offset..(offset + 4)], hello.ProtocolVersion); offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(header[offset..(offset + 4)], hello.OwnerPid); offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(header[offset..(offset + 4)], hello.MaxBytes); offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(header[offset..(offset + 4)], hello.HeartbeatIntervalMs); offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(header[offset..(offset + 4)], hello.HeartbeatTimeoutMs); offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(header[offset..(offset + 4)], channelBytes.Length);

            writer.Write(header);
            writer.Write(channelBytes);
            FlushResult flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flushResult.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        public static async ValueTask<SidecarHello> ReadHelloAsync(PipeReader reader, CancellationToken cancellationToken = default)
        {
            SidecarFrame frame = await ReadFrameAsync(reader, cancellationToken).ConfigureAwait(false);
            if (frame.Type != SidecarFrameType.Hello)
                throw new InvalidDataException($"Expected sidecar frame '{SidecarFrameType.Hello}' but received '{frame.Type}'.");

            ReadOnlySpan<byte> span = frame.Payload.Span;
            if (span.Length < 24)
                throw new InvalidDataException("HELLO payload is truncated.");

            int offset = 0;
            int version = BinaryPrimitives.ReadInt32LittleEndian(span[offset..(offset + 4)]); offset += 4;
            int ownerPid = BinaryPrimitives.ReadInt32LittleEndian(span[offset..(offset + 4)]); offset += 4;
            int maxBytes = BinaryPrimitives.ReadInt32LittleEndian(span[offset..(offset + 4)]); offset += 4;
            int heartbeatIntervalMs = BinaryPrimitives.ReadInt32LittleEndian(span[offset..(offset + 4)]); offset += 4;
            int heartbeatTimeoutMs = BinaryPrimitives.ReadInt32LittleEndian(span[offset..(offset + 4)]); offset += 4;
            int channelLength = BinaryPrimitives.ReadInt32LittleEndian(span[offset..(offset + 4)]); offset += 4;

            if (channelLength < 0 || offset + channelLength > span.Length)
                throw new InvalidDataException("HELLO channel payload is invalid.");

            string channel = Encoding.UTF8.GetString(span.Slice(offset, channelLength));
            return new SidecarHello(channel, maxBytes, ownerPid, heartbeatIntervalMs, heartbeatTimeoutMs, version);
        }

        public static ValueTask WritePublishAsync(PipeWriter writer, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => WriteFrameAsync(writer, SidecarFrameType.Publish, payload, cancellationToken);

        public static ValueTask WriteDisposeAsync(PipeWriter writer, CancellationToken cancellationToken = default)
            => WriteFrameAsync(writer, SidecarFrameType.Dispose, ReadOnlyMemory<byte>.Empty, cancellationToken);

        public static ValueTask WriteHeartbeatAsync(PipeWriter writer, CancellationToken cancellationToken = default)
            => WriteFrameAsync(writer, SidecarFrameType.Heartbeat, ReadOnlyMemory<byte>.Empty, cancellationToken);

        public static ValueTask WriteReadyAsync(PipeWriter writer, CancellationToken cancellationToken = default)
            => WriteFrameAsync(writer, SidecarFrameType.Ready, ReadOnlyMemory<byte>.Empty, cancellationToken);

        public static ValueTask WriteMessageAsync(PipeWriter writer, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => WriteFrameAsync(writer, SidecarFrameType.Message, payload, cancellationToken);

        public static ValueTask WriteErrorAsync(PipeWriter writer, string message, CancellationToken cancellationToken = default)
            => WriteFrameAsync(writer, SidecarFrameType.Error, Encoding.UTF8.GetBytes(message ?? string.Empty), cancellationToken);

        public static async ValueTask<SidecarFrame> ReadFrameAsync(PipeReader reader, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                ReadResult headerResult = await reader.ReadAtLeastAsync(LengthPrefixBytes, cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> headerBuffer = headerResult.Buffer;
                if (headerBuffer.Length < LengthPrefixBytes)
                {
                    if (headerResult.IsCompleted)
                    {
                        reader.AdvanceTo(headerBuffer.Start, headerBuffer.End);
                        throw new EndOfStreamException();
                    }

                    reader.AdvanceTo(headerBuffer.Start, headerBuffer.End);
                    continue;
                }

                Span<byte> headerBytes = stackalloc byte[LengthPrefixBytes];
                headerBuffer.Slice(0, LengthPrefixBytes).CopyTo(headerBytes);
                int frameLength = BinaryPrimitives.ReadInt32LittleEndian(headerBytes);
                if (frameLength < 1)
                {
                    reader.AdvanceTo(headerBuffer.Start, headerBuffer.End);
                    throw new InvalidDataException("Frame length is invalid.");
                }

                long totalLength = LengthPrefixBytes + frameLength;
                reader.AdvanceTo(headerBuffer.Start, headerBuffer.Start);

                ReadResult frameResult = await reader.ReadAtLeastAsync((int)totalLength, cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> frameBuffer = frameResult.Buffer;
                if (frameBuffer.Length < totalLength)
                {
                    if (frameResult.IsCompleted)
                    {
                        reader.AdvanceTo(frameBuffer.Start, frameBuffer.End);
                        throw new EndOfStreamException();
                    }

                    reader.AdvanceTo(frameBuffer.Start, frameBuffer.Start);
                    continue;
                }

                SequencePosition payloadStart = frameBuffer.GetPosition(LengthPrefixBytes, frameBuffer.Start);
                ReadOnlySequence<byte> frameContent = frameBuffer.Slice(payloadStart, frameLength);
                Span<byte> typeByte = stackalloc byte[1];
                frameContent.Slice(0, 1).CopyTo(typeByte);
                SidecarFrameType type = (SidecarFrameType)typeByte[0];
                byte[] payload = frameContent.Length > 1 ? frameContent.Slice(1).ToArray() : Array.Empty<byte>();

                SequencePosition consumed = frameBuffer.GetPosition(totalLength, frameBuffer.Start);
                reader.AdvanceTo(consumed, consumed);
                return new SidecarFrame(type, payload);
            }
        }

        public static async ValueTask<byte[]> ReadPayloadAsync(PipeReader reader, SidecarFrameType expectedType, CancellationToken cancellationToken = default)
        {
            SidecarFrame frame = await ReadFrameAsync(reader, cancellationToken).ConfigureAwait(false);
            if (frame.Type != expectedType)
                throw new InvalidDataException($"Expected sidecar frame '{expectedType}' but received '{frame.Type}'.");
            return frame.Payload.ToArray();
        }

        public static async ValueTask<string> ReadErrorAsync(PipeReader reader, CancellationToken cancellationToken = default)
        {
            byte[] payload = await ReadPayloadAsync(reader, SidecarFrameType.Error, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(payload);
        }

        public static async ValueTask WriteFrameAsync(PipeWriter writer, SidecarFrameType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            int frameLength = 1 + payload.Length;
            Span<byte> prefix = stackalloc byte[LengthPrefixBytes + 1];
            BinaryPrimitives.WriteInt32LittleEndian(prefix[..4], frameLength);
            prefix[4] = (byte)type;
            writer.Write(prefix);
            if (!payload.IsEmpty)
                writer.Write(payload.Span);
            FlushResult flushResult = await writer.FlushAsync(cancellationToken);
            if (flushResult.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }
}
