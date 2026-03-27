namespace XivIpc.Messaging;

internal sealed class UnixInMemoryTinyMessageBus : IXivMessageBus
{
    private readonly IDirectMessageStorage _storage;
    private int _disposeSignaled;

    public UnixInMemoryTinyMessageBus(ChannelInfo channelInfo)
        : this(channelInfo.Name, checked(channelInfo.Size))
    {
    }

    public UnixInMemoryTinyMessageBus(string channelName, int maxPayloadBytes)
    {
        _storage = new InMemoryDirectStorage(channelName, maxPayloadBytes);
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
