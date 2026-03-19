using System;

namespace XivIpc.Messaging
{
    internal sealed class XivMessageReceivedEventArgs : EventArgs
    {
        public XivMessageReceivedEventArgs(byte[] message)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public byte[] Message { get; }
    }
}
