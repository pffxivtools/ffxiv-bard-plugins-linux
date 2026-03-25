namespace TinyIpc.Messaging;

public class TinyMessageReceivedEventArgs : EventArgs
{
#if TINYIPC_ABI_5X
        public TinyMessageReceivedEventArgs(BinaryData message)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public BinaryData Message { get; }
#elif TINYIPC_ABI_4X
        public TinyMessageReceivedEventArgs(IReadOnlyList<byte> message)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public IReadOnlyList<byte> Message { get; }
#elif TINYIPC_ABI_3X
        public TinyMessageReceivedEventArgs(byte[] message)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public byte[] Message { get; }
#else
    public TinyMessageReceivedEventArgs(byte[] message)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public TinyMessageReceivedEventArgs(BinaryData message)
        : this((message ?? throw new ArgumentNullException(nameof(message))).ToArray())
    {
    }

    public TinyMessageReceivedEventArgs(IReadOnlyList<byte> message)
        : this(CopyToByteArray(message))
    {
    }

    public byte[] Message { get; }

    public BinaryData BinaryData => BinaryData.FromBytes(Message);
#endif

#if !TINYIPC_ABI_3X && !TINYIPC_ABI_4X
    private static byte[] CopyToByteArray(IReadOnlyList<byte> message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message is byte[] bytes)
            return bytes;

        byte[] copy = new byte[message.Count];
        for (int i = 0; i < message.Count; i++)
            copy[i] = message[i];

        return copy;
    }
#endif
}

