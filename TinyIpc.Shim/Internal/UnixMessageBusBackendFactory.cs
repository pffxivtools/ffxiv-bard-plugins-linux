using System.ComponentModel;
using System.Net.Sockets;
using System.Reflection;
using TinyIpc.Messaging;
using XivIpc.Internal;
using XivIpc.Messaging;

namespace TinyIpc.Internal;

internal static class UnixMessageBusBackendFactory
{
    internal const string AutoBackend = "auto";
    internal const string DirectSharedMemoryBackend = "shared-memory";
    internal const string DirectAliasBackend = "direct";
    internal const string SidecarSharedMemoryBackend = "sidecar";

    internal static IShimTinyMessageBus Create(ChannelInfo channelInfo)
    {
        ArgumentNullException.ThrowIfNull(channelInfo);

        return ResolveBackendName() switch
        {
            DirectAliasBackend or DirectSharedMemoryBackend => CreateDirect(channelInfo),
            SidecarSharedMemoryBackend => CreateSidecar(channelInfo),
            AutoBackend => CreateAuto(channelInfo),

            var backend => throw new InvalidOperationException($"Unknown TinyIpc message bus backend '{backend}'.")
        };
    }

    internal static string ResolveBackendName()
    {
        string? configured = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.MessageBusBackend);
        if (string.IsNullOrWhiteSpace(configured))
            return AutoBackend;

        return NormalizeBackendName(configured);
    }

    private static IShimTinyMessageBus CreateAuto(ChannelInfo channelInfo)
    {
        bool brokerRequired = IsBrokerRequiredForAuto();
        bool nativeHostAvailable = UnixSidecarProcessManager.TryResolveNativeHostPath(out _);

        if (brokerRequired || nativeHostAvailable)
        {
            return CreateSidecar(channelInfo);
        }

        try
        {
            return CreateDirect(channelInfo);
        }
        catch (Exception directEx)
        {
            TinyIpcLogger.Warning(
                nameof(UnixMessageBusBackendFactory),
                "AutoDirectFailed",
                "Direct/in-memory backend initialization failed during auto backend selection; using safe no-op behavior.",
                directEx,
                ("channel", channelInfo.Name),
                ("sharedDir", TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedDirectory)),
                ("configuredBackend", TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.MessageBusBackend) ?? AutoBackend));

            return new DisabledShimMessageBus(
                channelInfo.Name,
                "Auto backend could not initialize either sidecar or direct/in-memory backend.",
                new AggregateException(
                    "TinyIpc backend startup failures.",
                    new InvalidOperationException("Sidecar backend was not selected for this auto-start path."),
                    directEx));
        }
    }

    private static IShimTinyMessageBus CreateDirect(ChannelInfo channelInfo)
        => new XivMessageBusAdapter(new UnixInMemoryTinyMessageBus(channelInfo));

    private static IShimTinyMessageBus CreateSidecar(ChannelInfo channelInfo)
         => new XivMessageBusAdapter(new UnixSidecarTinyMessageBus(channelInfo));

    private static bool ShouldFallbackFromSidecar(Exception ex)
    {
        ex = Unwrap(ex);

        return ex is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or PlatformNotSupportedException
            or SocketException
            or Win32Exception
            or SidecarStartupException;
    }

    private static bool IsBrokerRequiredForAuto()
    {
        if (string.IsNullOrWhiteSpace(TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedGroup)))
            return false;

        return UnixSidecarProcessManager.TryResolveNativeHostPath(out _);
    }

    private static void LogAutoSidecarFailure(ChannelInfo channelInfo, Exception ex, bool brokerRequired)
    {
        TinyIpcLogger.Warning(
            nameof(UnixMessageBusBackendFactory),
            brokerRequired ? "AutoBrokerRequiredFailed" : "AutoFallbackToDirect",
            brokerRequired
                ? "Sidecar startup failed during auto backend selection; brokered mode was required so TinyIpc will use a safe no-op bus."
                : "Sidecar startup failed during auto backend selection; falling back to direct/in-memory backend.",
            ex,
            ("channel", channelInfo.Name),
            ("sharedDir", TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedDirectory)),
            ("configuredBackend", TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.MessageBusBackend) ?? AutoBackend),
            ("brokerRequired", brokerRequired),
            ("nativeHostCandidates", string.Join(";", UnixSidecarProcessManager.GetNativeHostCandidatePathsForDiagnostics())));
    }

    private static Exception Unwrap(Exception ex)
    {
        while (true)
        {
            switch (ex)
            {
                case AggregateException agg when agg.InnerExceptions.Count == 1:
                    ex = agg.InnerExceptions[0];
                    continue;
                case TargetInvocationException tie when tie.InnerException is not null:
                    ex = tie.InnerException;
                    continue;
                default:
                    return ex;
            }
        }
    }

    private static string NormalizeBackendName(string configured)
    {
        string normalized = configured.Trim().ToLowerInvariant();
        return normalized switch
        {
            "shm" => DirectSharedMemoryBackend,
            "sharedmemory" => DirectSharedMemoryBackend,
            "shared_memory" => DirectSharedMemoryBackend,
            "direct" => DirectAliasBackend,
            "sidecar-shared-memory" => SidecarSharedMemoryBackend,
            "sidecar_shared_memory" => SidecarSharedMemoryBackend,
            _ => normalized
        };
    }
}
