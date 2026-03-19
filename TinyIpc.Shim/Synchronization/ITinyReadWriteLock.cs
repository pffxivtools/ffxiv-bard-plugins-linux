using System.Threading;

namespace TinyIpc.Synchronization;

public interface ITinyReadWriteLock : IDisposable
{
    bool IsReaderLockHeld { get; }
    bool IsWriterLockHeld { get; }
    string? Name { get; }

    void AcquireReadLock();
    IDisposable AcquireReadLock(CancellationToken cancellationToken = default);

    void AcquireWriteLock();
    IDisposable AcquireWriteLock(CancellationToken cancellationToken = default);

    void ReleaseReadLock();
    void ReleaseWriteLock();
}