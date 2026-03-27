using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Sockets;
using XivIpc.Internal;

namespace XivIpc.Messaging;

internal static class UnixSidecarProcessManager
{
    private const string BrokerSocketFileName = "tinyipc-sidecar.sock";
    private const string DefaultNativeHostPathUnix = "/tmp/tinyipc-shared-ffxiv/tinyipc-native-host/XivIpc.NativeHost";
    private const string DefaultNativeHostDownloadUrl = "https://github.com/pffxivtools/ffxiv-bard-plugins-linux/releases/latest/download/XivIpc.NativeHost-linux-x64.tar.gz";
    private const int StartupTimeoutMs = 10_000;
    private static readonly TimeSpan BrokerTerminationTimeout = TimeSpan.FromSeconds(5);
    private static readonly HttpClient NativeHostHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly object Sync = new();
    private static readonly AsyncLocal<NativeHostDefaultsOverrideScope?> CurrentNativeHostDefaultsOverride = new();

    private static Process? _process;
    private static int _refCount;

    internal static IDisposable OverrideNativeHostDefaultsForTests(string? defaultNativeHostPath = null, string? downloadUrl = null)
    {
        NativeHostDefaultsOverrideScope? previous = CurrentNativeHostDefaultsOverride.Value;
        var next = new NativeHostDefaultsOverrideScope(previous, defaultNativeHostPath, downloadUrl);
        CurrentNativeHostDefaultsOverride.Value = next;
        return next;
    }

    internal static Lease Acquire()
        => Acquire(CaptureSettings());

    internal static Lease Acquire(RuntimeSettings settings)
    {
        lock (Sync)
        {
            string socketPath = settings.SocketPath;
            EnsureBrokerStarted(settings, socketPath);
            _refCount++;
            return new Lease(socketPath);
        }
    }

    internal static RuntimeSettings CaptureSettings()
    {
        SharedScopeSettings sharedScope = TinyIpcRuntimeSettings.ResolveSharedScope();
        BrokerRuntimeSettings brokerRuntime = TinyIpcRuntimeSettings.ResolveBrokerRuntime(sharedScope);
        LoggingSettings logging = TinyIpcRuntimeSettings.ResolveLogging();
        NativeHostSettings nativeHost = TinyIpcRuntimeSettings.ResolveNativeHost();
        SidecarTransportSettings transport = TinyIpcRuntimeSettings.ResolveSidecarTransport();
        string diagnosticsDirectory = ResolveDiagnosticsLogDirectory(brokerRuntime.SocketPath, logging.LogDirectory);

        return new RuntimeSettings(
            brokerRuntime.SocketPath,
            sharedScope.BrokerDirectory,
            sharedScope.SharedDirectory,
            diagnosticsDirectory,
            sharedScope.SharedGroup,
            brokerRuntime.IdleShutdownDelay,
            logging,
            transport,
            nativeHost.ExplicitNativeHostPath);
    }

    private static void EnsureBrokerStarted(RuntimeSettings settings, string socketPath)
    {
        string statePath = BrokerStateSnapshot.ResolvePath(socketPath);
        if (CanConnect(socketPath))
            return;

        string brokerDirectory = Path.GetDirectoryName(socketPath) ?? throw new InvalidOperationException("Broker socket path must have a parent directory.");
        Directory.CreateDirectory(UnixSharedStorageHelpers.ConvertPathForCurrentRuntime(brokerDirectory));
        UnixSharedStorageHelpers.ApplyBrokerPermissions(brokerDirectory, isDirectory: true);

        BrokerStateSnapshot? state = BrokerStateSnapshot.TryRead(statePath);
        if (state is not null && BrokerStateSnapshot.IsLive(state))
        {
            if (CanConnect(socketPath))
                return;

            if (!BrokerStateSnapshot.TryTerminate(state, BrokerTerminationTimeout))
                throw new SidecarStartupException($"Failed to terminate unreachable TinyIpc broker pid={state.Pid} instance={state.InstanceId}.");

            CleanupOwnedProcess(state.Pid);
        }

        if (!TryStartHelper(settings, socketPath, out string failureDetails))
            throw new SidecarStartupException($"Failed to start native TinyIpc broker for socket '{socketPath}'.{Environment.NewLine}{failureDetails}");

        WaitForBroker(settings, socketPath, statePath, TimeSpan.FromMilliseconds(StartupTimeoutMs));
    }

