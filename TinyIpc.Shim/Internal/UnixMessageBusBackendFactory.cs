using System.ComponentModel;
using System.Net.Sockets;
using System.Reflection;
using TinyIpc.IO;
using TinyIpc.Messaging;
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
        string? configured = Environment.GetEnvironmentVariable("TINYIPC_MESSAGE_BUS_BACKEND");
        if (string.IsNullOrWhiteSpace(configured))
            return AutoBackend;

        return NormalizeBackendName(configured);
    }

    private static IShimTinyMessageBus CreateAuto(ChannelInfo channelInfo)
    {
        Exception? sidecarFailure;
        try
        {
            return CreateSidecar(channelInfo);
        }
        catch (Exception ex) when (ShouldFallbackFromSidecar(ex))
        {
            sidecarFailure = ex;
        }

        try
        {
            return CreateDirect(channelInfo);
        }
        catch (Exception directEx)
        {
            throw new InvalidOperationException(
                "TinyIpc auto backend selection failed. Sidecar startup failed, and direct shared-memory startup also failed.",
                new AggregateException(
                    "TinyIpc backend startup failures.",
                    sidecarFailure ?? new InvalidOperationException("Unknown sidecar startup failure."),
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
            or TimeoutException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or PlatformNotSupportedException
            or SocketException
            or Win32Exception;
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