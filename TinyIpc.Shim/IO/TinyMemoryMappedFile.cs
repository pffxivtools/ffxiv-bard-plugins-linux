using System.IO.MemoryMappedFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinyIpc.Synchronization;
using XivIpc.Internal;
using XivIpc.IO;

namespace TinyIpc.IO;

public class TinyMemoryMappedFile : ITinyMemoryMappedFile, IDisposable
{
    private readonly UnixTinyMemoryMappedFile _impl;

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
        _impl = new UnixTinyMemoryMappedFile(name, maxFileSize);
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

    internal UnixTinyMemoryMappedFile GetImplementation() => _impl;

    public event EventHandler? FileUpdated
    {
        add => _impl.FileUpdated += value;
        remove => _impl.FileUpdated -= value;
    }

    public long MaxFileSize => _impl.MaxFileSize;

    public int GetFileSize() => _impl.GetFileSize();

    public int GetFileSize(CancellationToken cancellationToken = default) => _impl.GetFileSize(cancellationToken);

    public byte[] Read() => _impl.Read();

    public T Read<T>(Func<MemoryStream, T> readData, CancellationToken cancellationToken = default)
        => _impl.Read(readData, cancellationToken);

    public void Write(byte[] data) => _impl.Write(data);

    public void Write(MemoryStream data, CancellationToken cancellationToken = default)
        => _impl.Write(data, cancellationToken);

    public void ReadWrite(Func<byte[], byte[]> updateFunc) => _impl.ReadWrite(updateFunc);

    public void ReadWrite(Action<MemoryStream, MemoryStream> updateFunc, CancellationToken cancellationToken = default)
        => _impl.ReadWrite(updateFunc, cancellationToken);

    public void Dispose() => _impl.Dispose();
}