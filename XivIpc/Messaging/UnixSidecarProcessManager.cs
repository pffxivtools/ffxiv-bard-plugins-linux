using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XivIpc.Internal;

namespace XivIpc.Messaging;

internal static class UnixSidecarProcessManager
{
    private const string NativeHostStagingDirectoryName = "tinyipc-native-host";
    private const string SharedSidecarScopeName = "tinyipc-sidecar";
    private const string EndpointFileName = "tinyipc-sidecar.endpoint";
    private const string StartupLockKind = "sidecar-startup";
    private const int StartupTimeoutMs = 10_000;

    private static readonly object Sync = new();

    private static Process? _process;
    private static int _port;
    private static int _refCount;
    private static string? _stdoutLogPath;
    private static string? _stderrLogPath;

    internal static Lease Acquire()
    {
        lock (Sync)
        {
            EnsureProcessStarted();
            _refCount++;

            TinyIpcLogger.Info(
                nameof(UnixSidecarProcessManager),
                "HelperLeaseAcquired",
                "Acquired sidecar helper lease.",
                ("port", _port),
                ("refCount", _refCount));

            return new Lease(_port);
        }
    }

    private static void EnsureProcessStarted()
    {
        if (_port > 0 && IsPortResponsive(_port, TimeSpan.FromMilliseconds(100)))
            return;

        string sharedDirectoryNative = ResolveSharedDirectoryNativePath();
        Directory.CreateDirectory(sharedDirectoryNative);

        string endpointPath = GetEndpointFilePath(sharedDirectoryNative);
        var startupLock = new UnixSharedFileLock(SharedSidecarScopeName, StartupLockKind);

        startupLock.Execute(() =>
        {
            if (TryReadLiveEndpoint(endpointPath, out int sharedPort))
            {
                _port = sharedPort;
                TinyIpcLogger.Info(
                    nameof(UnixSidecarProcessManager),
                    "ReusedSharedEndpoint",
                    "Reused existing shared sidecar endpoint.",
                    ("port", _port),
                    ("endpointPath", endpointPath));
                return;
            }

            CleanupStaleEndpoint(endpointPath);

            if (_process is not null && SafeGetPid(_process) != 0)
                DisposeOwnedProcessOnly();

            if (!TryStartHelper(StartupMode.Shell, endpointPath, out string shellFailure))
            {
                TinyIpcLogger.Info(
                    nameof(UnixSidecarProcessManager),
                    "HelperStartFailed",
                    "Failed to start native sidecar helper with shell. Attempting fallback.",
                    ("failureDetails", shellFailure));

                DisposeOwnedProcessOnly();

                if (!TryStartHelper(StartupMode.CmdUnix, endpointPath, out string cmdUnixFailure))
                {
                    TinyIpcLogger.Info(
                        nameof(UnixSidecarProcessManager),
                        "CmdUnixLaunchFailed",
                        "Failed to start native sidecar helper with cmd /unix.",
                        ("failureDetails", cmdUnixFailure));

                    DisposeOwnedProcessOnly();

                    throw new InvalidOperationException(
                        "Native host did not report a listening port."
                        + $"{Environment.NewLine}ShellLaunchFailure:{Environment.NewLine}{shellFailure}"
                        + $"{Environment.NewLine}CmdUnixLaunchFailure:{Environment.NewLine}{cmdUnixFailure}");
                }
            }

            WriteEndpointFile(endpointPath, SafeGetPid(_process), _port);

            TinyIpcLogger.Info(
                nameof(UnixSidecarProcessManager),
                "HelperStarted",
                "Started native sidecar helper.",
                ("port", _port),
                ("pid", SafeGetPid(_process)),
                ("endpointPath", endpointPath));
        });
    }