    private static void WaitForBroker(RuntimeSettings settings, string socketPath, string statePath, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Exception? last = null;

        while (sw.Elapsed < timeout)
        {
            try
            {
                if (CanConnect(socketPath) && BrokerStateSnapshot.TryRead(statePath) is not null)
                    return;

            }
            catch (Exception ex)
            {
                if (ex is SidecarStartupException)
                    throw;

                last = ex;
            }

            Thread.Sleep(50);
        }

        string diagnostics = BuildBrokerStartupDiagnostics(settings, socketPath, statePath);
        throw new SidecarStartupException(
            $"Timed out waiting for broker socket '{socketPath}' to become reachable.{Environment.NewLine}{diagnostics}",
            last ?? new TimeoutException());
    }

    private static bool TryStartHelper(RuntimeSettings settings, string socketPath, out string failureDetails)
    {
        ProcessStartInfo psi = BuildStartInfo(
            settings,
            socketPath,
            out string launchMode,
            out string launchCommand,
            out string hostPathUnix,
            out string hostPathWindows,
            out string launchId,
            out string stdoutLogPath,
            out string stderrLogPath);

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            failureDetails = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            failureDetails =
                $"mode={launchMode}"
                + $" launchId={launchId}"
                + $" launchCommand={launchCommand}"
                + $" hostPathWindows={hostPathWindows}"
                + $" hostPathUnix={hostPathUnix}"
                + $" stdoutLogPath={stdoutLogPath}"
                + $" stderrLogPath={stderrLogPath}"
                + Environment.NewLine
                + ex;
            return false;
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        RuntimeSettings settings,
        string socketPath,
        out string launchMode,
        out string launchCommand,
        out string hostPathUnix,
        out string hostPathWindows,
        out string launchId,
        out string stdoutLogPath,
        out string stderrLogPath)
    {
        hostPathWindows = ResolveNativeHostPath(settings);
        hostPathUnix = ConvertWindowsPathToUnix(hostPathWindows);
        launchId = Guid.NewGuid().ToString("N");

        string diagnosticsDirectory = ResolveUnixPath(settings.DiagnosticsDirectory);
        Directory.CreateDirectory(diagnosticsDirectory);
        stdoutLogPath = CombineUnixPath(diagnosticsDirectory, $"tinyipc-sidecar-{launchId}.stdout.log");
        stderrLogPath = CombineUnixPath(diagnosticsDirectory, $"tinyipc-sidecar-{launchId}.stderr.log");

        bool isDll = string.Equals(Path.GetExtension(hostPathUnix), ".dll", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo psi;
        if (isDll)
        {
            launchMode = "dotnet-dll";
            launchCommand = $"/usr/bin/env dotnet {QuoteForShell(hostPathUnix)}";
            psi = new ProcessStartInfo("/usr/bin/env")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            psi.ArgumentList.Add("dotnet");
            psi.ArgumentList.Add(hostPathUnix);
        }
        else
        {
            launchMode = "direct-exec";
            launchCommand = hostPathUnix;
            psi = new ProcessStartInfo(hostPathUnix)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
        }

        CopyFixedPathEnvironment(psi, "TINYIPC_SHARED_DIR", settings.SharedDirectory);
        CopyFixedPathEnvironment(psi, "TINYIPC_BROKER_DIR", settings.BrokerDirectory);
        CopyFixedVerbatimEnvironment(psi, "TINYIPC_ENABLE_LOGGING", settings.Logging.EnableLogging);
        CopyFixedPathEnvironment(psi, "TINYIPC_LOG_DIR", settings.DiagnosticsDirectory);
        CopyFixedVerbatimEnvironment(psi, "TINYIPC_LOG_LEVEL", settings.Logging.LogLevel);
        CopyFixedVerbatimEnvironment(psi, "TINYIPC_LOG_PAYLOAD", settings.Logging.LogPayload);
        CopyFixedVerbatimEnvironment(psi, "TINYIPC_LOG_MAX_BYTES", settings.Logging.LogMaxBytes);
        CopyFixedVerbatimEnvironment(psi, "TINYIPC_LOG_FILE_COUNT", settings.Logging.LogFileCount);
        CopyFixedVerbatimEnvironment(psi, "TINYIPC_SHARED_GROUP", settings.SharedGroup);
        CopyFixedVerbatimEnvironment(psi, "TINYIPC_FILE_NOTIFIER", settings.Logging.FileNotifier);
        psi.Environment[TinyIpcEnvironment.SidecarStorageMode] = settings.Transport.Storage == SidecarStorageKind.Ring ? "ring" : "journal";
        psi.Environment[TinyIpcEnvironment.SlotCount] = settings.Transport.RingSlotCount.ToString();
        psi.Environment[TinyIpcEnvironment.MessageTtlMs] = settings.Transport.MessageTtlMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment[TinyIpcEnvironment.BrokerIdleShutdownMs] = ((int)settings.IdleShutdownDelay.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        psi.Environment["TINYIPC_BROKER_SOCKET_PATH"] = ConvertUnixPathForChild(socketPath);
        psi.Environment["TINYIPC_BUS_BACKEND"] = "sidecar-brokered";
        psi.Environment["TINYIPC_LAUNCH_ID"] = launchId;

        TinyIpcProcessStamp stamp = TinyIpcProcessStamp.Create(typeof(UnixSidecarProcessManager));
        string hostSha256 = TinyIpcProcessStamp.ComputeSha256(ResolveNativePath(hostPathWindows));
        TinyIpcLogger.Info(
            nameof(UnixSidecarProcessManager),
            "BrokerLaunchPrepared",
            "Prepared native TinyIpc broker start info.",
            ("mode", launchMode),
            ("launchId", launchId),
            ("hostPathWindows", hostPathWindows),
            ("hostPathUnix", hostPathUnix),
            ("hostSha256", hostSha256),
            ("launchCommand", launchCommand),
            ("stdoutLogPath", stdoutLogPath),
            ("stderrLogPath", stderrLogPath),
            ("childSharedDir", psi.Environment.TryGetValue("TINYIPC_SHARED_DIR", out string? childSharedDir) ? childSharedDir : string.Empty),
            ("childBrokerDir", psi.Environment.TryGetValue("TINYIPC_BROKER_DIR", out string? childBrokerDir) ? childBrokerDir : string.Empty),
            ("childLogDir", psi.Environment.TryGetValue("TINYIPC_LOG_DIR", out string? childLogDir) ? childLogDir : string.Empty),
            ("childSharedGroup", psi.Environment.TryGetValue("TINYIPC_SHARED_GROUP", out string? childSharedGroup) ? childSharedGroup : string.Empty),
            ("childBrokerSocketPath", psi.Environment["TINYIPC_BROKER_SOCKET_PATH"]),
            ("childBackend", psi.Environment["TINYIPC_BUS_BACKEND"]),
            ("childLaunchId", psi.Environment["TINYIPC_LAUNCH_ID"]),
            ("assemblyPath", stamp.AssemblyPath),
            ("sha256", stamp.Sha256),
            ("processPath", stamp.ProcessPath),
            ("processSha256", stamp.ProcessSha256));

        return psi;
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
        return CaptureSettings().SocketPath;
    }

    private static string ResolveBrokerDirectory()
    {
        SharedScopeSettings sharedScope = TinyIpcRuntimeSettings.ResolveSharedScope();
        return ResolveBrokerDirectory(sharedScope.SharedDirectory, sharedScope.BrokerDirectoryWasConfigured ? sharedScope.BrokerDirectory : null);
    }

    private static string ResolveBrokerDirectory(string sharedDirectory, string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return ResolveUnixPath(explicitDirectory);

        return ResolveUnixPath(sharedDirectory);
    }

    private static string BuildBrokerStartupDiagnostics(RuntimeSettings settings, string socketPath, string statePath)
    {
        string logDirectory = settings.DiagnosticsDirectory;
        string latestNativeLog = FindLatestNativeBrokerLog(logDirectory);
        string nativeLogTail = string.IsNullOrWhiteSpace(latestNativeLog)
            ? "<none>"
            : ReadLogTail(latestNativeLog, 20);

        string stateSummary;
        try
        {
            BrokerStateSnapshot? state = BrokerStateSnapshot.TryRead(statePath);
            stateSummary = state is null
                ? "<none>"
                : $"pid={state.Pid} instanceId={state.InstanceId} sessions={state.SessionCount} channels={state.ChannelCount}";
        }
        catch (Exception ex)
        {
            stateSummary = $"<error: {ex.GetType().Name}: {ex.Message}>";
        }

        return string.Join(
            Environment.NewLine,
            "Broker startup diagnostics:",
            $"socketExists={File.Exists(socketPath)}",
            $"stateExists={File.Exists(statePath)}",
            $"state={stateSummary}",
            $"logDirectory={logDirectory}",
            $"latestNativeLog={latestNativeLog}",
            "latestNativeLogTail:",
            nativeLogTail);
    }

    internal static string ResolveNativeHostPath()
        => ResolveNativeHostPath(CaptureSettings());

    internal static string ResolveNativeHostPath(RuntimeSettings settings)
    {
        string? explicitPath = settings.ExplicitNativeHostPath;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string resolved = ResolveNativePath(explicitPath);
            if (File.Exists(resolved))
                return resolved;
        }

        EnsureDefaultNativeHostAvailable(settings);

        string[] candidates = GetNativeHostCandidatePathsForDiagnostics(settings);

        string? existing = candidates.FirstOrDefault(File.Exists);
        return existing ?? throw new SidecarStartupException("XivIpc.NativeHost executable or dll was not found.");
    }

    internal static bool TryResolveNativeHostPath(out string path)
    {
        try
        {
            path = ResolveNativeHostPath();
            return true;
        }
        catch
        {
            path = string.Empty;
            return false;
        }
    }

    internal static string[] GetNativeHostCandidatePathsForDiagnostics()
        => GetNativeHostCandidatePathsForDiagnostics(CaptureSettings());

    private static string[] GetNativeHostCandidatePathsForDiagnostics(RuntimeSettings settings)
    {
        var candidates = new List<string>();

        void AddCandidates(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                return;

            string resolvedBase = ResolveNativePath(baseDirectory);
            candidates.Add(Path.Combine(resolvedBase, "XivIpc.NativeHost"));
            candidates.Add(Path.Combine(resolvedBase, "XivIpc.NativeHost.dll"));
            candidates.Add(Path.Combine(resolvedBase, "XivIpc.NativeHost.exe"));
        }

        string? sharedDirectory = settings.SharedDirectory;
        if (!string.IsNullOrWhiteSpace(sharedDirectory))
            AddCandidates(Path.Combine(sharedDirectory, "tinyipc-native-host"));

        string? brokerDirectory = settings.BrokerDirectory;
        if (!string.IsNullOrWhiteSpace(brokerDirectory))
            AddCandidates(Path.Combine(brokerDirectory, "tinyipc-native-host"));

        string resolvedDefaultHostPath = ResolveNativePath(GetDefaultNativeHostPathRaw());
        candidates.Add(resolvedDefaultHostPath);
        candidates.Add(resolvedDefaultHostPath + ".dll");
        candidates.Add(resolvedDefaultHostPath + ".exe");

        string baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "XivIpc.NativeHost"));
        candidates.Add(Path.Combine(baseDir, "XivIpc.NativeHost.dll"));
        candidates.Add(Path.Combine(baseDir, "XivIpc.NativeHost.exe"));

        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net10.0", "XivIpc.NativeHost")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net10.0", "XivIpc.NativeHost.dll")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net9.0", "XivIpc.NativeHost")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net9.0", "XivIpc.NativeHost.dll")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net10.0", "XivIpc.NativeHost")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net10.0", "XivIpc.NativeHost.dll")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net9.0", "XivIpc.NativeHost")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net9.0", "XivIpc.NativeHost.dll")));

        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void EnsureDefaultNativeHostAvailable(RuntimeSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ExplicitNativeHostPath))
            return;

        string defaultHostPath = ResolveNativePath(GetDefaultNativeHostPathRaw());
        string defaultHostUnixPath = ResolveUnixPath(GetDefaultNativeHostPathRaw());
        if (File.Exists(defaultHostPath))
        {
            UnixSharedStorageHelpers.ApplyHostExecutablePermissions(defaultHostPath);
            return;
        }

        string? hostDirectory = Path.GetDirectoryName(defaultHostPath);
        string? hostDirectoryUnix = Path.GetDirectoryName(defaultHostUnixPath);
        if (string.IsNullOrWhiteSpace(hostDirectory))
            throw new SidecarStartupException("The default native host path must have a parent directory.");
        if (string.IsNullOrWhiteSpace(hostDirectoryUnix))
            throw new SidecarStartupException("The default native host Unix path must have a parent directory.");

        Directory.CreateDirectory(hostDirectory);
        string lockPath = Path.Combine(hostDirectory, ".tinyipc-native-host.install.lock");
        using var installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        if (File.Exists(defaultHostPath))
        {
            UnixSharedStorageHelpers.ApplyHostExecutablePermissions(defaultHostPath);
            return;
        }

        string archivePath = Path.Combine(hostDirectory, "XivIpc.NativeHost-linux-x64.tar.gz.download");
        string archiveUnixPath = Path.Combine(hostDirectoryUnix, "XivIpc.NativeHost-linux-x64.tar.gz.download");
        string tempExtractDirectory = Path.Combine(hostDirectory, ".tinyipc-native-host.extract");
        string tempExtractDirectoryUnix = Path.Combine(hostDirectoryUnix, ".tinyipc-native-host.extract");
        string tempExtractPath = Path.Combine(tempExtractDirectory, "XivIpc.NativeHost");

        try
        {
            DownloadNativeHostArchive(archivePath);
            ExtractNativeHostFromArchive(archivePath, archiveUnixPath, tempExtractDirectory, tempExtractDirectoryUnix);
            UnixSharedStorageHelpers.ApplyHostExecutablePermissions(tempExtractPath);
            File.Move(tempExtractPath, defaultHostPath, overwrite: true);
            UnixSharedStorageHelpers.ApplyHostExecutablePermissions(defaultHostPath);
        }
        finally
        {
            TryDeleteDirectory(tempExtractDirectory);
            TryDeleteFile(archivePath);
        }
    }

    private static void DownloadNativeHostArchive(string archivePath)
    {
        using HttpResponseMessage response = NativeHostHttpClient.GetAsync(GetDefaultNativeHostDownloadUrl(), HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();

        using Stream responseStream = response.Content.ReadAsStream();
        using var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        responseStream.CopyTo(output);
    }

    private static void ExtractNativeHostFromArchive(string archivePath, string archiveUnixPath, string destinationDirectory, string destinationDirectoryUnix)
    {
        TryDeleteDirectory(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        if (RuntimeEnvironmentDetector.Detect().IsWindowsProcess &&
            TryExtractNativeHostWithTarCommand(archiveUnixPath, destinationDirectoryUnix))
        {
            string extractedHostPath = Path.Combine(destinationDirectory, "XivIpc.NativeHost");
            if (File.Exists(extractedHostPath))
                return;

            throw new SidecarStartupException("The native tar extraction path completed without producing XivIpc.NativeHost.");
        }

        using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzipStream);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.EntryType is TarEntryType.Directory)
                continue;

            string name = entry.Name.Replace('\\', '/');
            if (!string.Equals(Path.GetFileName(name), "XivIpc.NativeHost", StringComparison.Ordinal))
                continue;

            using Stream entryStream = entry.DataStream ?? throw new InvalidOperationException("Native host archive entry did not include file contents.");
            using var output = new FileStream(Path.Combine(destinationDirectory, "XivIpc.NativeHost"), FileMode.Create, FileAccess.Write, FileShare.None);
            entryStream.CopyTo(output);
            return;
        }

        throw new SidecarStartupException("Downloaded native host archive did not contain XivIpc.NativeHost.");
    }

    private static bool TryExtractNativeHostWithTarCommand(string archiveUnixPath, string destinationDirectoryUnix)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/env")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList =
                {
                    "tar",
                    "-xzf",
                    archiveUnixPath,
                    "-C",
                    destinationDirectoryUnix,
                    "XivIpc.NativeHost"
                }
            });

            process?.WaitForExit();
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
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

    internal static string ResolveBrokerStatePathForDiagnostics()
        => BrokerStateSnapshot.ResolvePath(ResolveBrokerSocketPath());

    internal static int GetBrokerProcessIdForDiagnostics()
        => BrokerStateSnapshot.TryRead(ResolveBrokerStatePathForDiagnostics())?.Pid ?? 0;

    private static void CleanupOwnedProcess(int pid)
    {
        lock (Sync)
        {
            if (_process is null)
                return;

            try
            {
                if (!_process.HasExited && _process.Id != pid)
                    return;
            }
            catch
            {
            }

            try { _process.Dispose(); } catch { }
            _process = null;
        }
    }

    private static void CopyFixedVerbatimEnvironment(ProcessStartInfo psi, string variableName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            psi.Environment[variableName] = value;
    }

    private static void CopyFixedPathEnvironment(ProcessStartInfo psi, string variableName, string? value)
    {
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

    private static string ResolveDiagnosticsLogDirectory(string socketPath, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return ResolveUnixPath(configured);

        return Path.GetDirectoryName(socketPath) ?? "/tmp";
    }

    private static string FindLatestNativeBrokerLog(string logDirectory)
    {
        if (!Directory.Exists(logDirectory))
            return string.Empty;

        foreach (string path in Directory.EnumerateFiles(logDirectory, "tinyipc-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                string contents = File.ReadAllText(path);
                if (contents.Contains("runtime=UnixProcess", StringComparison.Ordinal))
                    return path;
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static string ReadLogTail(string path, int lineCount)
    {
        try
        {
            string[] lines = File.ReadAllLines(path);
            return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - lineCount)));
        }
        catch (Exception ex)
        {
            return $"<error reading log tail: {ex.GetType().Name}: {ex.Message}>";
        }
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

    private static string GetDefaultNativeHostPathRaw()
        => CurrentNativeHostDefaultsOverride.Value?.DefaultNativeHostPath ?? DefaultNativeHostPathUnix;

    private static string GetDefaultNativeHostDownloadUrl()
        => CurrentNativeHostDefaultsOverride.Value?.DownloadUrl ?? DefaultNativeHostDownloadUrl;

    internal readonly struct Lease : IDisposable
    {
        internal Lease(string socketPath) => SocketPath = socketPath;
        internal string SocketPath { get; }
        public void Dispose() => Release();
    }

    internal readonly record struct RuntimeSettings(
        string SocketPath,
        string BrokerDirectory,
        string SharedDirectory,
        string DiagnosticsDirectory,
        string? SharedGroup,
        TimeSpan IdleShutdownDelay,
        LoggingSettings Logging,
        SidecarTransportSettings Transport,
        string? ExplicitNativeHostPath);

    private sealed class NativeHostDefaultsOverrideScope : IDisposable
    {
        private readonly NativeHostDefaultsOverrideScope? _previous;
        private bool _disposed;

        internal NativeHostDefaultsOverrideScope(NativeHostDefaultsOverrideScope? previous, string? defaultNativeHostPath, string? downloadUrl)
        {
            _previous = previous;
            DefaultNativeHostPath = defaultNativeHostPath;
            DownloadUrl = downloadUrl;
        }

        internal string? DefaultNativeHostPath { get; }
        internal string? DownloadUrl { get; }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentNativeHostDefaultsOverride.Value = _previous;
            _disposed = true;
        }
    }
}
