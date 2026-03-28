namespace XivIpc.Internal;

internal enum MessageBusBackendKind
{
    Auto,
    Direct,
    Sidecar
}

internal enum DirectStorageKind
{
    InMemory,
    SharedMemory
}

internal enum SidecarStorageKind
{
    Journal,
    Ring
}

internal readonly record struct MessageBusSelectionSettings(
    MessageBusBackendKind Backend,
    DirectStorageKind DirectStorage,
    SidecarStorageKind SidecarStorage,
    bool BrokerRequiredForAuto);

internal readonly record struct DirectTransportSettings(
    DirectStorageKind Storage,
    int SlotCount,
    long MessageTtlMs);

internal readonly record struct SharedScopeSettings(
    string SharedDirectory,
    string BrokerDirectory,
    string? SharedGroup,
    string SharedPrefix,
    bool SharedDirectoryWasConfigured,
    bool BrokerDirectoryWasConfigured);

internal readonly record struct SidecarTransportSettings(
    SidecarStorageKind Storage,
    int RingSlotCount,
    int HeartbeatIntervalMs,
    int HeartbeatTimeoutMs,
    long MessageTtlMs);

internal readonly record struct BrokerRuntimeSettings(
    string SocketPath,
    TimeSpan IdleShutdownDelay);

internal readonly record struct LoggingSettings(
    string? LogDirectory,
    string? EnableLogging,
    string? LogLevel,
    string? LogPayload,
    string? LogMaxBytes,
    string? LogFileCount,
    string? FileNotifier);

internal readonly record struct NativeHostSettings(
    string? ExplicitNativeHostPath,
    string? LaunchId,
    string? Backend);

internal static class TinyIpcRuntimeSettings
{
    private const int DefaultSlotCount = 64;
    private const int MinimumSlotCount = 4;
    private const long DefaultMessageTtlMs = 30_000;
    private const int DefaultHeartbeatIntervalMs = 2_000;
    private const int DefaultHeartbeatTimeoutMs = 60_000;
    private const string DefaultSharedPrefix = "tinyipc";
    private const int DefaultBrokerIdleShutdownMs = 120_000;
    private const int MinimumBrokerIdleShutdownMs = 250;

    internal static MessageBusSelectionSettings ResolveMessageBusSelection(bool brokerRequiredForAuto)
    {
        string? rawBackend = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.MessageBusBackend);
        string configured = NormalizeBusBackend(rawBackend);

        MessageBusBackendKind backend = configured switch
        {
            "direct" => MessageBusBackendKind.Direct,
            "sidecar" => MessageBusBackendKind.Sidecar,
            _ => MessageBusBackendKind.Auto
        };

        DirectStorageKind directStorage = ResolveDirectStorage();
        SidecarStorageKind sidecarStorage = ResolveSidecarStorage();

