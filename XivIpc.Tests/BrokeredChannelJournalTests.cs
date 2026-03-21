using System.Buffers.Binary;
using System.Text;
using XivIpc.Internal;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

public sealed class BrokeredChannelJournalTests
{
    [Fact]
    public void Create_ThenAttach_PreservesHeaderAndInitialPointers()
    {
        using var scope = new BrokeredChannelJournalTestScope(capacityBytes: 4096, maxPayloadBytes: 512, minAgeMs: 1_000);
        using BrokeredChannelJournal attached = scope.Attach();

        Assert.Equal(scope.CapacityBytes, attached.CapacityBytes);
        Assert.Equal(scope.MaxPayloadBytes, attached.MaxPayloadBytes);
        Assert.Equal(scope.MinAgeMs, attached.MinAgeMs);
        Assert.Equal(BrokeredChannelJournal.HeaderBytes + scope.CapacityBytes, attached.Length);
        Assert.Equal(0, attached.CaptureHeadSequence());
        Assert.Equal(0, attached.CaptureTailSequence());
        Assert.Equal(0, attached.CaptureHeadOffset());
        Assert.Equal(0, attached.CaptureTailOffset());
    }

    [Fact]
    public void Publish_UpdatesPointersAndRetainedBytes()
    {
        using var scope = new BrokeredChannelJournalTestScope(capacityBytes: 4096, maxPayloadBytes: 512, minAgeMs: 5_000);

        byte[] first = Encoding.UTF8.GetBytes("first-payload");
        byte[] second = Encoding.UTF8.GetBytes("second-payload");

        BrokeredJournalPublishResult firstResult = scope.Journal.Publish(Guid.NewGuid(), first);
        BrokeredJournalPublishResult secondResult = scope.Journal.Publish(Guid.NewGuid(), second);

        Assert.Equal(0, firstResult.HeadSequenceBefore);
        Assert.Equal(0, firstResult.TailSequenceBefore);
        Assert.Equal(1, firstResult.HeadSequenceAfter);
        Assert.Equal(0, firstResult.TailSequenceAfter);
        Assert.Equal(BrokeredChannelJournalTestScope.RecordLength(first.Length), firstResult.RetainedBytesAfter);
        Assert.False(firstResult.PrunedExpired);
        Assert.False(firstResult.Compacted);

        Assert.Equal(1, secondResult.HeadSequenceBefore);
        Assert.Equal(0, secondResult.TailSequenceBefore);
        Assert.Equal(2, secondResult.HeadSequenceAfter);
        Assert.Equal(0, secondResult.TailSequenceAfter);
        Assert.Equal(
            BrokeredChannelJournalTestScope.RecordLength(first.Length) + BrokeredChannelJournalTestScope.RecordLength(second.Length),
            secondResult.RetainedBytesAfter);

        long nextSequence = 0;
        BrokeredJournalDrainResult drain = scope.Journal.Drain(Guid.Empty, ref nextSequence);
        Assert.Equal(2, nextSequence);
        Assert.Equal(new[] { "first-payload", "second-payload" }, drain.Messages.Select(Encoding.UTF8.GetString).ToArray());
    }

    [Fact]
    public void Drain_SkipsSelfMessages_ButAdvancesCursor()
    {
        using var scope = new BrokeredChannelJournalTestScope(capacityBytes: 4096, maxPayloadBytes: 512, minAgeMs: 5_000);
        Guid self = Guid.NewGuid();
        Guid peer = Guid.NewGuid();

        scope.Journal.Publish(self, Encoding.UTF8.GetBytes("self-1"));
        scope.Journal.Publish(peer, Encoding.UTF8.GetBytes("peer-1"));
        scope.Journal.Publish(self, Encoding.UTF8.GetBytes("self-2"));

        long nextSequence = 0;
        BrokeredJournalDrainResult drain = scope.Journal.Drain(self, ref nextSequence);

        Assert.Equal(3, nextSequence);
        Assert.False(drain.CaughtUpToTail);
        Assert.Equal(new[] { "peer-1" }, drain.Messages.Select(Encoding.UTF8.GetString).ToArray());
    }

    [Fact]
    public void Publish_WhenJournalIsFullBeforeMinAgeExpires_Throws()
    {
        const int payloadBytes = 32;
        int capacityBytes = BrokeredChannelJournalTestScope.RecordLength(payloadBytes) * 2;
        using var scope = new BrokeredChannelJournalTestScope(capacityBytes, maxPayloadBytes: payloadBytes, minAgeMs: 60_000);

        byte[] payload = new byte[payloadBytes];
        scope.Journal.Publish(Guid.NewGuid(), payload);
        scope.Journal.Publish(Guid.NewGuid(), payload);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => scope.Journal.Publish(Guid.NewGuid(), payload));

