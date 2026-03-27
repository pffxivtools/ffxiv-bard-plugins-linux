namespace XivIpc.Messaging;

internal interface IDirectMessageStorage : IDisposable, IAsyncDisposable
{
    event EventHandler<XivMessageReceivedEventArgs>? MessageReceived;
    Task PublishAsync(byte[] message);
}
