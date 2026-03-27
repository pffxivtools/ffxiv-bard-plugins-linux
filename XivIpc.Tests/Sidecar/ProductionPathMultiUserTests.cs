using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class ProductionPathMultiUserTests
{
    private static readonly SidecarMultiUserTests Inner = new();

    [Fact]
    public Task SecondaryUnixUser_CanConnect_ReadRing_AndReceivePublish_WhenEnabled()
        => Inner.SecondaryUnixUser_CanConnect_ReadRing_AndReceivePublish_WhenEnabled(ProductionPathTestEnvironment.BackendName);
}
