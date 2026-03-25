using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

internal static class ProductionPathTestEnvironment
{
    internal const string BackendName = "production-auto";
    private static readonly object PublishedHostSync = new();
    private static string? _publishedHostPath;

    internal static bool IsProductionPath(string backendOrMode)
        => string.Equals(backendOrMode, BackendName, StringComparison.OrdinalIgnoreCase);

    internal static string ResolveSharedGroup()
    {
        string? configured = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedGroup);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        if (IsCurrentUserInGroup("steam"))
            return "steam";

        using var process = Process.Start(new ProcessStartInfo("id", "-gn")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start 'id -gn' to resolve the shared group.");

        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Failed to resolve the current user's primary group for production-path tests.");

        return output;
    }

    internal static string ToWindowsStylePath(string unixPath)
        => "Z:" + unixPath.Replace('/', '\\');

    internal static string PrepareStagedNativeHost(string unixSharedDirectory)
    {
        string sourceHost = ResolveBuildHostPath();
        string sourceDirectory = Path.GetDirectoryName(sourceHost)
            ?? throw new InvalidOperationException("Native host path must have a parent directory.");

        EnsureStrictSharedPermissions(unixSharedDirectory, isDirectory: true);

        string stagingDirectory = Path.Combine(unixSharedDirectory, "tinyipc-native-host");
        Directory.CreateDirectory(stagingDirectory);
        EnsureStrictSharedPermissions(stagingDirectory, isDirectory: true);

        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            string destination = Path.Combine(stagingDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destination, overwrite: true);
            EnsureStrictSharedPermissions(destination, isDirectory: false, isExecutable: IsExecutable(sourceFile));
        }

