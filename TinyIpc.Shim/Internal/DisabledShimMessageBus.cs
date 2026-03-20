using TinyIpc.Messaging;
using XivIpc.Internal;

namespace TinyIpc.Internal;

internal sealed class DisabledShimMessageBus : IShimTinyMessageBus
{
    private readonly string _channelName;
    private readonly string _reason;
    private int _publishLogCount;

    public DisabledShimMessageBus(string channelName, string reason, Exception? exception = null)
    {
        _channelName = channelName;
        _reason = reason;

        TinyIpcLogger.Warning(
            nameof(DisabledShimMessageBus),
            "Disabled",
            "TinyIpc message bus is disabled for this channel; using safe no-op behavior.",
            exception,
            ("channel", channelName),
            ("reason", reason));
    }

    public event EventHandler<TinyMessageReceivedEventArgs>? MessageReceived
    {
        add { }
        remove { }
    }

    public Task PublishAsync(byte[] message)
    {
        if (Interlocked.Increment(ref _publishLogCount) <= 3)
        {
            TinyIpcLogger.Warning(
                nameof(DisabledShimMessageBus),
                "PublishDropped",
                "Dropped publish because TinyIpc message bus is disabled.",
                null,
                ("channel", _channelName),
                ("reason", _reason),
                ("messageLength", message?.Length ?? 0));
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}
