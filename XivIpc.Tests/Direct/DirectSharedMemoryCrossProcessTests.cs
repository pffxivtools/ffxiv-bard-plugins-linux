using System.Diagnostics;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class DirectSharedMemoryCrossProcessTests
{
    [Fact]
    public async Task TinyMessageBus_DirectSharedMemory_AllowsCrossProcessReattach()
    {
        using TestEnvironmentScope scope = new();
        using var publisher = scope.CreateBus(maxPayloadBytes: 4096);

        string first = await scope.ReceiveSingleMessageFromHelperProcessAsync(publisher, "first-message");
        Assert.Equal("first-message", first);

        string second = await scope.ReceiveSingleMessageFromHelperProcessAsync(publisher, "second-message");
        Assert.Equal("second-message", second);
    }

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly IDisposable _overrides;

        public TestEnvironmentScope()
        {
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-direct-cross-process-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SharedDirectory);

            _overrides = TinyIpcEnvironment.Override(
                (TinyIpcEnvironment.MessageBusBackend, "direct"),
                (TinyIpcEnvironment.DirectStorageMode, "shared-memory"),
                (TinyIpcEnvironment.SharedDirectory, SharedDirectory),
                (TinyIpcEnvironment.SlotCount, "64"),
                (TinyIpcEnvironment.MessageTtlMs, "30000"));

            ChannelName = $"xivipc-direct-cross-process-{Guid.NewGuid():N}";
        }

        public string ChannelName { get; }
        public string SharedDirectory { get; }

        public TinyMessageBus CreateBus(int maxPayloadBytes)
            => new(new TinyMemoryMappedFile(ChannelName, maxPayloadBytes), disposeFile: true);

        public async Task<string> ReceiveSingleMessageFromHelperProcessAsync(TinyMessageBus publisher, string payload)
        {
            string readyPath = Path.Combine(SharedDirectory, Guid.NewGuid().ToString("N") + ".ready");
            string outputPath = Path.Combine(SharedDirectory, Guid.NewGuid().ToString("N") + ".msg");

            using Process process = StartHelperProcess(readyPath, outputPath);
            await WaitForFileAsync(readyPath, TimeSpan.FromSeconds(10));

            await publisher.PublishAsync(Encoding.UTF8.GetBytes(payload));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Direct test helper exited with code {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{await process.StandardOutput.ReadToEndAsync()}{Environment.NewLine}stderr:{Environment.NewLine}{await process.StandardError.ReadToEndAsync()}");
            }

            await WaitForFileAsync(outputPath, TimeSpan.FromSeconds(5));
            return Encoding.UTF8.GetString(await File.ReadAllBytesAsync(outputPath));
        }

        private Process StartHelperProcess(string readyPath, string outputPath)
        {
            ProcessStartInfo psi = new("dotnet")
            {
                Arguments = $"exec \"{ResolveDirectTestHostDllPath()}\" \"{ChannelName}\" 4096 \"{readyPath}\" \"{outputPath}\" 10000",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            psi.Environment[TinyIpcEnvironment.MessageBusBackend] = "direct";
            psi.Environment[TinyIpcEnvironment.DirectStorageMode] = "shared-memory";
            psi.Environment[TinyIpcEnvironment.SharedDirectory] = SharedDirectory;
            psi.Environment[TinyIpcEnvironment.SlotCount] = "64";
            psi.Environment[TinyIpcEnvironment.MessageTtlMs] = "30000";

            return Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start direct shared-memory test helper process.");
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

        public void Dispose()
        {
            _overrides.Dispose();
        }
    }
}
