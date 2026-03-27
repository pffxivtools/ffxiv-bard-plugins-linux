using System.Net.Sockets;
using System.Diagnostics;
using System.Net;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
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

    [Fact]
    public void AutoBackend_WithoutConfiguredPaths_DownloadsDefaultHost_AndUsesOpenPermissions()
    {
        using TestEnvironmentScope scope = new(StagedHostMode.None, setSharedGroup: false, useDefaultPaths: true, downloadDefaultHost: true);
        using var bus = new TinyMessageBus(new TinyMemoryMappedFile(scope.ChannelName, 4096), disposeFile: true);

        scope.WaitForSocket();
        scope.WaitForStateFile();

        string clientLog = scope.WaitForClientLog("BrokerLaunchPrepared");
        string nativeLog = scope.WaitForUnixProcessLog("BrokerStartupObservedEnvironment");

        ProductionPathTestEnvironment.AssertStartedSidecar(clientLog);
        ProductionPathTestEnvironment.AssertHealthyNativeBrokerLog(nativeLog);
        ProductionPathTestEnvironment.AssertLaunchContract(clientLog, nativeLog, scope.UnixSharedDirectory, scope.SocketPath, scope.StagedHostPath);
        Assert.True(File.Exists(scope.StagedHostPath));
        Assert.Equal("1777", ProductionPathTestEnvironment.ReadUnixMode(scope.UnixSharedDirectory));
        Assert.Equal("666", ProductionPathTestEnvironment.ReadUnixMode(scope.SocketPath));
        Assert.Equal("666", ProductionPathTestEnvironment.ReadUnixMode(scope.StatePath));
        Assert.Equal("755", ProductionPathTestEnvironment.ReadUnixMode(scope.StagedHostPath));
    }

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly List<IDisposable> _cleanup = new();

        public TestEnvironmentScope(StagedHostMode stagedHostMode, bool setSharedGroup = true, bool useDefaultPaths = false, bool downloadDefaultHost = false)
        {
            ChannelName = $"xivipc-production-startup-{Guid.NewGuid():N}";
            UnixSharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-production-startup-tests", Guid.NewGuid().ToString("N"));
            SharedDirectory = ProductionPathTestEnvironment.ToWindowsStylePath(UnixSharedDirectory);
            SocketPath = Path.Combine(UnixSharedDirectory, "tinyipc-sidecar.sock");
            StatePath = Path.Combine(UnixSharedDirectory, "tinyipc-sidecar.state.json");
            StagedHostDirectory = useDefaultPaths
                ? Path.Combine(Path.GetTempPath(), "xivipc-production-startup-hosts", Guid.NewGuid().ToString("N"), "tinyipc-native-host")
                : Path.Combine(UnixSharedDirectory, "tinyipc-native-host");

            Directory.CreateDirectory(UnixSharedDirectory);
            if (stagedHostMode == StagedHostMode.Valid && useDefaultPaths)
            {
                Directory.CreateDirectory(StagedHostDirectory);
                ProductionPathTestEnvironment.PrepareStandaloneNativeHost(StagedHostPath);
            }
            else if (stagedHostMode == StagedHostMode.Valid)
                StagedHostDirectory = ProductionPathTestEnvironment.PrepareStagedNativeHost(UnixSharedDirectory);
            else if (stagedHostMode == StagedHostMode.Invalid)
                StagedHostDirectory = ProductionPathTestEnvironment.PrepareInvalidStagedNativeHost(UnixSharedDirectory);

            if (useDefaultPaths)
            {
                _cleanup.Add(UnixSharedStorageHelpers.OverrideDefaultSharedDirectoryForTests(UnixSharedDirectory));

                NativeHostArchiveServer? server = null;
                if (downloadDefaultHost)
                {
                    server = new NativeHostArchiveServer(ProductionPathTestEnvironment.CreateNativeHostArchiveBytes());
                    _cleanup.Add(server);
                }

                _cleanup.Add(XivIpc.Messaging.UnixSidecarProcessManager.OverrideNativeHostDefaultsForTests(StagedHostPath, server?.Url));
            }

            ProductionPathTestEnvironment.ResetLogger();
            _cleanup.Add(TinyIpcEnvironment.Override(
                (TinyIpcEnvironment.MessageBusBackend, null),
                (TinyIpcEnvironment.NativeHostPath, null),
                (TinyIpcEnvironment.BrokerDirectory, null),
                (TinyIpcEnvironment.SharedDirectory, useDefaultPaths ? null : SharedDirectory),
                (TinyIpcEnvironment.SharedGroup, setSharedGroup ? ProductionPathTestEnvironment.ResolveSharedGroup() : null),
                (TinyIpcEnvironment.LogDirectory, SharedDirectory),
                (TinyIpcEnvironment.LogLevel, "info"),
                (TinyIpcEnvironment.EnableLogging, "1"),
                (TinyIpcEnvironment.FileNotifier, "auto")));
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
            psi.Environment["TINYIPC_SHARED_GROUP"] = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedGroup) ?? ProductionPathTestEnvironment.ResolveSharedGroup();
            psi.Environment["TINYIPC_BROKER_SOCKET_PATH"] = SocketPath;
            psi.Environment["TINYIPC_BUS_BACKEND"] = "sidecar-brokered";
            psi.Environment["TINYIPC_LAUNCH_ID"] = launchId;
            return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start staged native host directly.");
        }

        public void Dispose()
        {
            for (int i = _cleanup.Count - 1; i >= 0; i--)
                _cleanup[i].Dispose();

            ProductionPathTestEnvironment.ResetLogger();

            try
            {
                if (Directory.Exists(Path.GetDirectoryName(StagedHostDirectory) ?? string.Empty))
                    Directory.Delete(Path.GetDirectoryName(StagedHostDirectory)!, recursive: true);
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(UnixSharedDirectory))
                    Directory.Delete(UnixSharedDirectory, recursive: true);
            }
            catch
            {
            }
        }

    }

    private enum StagedHostMode
    {
        None,
        Valid,
        Invalid
    }

    private sealed class NativeHostArchiveServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;
        private readonly byte[] _payload;

        public NativeHostArchiveServer(byte[] payload)
        {
            _payload = payload;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/XivIpc.NativeHost-linux-x64.tar.gz";
            _serverTask = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public void Dispose()
        {
            _cts.Cancel();

            try
            {
                _listener.Stop();
            }
            catch
            {
            }

            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            _cts.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                while (!_cts.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line is null || line.Length == 0)
                        break;
                }

                string header =
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: application/gzip\r\n"
                    + $"Content-Length: {_payload.Length}\r\n"
                    + "Connection: close\r\n\r\n";

                byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
                await stream.WriteAsync(headerBytes, _cts.Token);
                await stream.WriteAsync(_payload, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }
    }
}
