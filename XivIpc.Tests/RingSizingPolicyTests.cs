using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class RingSizingPolicyTests
{
    [Fact]
    public void Compute_DefaultOneMiBRequest_DoublesBudgetWithoutCap()
    {
        RingSizing sizing = RingSizingPolicy.Compute(1024 * 1024, 256, 64, 40);

        Assert.Equal(1024 * 1024, sizing.RequestedBufferBytes);
        Assert.Equal(2 * 1024 * 1024, sizing.BudgetBytes);
        Assert.Equal(256, sizing.SlotCount);
        Assert.Equal(8151, sizing.SlotPayloadBytes);
        Assert.Equal(2_096_960, sizing.ImageSize);
        Assert.False(sizing.WasCapped);
    }

    [Fact]
    public void Compute_SixteenMiBRequest_UsesThirtyTwoMiBBudget()
    {
        RingSizing sizing = RingSizingPolicy.Compute(1 << 24, 256, 64, 40);

        Assert.Equal(1 << 24, sizing.RequestedBufferBytes);
        Assert.Equal(32 * 1024 * 1024, sizing.BudgetBytes);
        Assert.Equal(256, sizing.SlotCount);
        Assert.Equal(131_031, sizing.SlotPayloadBytes);
        Assert.Equal(33_554_240, sizing.ImageSize);
        Assert.False(sizing.WasCapped);
    }

    [Fact]
    public void Compute_RequestAboveCap_ClampsBudgetToSixtyFourMiB()
    {
        RingSizing sizing = RingSizingPolicy.Compute(40 * 1024 * 1024, 256, 64, 40);

        Assert.Equal(40 * 1024 * 1024, sizing.RequestedBufferBytes);
        Assert.Equal(64 * 1024 * 1024, sizing.BudgetBytes);
        Assert.True(sizing.WasCapped);
    }

    [Fact]
    public void Compute_TinyRequest_ThrowsWhenBudgetCannotFitConfiguredSlots()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => RingSizingPolicy.Compute(2048, 256, 64, 40));

        Assert.Contains("too small", exception.Message, StringComparison.Ordinal);
    }
}
