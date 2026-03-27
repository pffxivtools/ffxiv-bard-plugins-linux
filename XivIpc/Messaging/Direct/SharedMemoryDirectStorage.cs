namespace XivIpc.Messaging;

internal sealed class SharedMemoryDirectStorage : IDirectMessageStorage
{
    private readonly UnixSharedMemoryTinyMessageBus _inner;
    private int _disposeSignaled;

    public SharedMemoryDirectStorage(ChannelInfo channelInfo)
        : this(channelInfo.Name, checked(channelInfo.Size))
    {
    }

    public SharedMemoryDirectStorage(string channelName, int maxPayloadBytes)
    {
        _inner = new UnixSharedMemoryTinyMessageBus(channelName, maxPayloadBytes);
        _inner.MessageReceived += OnInnerMessageReceived;
    }

    public event EventHandler<XivMessageReceivedEventArgs>? MessageReceived;

    public Task PublishAsync(byte[] message) => _inner.PublishAsync(message);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return;

        _inner.MessageReceived -= OnInnerMessageReceived;
        _inner.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return ValueTask.CompletedTask;

        _inner.MessageReceived -= OnInnerMessageReceived;
        return _inner.DisposeAsync();
    }

    private void OnInnerMessageReceived(object? sender, XivMessageReceivedEventArgs e)
        => MessageReceived?.Invoke(this, e);
}