        return stagingDirectory;
    }

    internal static string PrepareStandaloneNativeHost(string hostPath)
    {
        string sourceHost = ResolvePublishedHostPath();
        string? directory = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.Copy(sourceHost, hostPath, overwrite: true);
        EnsureExecutablePermissions(hostPath);
        return hostPath;
    }

    internal static byte[] CreateNativeHostArchiveBytes()
    {
        string sourceHost = ResolvePublishedHostPath();

        using var archiveStream = new MemoryStream();
        using (var gzipStream = new GZipStream(archiveStream, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tarWriter = new TarWriter(gzipStream, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "XivIpc.NativeHost")
            {
                DataStream = File.OpenRead(sourceHost),
                Mode = (UnixFileMode)Convert.ToInt32("755", 8)
            };

            using (entry.DataStream)
                tarWriter.WriteEntry(entry);
        }

        return archiveStream.ToArray();
    }

    internal static string PrepareInvalidStagedNativeHost(string unixSharedDirectory)
    {
        EnsureStrictSharedPermissions(unixSharedDirectory, isDirectory: true);

        string stagingDirectory = Path.Combine(unixSharedDirectory, "tinyipc-native-host");
        Directory.CreateDirectory(stagingDirectory);
        EnsureStrictSharedPermissions(stagingDirectory, isDirectory: true);

        string invalidHostPath = Path.Combine(stagingDirectory, "XivIpc.NativeHost");
        File.WriteAllText(invalidHostPath, "not-a-real-native-host");

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            using var chmod = Process.Start(new ProcessStartInfo("chmod")
            {
                ArgumentList = { "770", invalidHostPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            chmod?.WaitForExit();
        }

        EnsureStrictSharedPermissions(invalidHostPath, isDirectory: false, isExecutable: true);

        return stagingDirectory;
    }

    internal static void ResetLogger()
        => TinyIpcLogger.ResetForTests();

    internal static string? FindLatestLogFile(string unixLogDirectory)
    {
        if (!Directory.Exists(unixLogDirectory))
            return null;

        return Directory.EnumerateFiles(unixLogDirectory, "tinyipc-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    internal static string ReadLatestLogOrEmpty(string unixLogDirectory)
    {
        string? path = FindLatestLogFile(unixLogDirectory);
        return path is null ? string.Empty : File.ReadAllText(path);
    }

    internal static string? FindLatestWindowsProcessLogFile(string unixLogDirectory)
    {
        if (!Directory.Exists(unixLogDirectory))
            return null;

        foreach (string path in Directory.EnumerateFiles(unixLogDirectory, "tinyipc-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            string contents;
            try
            {
                contents = File.ReadAllText(path);
            }
            catch
            {
                continue;
            }

            if (contents.Contains("runtime=WindowsProcess", StringComparison.Ordinal))
                return path;
        }

        return null;
    }

    internal static string ReadLatestWindowsProcessLogOrEmpty(string unixLogDirectory)
    {
        string? path = FindLatestWindowsProcessLogFile(unixLogDirectory);
        return path is null ? string.Empty : File.ReadAllText(path);
    }

    internal static string? FindLatestLogContaining(string unixLogDirectory, params string[] markers)
    {
        if (!Directory.Exists(unixLogDirectory))
            return null;

        foreach (string path in Directory.EnumerateFiles(unixLogDirectory, "tinyipc-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            string contents;
            try
            {
                contents = File.ReadAllText(path);
            }
            catch
            {
                continue;
            }

            if (markers.Any(marker => contents.Contains(marker, StringComparison.Ordinal)))
                return path;
        }

        return null;
    }

    internal static string ReadLatestClientLogOrEmpty(string unixLogDirectory)
        => ReadLatestWindowsProcessLogOrEmpty(unixLogDirectory);

    internal static string? FindLatestUnixProcessLogFile(string unixLogDirectory)
    {
        if (!Directory.Exists(unixLogDirectory))
            return null;

        foreach (string path in Directory.EnumerateFiles(unixLogDirectory, "tinyipc-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            string contents;
            try
            {
                contents = File.ReadAllText(path);
            }
            catch
            {
                continue;
            }

            if (contents.Contains("runtime=UnixProcess", StringComparison.Ordinal))
                return path;
        }

        return null;
    }

    internal static string ReadLatestUnixProcessLogOrEmpty(string unixLogDirectory)
    {
        string? path = FindLatestUnixProcessLogFile(unixLogDirectory);
        return path is null ? string.Empty : File.ReadAllText(path);
    }

    internal static void AssertStartedSidecar(string logContents)
    {
        Assert.DoesNotContain("AutoBrokerRequiredFailed", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoFallbackToDirect", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("UnixInMemoryTinyMessageBus", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("DisabledShimMessageBus", logContents, StringComparison.Ordinal);
    }

    internal static void AssertHealthyNativeBrokerLog(string logContents)
    {
        Assert.Contains("runtime=UnixProcess", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("UnhandledException", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("component=GlobalException", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("level=ERROR", logContents, StringComparison.Ordinal);
        Assert.Contains("TinyIpc logging enabled.", logContents, StringComparison.Ordinal);
    }

    internal static void AssertLaunchContract(string clientLogContents, string nativeLogContents, string unixSharedDirectory, string socketPath, string stagedHostPath)
    {
        Assert.Contains("event=BrokerLaunchPrepared", clientLogContents, StringComparison.Ordinal);
        Assert.Contains("event=BrokerStartupObservedEnvironment", nativeLogContents, StringComparison.Ordinal);
        Assert.Contains("event=BrokerProcessStamp", nativeLogContents, StringComparison.Ordinal);
        Assert.Contains("event=ClientProcessStamp", clientLogContents, StringComparison.Ordinal);

        string expectedSharedDir = unixSharedDirectory;
        string expectedSocketPath = socketPath;
        string expectedHostHash = ComputeFileSha256(stagedHostPath);
        string launchId = ExtractField(clientLogContents, "event=BrokerLaunchPrepared", "launchId");

        Assert.False(string.IsNullOrWhiteSpace(launchId));
        Assert.Equal(expectedSharedDir, ExtractField(clientLogContents, "event=BrokerLaunchPrepared", "childSharedDir"));
        Assert.Equal(expectedSharedDir, ExtractField(nativeLogContents, "event=BrokerStartupObservedEnvironment", "sharedDir"));
        Assert.Equal(expectedSocketPath, ExtractField(clientLogContents, "event=BrokerLaunchPrepared", "childBrokerSocketPath"));
        Assert.Equal(expectedSocketPath, ExtractField(nativeLogContents, "event=BrokerStartupObservedEnvironment", "brokerSocketPath"));
        Assert.Equal("sidecar-brokered", ExtractField(clientLogContents, "event=BrokerLaunchPrepared", "childBackend"));
        Assert.Equal("sidecar-brokered", ExtractField(nativeLogContents, "event=BrokerStartupObservedEnvironment", "backend"));
        Assert.Equal(launchId, ExtractField(nativeLogContents, "event=BrokerStartupObservedEnvironment", "launchId"));
        Assert.Equal(expectedHostHash, ExtractField(clientLogContents, "event=BrokerLaunchPrepared", "hostSha256"));
        Assert.Equal(expectedHostHash, ExtractField(nativeLogContents, "event=BrokerProcessStamp", "processSha256"));
    }

    internal static void AssertObservedBrokerEnvironment(string nativeLogContents, string unixSharedDirectory, string socketPath, string stagedHostPath, string? expectedLaunchId = null)
    {
        Assert.Contains("event=BrokerStartupObservedEnvironment", nativeLogContents, StringComparison.Ordinal);
        Assert.Contains("event=BrokerProcessStamp", nativeLogContents, StringComparison.Ordinal);
        Assert.Equal(unixSharedDirectory, ExtractField(nativeLogContents, "event=BrokerStartupObservedEnvironment", "sharedDir"));
        Assert.Equal(socketPath, ExtractField(nativeLogContents, "event=BrokerStartupObservedEnvironment", "brokerSocketPath"));
        Assert.Equal("sidecar-brokered", ExtractField(nativeLogContents, "event=BrokerStartupObservedEnvironment", "backend"));
        Assert.Equal(ComputeFileSha256(stagedHostPath), ExtractField(nativeLogContents, "event=BrokerProcessStamp", "processSha256"));
        if (!string.IsNullOrWhiteSpace(expectedLaunchId))
            Assert.Equal(expectedLaunchId, ExtractField(nativeLogContents, "event=BrokerStartupObservedEnvironment", "launchId"));
    }

    internal static void AssertSidecarReconnectFailureLogged(string logContents)
    {
        Assert.Contains("event=InitialConnectScheduled", logContents, StringComparison.Ordinal);
        Assert.Contains("event=ReconnectAttemptFailed", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoFallbackToDirect", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoBrokerRequiredFailed", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("DisabledShimMessageBus", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("UnixInMemoryTinyMessageBus", logContents, StringComparison.Ordinal);
    }

    internal static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static string ReadUnixMode(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("stat")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { "-c", "%a", path }
        }) ?? throw new InvalidOperationException("Failed to start 'stat'.");

        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            string error = process.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException($"stat '%a' failed for '{path}' with exit code {process.ExitCode}: {error}");
        }

        return output;
    }

    internal static string ExtractField(string logContents, string eventMarker, string key)
    {
        foreach (string line in logContents.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(eventMarker, StringComparison.Ordinal))
                continue;

            string marker = key + "=\"";
            int start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                continue;

            start += marker.Length;
            int end = line.IndexOf('"', start);
            if (end < 0)
                continue;

            return line[start..end];
        }

        throw new Xunit.Sdk.XunitException($"Could not find field '{key}' on event '{eventMarker}'.");
    }

    private static string ResolveBuildHostPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net10.0", "XivIpc.NativeHost")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Debug", "net10.0", "XivIpc.NativeHost.dll")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net10.0", "XivIpc.NativeHost")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XivIpc.NativeHost", "bin", "Release", "net10.0", "XivIpc.NativeHost.dll")),
            Path.Combine(baseDir, "XivIpc.NativeHost"),
            Path.Combine(baseDir, "XivIpc.NativeHost.dll")
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Could not resolve a built XivIpc.NativeHost artifact for production-path tests.");
    }

    private static string ResolvePublishedHostPath()
    {
        lock (PublishedHostSync)
        {
            if (!string.IsNullOrWhiteSpace(_publishedHostPath) && File.Exists(_publishedHostPath))
                return _publishedHostPath;

            string baseDir = AppContext.BaseDirectory;
            string repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            string projectPath = Path.Combine(repoRoot, "XivIpc.NativeHost", "XivIpc.NativeHost.csproj");
            string outputDir = Path.Combine(Path.GetTempPath(), "xivipc-native-host-publish-tests", "linux-x64");
            Directory.CreateDirectory(outputDir);

            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList =
                {
                    "publish",
                    projectPath,
                    "-c", "Release",
                    "-f", "net9.0",
                    "-r", "linux-x64",
                    "--self-contained", "true",
                    "-p:PublishSingleFile=true",
                    "-o", outputDir
                }
            }) ?? throw new InvalidOperationException("Failed to start 'dotnet publish' for native host test fixture generation.");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            string publishedHostPath = Path.Combine(outputDir, "XivIpc.NativeHost");
            if (process.ExitCode != 0 || !File.Exists(publishedHostPath))
            {
                throw new InvalidOperationException(
                    $"Failed to publish a self-contained XivIpc.NativeHost test fixture. exitCode={process.ExitCode}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            }

            _publishedHostPath = publishedHostPath;
            return publishedHostPath;
        }
    }

    private static bool IsCurrentUserInGroup(string groupName)
    {
        using var process = Process.Start(new ProcessStartInfo("id", "-Gn")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start 'id -Gn'.");

        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            return false;

        return output.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(group => string.Equals(group, groupName, StringComparison.Ordinal));
    }

    private static void EnsureStrictSharedPermissions(string path, bool isDirectory, bool isExecutable = false)
    {
        if (!OperatingSystem.IsLinux())
            return;

        string sharedGroup = ResolveSharedGroup();
        ApplyCommand("chgrp", sharedGroup, path);
        ApplyCommand("chmod", isDirectory ? "2770" : isExecutable ? "770" : "660", path);
    }

    private static bool IsExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        using var process = Process.Start(new ProcessStartInfo("test")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { "-x", path }
        }) ?? throw new InvalidOperationException("Failed to start 'test -x'.");

        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static void EnsureExecutablePermissions(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;

        ApplyCommand("chmod", "755", path);
    }

    private static void ApplyCommand(string command, string argument, string path)
    {
        using var process = Process.Start(new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { argument, path }
        }) ?? throw new InvalidOperationException($"Failed to start '{command}'.");

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string error = process.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException($"{command} {argument} '{path}' failed with exit code {process.ExitCode}: {error}");
        }
    }
}
