using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class SidecarMultiUserTests
{
    public static IEnumerable<object[]> Modes()
    {
        yield return new object[] { "sidecar" };
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task SecondaryUnixUser_CanConnect_ReadRing_AndReceivePublish_WhenEnabled(string mode)
    {
        if (!RuntimeHarness.ShouldRun())
            return;

        using TestEnvironmentScope scope = new(mode);
        using var publisher = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        string barrierPrefix = "__tinyipc_multiuser_barrier__:";
        string readyPath = Path.Combine(scope.SharedDirectory, Guid.NewGuid().ToString("N") + ".ready");
        string outputPath = Path.Combine(scope.SharedDirectory, Guid.NewGuid().ToString("N") + ".msg");
        using HostedProcess subscriber = RuntimeHarness.StartSecondaryUserProcess(
            scope.SecondaryUser,
            "dotnet",
            ["exec", scope.SecondarySubscriberHostPath, "sidecar-receive", scope.ChannelName, "4096", readyPath, outputPath, "15000", barrierPrefix],
            scope.CaptureChildEnvironment());

        await WaitForFileContentAsync(readyPath, "connected", TimeSpan.FromSeconds(15));
        while (true)
        {
            await publisher.PublishAsync(Encoding.UTF8.GetBytes(barrierPrefix + Guid.NewGuid().ToString("N")));
            if (await TryWaitForFileContentAsync(readyPath, "ready", TimeSpan.FromMilliseconds(250)))
                break;
        }

        string statePath = UnixSidecarProcessManager.ResolveBrokerStatePathForDiagnostics();
        string[] storagePaths = scope.EnumerateBrokerStoragePaths().ToArray();

        Assert.Equal($"{scope.SharedGroup} 2770", GetStat(scope.SharedDirectory, "%G %a"));
        Assert.Equal($"{scope.SharedGroup} 660", GetStat(scope.SocketPath, "%G %a"));
        Assert.Equal($"{scope.SharedGroup} 660", GetStat(statePath, "%G %a"));

        byte[] payload = Encoding.UTF8.GetBytes("multi-user-sidecar");
        await publisher.PublishAsync(payload);

        await WaitForFileAsync(outputPath, TimeSpan.FromSeconds(15));
        Assert.Equal(payload, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(0, await subscriber.WaitForExitAsync(TimeSpan.FromSeconds(10)));

        storagePaths = scope.EnumerateBrokerStoragePaths().ToArray();
        foreach (string storagePath in storagePaths)
            Assert.Equal($"{scope.SharedGroup} 660", GetStat(storagePath, "%G %a"));

        publisher.Dispose();
        await scope.WaitForBrokerShutdownAsync();

        Assert.False(File.Exists(scope.SocketPath));
        foreach (string storagePath in storagePaths)
            Assert.False(File.Exists(storagePath));
    }

    private static string GetStat(string path, string format)
    {
        using var process = Process.Start(new ProcessStartInfo("stat")
        {
            ArgumentList = { "-c", format, path },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start stat.");

        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"stat failed for '{path}': {error}");

        return output;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for file '{path}'.");
    }

    private static async Task WaitForFileContentAsync(string path, string expected, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                string content = await File.ReadAllTextAsync(path);
                if (string.Equals(content.Trim(), expected, StringComparison.Ordinal))
                    return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for file '{path}' to contain '{expected}'.");
    }

    private static async Task<bool> TryWaitForFileContentAsync(string path, string expected, TimeSpan timeout)
    {
        try
        {
            await WaitForFileContentAsync(path, expected, timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly IDisposable _overrides;
        private readonly Dictionary<string, string?> _overrideValues;

        public TestEnvironmentScope(string mode)
        {
            SecondaryUser = TinyIpcEnvironment.GetEnvironmentVariable("TINYIPC_MULTIUSER_SECONDARY_USER")
                ?? throw new InvalidOperationException("TINYIPC_MULTIUSER_SECONDARY_USER must be set for multi-user tests.");
            SharedGroup = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedGroup)
                ?? throw new InvalidOperationException("TINYIPC_SHARED_GROUP must be set for multi-user tests.");

            ChannelName = $"xivipc-multiuser-{Guid.NewGuid():N}";
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-multiuser-tests", Guid.NewGuid().ToString("N"));
            SocketPath = Path.Combine(SharedDirectory, "tinyipc-sidecar.sock");

            Directory.CreateDirectory(SharedDirectory);
            UnixSharedStorageHelpers.ApplyBrokerPermissions(SharedDirectory, isDirectory: true);
            SecondarySubscriberHostPath = PrepareStagedDirectTestHost(SharedDirectory);

            ProductionPathTestEnvironment.ResetLogger();
            _overrideValues = new Dictionary<string, string?>(StringComparer.Ordinal);

            if (ProductionPathTestEnvironment.IsProductionPath(mode))
            {
                ProductionPathTestEnvironment.PrepareStagedNativeHost(SharedDirectory);
                _overrideValues[TinyIpcEnvironment.MessageBusBackend] = null;
                _overrideValues[TinyIpcEnvironment.NativeHostPath] = null;
                _overrideValues[TinyIpcEnvironment.SharedDirectory] = ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory);
                _overrideValues[TinyIpcEnvironment.SharedGroup] = SharedGroup;
                _overrideValues[TinyIpcEnvironment.LogDirectory] = ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory);
                _overrideValues[TinyIpcEnvironment.LogLevel] = "info";
                _overrideValues[TinyIpcEnvironment.EnableLogging] = "1";
                _overrideValues[TinyIpcEnvironment.FileNotifier] = "auto";
                _overrideValues[TinyIpcEnvironment.BrokerIdleShutdownMs] = "2000";

                if (ProductionPathTestEnvironment.IsProductionRingPath(mode))
                    _overrideValues[TinyIpcEnvironment.SidecarStorageMode] = "ring";
            }
            else
            {
                _overrideValues[TinyIpcEnvironment.MessageBusBackend] = "sidecar";
                _overrideValues[TinyIpcEnvironment.SharedDirectory] = SharedDirectory;
                _overrideValues[TinyIpcEnvironment.SharedGroup] = SharedGroup;
                _overrideValues[TinyIpcEnvironment.NativeHostPath] = UnixSidecarProcessManager.ResolveNativeHostPath();
                _overrideValues[TinyIpcEnvironment.BrokerIdleShutdownMs] = "2000";

                if (string.Equals(mode, "sidecar-ring", StringComparison.OrdinalIgnoreCase))
                    _overrideValues[TinyIpcEnvironment.SidecarStorageMode] = "ring";
            }

            if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.UnixShell)))
                _overrideValues[TinyIpcEnvironment.UnixShell] = "/bin/sh";

            _overrides = TinyIpcEnvironment.Override(_overrideValues);
        }

        public string ChannelName { get; }
        public string SecondaryUser { get; }
        public string SharedDirectory { get; }
        public string SharedGroup { get; }
        public string SecondarySubscriberHostPath { get; }
        public string SocketPath { get; }

        public TinyMessageBus CreateBus(int maxPayloadBytes = 4096)
            => new(new TinyMemoryMappedFile(ChannelName, maxPayloadBytes), disposeFile: true);

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
                        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                        socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(SocketPath));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                await Task.Delay(50);
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

                await Task.Delay(50);
            }

            Assert.False(File.Exists(SocketPath), $"Expected broker socket '{SocketPath}' to be removed.");
        }

        public IEnumerable<string> EnumerateBrokerStoragePaths()
            => Directory.EnumerateFiles(SharedDirectory, "*broker-*", SearchOption.TopDirectoryOnly);

        public IReadOnlyDictionary<string, string> CaptureChildEnvironment()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach ((string key, string? value) in _overrideValues)
            {
                if (key.StartsWith("TINYIPC_", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value))
                    values[key] = value;
            }

            return values;
        }

        public void Dispose()
        {
            _overrides.Dispose();
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
            string line = await WaitForStdoutPrefixAsync(expected, timeout);
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
            await _process.WaitForExitAsync(cts.Token);
            await Task.WhenAll(_stdoutPump, _stderrPump);
            return _process.ExitCode;
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
                    string? line = await reader.ReadLineAsync();
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
        public static bool ShouldRun()
        {
            string? raw = Environment.GetEnvironmentVariable("TINYIPC_ENABLE_MULTIUSER_TESTS");
            bool enabled = string.Equals(raw, "1", StringComparison.Ordinal)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);

            if (!enabled || !OperatingSystem.IsLinux())
                return false;

            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TINYIPC_MULTIUSER_SECONDARY_USER"))
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP"));
        }

        public static HostedProcess StartSecondaryUserProcess(string username, string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
        {
            ProcessStartInfo psi = new("sudo")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(username);

            if (environment is not null && environment.Count > 0)
            {
                psi.ArgumentList.Add("env");
                foreach ((string key, string value) in environment)
                    psi.ArgumentList.Add($"{key}={value}");
            }

            psi.ArgumentList.Add(executable);

            foreach (string argument in arguments)
                psi.ArgumentList.Add(argument);

            Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start secondary-user process.");
            return new HostedProcess(process);
        }
    }

    private static string ResolveDirectTestHostDllPath()
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XivIpc.DirectTestHost", "bin", "Debug", "net9.0", "XivIpc.DirectTestHost.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XivIpc.DirectTestHost", "bin", "Release", "net9.0", "XivIpc.DirectTestHost.dll"))
        ];

        string? existing = candidates.FirstOrDefault(File.Exists);
        return existing ?? throw new InvalidOperationException("Could not resolve the current XivIpc.DirectTestHost build output.");
    }

    private static string PrepareStagedDirectTestHost(string unixSharedDirectory)
    {
        string sourceDll = ResolveDirectTestHostDllPath();
        string sourceDirectory = Path.GetDirectoryName(sourceDll)
            ?? throw new InvalidOperationException("Direct test host path must have a parent directory.");

        string stagingDirectory = Path.Combine(unixSharedDirectory, "tinyipc-test-host");
        Directory.CreateDirectory(stagingDirectory);
        UnixSharedStorageHelpers.ApplyBrokerPermissions(stagingDirectory, isDirectory: true);

        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            string destination = Path.Combine(stagingDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destination, overwrite: true);
            UnixSharedStorageHelpers.ApplyBrokerPermissions(destination, isDirectory: false);
        }

        return Path.Combine(stagingDirectory, Path.GetFileName(sourceDll));
    }
}