    private static bool TryStartHelper(StartupMode mode, string endpointPath, out string failureDetails)
    {
        failureDetails = string.Empty;

        ProcessStartInfo psi = BuildStartInfo(
            mode,
            out string stdoutLogPath,
            out string stderrLogPath,
            out string launchCommand,
            out string hostPathUnix,
            out string hostPathWindows);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start XivIpc.NativeHost.");
        }
        catch (Exception ex)
        {
            failureDetails =
                $"mode={mode}"
                + $" launchCommand={launchCommand}"
                + $" hostPathWindows={hostPathWindows}"
                + $" hostPathUnix={hostPathUnix}"
                + $"{Environment.NewLine}startFailure:{Environment.NewLine}{ex}";
            return false;
        }

        _process = process;
        _stdoutLogPath = stdoutLogPath;
        _stderrLogPath = stderrLogPath;

        if (WaitForStartupFromLogOrPort(stdoutLogPath, TimeSpan.FromMilliseconds(StartupTimeoutMs), out int actualPort))
        {
            _port = actualPort;
            return true;
        }

        string stdout = ReadLogSnippet(stdoutLogPath);
        string stderr = ReadLogSnippet(stderrLogPath);

        try
        {
            if (_process is { HasExited: true })
                stderr += $"{Environment.NewLine}<process exited with code {_process.ExitCode}>";
        }
        catch
        {
        }

        failureDetails =
            $"mode={mode}"
            + $" launchCommand={launchCommand}"
            + $" hostPathWindows={hostPathWindows}"
            + $" hostPathUnix={hostPathUnix}"
            + $" stdoutLog={stdoutLogPath}"
            + $" stderrLog={stderrLogPath}"
            + $" endpointPath={endpointPath}"
            + $"{Environment.NewLine}stdout:{Environment.NewLine}{stdout}"
            + $"{Environment.NewLine}stderr:{Environment.NewLine}{stderr}";

