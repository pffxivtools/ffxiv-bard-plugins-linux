using System.Diagnostics;
using System.Net.Sockets;
using XivIpc.Internal;

namespace XivIpc.Messaging;

internal static class UnixSidecarProcessManager
{
    private const string BrokerDirectoryName = "xivipc";
    private const string BrokerSocketFileName = "tinyipc-sidecar.sock";
    private const string StartupLockName = "tinyipc-broker";
    private const string StartupLockKind = "sidecar-startup";
    private const int StartupTimeoutMs = 10_000;

    private static readonly object Sync = new();

    private static Process? _process;
    private static int _refCount;

    internal static Lease Acquire()
    {
        lock (Sync)
        {
            string socketPath = ResolveBrokerSocketPath();
            EnsureBrokerStarted(socketPath);
            _refCount++;
            return new Lease(socketPath);
        }
    }

    private static void EnsureBrokerStarted(string socketPath)
    {
        if (CanConnect(socketPath))
            return;

        string brokerDirectory = Path.GetDirectoryName(socketPath) ?? throw new InvalidOperationException("Broker socket path must have a parent directory.");
        Directory.CreateDirectory(UnixSharedStorageHelpers.ConvertPathForCurrentRuntime(brokerDirectory));

        var startupLock = new UnixSharedFileLock(StartupLockName, StartupLockKind);
        startupLock.Execute(() =>
        {
            if (CanConnect(socketPath))
                return;

            CleanupStaleSocket(socketPath);

            if (!TryStartHelper(socketPath, out string failureDetails))
            {
                throw new InvalidOperationException(
                    $"Failed to start native TinyIpc broker for socket '{socketPath}'.{Environment.NewLine}{failureDetails}");
            }

            WaitForBroker(socketPath, TimeSpan.FromMilliseconds(StartupTimeoutMs));
        });
    }

    private static void WaitForBroker(string socketPath, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Exception? last = null;

        while (sw.Elapsed < timeout)
        {
            try
            {
                if (CanConnect(socketPath))
                    return;

                lock (Sync)
                {
                    if (_process is not null && _process.HasExited)
                        throw new InvalidOperationException($"Native TinyIpc broker exited before socket '{socketPath}' became reachable. ExitCode={_process.ExitCode}.");
                }
            }
            catch (Exception ex)
            {
                if (ex is InvalidOperationException)
                    throw;

                last = ex;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"Timed out waiting for broker socket '{socketPath}' to become reachable.", last);
    }

    private static bool TryStartHelper(string socketPath, out string failureDetails)
    {
        ProcessStartInfo psi = BuildStartInfo(socketPath, out string launchCommand, out string hostPathUnix, out string hostPathWindows);

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            failureDetails = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            failureDetails =
                $"launchCommand={launchCommand}"
                + $" hostPathWindows={hostPathWindows}"
                + $" hostPathUnix={hostPathUnix}"
                + Environment.NewLine
                + ex;
            return false;
        }
    }

