namespace TinyIpc.IO;

public interface ITinyMemoryMappedFile : IDisposable
{
    event EventHandler? FileUpdated;

    long MaxFileSize { get; }

    string? Name { get; }

    int GetFileSize();

    int GetFileSize(CancellationToken cancellationToken = default);

    byte[] Read();

    T Read<T>(Func<MemoryStream, T> readData, CancellationToken cancellationToken = default);

    void Write(byte[] data);

    void Write(MemoryStream data, CancellationToken cancellationToken = default);

    void ReadWrite(Func<byte[], byte[]> updateFunc);

    void ReadWrite(Action<MemoryStream, MemoryStream> updateFunc, CancellationToken cancellationToken = default);
}

