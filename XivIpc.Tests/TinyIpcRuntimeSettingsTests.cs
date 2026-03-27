using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

public sealed class TinyIpcRuntimeSettingsTests
{
    [Fact]
    public void ResolveSharedScope_DefaultsBrokerDirectoryAndPrefix()
    {
        using IDisposable overrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.SharedDirectory, "/tmp/xivipc-settings-shared"),
            (TinyIpcEnvironment.BrokerDirectory, null),
            (TinyIpcEnvironment.SharedGroup, "steam"),
            (TinyIpcEnvironment.SharedPrefix, null));

        SharedScopeSettings settings = TinyIpcRuntimeSettings.ResolveSharedScope();

        Assert.Equal("/tmp/xivipc-settings-shared", settings.SharedDirectory);
        Assert.Equal("/tmp/xivipc-settings-shared", settings.BrokerDirectory);
        Assert.Equal("steam", settings.SharedGroup);
        Assert.Equal("tinyipc", settings.SharedPrefix);
    }

    [Fact]
    public void ResolveMessageBusSelection_ParsesBackendAndStorageModes()
    {
        using IDisposable overrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.MessageBusBackend, "sidecar"),
            (TinyIpcEnvironment.SidecarStorageMode, "ring"));

        MessageBusSelectionSettings settings = TinyIpcRuntimeSettings.ResolveMessageBusSelection(brokerRequiredForAuto: false);

        Assert.Equal(MessageBusBackendKind.Sidecar, settings.Backend);
        Assert.Equal(DirectStorageKind.InMemory, settings.DirectStorage);
        Assert.Equal(SidecarStorageKind.Ring, settings.SidecarStorage);
        Assert.False(settings.BrokerRequiredForAuto);
    }

    [Fact]
    public void ResolveMessageBusSelection_SupportsAliases_AndDefaultsUnknownValues()
    {
        using IDisposable overrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.MessageBusBackend, "shm"),
            (TinyIpcEnvironment.DirectStorageMode, "unexpected"),
            (TinyIpcEnvironment.SidecarStorageMode, null));

        MessageBusSelectionSettings aliased = TinyIpcRuntimeSettings.ResolveMessageBusSelection(brokerRequiredForAuto: true);

        Assert.Equal(MessageBusBackendKind.Direct, aliased.Backend);
        Assert.Equal(DirectStorageKind.InMemory, aliased.DirectStorage);
        Assert.Equal(SidecarStorageKind.Journal, aliased.SidecarStorage);
        Assert.True(aliased.BrokerRequiredForAuto);

        using IDisposable unknownOverrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.MessageBusBackend, "not-a-backend"));

        MessageBusSelectionSettings defaulted = TinyIpcRuntimeSettings.ResolveMessageBusSelection(brokerRequiredForAuto: false);

        Assert.Equal(MessageBusBackendKind.Auto, defaulted.Backend);
    }

    [Fact]
    public void ResolveMessageBusSelection_RejectsExplicitDirectBackendWithSidecarStorageOverride()
    {
        using IDisposable overrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.MessageBusBackend, "direct"),
            (TinyIpcEnvironment.SidecarStorageMode, "ring"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => TinyIpcRuntimeSettings.ResolveMessageBusSelection(brokerRequiredForAuto: false));

        Assert.Contains(TinyIpcEnvironment.SidecarStorageMode, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"{TinyIpcEnvironment.MessageBusBackend}=direct", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveMessageBusSelection_RejectsExplicitSidecarBackendWithDirectStorageOverride()
    {
        using IDisposable overrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.MessageBusBackend, "sidecar"),
            (TinyIpcEnvironment.DirectStorageMode, "shared-memory"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => TinyIpcRuntimeSettings.ResolveMessageBusSelection(brokerRequiredForAuto: false));

        Assert.Contains(TinyIpcEnvironment.DirectStorageMode, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"{TinyIpcEnvironment.MessageBusBackend}=sidecar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveMessageBusSelection_AllowsAutoBackendWithBothStorageOverrides()
    {
        using IDisposable overrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.MessageBusBackend, "auto"),
            (TinyIpcEnvironment.DirectStorageMode, "shared-memory"),
            (TinyIpcEnvironment.SidecarStorageMode, "ring"));

        MessageBusSelectionSettings settings = TinyIpcRuntimeSettings.ResolveMessageBusSelection(brokerRequiredForAuto: true);

        Assert.Equal(MessageBusBackendKind.Auto, settings.Backend);
        Assert.Equal(DirectStorageKind.SharedMemory, settings.DirectStorage);
        Assert.Equal(SidecarStorageKind.Ring, settings.SidecarStorage);
        Assert.True(settings.BrokerRequiredForAuto);
    }

    [Fact]
    public void ResolveBrokerRuntime_UsesExplicitSocketPathAndIdleShutdownOverride()
    {
        using IDisposable overrides = TinyIpcEnvironment.Override(
            (TinyIpcEnvironment.SharedDirectory, "/tmp/xivipc-settings-shared"),
            (TinyIpcEnvironment.BrokerDirectory, "/tmp/xivipc-settings-broker"),
            (TinyIpcEnvironment.BrokerSocketPath, "/tmp/xivipc-settings-socket/custom.sock"),
            (TinyIpcEnvironment.BrokerIdleShutdownMs, "1500"));

        SharedScopeSettings sharedScope = TinyIpcRuntimeSettings.ResolveSharedScope();
        BrokerRuntimeSettings settings = TinyIpcRuntimeSettings.ResolveBrokerRuntime(sharedScope);

        Assert.Equal("/tmp/xivipc-settings-socket/custom.sock", settings.SocketPath);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), settings.IdleShutdownDelay);
    }
}
