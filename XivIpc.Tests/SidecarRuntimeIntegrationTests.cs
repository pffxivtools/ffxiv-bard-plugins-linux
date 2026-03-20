using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class SidecarRuntimeIntegrationTests
{
    [Theory]
    [InlineData(false)]
    public async Task Wine_Client_StartsBrokerAndReceivesFromNative_WhenEnabled(bool productionDiscovery)
    {
        if (!RuntimeHarness.ShouldRun("TINYIPC_ENABLE_WINE_TESTS"))
            return;

        using TestEnvironmentScope scope = new(productionDiscovery);
        string hostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_TEST_HOST_PATH");
        string nativeHostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_NATIVE_HOST_PATH");
        string runnerPath = RuntimeHarness.ResolveScriptPath("run-wine-test-host.sh");

        using HostedProcess subscriber = RuntimeHarness.StartProcess(
            runnerPath,
            hostPath,
            "subscribe-once",
            scope.ChannelName,
            "15000",
            scope.CreateEnvironment(nativeHostPath));

        await subscriber.WaitForStdoutLineAsync("CONNECTED", TimeSpan.FromSeconds(30));
        scope.AssertProductionSidecarStarted();

        using var publisher = scope.CreateBus();
        await Task.Delay(750);

        byte[] payload = Encoding.UTF8.GetBytes("native-to-wine");
        await PublishBytesAsync(publisher, payload);

        string messageLine = await subscriber.WaitForStdoutPrefixAsync("MESSAGE:", TimeSpan.FromSeconds(15));
        Assert.Equal(Convert.ToBase64String(payload), messageLine["MESSAGE:".Length..]);
        Assert.Equal(0, await subscriber.WaitForExitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(false)]
    public async Task Proton_Client_StartsBrokerAndReceivesFromNative_WhenEnabled(bool productionDiscovery)
    {
        if (!RuntimeHarness.ShouldRun("TINYIPC_ENABLE_PROTON_TESTS"))
            return;

        using TestEnvironmentScope scope = new(productionDiscovery);
        string hostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_TEST_HOST_PATH");
        string nativeHostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_NATIVE_HOST_PATH");
        string runnerPath = RuntimeHarness.ResolveScriptPath("run-proton-test-host.sh");

        using HostedProcess subscriber = RuntimeHarness.StartProcess(
            runnerPath,
            hostPath,
            "subscribe-once",
            scope.ChannelName,
            "15000",
            scope.CreateEnvironment(nativeHostPath));

        await subscriber.WaitForStdoutLineAsync("CONNECTED", TimeSpan.FromSeconds(30));
        scope.AssertProductionSidecarStarted();

        using var publisher = scope.CreateBus();
        await Task.Delay(750);

        byte[] payload = Encoding.UTF8.GetBytes("native-to-proton");
        await PublishBytesAsync(publisher, payload);

        string messageLine = await subscriber.WaitForStdoutPrefixAsync("MESSAGE:", TimeSpan.FromSeconds(15));
        Assert.Equal(Convert.ToBase64String(payload), messageLine["MESSAGE:".Length..]);
        Assert.Equal(0, await subscriber.WaitForExitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(false)]
    public async Task Wine_Client_KilledWithoutDispose_CanReconnectToLiveBroker_WhenEnabled(bool productionDiscovery)
    {
        if (!RuntimeHarness.ShouldRun("TINYIPC_ENABLE_WINE_TESTS"))
            return;

        using TestEnvironmentScope scope = new(productionDiscovery);
        string hostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_TEST_HOST_PATH");
        string nativeHostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_NATIVE_HOST_PATH");
        string runnerPath = RuntimeHarness.ResolveScriptPath("run-wine-test-host.sh");

        using var nativePublisher = scope.CreateBus();
        using var nativeSubscriber = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        using HostedProcess holder = RuntimeHarness.StartProcess(
            runnerPath,
            hostPath,
            "hold",
            scope.ChannelName,
            "30",
            scope.CreateEnvironment(nativeHostPath));

        await holder.WaitForStdoutLineAsync("CONNECTED", TimeSpan.FromSeconds(30));
        scope.AssertProductionSidecarStarted();
        holder.Kill();
        Assert.NotEqual(0, await holder.WaitForExitAsync(TimeSpan.FromSeconds(10)));

        using HostedProcess subscriber = RuntimeHarness.StartProcess(
            runnerPath,
            hostPath,
            "subscribe-once",
            scope.ChannelName,
            "15000",
            scope.CreateEnvironment(nativeHostPath));

        await subscriber.WaitForStdoutLineAsync("CONNECTED", TimeSpan.FromSeconds(30));
        await Task.Delay(750);

        byte[] payload = Encoding.UTF8.GetBytes("wine-reconnect");
        await PublishBytesAsync(nativePublisher, payload);

        string messageLine = await subscriber.WaitForStdoutPrefixAsync("MESSAGE:", TimeSpan.FromSeconds(15));
        Assert.Equal(Convert.ToBase64String(payload), messageLine["MESSAGE:".Length..]);
        Assert.Equal(0, await subscriber.WaitForExitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(false)]
    public async Task Wine_Client_Dispose_RemovesSession_AndBrokerShutsDown_WhenEnabled(bool productionDiscovery)
    {
        if (!RuntimeHarness.ShouldRun("TINYIPC_ENABLE_WINE_TESTS"))
            return;

        using TestEnvironmentScope scope = new(productionDiscovery);
        string hostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_TEST_HOST_PATH");
        string nativeHostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_NATIVE_HOST_PATH");
        string runnerPath = RuntimeHarness.ResolveScriptPath("run-wine-test-host.sh");

        using HostedProcess client = RuntimeHarness.StartProcess(
            runnerPath,
            hostPath,
            "connect-dispose-and-wait",
            scope.ChannelName,
            "3000",
            scope.CreateEnvironment(nativeHostPath));

        await client.WaitForStdoutLineAsync("CONNECTED", TimeSpan.FromSeconds(30));
        scope.AssertProductionSidecarStarted();
        await client.WaitForStdoutLineAsync("DISPOSED", TimeSpan.FromSeconds(30));
        await scope.WaitForBrokerShutdownAsync();
        Assert.Equal(0, await client.WaitForExitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(false)]
    public async Task Proton_Client_KilledWithoutDispose_CanReconnectToLiveBroker_WhenEnabled(bool productionDiscovery)
    {
        if (!RuntimeHarness.ShouldRun("TINYIPC_ENABLE_PROTON_TESTS"))
            return;

        using TestEnvironmentScope scope = new(productionDiscovery);
        string hostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_TEST_HOST_PATH");
        string nativeHostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_NATIVE_HOST_PATH");
        string runnerPath = RuntimeHarness.ResolveScriptPath("run-proton-test-host.sh");

        using var nativePublisher = scope.CreateBus();
        using var nativeSubscriber = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        using HostedProcess holder = RuntimeHarness.StartProcess(
            runnerPath,
            hostPath,
            "hold",
            scope.ChannelName,
            "30",
            scope.CreateEnvironment(nativeHostPath));

        await holder.WaitForStdoutLineAsync("CONNECTED", TimeSpan.FromSeconds(30));
        scope.AssertProductionSidecarStarted();
        holder.Kill();
        Assert.NotEqual(0, await holder.WaitForExitAsync(TimeSpan.FromSeconds(10)));

        using HostedProcess subscriber = RuntimeHarness.StartProcess(
            runnerPath,
            hostPath,
            "subscribe-once",
            scope.ChannelName,
            "15000",
            scope.CreateEnvironment(nativeHostPath));

        await subscriber.WaitForStdoutLineAsync("CONNECTED", TimeSpan.FromSeconds(30));
        await Task.Delay(750);

        byte[] payload = Encoding.UTF8.GetBytes("proton-reconnect");
        await PublishBytesAsync(nativePublisher, payload);

        string messageLine = await subscriber.WaitForStdoutPrefixAsync("MESSAGE:", TimeSpan.FromSeconds(15));
        Assert.Equal(Convert.ToBase64String(payload), messageLine["MESSAGE:".Length..]);
        Assert.Equal(0, await subscriber.WaitForExitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(false)]
    public async Task Proton_Client_Dispose_RemovesSession_AndBrokerShutsDown_WhenEnabled(bool productionDiscovery)
    {
        if (!RuntimeHarness.ShouldRun("TINYIPC_ENABLE_PROTON_TESTS"))
            return;

        using TestEnvironmentScope scope = new(productionDiscovery);
        string hostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_TEST_HOST_PATH");
        string nativeHostPath = RuntimeHarness.ResolveRequiredArtifactPath("TINYIPC_RUNTIME_NATIVE_HOST_PATH");
        string runnerPath = RuntimeHarness.ResolveScriptPath("run-proton-test-host.sh");

        using HostedProcess client = RuntimeHarness.StartProcess(
            runnerPath,
            hostPath,
            "connect-dispose-and-wait",
            scope.ChannelName,
            "3000",
            scope.CreateEnvironment(nativeHostPath));

        await client.WaitForStdoutLineAsync("CONNECTED", TimeSpan.FromSeconds(30));
        scope.AssertProductionSidecarStarted();
        await client.WaitForStdoutLineAsync("DISPOSED", TimeSpan.FromSeconds(30));
        await scope.WaitForBrokerShutdownAsync();
        Assert.Equal(0, await client.WaitForExitAsync(TimeSpan.FromSeconds(10)));
    }

    private static async Task PublishBytesAsync(TinyMessageBus bus, byte[] bytes)
    {
        MethodInfo[] methods = typeof(TinyMessageBus)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "PublishAsync")
            .ToArray();

        MethodInfo? binaryDataOverload = methods.FirstOrDefault(m =>
        {
            ParameterInfo[] parameters = m.GetParameters();
            return parameters.Length >= 1 && parameters[0].ParameterType == typeof(BinaryData);
        });

        if (binaryDataOverload is not null)
        {
            object?[] args = BuildArguments(binaryDataOverload, new BinaryData(bytes));
            await InvokePublishAsync(bus, binaryDataOverload, args).ConfigureAwait(false);
            return;
        }

        MethodInfo byteArrayOverload = methods.First(m =>
        {
            ParameterInfo[] parameters = m.GetParameters();
            return parameters.Length >= 1 && parameters[0].ParameterType == typeof(byte[]);
        });

        await InvokePublishAsync(bus, byteArrayOverload, BuildArguments(byteArrayOverload, bytes)).ConfigureAwait(false);
    }

    private static object?[] BuildArguments(MethodInfo method, object firstArgument)
    {
        ParameterInfo[] parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = firstArgument;

        for (int i = 1; i < parameters.Length; i++)
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : GetDefault(parameters[i].ParameterType);

        return args;
    }

    private static async Task InvokePublishAsync(TinyMessageBus bus, MethodInfo method, object?[] args)
    {
        object? result = method.Invoke(bus, args);
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("TinyMessageBus.PublishAsync did not return a Task.");
    }

    private static object? GetDefault(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);
        private readonly bool _productionDiscovery;

        public TestEnvironmentScope(bool productionDiscovery)
        {
            _productionDiscovery = productionDiscovery;
            ChannelName = $"xivipc-runtime-{Guid.NewGuid():N}";
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-runtime-tests", Guid.NewGuid().ToString("N"));
            SocketPath = Path.Combine(SharedDirectory, "tinyipc-sidecar.sock");

            Directory.CreateDirectory(SharedDirectory);

            Capture("TINYIPC_MESSAGE_BUS_BACKEND");
            Capture("TINYIPC_SHARED_DIR");
            Capture("TINYIPC_SHARED_GROUP");
            Capture("TINYIPC_NATIVE_HOST_PATH");
            Capture("TINYIPC_UNIX_SHELL");
            Capture("TINYIPC_TEST_MAX_PAYLOAD_BYTES");
            Capture("TINYIPC_LOG_DIR");
            Capture("TINYIPC_LOG_LEVEL");
            Capture("TINYIPC_ENABLE_LOGGING");
            Capture("TINYIPC_FILE_NOTIFIER");

            ProductionPathTestEnvironment.ResetLogger();

            if (_productionDiscovery)
            {
                ProductionPathTestEnvironment.PrepareStagedNativeHost(SharedDirectory);
                Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", null);
                Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", null);
                Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory));
                Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", ResolveCurrentSharedGroup());
                Environment.SetEnvironmentVariable("TINYIPC_LOG_DIR", ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory));
                Environment.SetEnvironmentVariable("TINYIPC_LOG_LEVEL", "info");
                Environment.SetEnvironmentVariable("TINYIPC_ENABLE_LOGGING", "1");
                Environment.SetEnvironmentVariable("TINYIPC_FILE_NOTIFIER", "auto");
            }
            else
            {
                Environment.SetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND", "sidecar");
                Environment.SetEnvironmentVariable("TINYIPC_SHARED_DIR", SharedDirectory);
                Environment.SetEnvironmentVariable("TINYIPC_SHARED_GROUP", ResolveCurrentSharedGroup());
                Environment.SetEnvironmentVariable("TINYIPC_NATIVE_HOST_PATH", UnixSidecarProcessManager.ResolveNativeHostPath());
            }
            Environment.SetEnvironmentVariable("TINYIPC_TEST_MAX_PAYLOAD_BYTES", "4096");

            if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TINYIPC_UNIX_SHELL")))
                Environment.SetEnvironmentVariable("TINYIPC_UNIX_SHELL", "/bin/sh");
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }
        public string SocketPath { get; }

        public TinyMessageBus CreateBus(int maxPayloadBytes = 4096)
            => new(new TinyMemoryMappedFile(ChannelName, maxPayloadBytes), disposeFile: true);

        public void AssertProductionSidecarStarted()
        {
            if (!_productionDiscovery)
                return;

            Assert.True(File.Exists(SocketPath), $"Expected broker socket '{SocketPath}' to exist.");
            Assert.True(File.Exists(Path.Combine(SharedDirectory, "tinyipc-sidecar.state.json")), "Expected broker state file to exist.");

            string clientLog = ProductionPathTestEnvironment.ReadLatestClientLogOrEmpty(SharedDirectory);
            string nativeLog = ProductionPathTestEnvironment.ReadLatestUnixProcessLogOrEmpty(SharedDirectory);
            ProductionPathTestEnvironment.AssertStartedSidecar(clientLog);
            ProductionPathTestEnvironment.AssertHealthyNativeBrokerLog(nativeLog);
            ProductionPathTestEnvironment.AssertLaunchContract(
                clientLog,
                nativeLog,
                SharedDirectory,
                SocketPath,
                Path.Combine(SharedDirectory, "tinyipc-native-host", "XivIpc.NativeHost"));
        }

        public Dictionary<string, string> CreateEnvironment(string nativeHostPath)
        {
            if (_productionDiscovery)
            {
                ProductionPathTestEnvironment.PrepareStagedNativeHost(SharedDirectory);

                return new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TINYIPC_SHARED_DIR"] = ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory),
                    ["TINYIPC_SHARED_GROUP"] = Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP") ?? ResolveCurrentSharedGroup(),
                    ["TINYIPC_TEST_MAX_PAYLOAD_BYTES"] = "4096",
                    ["TINYIPC_UNIX_SHELL"] = Environment.GetEnvironmentVariable("TINYIPC_UNIX_SHELL") ?? "/bin/sh",
                    ["TINYIPC_LOG_DIR"] = ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory),
                    ["TINYIPC_LOG_LEVEL"] = "info",
                    ["TINYIPC_ENABLE_LOGGING"] = "1",
                    ["TINYIPC_FILE_NOTIFIER"] = "auto"
                };
            }

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TINYIPC_MESSAGE_BUS_BACKEND"] = "sidecar",
                ["TINYIPC_SHARED_DIR"] = SharedDirectory,
                ["TINYIPC_SHARED_GROUP"] = Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP") ?? ResolveCurrentSharedGroup(),
                ["TINYIPC_NATIVE_HOST_PATH"] = nativeHostPath,
                ["TINYIPC_TEST_MAX_PAYLOAD_BYTES"] = "4096",
                ["TINYIPC_UNIX_SHELL"] = Environment.GetEnvironmentVariable("TINYIPC_UNIX_SHELL") ?? "/bin/sh"
            };
        }

        public async Task WaitForLiveSocketAsync()
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
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

                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException($"Timed out waiting for live broker socket '{SocketPath}'.", last);
        }

        public async Task WaitForBrokerShutdownAsync()
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (!File.Exists(SocketPath))
                    return;

                await Task.Delay(50).ConfigureAwait(false);
            }

            Assert.False(File.Exists(SocketPath), $"Expected broker socket '{SocketPath}' to be removed.");
        }

        public void Dispose()
        {
            foreach ((string key, string? value) in _previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);

            ProductionPathTestEnvironment.ResetLogger();

            try
            {
                if (Directory.Exists(SharedDirectory))
                    Directory.Delete(SharedDirectory, recursive: true);
            }
            catch
            {
            }
        }

        private void Capture(string variableName)
            => _previousEnvironment[variableName] = Environment.GetEnvironmentVariable(variableName);

        private static string ResolveCurrentSharedGroup()
        {
            using var process = Process.Start(new ProcessStartInfo("id", "-gn")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Failed to start 'id -gn' to resolve the shared group.");

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("Failed to resolve the current user's primary group for runtime tests.");

            return output;
        }
    }

    private sealed class HostedProcess : IDisposable
    {
        private readonly Process _process;
        private readonly BlockingCollection<string> _stdoutLines = new();
        private readonly BlockingCollection<string> _stderrLines = new();
        private readonly Task _stdoutPump;
        private readonly Task _stderrPump;

        public HostedProcess(Process process)
        {
            _process = process;
            _stdoutPump = Task.Run(() => PumpAsync(process.StandardOutput, _stdoutLines));
            _stderrPump = Task.Run(() => PumpAsync(process.StandardError, _stderrLines));
        }

        public async Task<string> WaitForStdoutLineAsync(string expected, TimeSpan timeout)
        {
            string line = await WaitForStdoutPrefixAsync(expected, timeout).ConfigureAwait(false);
            if (!string.Equals(line, expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"Expected stdout line '{expected}' but received '{line}'.");

            return line;
        }

        public async Task<string> WaitForStdoutPrefixAsync(string prefix, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (_stdoutLines.TryTake(out string? line, millisecondsTimeout: 100))
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        return line;
                }

                if (_process.HasExited && _stdoutLines.Count == 0)
                    break;
            }

            throw new TimeoutException(
                $"Timed out waiting for stdout prefix '{prefix}'. stderr={string.Join(Environment.NewLine, _stderrLines.ToArray())}");
        }

        public async Task<int> WaitForExitAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.WhenAll(_stdoutPump, _stderrPump).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public void Kill()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try { _process.Dispose(); } catch { }
            _stdoutLines.Dispose();
            _stderrLines.Dispose();
        }

        private static async Task PumpAsync(StreamReader reader, BlockingCollection<string> sink)
        {
            try
            {
                while (true)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                        break;

                    sink.Add(line);
                }
            }
            finally
            {
                sink.CompleteAdding();
            }
        }
    }

    private static class RuntimeHarness
    {
        public static bool ShouldRun(string variableName)
        {
            string? raw = Environment.GetEnvironmentVariable(variableName);
            bool enabled = string.Equals(raw, "1", StringComparison.Ordinal)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);

            if (!enabled)
                return false;

#if NET10_0
            return true;
#else
            return false;
#endif
        }

        public static string ResolveScriptPath(string scriptName)
        {
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", scriptName));
            if (!File.Exists(path))
                throw new FileNotFoundException($"Script '{scriptName}' was not found.", path);

            return path;
        }

        public static string ResolveRequiredArtifactPath(string variableName)
        {
            string? path = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException($"Runtime integration requires {variableName} to be set.");

            string resolved = Path.GetFullPath(path);
            if (!File.Exists(resolved))
                throw new FileNotFoundException($"Runtime integration artifact '{variableName}' was not found.", resolved);

            return resolved;
        }

        public static HostedProcess StartProcess(string runnerScriptPath, string hostPath, string command, string channel, string extraArgument, IReadOnlyDictionary<string, string> environment)
        {
            ProcessStartInfo psi = new(runnerScriptPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add(hostPath);
            psi.ArgumentList.Add(command);
            psi.ArgumentList.Add(channel);
            psi.ArgumentList.Add(extraArgument);

            foreach ((string key, string value) in environment)
                psi.Environment[key] = value;

            Process process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{runnerScriptPath}'.");
            return new HostedProcess(process);
        }
    }
}