        return false;
    }

    private static bool WaitForStartupFromLogOrPort(string stdoutLogPath, TimeSpan timeout, out int actualPort)
    {
        actualPort = 0;
        Stopwatch sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            if (TryParseReadyPort(stdoutLogPath, out int loggedPort))
            {
                actualPort = loggedPort;
                if (IsPortResponsive(loggedPort, TimeSpan.FromMilliseconds(150)))
                    return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static bool TryParseReadyPort(string path, out int port)
    {
        port = 0;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith("PORT:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[5..].Trim(), out int parsed) &&
                    parsed > 0 && parsed <= 65535)
                {
                    port = parsed;
                    TinyIpcLogger.Info(
                        nameof(UnixSidecarProcessManager),
                        "HelperReportedPort",
                        "Native sidecar helper reported listening port in log.",
                        ("loggedPort", port));

                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryReadLiveEndpoint(string endpointPath, out int port)
    {
        port = 0;

        if (!TryReadEndpoint(endpointPath, out _, out int storedPort, out _))
            return false;

        if (!IsPortResponsive(storedPort, TimeSpan.FromMilliseconds(150)))
            return false;

        port = storedPort;
        return true;
    }

    private static bool TryReadEndpoint(string endpointPath, out int pid, out int port, out long createdUnixMs)
    {
        pid = 0;
        port = 0;
        createdUnixMs = 0;

        if (string.IsNullOrWhiteSpace(endpointPath) || !File.Exists(endpointPath))
            return false;

        try
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in File.ReadAllLines(endpointPath))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                int separator = rawLine.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = rawLine[..separator].Trim();
                string value = rawLine[(separator + 1)..].Trim();
                values[key] = value;
            }

            if (!values.TryGetValue("port", out string? portRaw) ||
                !int.TryParse(portRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                port <= 0 || port > 65535)
            {
                return false;
            }

            if (values.TryGetValue("pid", out string? pidRaw))
                _ = int.TryParse(pidRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);

            if (values.TryGetValue("createdUnixMs", out string? createdRaw))
                _ = long.TryParse(createdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out createdUnixMs);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteEndpointFile(string endpointPath, int pid, int port)
    {
        string directory = Path.GetDirectoryName(endpointPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string tempPath = endpointPath + ".tmp";

        string[] lines =
        {
            $"pid={pid.ToString(CultureInfo.InvariantCulture)}",
            $"port={port.ToString(CultureInfo.InvariantCulture)}",
            $"createdUnixMs={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}"
        };

        File.WriteAllLines(tempPath, lines, Encoding.UTF8);
        File.Move(tempPath, endpointPath, overwrite: true);

        try
        {
            UnixSharedStorageHelpers.ApplyPermissions(endpointPath, isDirectory: false);
        }
        catch
        {
        }

        TinyIpcLogger.Info(
            nameof(UnixSidecarProcessManager),
            "EndpointWritten",
            "Wrote shared sidecar endpoint file.",
            ("endpointPath", endpointPath),
            ("pid", pid),
            ("port", port));
    }

    private static void CleanupStaleEndpoint(string endpointPath)
    {
        try
        {
            if (!File.Exists(endpointPath))
                return;

            if (TryReadLiveEndpoint(endpointPath, out _))
                return;

            File.Delete(endpointPath);

            TinyIpcLogger.Info(
                nameof(UnixSidecarProcessManager),
                "EndpointDeleted",
                "Deleted stale shared sidecar endpoint file.",
                ("endpointPath", endpointPath));
        }
        catch (Exception ex)
        {
            TinyIpcLogger.Warning(
                nameof(UnixSidecarProcessManager),
                "EndpointCleanupFailed",
                "Failed to delete stale sidecar endpoint file.",
                ex,
                ("endpointPath", endpointPath));
        }
    }

    private static int SafeGetPid(Process? process)
    {
        try
        {
            return process?.Id ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        StartupMode mode,
        out string stdoutLogPath,
        out string stderrLogPath,
        out string launchCommand,
        out string hostPathUnix,
        out string hostPathWindows)
    {
        string shellPath = Environment.GetEnvironmentVariable("TINYIPC_UNIX_SHELL") ?? @"Z:\bin\sh";

        string diagnosticsDirectoryWindows = ResolveDiagnosticsDirectoryWindowsPath();
        string diagnosticsDirectoryNative = ResolveDiagnosticsDirectoryNativePath();
        Directory.CreateDirectory(diagnosticsDirectoryNative);

        string suffix = $"{Process.GetCurrentProcess().Id}-{Guid.NewGuid():N}";
        stdoutLogPath = Path.Combine(diagnosticsDirectoryNative, $"tinyipc-sidecar-{suffix}.stdout.log");
        stderrLogPath = Path.Combine(diagnosticsDirectoryNative, $"tinyipc-sidecar-{suffix}.stderr.log");

        string stdoutLogUnixPath = ConvertWindowsPathToUnix(stdoutLogPath);
        string stderrLogUnixPath = ConvertWindowsPathToUnix(stderrLogPath);

        hostPathWindows = ResolveNativeHostPath();
        hostPathUnix = ConvertWindowsPathToUnix(hostPathWindows);

        ProcessStartInfo psi;

        switch (mode)
        {
            case StartupMode.CmdUnix:
            {
                string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                launchCommand = $"{commandInterpreter} /c start \"\" /b /unix {hostPathUnix}";

                psi = new ProcessStartInfo(commandInterpreter)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory
                };

                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("start");
                psi.ArgumentList.Add("");
                psi.ArgumentList.Add("/b");
                psi.ArgumentList.Add("/unix");
                psi.ArgumentList.Add(hostPathUnix);
                break;
            }

            default:
            {
                launchCommand = QuoteForShell(hostPathUnix) + " </dev/null >" + QuoteForShell(stdoutLogUnixPath) + " 2>" + QuoteForShell(stderrLogUnixPath);

                psi = new ProcessStartInfo(shellPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory
                };

                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add(launchCommand);
                break;
            }
        }

        CopyUnixPathEnvironment(psi, "TINYIPC_SHARED_DIR");
        CopyUnixPathEnvironment(psi, "TINYIPC_LOG_DIR");
        CopyVerbatimEnvironment(psi, "TINYIPC_ENABLE_LOGGING");
        CopyVerbatimEnvironment(psi, "TINYIPC_LOG_LEVEL");
        CopyVerbatimEnvironment(psi, "TINYIPC_LOG_PAYLOAD");
        CopyVerbatimEnvironment(psi, "TINYIPC_LOG_MAX_BYTES");
        CopyVerbatimEnvironment(psi, "TINYIPC_LOG_FILE_COUNT");
        CopyVerbatimEnvironment(psi, "TINYIPC_FILE_NOTIFIER");
        CopyVerbatimEnvironment(psi, "TINYIPC_SHARED_GROUP");
        CopyVerbatimEnvironment(psi, "TINYIPC_SIDECAR_HEARTBEAT_INTERVAL_MS");
        CopyVerbatimEnvironment(psi, "TINYIPC_SIDECAR_HEARTBEAT_TIMEOUT_MS");

        psi.Environment["TINYIPC_BUS_BACKEND"] = "sidecar-memory";

        TinyIpcLogger.Info(
            nameof(UnixSidecarProcessManager),
            "BuildStartInfo",
            "Prepared native sidecar start info.",
            ("mode", mode),
            ("diagnosticsDirectoryWindows", diagnosticsDirectoryWindows),
            ("diagnosticsDirectoryNative", diagnosticsDirectoryNative),
            ("stdoutLogPath", stdoutLogPath),
            ("stderrLogPath", stderrLogPath),
            ("hostPathWindows", hostPathWindows),
            ("hostPathUnix", hostPathUnix));

        return psi;
    }

    internal static string ResolveNativeHostPath()
    {
        string? configuredExecutable = Environment.GetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            string? resolvedConfigured = FirstExistingPath(
                NormalizeWindowsPath(configuredExecutable),
                TryConvertWinePathToUnix(configuredExecutable));

            if (!string.IsNullOrWhiteSpace(resolvedConfigured))
                return resolvedConfigured;
        }

        string[] probeDirectories = GetNativeHostProbeDirectories();
        LogNativeHostProbeContext(probeDirectories);

        string? firstCandidate = null;

        foreach (string directory in probeDirectories)
        {
            foreach (string fileName in new[] { "XivIpc.NativeHost", "XivIpc.NativeHost.dll" })
            {
                string candidate = NormalizePathForProbe(Path.Combine(directory, fileName));
                firstCandidate ??= candidate;

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new InvalidOperationException(
            $"XivIpc.NativeHost executable or dll was not found. First candidate was '{firstCandidate ?? "XivIpc.NativeHost"}'. " +
            "Set TINYIPC_NATIVE_HOST_PATH or ensure it exists in the shared staging directory.");
    }

    internal static string[] GetNativeHostProbeDirectories()
    {
        return GetNativeHostProbeDirectories(
            typeof(UnixSidecarProcessManager).Assembly.Location,
            AppContext.BaseDirectory,
            GetKnownPluginAssemblyLocations().Concat(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.Location)),
            Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR"));
    }

    internal static string[] GetNativeHostProbeDirectories(
        string? assemblyLocation,
        string appContextBaseDirectory,
        IEnumerable<string>? loadedAssemblyLocations = null,
        string? sharedDirectory = null)
    {
        var directories = new List<string>();

        void AddDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            foreach (string normalized in ExpandProbePath(path))
            {
                if (!directories.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                    directories.Add(normalized);
            }
        }

        if (!string.IsNullOrWhiteSpace(assemblyLocation))
            AddDirectory(Path.GetDirectoryName(assemblyLocation));

        foreach (string stagingDirectory in GetNativeHostStagingDirectories(sharedDirectory))
            AddDirectory(stagingDirectory);

        if (loadedAssemblyLocations is not null)
        {
            foreach (string location in loadedAssemblyLocations)
            {
                if (string.IsNullOrWhiteSpace(location))
                    continue;

                try
                {
                    if (!ShouldProbeAssemblyLocation(location))
                        continue;

                    AddDirectory(Path.GetDirectoryName(location));
                }
                catch
                {
                }
            }
        }

        AddDirectory(appContextBaseDirectory);
        return directories.ToArray();
    }

    internal static string[] GetNativeHostStagingDirectories(string? sharedDirectory)
    {
        if (string.IsNullOrWhiteSpace(sharedDirectory))
            return Array.Empty<string>();

        var results = new List<string>();

        foreach (string root in ExpandProbePath(sharedDirectory))
            results.Add(Path.Combine(root, NativeHostStagingDirectoryName));

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IEnumerable<string> GetKnownPluginAssemblyLocations()
    {
        yield return typeof(UnixSidecarProcessManager).Assembly.Location;

        foreach (string typeName in new[]
        {
            "TinyIpc.IO.TinyMemoryMappedFile, TinyIpc",
            "TinyIpc.Messaging.TinyMessageBus, TinyIpc",
            "TinyIpc.Messaging.ITinyMessageBus, TinyIpc"
        })
        {
            Type? type = Type.GetType(typeName, throwOnError: false);
            string? location = type?.Assembly.Location;
            if (!string.IsNullOrWhiteSpace(location))
                yield return location;
        }
    }

    private static IEnumerable<string> ExpandProbePath(string path)
    {
        string normalized = NormalizeWindowsPath(path);
        yield return normalized;

        string? unix = TryConvertWinePathToUnix(path);
        if (!string.IsNullOrWhiteSpace(unix) &&
            !string.Equals(unix, normalized, StringComparison.OrdinalIgnoreCase))
        {
            yield return unix;
        }
    }

    private static bool ShouldProbeAssemblyLocation(string location)
    {
        string fileName = Path.GetFileName(location);
        return string.Equals(fileName, "TinyIpc.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "XivIpc.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "XivIpc.NativeHost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "XivIpc.NativeHost.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static void LogNativeHostProbeContext(IReadOnlyList<string> probeDirectories)
    {
        if (!TinyIpcLogger.IsEnabled(TinyIpcLogLevel.Info))
            return;

        TinyIpcLogger.Info(
            nameof(UnixSidecarProcessManager),
            "NativeHostProbe",
            "Resolving native sidecar host path.",
            ("appContextBaseDirectory", AppContext.BaseDirectory),
            ("sharedDirectory", Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR") ?? string.Empty),
            ("sidecarAssemblyLocation", typeof(UnixSidecarProcessManager).Assembly.Location ?? string.Empty),
            ("probeDirectories", string.Join(" | ", probeDirectories)));
    }

    private static string NormalizePathForProbe(string path)
    {
        string normalized = NormalizeWindowsPath(path);
        string? unix = TryConvertWinePathToUnix(normalized);

        if (!string.IsNullOrWhiteSpace(unix) && File.Exists(unix))
            return unix;

        return normalized;
    }

    private static string NormalizeWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        try
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }
        catch
        {
            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }
    }

    private static string ResolveDiagnosticsDirectoryWindowsPath()
    {
        string? logDir = Environment.GetEnvironmentVariable("TINYIPC_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(logDir))
            return NormalizeWindowsPath(logDir);

        string? sharedDir = Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");
        if (!string.IsNullOrWhiteSpace(sharedDir))
            return NormalizeWindowsPath(sharedDir);

        return NormalizeWindowsPath(Path.GetTempPath());
    }

    private static string ResolveDiagnosticsDirectoryNativePath()
    {
        string? logDir = Environment.GetEnvironmentVariable("TINYIPC_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(logDir))
            return TryConvertWinePathToUnix(logDir) ?? ConvertWindowsPathToUnix(logDir);

        string? sharedDir = Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");
        if (!string.IsNullOrWhiteSpace(sharedDir))
            return TryConvertWinePathToUnix(sharedDir) ?? ConvertWindowsPathToUnix(sharedDir);

        return "/tmp";
    }

    private static string ResolveSharedDirectoryNativePath()
    {
        string? sharedDir = Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");
        if (!string.IsNullOrWhiteSpace(sharedDir))
            return TryConvertWinePathToUnix(sharedDir) ?? ConvertWindowsPathToUnix(sharedDir);

        return "/tmp";
    }

    private static string GetEndpointFilePath(string sharedDirectoryNative)
        => Path.Combine(sharedDirectoryNative, EndpointFileName);

    private static void CopyVerbatimEnvironment(ProcessStartInfo psi, string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            psi.Environment[name] = value;
    }

    private static void CopyUnixPathEnvironment(ProcessStartInfo psi, string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return;

        psi.Environment[name] = TryConvertWinePathToUnix(value) ?? ConvertWindowsPathToUnix(value);
    }

    private static string ConvertWindowsPathToUnix(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        string normalized = path.Replace('\\', '/');

        if (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/')
        {
            char drive = char.ToUpperInvariant(normalized[0]);
            string remainder = normalized[3..];

            if (drive == 'Z')
                return "/" + remainder;

            return $"/mnt/{char.ToLowerInvariant(drive)}/{remainder}";
        }

        return normalized;
    }

    private static string? TryConvertWinePathToUnix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string normalized = path.Replace('/', '\\').Trim();

        if (normalized.Length >= 3 &&
            (normalized[0] == 'Z' || normalized[0] == 'z') &&
            normalized[1] == ':' &&
            normalized[2] == '\\')
        {
            string suffix = normalized[2..].Replace('\\', '/');
            return suffix;
        }

        return null;
    }

    private static string QuoteForShell(string value)
        => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static bool IsPortResponsive(int port, TimeSpan connectTimeout)
    {
        try
        {
            using var client = new TcpClient();
            IAsyncResult result = client.BeginConnect(IPAddress.Loopback.ToString(), port, null, null);
            try
            {
                if (result.AsyncWaitHandle.WaitOne(connectTimeout))
                {
                    client.EndConnect(result);
                    return true;
                }
            }
            finally
            {
                result.AsyncWaitHandle.Dispose();
            }
        }
        catch
        {
        }

        return false;
    }

    private static void Release()
    {
        lock (Sync)
        {
            if (_refCount > 0)
                _refCount--;

            TinyIpcLogger.Info(
                nameof(UnixSidecarProcessManager),
                "HelperLeaseReleased",
                "Released sidecar helper lease.",
                ("port", _port),
                ("refCount", _refCount));
        }
    }

    private static void DisposeOwnedProcessOnly()
    {
        Process? ownedProcess = _process;
        int pid = SafeGetPid(ownedProcess);

        try
        {
            ownedProcess?.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            ownedProcess?.Dispose();
        }
        catch
        {
        }

        if (ownedProcess is not null)
        {
            TinyIpcLogger.Info(
                nameof(UnixSidecarProcessManager),
                "HelperStopped",
                "Stopped owned native sidecar helper.",
                ("pid", pid));
        }

        _process = null;
        _port = 0;
        _stdoutLogPath = null;
        _stderrLogPath = null;
    }

    private static string ReadLogSnippet(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "<missing>";

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return "<unreadable:" + ex.GetType().Name + ">";
        }
    }

    private static string? FirstExistingPath(params string?[] paths)
    {
        foreach (string? path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;
        }

        return null;
    }

    private enum StartupMode
    {
        Shell,
        CmdUnix
    }

    internal readonly struct Lease : IDisposable
    {
        internal Lease(int port) => Port = port;
        internal int Port { get; }
        public void Dispose() => Release();
    }
}