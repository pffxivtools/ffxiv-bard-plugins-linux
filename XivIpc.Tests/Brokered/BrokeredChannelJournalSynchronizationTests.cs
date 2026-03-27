using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

public sealed class BrokeredChannelJournalSynchronizationTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);

    public BrokeredChannelJournalSynchronizationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "XivIpc.Tests", nameof(BrokeredChannelJournalSynchronizationTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        Capture("TINYIPC_SHARED_GROUP");
        Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", ResolveCurrentSharedGroup());
    }

    [Fact]
    public void Drain_SkipsSelfAuthoredMessages_AndDeliversOtherSendersInOrder_WhenRetentionPreventsImmediatePrune()
    {
        string path = BuildJournalPath("self-skip");
        using var journal = BrokeredChannelJournal.Create(
            path,
            capacityBytes: 64 * 1024,
            maxPayloadBytes: 2048,
            minAgeMs: 60_000);

        Guid self = Guid.NewGuid();
        Guid senderB = Guid.NewGuid();
        Guid senderC = Guid.NewGuid();

        journal.Publish(self, Encode("self-1"));
        journal.Publish(senderB, Encode("b-1"));
        journal.Publish(senderC, Encode("c-1"));
        journal.Publish(self, Encode("self-2"));
        journal.Publish(senderB, Encode("b-2"));
        journal.Publish(senderC, Encode("c-2"));

        long nextSequence = 0;
        BrokeredJournalDrainResult result = journal.Drain(self, ref nextSequence);
        string[] decoded = result.Messages.Select(Decode).ToArray();

        Assert.Equal(new[] { "b-1", "c-1", "b-2", "c-2" }, decoded);
        Assert.Equal(6, nextSequence);
        Assert.False(result.CaughtUpToTail);
    }

    [Fact]
    public void Drain_CatchesUpToTail_WhenSubscriberFallsBehindAfterCompaction()
    {
        string path = BuildJournalPath("tail-catchup");
        using var journal = BrokeredChannelJournal.Create(
            path,
            capacityBytes: 8 * 1024,
            maxPayloadBytes: 256,
            minAgeMs: 0);

        Guid senderA = Guid.NewGuid();
        Guid senderB = Guid.NewGuid();
        long nextSequence = 0;

        for (int i = 0; i < 200; i++)
            journal.Publish(i % 2 == 0 ? senderA : senderB, Encode($"msg-{i:000}"));

        BrokeredJournalDrainResult result = journal.Drain(Guid.NewGuid(), ref nextSequence);

        Assert.True(result.CaughtUpToTail);
        Assert.NotEmpty(result.Messages);
        Assert.Equal(result.HeadSequenceObserved, nextSequence);
        Assert.True(result.NextSequenceBefore < result.TailSequenceObserved);
        Assert.True(result.NextSequenceAfter >= result.TailSequenceObserved);
    }

    [Fact]
    public async Task ConcurrentPublishAndDrain_MultiReader_NoCompaction_AllReadersSeeAllSequencesExactlyOnce()
    {
        string path = BuildJournalPath("concurrent-full-retention");
        using var journal = BrokeredChannelJournal.Create(
            path,
            capacityBytes: 4 * 1024 * 1024,
            maxPayloadBytes: 1024,
            minAgeMs: 60_000);

        Guid reader1Id = Guid.NewGuid();
        Guid reader2Id = Guid.NewGuid();
        Guid reader3Id = Guid.NewGuid();

        Guid writer1Id = Guid.NewGuid();
        Guid writer2Id = Guid.NewGuid();
        Guid writer3Id = Guid.NewGuid();

        var publishedBySequence = new ConcurrentDictionary<long, PublishedRecord>();
        int writers = 3;
        int messagesPerWriter = 400;
        int expectedHead = writers * messagesPerWriter;

        ReaderState[] readers =
        {
        new(reader1Id),
        new(reader2Id),
        new(reader3Id)
    };

        Task[] writerTasks =
        {
        Task.Run(() => PublishRange(journal, writer1Id, "W1", 0, messagesPerWriter, publishedBySequence)),
        Task.Run(() => PublishRange(journal, writer2Id, "W2", 0, messagesPerWriter, publishedBySequence)),
        Task.Run(() => PublishRange(journal, writer3Id, "W3", 0, messagesPerWriter, publishedBySequence))
    };

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            foreach (ReaderState reader in readers)
                DrainInto(reader, journal, publishedBySequence);

            if (writerTasks.All(t => t.IsCompleted) &&
                readers.All(r => Volatile.Read(ref r.NextSequence) >= expectedHead))
            {
                break;
            }

            await Task.Delay(1).ConfigureAwait(false);
        }

        await Task.WhenAll(writerTasks).ConfigureAwait(false);

        DateTime catchupDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (readers.Any(r => Volatile.Read(ref r.NextSequence) < expectedHead) && DateTime.UtcNow < catchupDeadline)
        {
            foreach (ReaderState reader in readers)
                DrainInto(reader, journal, publishedBySequence);

            await Task.Delay(1).ConfigureAwait(false);
        }

        foreach (ReaderState reader in readers)
        {
            Assert.Equal(expectedHead, reader.NextSequence);

            long[] seen = reader.SeenSequences.OrderBy(x => x).ToArray();
            Assert.Equal(seen.Length, seen.Distinct().Count());

            for (int i = 0; i < expectedHead; i++)
                Assert.Contains(i, seen);

            foreach (long sequence in seen)
            {
                PublishedRecord record = Assert.Contains(sequence, publishedBySequence);
                Assert.NotEqual(reader.ReaderClientId, record.SenderInstanceId);
            }
        }
    }

    [Fact]
    public async Task ConcurrentPublishAndDrain_WithCompaction_DoesNotThrowOrProduceCorruptPayloads()
    {
        string path = BuildJournalPath("heavy-compaction");
        using var journal = BrokeredChannelJournal.Create(
            path,
            capacityBytes: 32 * 1024,
            maxPayloadBytes: 512,
            minAgeMs: 0);

        Guid readerId = Guid.NewGuid();
        Guid writer1Id = Guid.NewGuid();
        Guid writer2Id = Guid.NewGuid();

        var publishedBySequence = new ConcurrentDictionary<long, PublishedRecord>();
        var exceptions = new ConcurrentQueue<Exception>();

        int messagesPerWriter = 1500;

        Task writer1 = Task.Run(() =>
        {
            try
            {
                PublishRange(journal, writer1Id, "A", 0, messagesPerWriter, publishedBySequence);
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        Task writer2 = Task.Run(() =>
        {
            try
            {
                PublishRange(journal, writer2Id, "B", 0, messagesPerWriter, publishedBySequence);
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        long nextSequence = 0;
        long previousNextSequence = 0;
        var deliveredSequences = new HashSet<long>();
        var decodedPayloads = new List<string>();
        bool sawCatchUp = false;

        Task reader = Task.Run(async () =>
        {
            try
            {
                while (!writer1.IsCompleted || !writer2.IsCompleted)
                {
                    DrainAndValidate(readerId, journal, publishedBySequence, ref nextSequence, deliveredSequences, decodedPayloads, ref sawCatchUp);
                    Assert.True(nextSequence >= previousNextSequence, "nextSequence must be monotonic.");
                    previousNextSequence = nextSequence;
                    await Task.Delay(1).ConfigureAwait(false);
                }

                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (DateTime.UtcNow < deadline)
                {
                    long before = nextSequence;
                    DrainAndValidate(readerId, journal, publishedBySequence, ref nextSequence, deliveredSequences, decodedPayloads, ref sawCatchUp);
                    Assert.True(nextSequence >= previousNextSequence, "nextSequence must be monotonic.");
                    previousNextSequence = nextSequence;

                    if (nextSequence == before)
                        break;

                    await Task.Delay(1).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        await Task.WhenAll(writer1, writer2, reader).ConfigureAwait(false);

        Assert.True(exceptions.IsEmpty, string.Join(Environment.NewLine, exceptions.Select(static ex => ex.ToString())));
        Assert.NotEmpty(decodedPayloads);
        Assert.All(decodedPayloads, static payload => Assert.Matches("^[AB]:\\d{4}$", payload));
        Assert.True(deliveredSequences.Count > 0);
    }

    private static void PublishRange(
        BrokeredChannelJournal journal,
        Guid senderId,
        string prefix,
        int start,
        int count,
        ConcurrentDictionary<long, PublishedRecord> publishedBySequence)
    {
        for (int i = start; i < start + count; i++)
        {
            byte[] payload = Encode($"{prefix}:{i:0000}");
            BrokeredJournalPublishResult publishResult = journal.Publish(senderId, payload);
            long sequence = publishResult.HeadSequenceBefore;
            if (!publishedBySequence.TryAdd(sequence, new PublishedRecord(sequence, senderId, Decode(payload))))
                throw new InvalidOperationException($"Duplicate published sequence observed: {sequence}.");
        }
    }

    private static void DrainInto(
        ReaderState reader,
        BrokeredChannelJournal journal,
        ConcurrentDictionary<long, PublishedRecord> publishedBySequence)
    {
        long nextSequence = reader.NextSequence;
        BrokeredJournalDrainResult result = journal.Drain(reader.ReaderClientId, ref nextSequence);

        long firstDeliveredSequence = result.NextSequenceAfter - result.Messages.Count;
        for (int i = 0; i < result.Messages.Count; i++)
        {
            long sequence = firstDeliveredSequence + i;
            PublishedRecord published = Assert.Contains(sequence, publishedBySequence);

            Assert.NotEqual(reader.ReaderClientId, published.SenderInstanceId);
            Assert.Equal(published.PayloadText, Decode(result.Messages[i]));

            lock (reader.SeenSequences)
                reader.SeenSequences.Add(sequence);
        }

        reader.NextSequence = nextSequence;
    }

    private static void DrainAndValidate(
        Guid readerId,
        BrokeredChannelJournal journal,
        ConcurrentDictionary<long, PublishedRecord> publishedBySequence,
        ref long nextSequence,
        HashSet<long> deliveredSequences,
        List<string> decodedPayloads,
        ref bool sawCatchUp)
    {
        BrokeredJournalDrainResult result = journal.Drain(readerId, ref nextSequence);
        if (result.CaughtUpToTail)
            sawCatchUp = true;

        long firstDeliveredSequence = result.NextSequenceAfter - result.Messages.Count;

        for (int i = 0; i < result.Messages.Count; i++)
        {
            long sequence = firstDeliveredSequence + i;
            PublishedRecord published = Assert.Contains(sequence, publishedBySequence);
            string decoded = Decode(result.Messages[i]);

            Assert.Equal(published.PayloadText, decoded);
            Assert.True(deliveredSequences.Add(sequence), $"Duplicate delivered sequence {sequence} observed.");
            decodedPayloads.Add(decoded);
        }
    }

    private string BuildJournalPath(string testName)
        => Path.Combine(_tempDirectory, $"{testName}-{Guid.NewGuid():N}.bin");

    private static byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public void Dispose()
    {
        foreach ((string key, string? value) in _previousEnvironment)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
        }
    }

    private void Capture(string variableName)
        => _previousEnvironment[variableName] = Environment.GetEnvironmentVariable(variableName);

    private static string ResolveCurrentSharedGroup()
    {
        using var process = Process.Start(new ProcessStartInfo("id", "-gn")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start 'id -gn' to resolve the shared group.");

        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Failed to resolve the current user's primary group for journal synchronization tests.");

        return output;
    }

    private sealed class ReaderState
    {
        public ReaderState(Guid readerClientId)
        {
            ReaderClientId = readerClientId;
        }

        public Guid ReaderClientId { get; }
        public long NextSequence;
        public List<long> SeenSequences { get; } = new();
    }

    private readonly record struct PublishedRecord(long Sequence, Guid SenderInstanceId, string PayloadText);
}