using System.Collections.Generic;

namespace TinyIpc.Messaging;

public interface ITinyMessageBus : IDisposable, IAsyncDisposable
{
    event EventHandler<TinyMessageReceivedEventArgs>? MessageReceived;

    long MessagesPublished { get; }

    long MessagesReceived { get; }

    string? Name { get; }

    void ResetMetrics();

    Task PublishAsync(byte[] message);

    Task PublishAsync(IReadOnlyList<byte> message);

    Task PublishAsync(IReadOnlyList<IReadOnlyList<byte>> messages);

    Task PublishAsync(IEnumerable<byte[]> messages);

    Task PublishAsync(BinaryData message, CancellationToken cancellationToken = default);

    Task PublishAsync(IReadOnlyList<BinaryData> messages, CancellationToken cancellationToken = default);

#if TINYIPC_ABI_4X
    IAsyncEnumerable<IReadOnlyList<byte>> SubscribeAsync(CancellationToken cancellationToken = default);
#else
    IAsyncEnumerable<BinaryData> SubscribeAsync(CancellationToken cancellationToken = default);
#endif
}
