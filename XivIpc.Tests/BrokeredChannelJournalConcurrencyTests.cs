using System.Collections.Concurrent;
using System.Text;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

public sealed class BrokeredChannelJournalConcurrencyTests
{
    [Fact]
    public async Task ConcurrentPublishes_OnSharedWriter_ProduceContiguousHeadSequence_AndExactDrainForReaders()
    {
        using var scope = new BrokeredChannelJournalTestScope(capacityBytes: 256 * 1024, maxPayloadBytes: 256, minAgeMs: 60_000);
        using BrokeredChannelJournal readerA = scope.Attach();
        using BrokeredChannelJournal readerB = scope.Attach();

        const int publisherCount = 4;
        const int messagesPerPublisher = 50;
        int expectedTotal = publisherCount * messagesPerPublisher;
        string[] expected = Enumerable.Range(0, publisherCount)
            .SelectMany(pub => Enumerable.Range(0, messagesPerPublisher)
                .Select(msg => $"pub-{pub:D2}-msg-{msg:D3}"))
            .ToArray();

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] publishers = Enumerable.Range(0, publisherCount)
            .Select(publisherIndex => Task.Run(async () =>
            {
                await startGate.Task;
                for (int messageIndex = 0; messageIndex < messagesPerPublisher; messageIndex++)
                {
                    byte[] payload = Encoding.UTF8.GetBytes($"pub-{publisherIndex:D2}-msg-{messageIndex:D3}");
                    scope.Journal.Publish(Guid.NewGuid(), payload);
                }
            }))
            .ToArray();

        startGate.TrySetResult();
        await Task.WhenAll(publishers);

        Assert.Equal(expectedTotal, scope.Journal.CaptureHeadSequence());
        Assert.Equal(0, scope.Journal.CaptureTailSequence());

        long nextA = 0;
        long nextB = 0;
        BrokeredJournalDrainResult drainA = readerA.Drain(Guid.Empty, ref nextA);
        BrokeredJournalDrainResult drainB = readerB.Drain(Guid.Empty, ref nextB);

        Assert.Equal(expectedTotal, nextA);
        Assert.Equal(expectedTotal, nextB);
        Assert.Equal(expectedTotal, drainA.Messages.Count);
        Assert.Equal(expectedTotal, drainB.Messages.Count);
        Assert.Equal(expected.OrderBy(static value => value, StringComparer.Ordinal), drainA.Messages.Select(Encoding.UTF8.GetString).OrderBy(static value => value, StringComparer.Ordinal));
        Assert.Equal(expected.OrderBy(static value => value, StringComparer.Ordinal), drainB.Messages.Select(Encoding.UTF8.GetString).OrderBy(static value => value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadersPollingDuringConcurrentPublishes_ReceiveAllMessagesWithoutDuplicationOrCorruption()
    {
        using var scope = new BrokeredChannelJournalTestScope(capacityBytes: 256 * 1024, maxPayloadBytes: 256, minAgeMs: 60_000);
        using BrokeredChannelJournal readerA = scope.Attach();
        using BrokeredChannelJournal readerB = scope.Attach();

        const int publisherCount = 3;
        const int messagesPerPublisher = 40;
        int expectedTotal = publisherCount * messagesPerPublisher;
        string[] expected = Enumerable.Range(0, publisherCount)
            .SelectMany(pub => Enumerable.Range(0, messagesPerPublisher)
                .Select(msg => $"pub-{pub:D2}-msg-{msg:D3}"))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] publishers = Enumerable.Range(0, publisherCount)
            .Select(publisherIndex => Task.Run(async () =>
            {
                await startGate.Task;
                for (int messageIndex = 0; messageIndex < messagesPerPublisher; messageIndex++)
                {
                    byte[] payload = Encoding.UTF8.GetBytes($"pub-{publisherIndex:D2}-msg-{messageIndex:D3}");
                    scope.Journal.Publish(Guid.NewGuid(), payload);
                    if ((messageIndex % 5) == 0)
                        await Task.Delay(1);
                }
            }))
            .ToArray();

        Task<string[]> readerTaskA = CaptureAllMessagesAsync(readerA, expectedTotal);
        Task<string[]> readerTaskB = CaptureAllMessagesAsync(readerB, expectedTotal);

        startGate.TrySetResult();
        await Task.WhenAll(publishers);

        string[] actualA = (await readerTaskA).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        string[] actualB = (await readerTaskB).OrderBy(static value => value, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actualA);
        Assert.Equal(expected, actualB);
    }

    private static async Task<string[]> CaptureAllMessagesAsync(BrokeredChannelJournal journal, int expectedTotal)
    {
        var observed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        long nextSequence = 0;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && observed.Count < expectedTotal)
        {
            BrokeredJournalDrainResult drain = journal.Drain(Guid.Empty, ref nextSequence);
            foreach (byte[] payload in drain.Messages)
            {
                string message = Encoding.UTF8.GetString(payload);
                Assert.StartsWith("pub-", message, StringComparison.Ordinal);
                Assert.True(observed.TryAdd(message, 0), $"Duplicate payload observed: {message}");
            }

            if (observed.Count >= expectedTotal)
                break;

            await Task.Delay(1);
        }

        Assert.Equal(expectedTotal, observed.Count);
        return observed.Keys.ToArray();
    }
}
