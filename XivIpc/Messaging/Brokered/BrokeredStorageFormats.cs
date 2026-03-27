using XivIpc.Internal;

namespace XivIpc.Messaging;

internal static class BrokeredStorageFormats
{
    internal static IBrokeredStorageFormat Resolve(SidecarStorageKind storageKind)
        => storageKind switch
        {
            SidecarStorageKind.Ring => BrokeredRingStorageFormat.Instance,
            _ => BrokeredJournalStorageFormat.Instance
        };

    internal static IBrokeredStorageFormat Resolve(BrokeredStorageKind storageKind)
        => storageKind switch
        {
            BrokeredStorageKind.Ring => BrokeredRingStorageFormat.Instance,
            _ => BrokeredJournalStorageFormat.Instance
        };
}
