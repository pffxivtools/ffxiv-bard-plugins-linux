using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TinyIpc.Synchronization;

public sealed class TinyReadWriteLock : ITinyReadWriteLock
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly TimeSpan _waitTimeout;
    private bool _disposed;

    [ThreadStatic]
    private static int _readerDepth;

    [ThreadStatic]
    private static int _writerDepth;

    public string? Name { get; }

    public bool IsReaderLockHeld => _readerDepth > 0;
    public bool IsWriterLockHeld => _writerDepth > 0;

    public TinyReadWriteLock(IOptions<TinyIpcOptions> options, ILogger<TinyReadWriteLock> logger)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).Value.Name,
            options.Value.MaxReaderCount,
            options.Value.WaitTimeout,
            logger)
    {
    }

    public TinyReadWriteLock(string name)
        : this(name, TinyIpcOptions.DefaultMaxReaderCount, TinyIpcOptions.DefaultWaitTimeout, logger: null)
    {
    }

    public TinyReadWriteLock(string name, ILogger<TinyReadWriteLock>? logger = null)
        : this(name, TinyIpcOptions.DefaultMaxReaderCount, TinyIpcOptions.DefaultWaitTimeout, logger)
    {
    }

    public TinyReadWriteLock(string name, int maxReaderCount)
        : this(name, maxReaderCount, TinyIpcOptions.DefaultWaitTimeout, logger: null)
    {
    }

    public TinyReadWriteLock(string name, int maxReaderCount, ILogger<TinyReadWriteLock>? logger = null)
        : this(name, maxReaderCount, TinyIpcOptions.DefaultWaitTimeout, logger)
    {
    }

    public TinyReadWriteLock(string name, int maxReaderCount, TimeSpan waitTimeout)
        : this(name, maxReaderCount, waitTimeout, logger: null)
    {
    }

    public TinyReadWriteLock(string name, int maxReaderCount, TimeSpan waitTimeout, ILogger<TinyReadWriteLock>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Lock must be named.", nameof(name));

        if (maxReaderCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxReaderCount));

        if (waitTimeout < Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));

        Name = name;
        _waitTimeout = waitTimeout;
    }

    public TinyReadWriteLock(Mutex mutex, Semaphore semaphore, int maxReaderCount, TimeSpan waitTimeout, ILogger<TinyReadWriteLock>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        ArgumentNullException.ThrowIfNull(semaphore);

        if (maxReaderCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxReaderCount));

        if (waitTimeout < Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));

        Name = null;
        _waitTimeout = waitTimeout;
    }

    public TinyReadWriteLock(Mutex mutex, Semaphore semaphore, int maxReaderCount, TimeSpan waitTimeout)
        : this(mutex, semaphore, maxReaderCount, waitTimeout, logger: null)
    {
    }

    public void AcquireReadLock()
    {
        ThrowIfDisposed();

        if (!TryEnterReadLock(_waitTimeout, CancellationToken.None))
            throw new TimeoutException("Timed out acquiring read lock.");
    }

    public IDisposable AcquireReadLock(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!TryEnterReadLock(_waitTimeout, cancellationToken))
            throw new TimeoutException("Timed out acquiring read lock.");

        return new Releaser(this, write: false);
    }

    public void AcquireWriteLock()
    {
        ThrowIfDisposed();

        if (!TryEnterWriteLock(_waitTimeout, CancellationToken.None))
            throw new TimeoutException("Timed out acquiring write lock.");
    }

    public IDisposable AcquireWriteLock(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!TryEnterWriteLock(_waitTimeout, cancellationToken))
            throw new TimeoutException("Timed out acquiring write lock.");

        return new Releaser(this, write: true);
    }

    public void ReleaseReadLock()
    {
        ThrowIfDisposed();

        if (_readerDepth <= 0)
            return;

        _readerDepth--;
        _lock.ExitReadLock();
    }

    public void ReleaseWriteLock()
    {
        ThrowIfDisposed();

        if (_writerDepth <= 0)
            return;

        _writerDepth--;
        _lock.ExitWriteLock();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lock.Dispose();
    }

    private bool TryEnterReadLock(TimeSpan timeout, CancellationToken cancellationToken)
    {
        int timeoutMs = ToMilliseconds(timeout);
        var started = Environment.TickCount64;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_lock.TryEnterReadLock(50))
            {
                _readerDepth++;
                return true;
            }

            if (timeoutMs != Timeout.Infinite && ElapsedMilliseconds(started) >= timeoutMs)
                return false;
        }
    }

    private bool TryEnterWriteLock(TimeSpan timeout, CancellationToken cancellationToken)
    {
        int timeoutMs = ToMilliseconds(timeout);
        var started = Environment.TickCount64;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_lock.TryEnterWriteLock(50))
            {
                _writerDepth++;
                return true;
            }

            if (timeoutMs != Timeout.Infinite && ElapsedMilliseconds(started) >= timeoutMs)
                return false;
        }
    }

    private static int ToMilliseconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.Infinite;

        if (timeout < TimeSpan.Zero)
            return 0;

        if (timeout.TotalMilliseconds > int.MaxValue)
            return int.MaxValue;

        return (int)timeout.TotalMilliseconds;
    }

    private static long ElapsedMilliseconds(long started)
        => unchecked(Environment.TickCount64 - started);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TinyReadWriteLock));
    }

    private sealed class Releaser : IDisposable
    {
        private readonly TinyReadWriteLock _owner;
        private readonly bool _write;
        private int _disposed;

        public Releaser(TinyReadWriteLock owner, bool write)
        {
            _owner = owner;
            _write = write;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (_write)
                _owner.ReleaseWriteLock();
            else
                _owner.ReleaseReadLock();
        }
    }
}