        Assert.Contains("Broker journal is full", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, scope.Journal.CaptureHeadSequence());
        Assert.Equal(0, scope.Journal.CaptureTailSequence());
    }

    [Fact]
    public async Task Publish_AfterMinAge_PrunesExpiredRecordsAndCompactsRetainedBytes()
    {
        const int payloadBytes = 32;
        int capacityBytes = BrokeredChannelJournalTestScope.RecordLength(payloadBytes) * 2;
        using var scope = new BrokeredChannelJournalTestScope(capacityBytes, maxPayloadBytes: payloadBytes, minAgeMs: 30);

        scope.Journal.Publish(Guid.NewGuid(), Encoding.UTF8.GetBytes("old-1".PadRight(payloadBytes, 'a')));
        scope.Journal.Publish(Guid.NewGuid(), Encoding.UTF8.GetBytes("old-2".PadRight(payloadBytes, 'b')));

        await Task.Delay(60);

        byte[] fresh = Encoding.UTF8.GetBytes("fresh".PadRight(payloadBytes, 'c'));
        BrokeredJournalPublishResult result = scope.Journal.Publish(Guid.NewGuid(), fresh);

        Assert.True(result.PrunedExpired);
        Assert.True(result.Compacted);
        Assert.Equal(3, result.HeadSequenceAfter);
        Assert.Equal(2, result.TailSequenceAfter);
        Assert.Equal(BrokeredChannelJournalTestScope.RecordLength(payloadBytes), result.RetainedBytesAfter);
        Assert.Equal(0, scope.Journal.CaptureTailOffset());
        Assert.Equal(BrokeredChannelJournalTestScope.RecordLength(payloadBytes), scope.Journal.CaptureHeadOffset());

        long nextSequence = 0;
        BrokeredJournalDrainResult drain = scope.Journal.Drain(Guid.Empty, ref nextSequence);

        Assert.True(drain.CaughtUpToTail);
        Assert.Equal(3, nextSequence);
        Assert.Equal(new[] { Encoding.UTF8.GetString(fresh) }, drain.Messages.Select(Encoding.UTF8.GetString).ToArray());
    }

    [Fact]
    public void Attach_WithTamperedHeader_ThrowsInvalidData()
    {
        using var scope = new BrokeredChannelJournalTestScope(capacityBytes: 4096, maxPayloadBytes: 512, minAgeMs: 1_000);

        scope.OverwriteInt32(BrokeredChannelJournalTestScope.HeaderVersionOffset, 99);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => scope.Attach());
        Assert.Contains("header is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Drain_StopsSafelyAtCorruptRecord_AfterReturningValidPrefix()
    {
        using var scope = new BrokeredChannelJournalTestScope(capacityBytes: 4096, maxPayloadBytes: 512, minAgeMs: 5_000);

        byte[] first = Encoding.UTF8.GetBytes("one");
        byte[] second = Encoding.UTF8.GetBytes("two");
        scope.Journal.Publish(Guid.NewGuid(), first);
        scope.Journal.Publish(Guid.NewGuid(), second);

        int secondRecordOffset = BrokeredChannelJournal.HeaderBytes + BrokeredChannelJournalTestScope.RecordLength(first.Length);
        scope.OverwriteInt32(secondRecordOffset + BrokeredChannelJournalTestScope.RecordMagicOffset, 0);

        long nextSequence = 0;
        BrokeredJournalDrainResult drain = scope.Journal.Drain(Guid.Empty, ref nextSequence);

        Assert.Equal(1, nextSequence);
        Assert.Equal(new[] { "one" }, drain.Messages.Select(Encoding.UTF8.GetString).ToArray());
    }
}

internal sealed class BrokeredChannelJournalTestScope : IDisposable
{
    internal const int HeaderVersionOffset = 4;
    internal const int RecordMagicOffset = 0;

    private readonly string _directory;
    private readonly IDisposable _overrides;

    internal BrokeredChannelJournalTestScope(int capacityBytes, int maxPayloadBytes, long minAgeMs)
    {
        CapacityBytes = capacityBytes;
        MaxPayloadBytes = maxPayloadBytes;
        MinAgeMs = minAgeMs;
        _directory = Path.Combine(Path.GetTempPath(), "xivipc-brokered-journal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        FilePath = Path.Combine(_directory, "broker-journal.bin");
        _overrides = TinyIpcEnvironment.Override((TinyIpcEnvironment.SharedGroup, ProductionPathTestEnvironment.ResolveSharedGroup()));
        Journal = BrokeredChannelJournal.Create(FilePath, capacityBytes, maxPayloadBytes, minAgeMs);
    }

    internal BrokeredChannelJournal Journal { get; }
    internal string FilePath { get; }
    internal int CapacityBytes { get; }
    internal int MaxPayloadBytes { get; }
    internal long MinAgeMs { get; }
    internal int Length => BrokeredChannelJournal.HeaderBytes + CapacityBytes;

    internal BrokeredChannelJournal Attach()
        => BrokeredChannelJournal.Attach(FilePath, CapacityBytes, MaxPayloadBytes, Length, MinAgeMs);

    internal void OverwriteInt32(int offset, int value)
    {
        using var stream = new FileStream(
            UnixSharedStorageHelpers.ConvertPathForCurrentRuntime(FilePath),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Position = offset;
        stream.Write(buffer);
        stream.Flush(true);
    }

    internal static int RecordLength(int payloadLength)
        => BrokeredChannelJournal.RecordHeaderBytes + payloadLength;

    public void Dispose()
    {
        Journal.Dispose();
        _overrides.Dispose();

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