        ValidateStorageSelectionCompatibility(
            backend,
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.DirectStorageMode),
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SidecarStorageMode));

        return new MessageBusSelectionSettings(
            backend,
            directStorage,
            sidecarStorage,
            brokerRequiredForAuto);
    }

    internal static DirectTransportSettings ResolveDirectTransport()
        => new(
            ResolveDirectStorage(),
            ResolvePositiveInt32(TinyIpcEnvironment.SlotCount, DefaultSlotCount, MinimumSlotCount),
            ResolvePositiveInt64(TinyIpcEnvironment.MessageTtlMs, DefaultMessageTtlMs));

    internal static SharedScopeSettings ResolveSharedScope()
    {
        string? configuredSharedDirectory = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedDirectory);
        string? configuredBrokerDirectory = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.BrokerDirectory);
        string? configuredSharedGroup = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedGroup);
        string? configuredSharedPrefix = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedPrefix);

        string sharedDirectory = UnixSharedStorageHelpers.ResolveSharedDirectoryForCurrentRuntime(configuredSharedDirectory);
        string brokerDirectory = string.IsNullOrWhiteSpace(configuredBrokerDirectory)
            ? sharedDirectory
            : UnixSharedStorageHelpers.ConvertPathForCurrentRuntime(configuredBrokerDirectory);

        return new SharedScopeSettings(
            sharedDirectory,
            brokerDirectory,
            string.IsNullOrWhiteSpace(configuredSharedGroup) ? null : configuredSharedGroup,
            string.IsNullOrWhiteSpace(configuredSharedPrefix) ? DefaultSharedPrefix : configuredSharedPrefix.Trim(),
            !string.IsNullOrWhiteSpace(configuredSharedDirectory),
            !string.IsNullOrWhiteSpace(configuredBrokerDirectory));
    }

    internal static SidecarTransportSettings ResolveSidecarTransport()
        => new(
            ResolveSidecarStorage(),
            ResolvePositiveInt32(TinyIpcEnvironment.SlotCount, DefaultSlotCount, MinimumSlotCount),
            ResolvePositiveInt32(TinyIpcEnvironment.SidecarHeartbeatIntervalMs, DefaultHeartbeatIntervalMs),
            ResolvePositiveInt32(TinyIpcEnvironment.SidecarHeartbeatTimeoutMs, DefaultHeartbeatTimeoutMs),
            ResolvePositiveInt64(TinyIpcEnvironment.MessageTtlMs, DefaultMessageTtlMs));

    internal static BrokerRuntimeSettings ResolveBrokerRuntime(SharedScopeSettings sharedScope)
    {
        string? explicitSocketPath = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.BrokerSocketPath);
        string socketPath = string.IsNullOrWhiteSpace(explicitSocketPath)
            ? Path.Combine(sharedScope.BrokerDirectory, "tinyipc-sidecar.sock")
            : UnixSharedStorageHelpers.ConvertPathForCurrentRuntime(explicitSocketPath);

        int idleShutdownMs = ResolvePositiveInt32(
            TinyIpcEnvironment.BrokerIdleShutdownMs,
            DefaultBrokerIdleShutdownMs,
            MinimumBrokerIdleShutdownMs);

        return new BrokerRuntimeSettings(socketPath, TimeSpan.FromMilliseconds(idleShutdownMs));
    }

    internal static LoggingSettings ResolveLogging()
        => new(
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.LogDirectory),
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.EnableLogging),
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.LogLevel),
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.LogPayload),
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.LogMaxBytes),
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.LogFileCount),
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.FileNotifier));

    internal static NativeHostSettings ResolveNativeHost()
        => new(
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.NativeHostPath),
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.LaunchId),
            TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.BusBackend));

    private static DirectStorageKind ResolveDirectStorage()
    {
        string normalized = (TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.DirectStorageMode) ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        return normalized switch
        {
            "shared-memory" or "sharedmemory" or "shared_memory" or "shm" => DirectStorageKind.SharedMemory,
            _ => DirectStorageKind.InMemory
        };
    }

    private static SidecarStorageKind ResolveSidecarStorage()
    {
        string normalized = (TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SidecarStorageMode) ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        return normalized switch
        {
            "ring" => SidecarStorageKind.Ring,
            _ => SidecarStorageKind.Journal
        };
    }

    private static string NormalizeBusBackend(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return "auto";

        return configured.Trim().ToLowerInvariant() switch
        {
            "shared-memory" or "sharedmemory" or "shared_memory" or "shm" => "direct",
            "direct" => "direct",
            "sidecar-shared-memory" or "sidecar_shared_memory" or "sidecar" => "sidecar",
            _ => "auto"
        };
    }

    private static void ValidateStorageSelectionCompatibility(
        MessageBusBackendKind backend,
        string? configuredDirectStorage,
        string? configuredSidecarStorage)
    {
        if (backend == MessageBusBackendKind.Direct && !string.IsNullOrWhiteSpace(configuredSidecarStorage))
        {
            throw new InvalidOperationException(
                $"'{TinyIpcEnvironment.SidecarStorageMode}' cannot be set when '{TinyIpcEnvironment.MessageBusBackend}=direct'. " +
                "Use sidecar storage selection only with the sidecar backend or leave the backend on auto.");
        }

        if (backend == MessageBusBackendKind.Sidecar && !string.IsNullOrWhiteSpace(configuredDirectStorage))
        {
            throw new InvalidOperationException(
                $"'{TinyIpcEnvironment.DirectStorageMode}' cannot be set when '{TinyIpcEnvironment.MessageBusBackend}=sidecar'. " +
                "Use direct storage selection only with the direct backend or leave the backend on auto.");
        }
    }

    private static int ResolvePositiveInt32(string variableName, int fallback, int minimum = 1)
    {
        string? raw = TinyIpcEnvironment.GetEnvironmentVariable(variableName);
        if (int.TryParse(raw, out int value) && value >= minimum)
            return value;

        return fallback;
    }

    private static long ResolvePositiveInt64(string variableName, long fallback, long minimum = 1)
    {
        string? raw = TinyIpcEnvironment.GetEnvironmentVariable(variableName);
        if (long.TryParse(raw, out long value) && value >= minimum)
            return value;

        return fallback;
    }
}
