using XivIpc.Internal;

namespace XivIpc.Messaging;

internal sealed class UnixDirectTinyMessageBus : IXivMessageBus
{
    private readonly IDirectMessageStorage _storage;
    private int _disposeSignaled;

    public UnixDirectTinyMessageBus(ChannelInfo channelInfo, DirectStorageKind storageKind)
        : this(channelInfo.Name, checked(channelInfo.Size), storageKind)
    {
    }

    public UnixDirectTinyMessageBus(string channelName, int maxPayloadBytes, DirectStorageKind storageKind)
    {
        _storage = storageKind switch
        {
            DirectStorageKind.SharedMemory => new SharedMemoryDirectStorage(channelName, maxPayloadBytes),
            _ => new InMemoryDirectStorage(channelName, maxPayloadBytes)
        };

        _storage.MessageReceived += OnStorageMessageReceived;
    }

    public event EventHandler<XivMessageReceivedEventArgs>? MessageReceived;

    public Task PublishAsync(byte[] message) => _storage.PublishAsync(message);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return;

        _storage.MessageReceived -= OnStorageMessageReceived;
        _storage.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return ValueTask.CompletedTask;

        _storage.MessageReceived -= OnStorageMessageReceived;
        return _storage.DisposeAsync();
    }

    private void OnStorageMessageReceived(object? sender, XivMessageReceivedEventArgs e)
        => MessageReceived?.Invoke(this, e);
}
