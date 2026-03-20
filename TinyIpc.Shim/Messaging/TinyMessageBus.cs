using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinyIpc.IO;
using TinyIpc.Internal;
using XivIpc.Messaging;
using XivIpc.Internal;

namespace TinyIpc.Messaging;

public sealed class TinyMessageBus : ITinyMessageBus
{
    private readonly IShimTinyMessageBus _impl;
    private long _messagesPublished;
    private long _messagesReceived;
    private bool _disposed;

    public static readonly TimeSpan DefaultMinMessageAge = TinyIpcOptions.DefaultMinMessageAge;

    public TinyMessageBus(string name)
        : this(new TinyMemoryMappedFile(name), disposeFile: true, minMessageAge: null, timeProvider: null, options: null, logger: null)
    {
    }

    public TinyMessageBus(string name, ILogger<TinyMessageBus>? logger)
        : this(new TinyMemoryMappedFile(name), disposeFile: true, minMessageAge: null, timeProvider: null, options: null, logger)
    {
    }

    public TinyMessageBus(string name, TimeSpan minMessageAge)
        : this(new TinyMemoryMappedFile(name), disposeFile: true, minMessageAge, timeProvider: null, options: null, logger: null)
    {
    }

    public TinyMessageBus(string name, TimeSpan minMessageAge, ILogger<TinyMessageBus>? logger)
        : this(new TinyMemoryMappedFile(name), disposeFile: true, minMessageAge, timeProvider: null, options: null, logger)
    {
    }

    public TinyMessageBus(string name, IOptions<TinyIpcOptions>? options = null, ILogger<TinyMessageBus>? logger = null)
        : this(new TinyMemoryMappedFile(name), disposeFile: true, minMessageAge: null, timeProvider: null, options, logger)
    {
    }

    public TinyMessageBus(ITinyMemoryMappedFile memoryMappedFile, bool disposeFile)
        : this(memoryMappedFile, disposeFile, minMessageAge: null, timeProvider: null, options: null, logger: null)
    {
    }

    public TinyMessageBus(ITinyMemoryMappedFile memoryMappedFile, bool disposeFile, TimeSpan minMessageAge)
        : this(memoryMappedFile, disposeFile, minMessageAge, timeProvider: null, options: null, logger: null)
    {
    }

    public TinyMessageBus(ITinyMemoryMappedFile memoryMappedFile, bool disposeFile, ILogger<TinyMessageBus>? logger)
        : this(memoryMappedFile, disposeFile, minMessageAge: null, timeProvider: null, options: null, logger)
    {
    }

    public TinyMessageBus(ITinyMemoryMappedFile memoryMappedFile, IOptions<TinyIpcOptions>? options = null, ILogger<TinyMessageBus>? logger = null)
        : this(memoryMappedFile, disposeFile: false, minMessageAge: null, timeProvider: null, options, logger)
    {
    }

    public TinyMessageBus(ITinyMemoryMappedFile memoryMappedFile, bool disposeFile, IOptions<TinyIpcOptions>? options = null, ILogger<TinyMessageBus>? logger = null)
        : this(memoryMappedFile, disposeFile, minMessageAge: null, timeProvider: null, options, logger)
    {
    }

    public TinyMessageBus(ITinyMemoryMappedFile memoryMappedFile, bool disposeFile, TimeProvider timeProvider, IOptions<TinyIpcOptions> options, ILogger<TinyMessageBus>? logger = null)
        : this(memoryMappedFile, disposeFile, minMessageAge: null, timeProvider, options, logger)
    {
    }

    private TinyMessageBus(
        ITinyMemoryMappedFile memoryMappedFile,
        bool disposeFile,
        TimeSpan? minMessageAge,
        TimeProvider? timeProvider,
        IOptions<TinyIpcOptions>? options,
        ILogger<TinyMessageBus>? logger)
    {
        TinyIpcLogger.EnsureInitialized(RuntimeEnvironmentDetector.Detect());
        ArgumentNullException.ThrowIfNull(memoryMappedFile);
        TinyIpcProcessStamp stamp = TinyIpcProcessStamp.Create(typeof(TinyMessageBus));
        TinyIpcLogger.Info(
            nameof(TinyMessageBus),
            "ClientProcessStamp",
            "Resolved TinyIpc client process stamp.",
            ("assemblyPath", stamp.AssemblyPath),
            ("assemblyName", stamp.AssemblyName),
            ("informationalVersion", stamp.InformationalVersion),
            ("fileVersion", stamp.FileVersion),
            ("sha256", stamp.Sha256),
            ("processPath", stamp.ProcessPath),
            ("processSha256", stamp.ProcessSha256));

        if (memoryMappedFile is not TinyMemoryMappedFile shimFile)
            throw new ArgumentException("TinyMessageBus requires the TinyIpc.Shim TinyMemoryMappedFile implementation.", nameof(memoryMappedFile));

        Name = shimFile.Name;
        ChannelInfo channelInfo = new ChannelInfo(Name ?? throw new InvalidOperationException("Memory-mapped file must have a name."),
            checked((int)shimFile.DeclaredMaxFileSize));
        _impl = UnixMessageBusBackendFactory.Create(channelInfo);
        _impl.MessageReceived += OnImplMessageReceived;
    }

