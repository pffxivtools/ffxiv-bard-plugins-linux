using XivIpc.Internal;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class RingSizingPolicyTests
{
    [Fact]
    public void Compute_DefaultOneMiBRequest_DoublesBudgetWithoutCap()
    {
        RingSizing sizing = RingSizingPolicy.Compute(1024 * 1024, 64, 64, 40);

        Assert.Equal(1024 * 1024, sizing.RequestedBufferBytes);
        Assert.Equal(2 * 1024 * 1024, sizing.BudgetBytes);
        Assert.Equal(64, sizing.SlotCount);
        Assert.Equal(32_727, sizing.SlotPayloadBytes);
        Assert.Equal(2_097_152, sizing.ImageSize);
        Assert.False(sizing.WasCapped);
    }

    [Fact]
    public void Compute_SixteenMiBRequest_UsesThirtyTwoMiBBudget()
    {
        RingSizing sizing = RingSizingPolicy.Compute(1 << 24, 64, 64, 40);

        Assert.Equal(1 << 24, sizing.RequestedBufferBytes);
        Assert.Equal(32 * 1024 * 1024, sizing.BudgetBytes);
        Assert.Equal(64, sizing.SlotCount);
        Assert.Equal(524_247, sizing.SlotPayloadBytes);
        Assert.Equal(33_554_432, sizing.ImageSize);
        Assert.False(sizing.WasCapped);
    }

    [Fact]
    public void Compute_RequestAboveCap_ClampsBudgetToOneHundredTwentyEightMiB()
    {
        RingSizing sizing = RingSizingPolicy.Compute(80 * 1024 * 1024, 64, 64, 40);

        Assert.Equal(80 * 1024 * 1024, sizing.RequestedBufferBytes);
        Assert.Equal(128 * 1024 * 1024, sizing.BudgetBytes);
        Assert.True(sizing.WasCapped);
    }

    [Fact]
    public void Compute_TinyRequest_ThrowsWhenBudgetCannotFitConfiguredSlots()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => RingSizingPolicy.Compute(512, 64, 64, 40));

        Assert.Contains("too small", exception.Message, StringComparison.Ordinal);
    }
}
