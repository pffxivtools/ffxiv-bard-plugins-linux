using System.IO.MemoryMappedFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinyIpc.Synchronization;
using XivIpc.Internal;
using XivIpc.IO;

namespace TinyIpc.IO;

public class TinyMemoryMappedFile : ITinyMemoryMappedFile, IDisposable
{
    private readonly Lazy<UnixTinyMemoryMappedFile> _impl;

    public const int DefaultMaxFileSize = TinyIpcOptions.DefaultMaxFileSize;

    public string? Name { get; }
    internal long DeclaredMaxFileSize { get; }

    public TinyMemoryMappedFile(string name)
        : this(name, DefaultMaxFileSize)
    {
    }

    public TinyMemoryMappedFile(string name, ILogger<TinyMemoryMappedFile> logger)
        : this(name, DefaultMaxFileSize)
    {
    }

    public TinyMemoryMappedFile(string name, long maxFileSize)
    {
        TinyIpcLogger.EnsureInitialized(RuntimeEnvironmentDetector.Detect());

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("File must be named.", nameof(name));

        if (maxFileSize <= 0 || maxFileSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxFileSize), "Max file size must be between 1 and Int32.MaxValue.");

        Name = name;
        DeclaredMaxFileSize = maxFileSize;
        _impl = new Lazy<UnixTinyMemoryMappedFile>(() => new UnixTinyMemoryMappedFile(name, maxFileSize), isThreadSafe: true);
    }

    public TinyMemoryMappedFile(string name, long maxFileSize, ILogger<TinyMemoryMappedFile> logger)
        : this(name, maxFileSize)
    {
    }

    public TinyMemoryMappedFile(string name, long maxFileSize, ITinyReadWriteLock readWriteLock, bool disposeLock)
        : this(name, maxFileSize, readWriteLock, disposeLock, logger: null)
    {
    }

    public TinyMemoryMappedFile(ITinyReadWriteLock readWriteLock, IOptions<TinyIpcOptions> options, ILogger<TinyMemoryMappedFile> logger)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).Value.Name,
            options.Value.MaxFileSize,
            readWriteLock,
            disposeLock: false,
            logger)
    {
    }

    public TinyMemoryMappedFile(string name, long maxFileSize, ITinyReadWriteLock readWriteLock, bool disposeLock, ILogger<TinyMemoryMappedFile>? logger = null)
        : this(name, maxFileSize)
    {
        ArgumentNullException.ThrowIfNull(readWriteLock);
    }

    public TinyMemoryMappedFile(MemoryMappedFile memoryMappedFile, EventWaitHandle fileWaitHandle, long maxFileSize, ITinyReadWriteLock readWriteLock, bool disposeLock)
        : this(memoryMappedFile, fileWaitHandle, maxFileSize, readWriteLock, disposeLock, logger: null)
    {
    }

    public TinyMemoryMappedFile(MemoryMappedFile memoryMappedFile, EventWaitHandle fileWaitHandle, long maxFileSize, ITinyReadWriteLock readWriteLock, bool disposeLock, ILogger<TinyMemoryMappedFile>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(memoryMappedFile);
        ArgumentNullException.ThrowIfNull(fileWaitHandle);
        ArgumentNullException.ThrowIfNull(readWriteLock);

        throw new PlatformNotSupportedException(
            "The raw MemoryMappedFile/EventWaitHandle TinyMemoryMappedFile constructor is only supported by the Windows implementation. " +
            "This Unix-focused shim supports the named constructors only.");
    }

    public static MemoryMappedFile CreateOrOpenMemoryMappedFile(string name, long maxFileSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (maxFileSize <= 0 || maxFileSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxFileSize), "Max file size must be between 1 and Int32.MaxValue.");

        throw new PlatformNotSupportedException(
            "CreateOrOpenMemoryMappedFile is not supported by this Unix-focused shim. Use the named TinyMemoryMappedFile constructors instead.");
    }

    public static EventWaitHandle CreateEventWaitHandle(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        throw new PlatformNotSupportedException(
            "CreateEventWaitHandle is not supported by this Unix-focused shim.");
    }

    internal UnixTinyMemoryMappedFile GetImplementation() => _impl.Value;

    public event EventHandler? FileUpdated
    {
        add => GetImplementation().FileUpdated += value;
        remove
        {
            if (_impl.IsValueCreated)
                _impl.Value.FileUpdated -= value;
        }
    }

    public long MaxFileSize => DeclaredMaxFileSize;

    public int GetFileSize() => GetImplementation().GetFileSize();

    public int GetFileSize(CancellationToken cancellationToken = default) => GetImplementation().GetFileSize(cancellationToken);

    public byte[] Read() => GetImplementation().Read();

    public T Read<T>(Func<MemoryStream, T> readData, CancellationToken cancellationToken = default)
        => GetImplementation().Read(readData, cancellationToken);

    public void Write(byte[] data) => GetImplementation().Write(data);

    public void Write(MemoryStream data, CancellationToken cancellationToken = default)
        => GetImplementation().Write(data, cancellationToken);

    public void ReadWrite(Func<byte[], byte[]> updateFunc) => GetImplementation().ReadWrite(updateFunc);

    public void ReadWrite(Action<MemoryStream, MemoryStream> updateFunc, CancellationToken cancellationToken = default)
        => GetImplementation().ReadWrite(updateFunc, cancellationToken);

    public void Dispose()
    {
        if (_impl.IsValueCreated)
            _impl.Value.Dispose();
    }
}