    public event EventHandler<TinyMessageReceivedEventArgs>? MessageReceived;

    public long MessagesPublished => Interlocked.Read(ref _messagesPublished);

    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    public string? Name { get; }

    public void ResetMetrics()
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _messagesPublished, 0);
        Interlocked.Exchange(ref _messagesReceived, 0);
    }

    public Task PublishAsync(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        return PublishCoreAsync(message);
    }

    public Task PublishAsync(IReadOnlyList<byte> message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message is byte[] bytes)
            return PublishAsync(bytes);

        byte[] copy = new byte[message.Count];
        for (int i = 0; i < message.Count; i++)
            copy[i] = message[i];

        return PublishAsync(copy);
    }

    public async Task PublishAsync(IReadOnlyList<IReadOnlyList<byte>> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (IReadOnlyList<byte> message in messages)
            await PublishAsync(message).ConfigureAwait(false);
    }

    public async Task PublishAsync(IEnumerable<byte[]> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (byte[] message in messages)
            await PublishAsync(message).ConfigureAwait(false);
    }

    public Task PublishAsync(BinaryData message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        return PublishAsync(message.ToArray());
    }

    public async Task PublishAsync(IReadOnlyList<BinaryData> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (BinaryData message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

#if TINYIPC_ABI_4X
    public async IAsyncEnumerable<IReadOnlyList<byte>> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var channel = Channel.CreateUnbounded<IReadOnlyList<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        void Handler(object? sender, TinyMessageReceivedEventArgs e) => channel.Writer.TryWrite(e.Message);

        MessageReceived += Handler;
        using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
        {
            ((Channel<IReadOnlyList<byte>>)state!).Writer.TryComplete();
        }, channel);

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out IReadOnlyList<byte> item))
                    yield return item;
            }
        }
        finally
        {
            MessageReceived -= Handler;
            channel.Writer.TryComplete();
        }
    }
#else
    public async IAsyncEnumerable<BinaryData> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var channel = Channel.CreateUnbounded<BinaryData>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        void Handler(object? sender, TinyMessageReceivedEventArgs e) => channel.Writer.TryWrite(GetBinaryData(e));

        MessageReceived += Handler;
        using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
        {
            ((Channel<BinaryData>)state!).Writer.TryComplete();
        }, channel);

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out BinaryData item))
                    yield return item;
            }
        }
        finally
        {
            MessageReceived -= Handler;
            channel.Writer.TryComplete();
        }
    }
#endif

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _impl.MessageReceived -= OnImplMessageReceived;
        _impl.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _impl.MessageReceived -= OnImplMessageReceived;
        await _impl.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PublishCoreAsync(byte[] message)
    {
        await _impl.PublishAsync(message).ConfigureAwait(false);
        Interlocked.Increment(ref _messagesPublished);
    }

    private void OnImplMessageReceived(object? sender, TinyMessageReceivedEventArgs e)
    {
        Interlocked.Increment(ref _messagesReceived);
        MessageReceived?.Invoke(this, e);
    }

#if !TINYIPC_ABI_4X
    private static BinaryData GetBinaryData(TinyMessageReceivedEventArgs e)
    {
#if TINYIPC_ABI_5X
        return e.Message;
#else
        return BinaryData.FromBytes(e.Message);
#endif
    }
#endif

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TinyMessageBus));
    }
}

internal interface IShimTinyMessageBus : IDisposable, IAsyncDisposable
{
    event EventHandler<TinyMessageReceivedEventArgs>? MessageReceived;
    Task PublishAsync(byte[] message);
}

internal sealed class XivMessageBusAdapter : IShimTinyMessageBus
{
    private readonly IXivMessageBus _inner;

    public XivMessageBusAdapter(IXivMessageBus inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.MessageReceived += OnInnerMessageReceived;
    }

    public event EventHandler<TinyMessageReceivedEventArgs>? MessageReceived;

    public Task PublishAsync(byte[] message) => _inner.PublishAsync(message);

    public void Dispose()
    {
        _inner.MessageReceived -= OnInnerMessageReceived;
        _inner.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        _inner.MessageReceived -= OnInnerMessageReceived;
        return _inner.DisposeAsync();
    }

    private void OnInnerMessageReceived(object? sender, XivMessageReceivedEventArgs e)
    {
#if TINYIPC_ABI_5X
        MessageReceived?.Invoke(this, new TinyMessageReceivedEventArgs(BinaryData.FromBytes(e.Message)));
#else
        MessageReceived?.Invoke(this, new TinyMessageReceivedEventArgs(e.Message));
#endif
    }
}
