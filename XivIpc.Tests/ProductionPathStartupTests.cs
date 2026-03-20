using System.Net.Sockets;
using System.Diagnostics;
using TinyIpc.IO;
using TinyIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class ProductionPathStartupTests
{
    [Fact]
    public void AutoBackend_WithStagedHost_StartsSidecar_InsteadOfFallingBack()
    {
        using TestEnvironmentScope scope = new(StagedHostMode.Valid);
        using var bus = new TinyMessageBus(new TinyMemoryMappedFile(scope.ChannelName, 4096), disposeFile: true);

        scope.WaitForSocket();
        string clientLog = scope.WaitForClientLog("BrokerLaunchPrepared");
        string nativeLog = scope.WaitForUnixProcessLog("BrokerStartupObservedEnvironment");

        ProductionPathTestEnvironment.AssertStartedSidecar(clientLog);
        ProductionPathTestEnvironment.AssertHealthyNativeBrokerLog(nativeLog);
        ProductionPathTestEnvironment.AssertLaunchContract(clientLog, nativeLog, scope.UnixSharedDirectory, scope.SocketPath, scope.StagedHostPath);
        Assert.True(File.Exists(scope.SocketPath));
        scope.WaitForStateFile();
        Assert.True(File.Exists(scope.StatePath));
        Assert.Empty(scope.FindSharedFiles("tinyipc_sidecar-startup_*.bin"));
        Assert.Empty(scope.FindSharedFiles("tinyipc_mmf_*.bin"));
    }

    [Fact]
    public void AutoBackend_WithExistingStrictSharedDirectory_StartsSidecar()
    {
        using TestEnvironmentScope scope = new(StagedHostMode.Valid);
        using var bus = new TinyMessageBus(new TinyMemoryMappedFile(scope.ChannelName, 4096), disposeFile: true);

        scope.WaitForSocket();

        string clientLog = scope.WaitForClientLog("BrokerLaunchPrepared");
        string nativeLog = scope.WaitForUnixProcessLog("BrokerStartupObservedEnvironment");
        ProductionPathTestEnvironment.AssertHealthyNativeBrokerLog(nativeLog);
        ProductionPathTestEnvironment.AssertLaunchContract(clientLog, nativeLog, scope.UnixSharedDirectory, scope.SocketPath, scope.StagedHostPath);
        Assert.True(File.Exists(scope.SocketPath));
        scope.WaitForStateFile();
        Assert.True(File.Exists(scope.StatePath));
    }

    [Fact]
    public void AutoBackend_WithInvalidStagedHost_AndSharedGroup_UsesLoggedNoOp()
    {
        using TestEnvironmentScope scope = new(StagedHostMode.Invalid);
        using var bus = new TinyMessageBus(new TinyMemoryMappedFile(scope.ChannelName, 4096), disposeFile: true);

        string log = scope.WaitForClientLog("ReconnectAttemptFailed");
        ProductionPathTestEnvironment.AssertSidecarReconnectFailureLogged(log);
        Assert.False(File.Exists(scope.SocketPath));
        Assert.False(File.Exists(scope.StatePath));
        Assert.Empty(scope.FindSharedFiles("tinyipc_sidecar-startup_*.bin"));
        Assert.Empty(scope.FindSharedFiles("tinyipc_mmf_*.bin"));
    }

    [Fact]
    public void AutoBackend_WithInvalidStagedHost_AndNoSharedGroup_FallsBackToDirect()
    {
        using TestEnvironmentScope scope = new(StagedHostMode.Invalid, setSharedGroup: false);
        using var bus = new TinyMessageBus(new TinyMemoryMappedFile(scope.ChannelName, 4096), disposeFile: true);

        string log = scope.WaitForClientLog("ReconnectAttemptFailed");
        ProductionPathTestEnvironment.AssertSidecarReconnectFailureLogged(log);
        Assert.False(File.Exists(scope.SocketPath));
        Assert.False(File.Exists(scope.StatePath));
        Assert.Empty(scope.FindSharedFiles("tinyipc_sidecar-startup_*.bin"));
        Assert.Empty(scope.FindSharedFiles("tinyipc_mmf_*.bin"));
    }

    [Fact]
    public void ResolveNativeHostPath_PrefersStagedSharedDirHost_FromWindowsStyleSharedDir()
    {
        using TestEnvironmentScope scope = new(StagedHostMode.Valid);

        string resolved = XivIpc.Messaging.UnixSidecarProcessManager.ResolveNativeHostPath();

        Assert.StartsWith(Path.Combine(scope.UnixSharedDirectory, "tinyipc-native-host"), resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void StagedNativeHost_DirectLaunch_ObservesUnixChildEnvironment()
    {
        using TestEnvironmentScope scope = new(StagedHostMode.Valid);
        string launchId = Guid.NewGuid().ToString("N");
        using Process process = scope.StartNativeHostDirect(launchId);
        scope.WaitForSocket();

        string nativeLog = scope.ReadLatestUnixProcessLogOrEmpty();
        ProductionPathTestEnvironment.AssertHealthyNativeBrokerLog(nativeLog);
        ProductionPathTestEnvironment.AssertObservedBrokerEnvironment(nativeLog, scope.UnixSharedDirectory, scope.SocketPath, scope.StagedHostPath, launchId);
        Assert.False(process.HasExited);
    }

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);

        public TestEnvironmentScope(StagedHostMode stagedHostMode, bool setSharedGroup = true)
        {
            ChannelName = $"xivipc-production-startup-{Guid.NewGuid():N}";
            UnixSharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-production-startup-tests", Guid.NewGuid().ToString("N"));
            SharedDirectory = ProductionPathTestEnvironment.ToWindowsStylePath(UnixSharedDirectory);
            SocketPath = Path.Combine(UnixSharedDirectory, "tinyipc-sidecar.sock");
            StatePath = Path.Combine(UnixSharedDirectory, "tinyipc-sidecar.state.json");

            Directory.CreateDirectory(UnixSharedDirectory);
            if (stagedHostMode == StagedHostMode.Valid)
                StagedHostDirectory = ProductionPathTestEnvironment.PrepareStagedNativeHost(UnixSharedDirectory);
            else if (stagedHostMode == StagedHostMode.Invalid)
                StagedHostDirectory = ProductionPathTestEnvironment.PrepareInvalidStagedNativeHost(UnixSharedDirectory);
            else
                StagedHostDirectory = Path.Combine(UnixSharedDirectory, "tinyipc-native-host");

            Capture("TINYIPC_MESSAGE_BUS_BACKEND");
            Capture("TINYIPC_NATIVE_HOST_PATH");
            Capture("TINYIPC_SHARED_DIR");
            Capture("TINYIPC_SHARED_GROUP");
            Capture("TINYIPC_LOG_DIR");
            Capture("TINYIPC_LOG_LEVEL");
            Capture("TINYIPC_ENABLE_LOGGING");
            Capture("TINYIPC_FILE_NOTIFIER");

            ProductionPathTestEnvironment.ResetLogger();

            Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", null);
            Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", SharedDirectory);
            Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", setSharedGroup ? ProductionPathTestEnvironment.ResolveSharedGroup() : null);
            Environment.SetEnvironmentVariable("TINYIPC_LOG_DIR", SharedDirectory);
            Environment.SetEnvironmentVariable("TINYIPC_LOG_LEVEL", "info");
            Environment.SetEnvironmentVariable("TINYIPC_ENABLE_LOGGING", "1");
            Environment.SetEnvironmentVariable("TINYIPC_FILE_NOTIFIER", "auto");
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }
        public string UnixSharedDirectory { get; }
        public string SocketPath { get; }
        public string StatePath { get; }
        public string StagedHostDirectory { get; }
        public string StagedHostPath => Path.Combine(StagedHostDirectory, "XivIpc.NativeHost");

        public void WaitForSocket()
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(SocketPath))
                    {
                        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                Thread.Sleep(50);
            }

            throw new TimeoutException($"Expected broker socket '{SocketPath}' to become connectable.", last);
        }

        public void WaitForStateFile()
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(StatePath))
                    return;

                Thread.Sleep(50);
            }

            throw new TimeoutException($"Expected broker state file '{StatePath}' to be created.");
        }

        public string ReadLatestLogOrEmpty()
            => ProductionPathTestEnvironment.ReadLatestClientLogOrEmpty(UnixSharedDirectory);

        public string ReadLatestUnixProcessLogOrEmpty()
            => ProductionPathTestEnvironment.ReadLatestUnixProcessLogOrEmpty(UnixSharedDirectory);

        public string ReadAllLogsOrEmpty()
        {
            if (!Directory.Exists(UnixSharedDirectory))
                return string.Empty;

            return string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(UnixSharedDirectory, "tinyipc-*.log", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .Select(static path => File.ReadAllText(path)));
        }

        public string WaitForClientLog(string marker)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                string log = ReadAllLogsOrEmpty();
                if (log.Contains(marker, StringComparison.Ordinal))
                    return log;

                Thread.Sleep(50);
            }

            return ReadAllLogsOrEmpty();
        }

        public string WaitForUnixProcessLog(string marker)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                string log = ReadLatestUnixProcessLogOrEmpty();
                if (log.Contains(marker, StringComparison.Ordinal))
                    return log;

                Thread.Sleep(50);
            }

            return ReadLatestUnixProcessLogOrEmpty();
        }

        public string[] FindSharedFiles(string pattern)
            => Directory.Exists(UnixSharedDirectory)
                ? Directory.EnumerateFiles(UnixSharedDirectory, pattern, SearchOption.TopDirectoryOnly).ToArray()
                : Array.Empty<string>();

        public Process StartNativeHostDirect(string launchId)
        {
            var psi = new ProcessStartInfo(StagedHostPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            psi.Environment["TINYIPC_SHARED_DIR"] = UnixSharedDirectory;
            psi.Environment["TINYIPC_LOG_DIR"] = UnixSharedDirectory;
            psi.Environment["TINYIPC_ENABLE_LOGGING"] = "1";
            psi.Environment["TINYIPC_LOG_LEVEL"] = "info";
            psi.Environment["TINYIPC_FILE_NOTIFIER"] = "auto";
            psi.Environment["TINYIPC_SHARED_GROUP"] = Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP") ?? ProductionPathTestEnvironment.ResolveSharedGroup();
            psi.Environment["TINYIPC_BROKER_SOCKET_PATH"] = SocketPath;
            psi.Environment["TINYIPC_BUS_BACKEND"] = "sidecar-brokered";
            psi.Environment["TINYIPC_LAUNCH_ID"] = launchId;
            return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start staged native host directly.");
        }

        public void Dispose()
        {
            foreach ((string key, string? value) in _previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);

            ProductionPathTestEnvironment.ResetLogger();

            try
            {
                if (Directory.Exists(UnixSharedDirectory))
                    Directory.Delete(UnixSharedDirectory, recursive: true);
            }
            catch
            {
            }
        }

        private void Capture(string variableName)
            => _previousEnvironment[variableName] = Environment.GetEnvironmentVariable(variableName);
    }

    private enum StagedHostMode
    {
        None,
        Valid,
        Invalid
    }
}
