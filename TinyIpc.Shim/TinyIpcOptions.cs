using System.Diagnostics;

namespace TinyIpc;

public class TinyIpcOptions
{
    public const int DefaultMaxFileSize = 1024 * 1024;
    public const int DefaultMaxReaderCount = 6;

    public static readonly TimeSpan DefaultMinMessageAge = TimeSpan.FromMilliseconds(1_000);
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);

    public string Name { get; set; } = Process.GetCurrentProcess().ProcessName;

    public long MaxFileSize { get; set; } = DefaultMaxFileSize;

    public int MaxReaderCount { get; set; } = DefaultMaxReaderCount;

    public TimeSpan MinMessageAge { get; set; } = DefaultMinMessageAge;

    public TimeSpan WaitTimeout { get; set; } = DefaultWaitTimeout;
}