    private static ProcessStartInfo BuildStartInfo(string socketPath, out string launchCommand, out string hostPathUnix, out string hostPathWindows)
    {
        string shellPath = Environment.GetEnvironmentVariable("TINYIPC_UNIX_SHELL") ?? "/bin/sh";
        hostPathWindows = ResolveNativeHostPath();
        hostPathUnix = ConvertWindowsPathToUnix(hostPathWindows);

        bool isDll = string.Equals(Path.GetExtension(hostPathUnix), ".dll", StringComparison.OrdinalIgnoreCase);
        string executableCommand = isDll
            ? "/usr/bin/env dotnet " + QuoteForShell(hostPathUnix)
            : QuoteForShell(hostPathUnix);

        launchCommand = executableCommand;

        var psi = new ProcessStartInfo(shellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(launchCommand);

        CopyPathEnvironment(psi, "TINYIPC_SHARED_DIR");
        CopyPathEnvironment(psi, "TINYIPC_BROKER_DIR");
        CopyVerbatimEnvironment(psi, "TINYIPC_ENABLE_LOGGING");
        CopyVerbatimEnvironment(psi, "TINYIPC_LOG_LEVEL");
        CopyVerbatimEnvironment(psi, "TINYIPC_LOG_PAYLOAD");
        CopyVerbatimEnvironment(psi, "TINYIPC_LOG_MAX_BYTES");
        CopyVerbatimEnvironment(psi, "TINYIPC_LOG_FILE_COUNT");
        CopyVerbatimEnvironment(psi, "TINYIPC_SHARED_GROUP");

        psi.Environment["TINYIPC_BROKER_SOCKET_PATH"] = ConvertUnixPathForChild(socketPath);
        psi.Environment["TINYIPC_BUS_BACKEND"] = "sidecar-brokered";

        return psi;
    }

    private static void CleanupStaleSocket(string socketPath)
    {
        if (CanConnect(socketPath))
            return;

        try
        {
            File.Delete(UnixSharedStorageHelpers.ConvertPathForCurrentRuntime(socketPath));
        }
        catch
        {
        }
    }

    private static bool CanConnect(string socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
            return false;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = 250;
            socket.SendTimeout = 250;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveBrokerSocketPath()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("TINYIPC_BROKER_SOCKET_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return ResolveUnixPath(explicitPath);

        string directory = ResolveBrokerDirectory();
        Directory.CreateDirectory(directory);
        return CombineUnixPath(directory, BrokerSocketFileName);
    }

    private static string ResolveBrokerDirectory()
    {
        string? explicitDirectory = Environment.GetEnvironmentVariable("TINYIPC_BROKER_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return ResolveUnixPath(explicitDirectory);

        string? sharedDirectory = Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");
        if (!string.IsNullOrWhiteSpace(sharedDirectory))
            return ResolveUnixPath(sharedDirectory);

        return Path.Combine("/run", BrokerDirectoryName);
    }

    internal static string ResolveNativeHostPath()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string resolved = ResolveNativePath(explicitPath);
            if (File.Exists(resolved))
                return resolved;
        }

        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "XivIpc.NativeHost"),
            Path.Combine(baseDir, "XivIpc.NativeHost.dll"),
            Path.Combine(baseDir, "XivIpc.NativeHost.exe"),

            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net10.0", "XivIpc.NativeHost")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net10.0", "XivIpc.NativeHost.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net9.0", "XivIpc.NativeHost")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net9.0", "XivIpc.NativeHost.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net10.0", "XivIpc.NativeHost")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net10.0", "XivIpc.NativeHost.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net9.0", "XivIpc.NativeHost")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net9.0", "XivIpc.NativeHost.dll"))
        };

        string? existing = candidates.FirstOrDefault(File.Exists);
        return existing ?? throw new InvalidOperationException("XivIpc.NativeHost executable or dll was not found.");
    }

    internal static int GetOwnedProcessIdForDiagnostics()
    {
        lock (Sync)
        {
            try
            {
                return _process is { HasExited: false } ? _process.Id : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    internal static bool WaitForOwnedProcessExitForDiagnostics(TimeSpan timeout)
    {
        Process? process;

        lock (Sync)
            process = _process;

        if (process is null)
            return true;

        try
        {
            bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);
            if (exited)
            {
                lock (Sync)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        try { _process.Dispose(); } catch { }
                        _process = null;
                    }
                }
            }

            return exited;
        }
        catch
        {
            return false;
        }
    }

    private static void Release()
    {
        lock (Sync)
        {
            if (_refCount > 0)
                _refCount--;

            if (_process is null)
                return;

            bool hasExited;
            try
            {
                hasExited = _process.HasExited;
            }
            catch
            {
                hasExited = true;
            }

            if (!hasExited)
                return;

            try
            {
                _process.Dispose();
            }
            catch
            {
            }

            _process = null;
        }
    }

    private static void CopyVerbatimEnvironment(ProcessStartInfo psi, string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value))
            psi.Environment[variableName] = value;
    }

    private static void CopyPathEnvironment(ProcessStartInfo psi, string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value))
            psi.Environment[variableName] = ResolveUnixPath(value);
    }

    private static string ResolveNativePath(string rawPath)
    {
        RuntimeEnvironmentInfo runtime = RuntimeEnvironmentDetector.Detect();
        if (runtime.IsWindowsProcess)
        {
            string windowsPath = ConvertUnixPathToWindows(rawPath);
            return Path.IsPathRooted(windowsPath) ? windowsPath : Path.GetFullPath(windowsPath);
        }

        string unixPath = ConvertWindowsPathToUnix(rawPath);
        return Path.IsPathRooted(unixPath) ? unixPath : Path.GetFullPath(unixPath);
    }

    private static string ResolveUnixPath(string rawPath)
    {
        string unixPath = ConvertWindowsPathToUnix(rawPath);
        return Path.IsPathRooted(unixPath) ? unixPath : Path.GetFullPath(unixPath);
    }

    private static string ConvertUnixPathForChild(string path)
    {
        return ResolveUnixPath(path);
    }

    private static string ConvertWindowsPathToUnix(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        string normalized = path.Replace('\\', '/');
        if (normalized.Length >= 3 &&
            char.IsLetter(normalized[0]) &&
            normalized[1] == ':' &&
            normalized[2] == '/')
        {
            char drive = char.ToUpperInvariant(normalized[0]);
            string remainder = normalized[3..];
            return drive == 'Z' ? "/" + remainder : $"/mnt/{char.ToLowerInvariant(drive)}/{remainder}";
        }

        return normalized;
    }

    private static string ConvertUnixPathToWindows(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        if (IsWindowsStylePath(path))
            return path.Replace('/', '\\');

        string normalized = path.Replace('\\', '/');
        if (normalized[0] == '/')
            return "Z:" + normalized.Replace('/', '\\');

        return path.Replace('/', '\\');
    }

    private static string ConvertUnixPathToWine(string path)
        => "Z:" + path.Replace('/', '\\');

    private static string CombineUnixPath(string directory, string fileName)
    {
        string normalizedDirectory = ResolveUnixPath(directory).TrimEnd('/');
        return normalizedDirectory.Length == 0 ? "/" + fileName : normalizedDirectory + "/" + fileName;
    }

    private static bool IsWindowsStylePath(string path)
        => !string.IsNullOrWhiteSpace(path)
           && path.Length >= 3
           && char.IsLetter(path[0])
           && path[1] == ':'
           && (path[2] == '\\' || path[2] == '/');

    private static string QuoteForShell(string value)
        => "'" + value.Replace("'", "'\"'\"'") + "'";

    internal readonly struct Lease : IDisposable
    {
        internal Lease(string socketPath) => SocketPath = socketPath;
        internal string SocketPath { get; }
        public void Dispose() => Release();
    }
}
