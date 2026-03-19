using System;
using System.Threading.Tasks;

namespace XivIpc.Messaging
{
    internal interface IXivMessageBus : IDisposable, IAsyncDisposable
    {
        event EventHandler<XivMessageReceivedEventArgs>? MessageReceived;
        Task PublishAsync(byte[] message);
    }
}
