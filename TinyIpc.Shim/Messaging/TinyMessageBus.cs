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
    private readonly object _implGate = new();
    private IShimTinyMessageBus _impl;
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
        try
        {
            ChannelInfo channelInfo = new ChannelInfo(Name ?? throw new InvalidOperationException("Memory-mapped file must have a name."),
                checked((int)shimFile.DeclaredMaxFileSize));
            _impl = UnixMessageBusBackendFactory.Create(channelInfo);
            _impl.MessageReceived += OnImplMessageReceived;
        }
        catch (Exception ex)
        {
            _impl = CreateDisabledImpl("Constructor", "TinyMessageBus initialization failed and the bus was disabled.", ex);
            _impl.MessageReceived += OnImplMessageReceived;
        }
    }

    internal TinyMessageBus(string name, IShimTinyMessageBus impl)
    {
        TinyIpcLogger.EnsureInitialized(RuntimeEnvironmentDetector.Detect());
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _impl = impl ?? throw new ArgumentNullException(nameof(impl));
        _impl.MessageReceived += OnImplMessageReceived;
    }

    public event EventHandler<TinyMessageReceivedEventArgs>? MessageReceived;

    public long MessagesPublished => Interlocked.Read(ref _messagesPublished);

    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    public string? Name { get; }

    public void ResetMetrics()
    {
        ThrowIfDisposed();

        try
        {
            Interlocked.Exchange(ref _messagesPublished, 0);
            Interlocked.Exchange(ref _messagesReceived, 0);
        }
        catch (Exception ex)
        {
            HandleEntryPointFailure("ResetMetrics", ex);
        }
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
            while (true)
            {
                bool canRead;
                try
                {
                    canRead = await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    HandleEntryPointFailure("SubscribeAsync", ex);
                    yield break;
                }

                if (!canRead)
                    break;

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
            while (true)
            {
                bool canRead;
                try
                {
                    canRead = await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    HandleEntryPointFailure("SubscribeAsync", ex);
                    yield break;
                }

                if (!canRead)
                    break;

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
        IShimTinyMessageBus impl = GetCurrentImpl();
        try
        {
            impl.MessageReceived -= OnImplMessageReceived;
        }
        catch (Exception ex)
        {
            LogDisposeFailure("Dispose", ex, impl);
        }

        try
        {
            impl.Dispose();
        }
        catch (Exception ex)
        {
            LogDisposeFailure("Dispose", ex, impl);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        IShimTinyMessageBus impl = GetCurrentImpl();

        try
        {
            impl.MessageReceived -= OnImplMessageReceived;
        }
        catch (Exception ex)
        {
            LogDisposeFailure("DisposeAsync", ex, impl);
        }

        try
        {
            await impl.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDisposeFailure("DisposeAsync", ex, impl);
        }
    }

    private async Task PublishCoreAsync(byte[] message)
    {
        try
        {
            await GetCurrentImpl().PublishAsync(message).ConfigureAwait(false);
            Interlocked.Increment(ref _messagesPublished);
        }
        catch (Exception ex)
        {
            HandleEntryPointFailure("PublishAsync", ex);
        }
    }

    private void OnImplMessageReceived(object? sender, TinyMessageReceivedEventArgs e)
    {
        Interlocked.Increment(ref _messagesReceived);

        try
        {
            MessageReceived?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            TinyIpcLogger.Error(
                nameof(TinyMessageBus),
                "ShimMessageReceivedHandlerFailed",
                "A TinyIpc message handler threw while processing a received message.",
                ex,
                ("channel", Name),
                ("backendType", sender?.GetType().FullName ?? string.Empty));
        }
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

    private IShimTinyMessageBus GetCurrentImpl()
    {
        lock (_implGate)
            return _impl;
    }

    internal Task WaitForConnectedForDiagnosticsAsync(TimeSpan timeout)
    {
        IShimTinyMessageBus impl = GetCurrentImpl();
        if (impl is XivMessageBusAdapter adapter)
            return adapter.WaitForConnectedForDiagnosticsAsync(timeout);

        return Task.CompletedTask;
    }

    private DisabledShimMessageBus CreateDisabledImpl(string entryPoint, string reason, Exception ex)
    {
        TinyIpcLogger.Warning(
            nameof(TinyMessageBus),
            "ShimEntryPointFailed",
            "A TinyIpc shim entrypoint failed.",
            ex,
            ("channel", Name),
            ("entryPoint", entryPoint),
            ("backendType", _impl?.GetType().FullName ?? string.Empty),
            ("alreadyDisabled", _impl is DisabledShimMessageBus));

        var disabled = new DisabledShimMessageBus(Name ?? string.Empty, reason, ex);
        TinyIpcLogger.Warning(
            nameof(TinyMessageBus),
            "ShimDegradedToDisabled",
            "TinyIpc degraded this bus instance into disabled/no-op behavior after an entrypoint failure.",
            ex,
            ("channel", Name),
            ("entryPoint", entryPoint));
        return disabled;
    }

    private void HandleEntryPointFailure(string entryPoint, Exception ex)
    {
        IShimTinyMessageBus? previousImpl = null;

        lock (_implGate)
        {
            if (_impl is DisabledShimMessageBus)
            {
                TinyIpcLogger.Warning(
                    nameof(TinyMessageBus),
                    "ShimEntryPointFailed",
                    "A TinyIpc shim entrypoint failed while the bus was already disabled.",
                    ex,
                    ("channel", Name),
                    ("entryPoint", entryPoint),
                    ("backendType", _impl.GetType().FullName ?? string.Empty),
                    ("alreadyDisabled", true));
                return;
            }

            previousImpl = _impl;
            try
            {
                previousImpl.MessageReceived -= OnImplMessageReceived;
            }
            catch
            {
            }

            _impl = CreateDisabledImpl(entryPoint, $"TinyIpc entrypoint '{entryPoint}' failed and the bus was disabled.", ex);
            _impl.MessageReceived += OnImplMessageReceived;
        }

        try
        {
            previousImpl?.Dispose();
        }
        catch (Exception disposeEx)
        {
            LogDisposeFailure(entryPoint, disposeEx, previousImpl);
        }
    }

    private void LogDisposeFailure(string entryPoint, Exception ex, IShimTinyMessageBus? impl)
    {
        TinyIpcLogger.Warning(
            nameof(TinyMessageBus),
            "ShimDisposeFailed",
            "TinyIpc encountered a backend cleanup failure while disposing a bus instance.",
            ex,
            ("channel", Name),
            ("entryPoint", entryPoint),
            ("backendType", impl?.GetType().FullName ?? string.Empty));
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
    private int _disposeSignaled;

    public XivMessageBusAdapter(IXivMessageBus inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.MessageReceived += OnInnerMessageReceived;
    }

    public event EventHandler<TinyMessageReceivedEventArgs>? MessageReceived;

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

    internal Task WaitForConnectedForDiagnosticsAsync(TimeSpan timeout)
    {
        if (_inner is UnixSidecarTinyMessageBus sidecar)
            return sidecar.WaitForConnectedForDiagnosticsAsync(timeout);

        return Task.CompletedTask;
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
