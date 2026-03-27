using TinyIpc.Messaging;
using XivIpc.Internal;
using XivIpc.Messaging;

namespace TinyIpc.Internal;

internal static class UnixMessageBusBackendFactory
{
    internal static IShimTinyMessageBus Create(ChannelInfo channelInfo)
    {
        ArgumentNullException.ThrowIfNull(channelInfo);

        bool brokerRequired = IsBrokerRequiredForAuto();
        MessageBusSelectionSettings selection = TinyIpcRuntimeSettings.ResolveMessageBusSelection(brokerRequired);

        return selection.Backend switch
        {
            MessageBusBackendKind.Direct => CreateDirect(channelInfo, selection.DirectStorage),
            MessageBusBackendKind.Sidecar => CreateSidecar(channelInfo),
            MessageBusBackendKind.Auto => CreateAuto(channelInfo, selection),
            _ => throw new InvalidOperationException($"Unknown TinyIpc message bus backend '{selection.Backend}'.")
        };
    }

    private static IShimTinyMessageBus CreateAuto(ChannelInfo channelInfo, MessageBusSelectionSettings selection)
    {
        bool nativeHostAvailable = UnixSidecarProcessManager.TryResolveNativeHostPath(out _);
        SharedScopeSettings sharedScope = TinyIpcRuntimeSettings.ResolveSharedScope();

        if (selection.BrokerRequiredForAuto || nativeHostAvailable)
            return CreateSidecar(channelInfo);

        try
        {
            return CreateDirect(channelInfo, selection.DirectStorage);
        }
        catch (Exception directEx)
        {
            TinyIpcLogger.Warning(
                nameof(UnixMessageBusBackendFactory),
                "AutoDirectFailed",
                "Direct/in-memory backend initialization failed during auto backend selection; using safe no-op behavior.",
                directEx,
                ("channel", channelInfo.Name),
                ("sharedDir", sharedScope.SharedDirectory),
                ("configuredBackend", selection.Backend),
                ("directStorage", selection.DirectStorage));

            return new DisabledShimMessageBus(
                channelInfo.Name,
                "Auto backend could not initialize either sidecar or direct/in-memory backend.",
                new AggregateException(
                    "TinyIpc backend startup failures.",
                    new InvalidOperationException("Sidecar backend was not selected for this auto-start path."),
                    directEx));
        }
    }

    private static IShimTinyMessageBus CreateDirect(ChannelInfo channelInfo, DirectStorageKind storage)
        => new XivMessageBusAdapter(new UnixDirectTinyMessageBus(channelInfo, storage));

    private static IShimTinyMessageBus CreateSidecar(ChannelInfo channelInfo)
         => new XivMessageBusAdapter(new UnixSidecarTinyMessageBus(channelInfo));

    private static bool IsBrokerRequiredForAuto()
    {
        SharedScopeSettings sharedScope = TinyIpcRuntimeSettings.ResolveSharedScope();
        if (string.IsNullOrWhiteSpace(sharedScope.SharedGroup))
            return false;

        return UnixSidecarProcessManager.TryResolveNativeHostPath(out _);
    }
}
